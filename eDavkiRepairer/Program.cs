using Datapac.Posybe.POS.Domain.Extensions;
using Datapac.Posybe.POS.Fiscal.SLO.Api;
using Datapac.Posybe.POS.Fiscal.SLO.Api.Model;
using Datapac.Posybe.POS.Fiscal.SLO.Extensions;
using Datapac.Posybe.POS.Model.Configuration.Cloud.Fiscal;
using eDavkiRepairer;
using eDavkiRepairer.Extensions;
using eDavkiRepairer.Service;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

public static partial class Program
{
    private static IEDavkiApiClient _eDavkiApiClient;
    private static QueryService _queryService;
    private static X509Certificate2? _certificate;
    private static eDavkiRepairerOptions _appSettings;

    static Program()
    {
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .Build();
        _appSettings = config.GetSection("eDavkiRepairerOptions").Get<eDavkiRepairerOptions>();

        var services = new ServiceCollection();
        services.Configure<SloFiscalOptions>(opt => opt.BaseUrl = _appSettings.eDavkiBaseAddress);
        using var provider = services.BuildServiceProvider();
        var monitor = provider.GetRequiredService<IOptionsMonitor<SloFiscalOptions>>();

        _eDavkiApiClient = new EDavkiApiClient(new LoggerFactory().CreateLogger<EDavkiApiClient>(), new HttpClientFactory(() => _certificate), monitor);


        _queryService = new QueryService(_appSettings);
    }

    public static async Task Main(string[] args)
    {
        var includedSalesTransaction = GetIncludeOnlySalesTransactions(args);
        int? sellerTaxNumber = await GetSellerTaxNumberAsync();
        int lastReceiptNumber = await _queryService.GetLastReceiptNumberAsync();
        int receiptNumber = lastReceiptNumber;
        _certificate = await _queryService.GetCertificateAsync();

        var requests = GetOriginalRequests(_appSettings.RequestsDirectory).ToList();

        var sorted = requests.OrderBy(x => x.InvoiceRequestDto.InvoiceRequest.Header.DateTime);
        var firstRequestDateTime = sorted.FirstOrDefault();
        var lastRequestDateTime = sorted.LastOrDefault();

        var requestFrom = firstRequestDateTime?.InvoiceRequestDto?.InvoiceRequest?.Header?.DateTime;
        var requestTo = lastRequestDateTime?.InvoiceRequestDto?.InvoiceRequest?.Header?.DateTime;
        var from = requestFrom.HasValue ? requestFrom.Value.AddMinutes(-1) : DateTime.MinValue;
        var to = requestTo.HasValue ? requestTo.Value.AddMinutes(1) : DateTime.Now;

        var vatCustomers = await _queryService.GetCustomerVatNumbersAsync(from, to);
        PairReqeutsWithVatCustomers(requests, vatCustomers);

        var repairRequests = requests.Select(x => CreateRepairRequest(++lastReceiptNumber, sellerTaxNumber, x)).ToList();
        PrintRequests(repairRequests);

        var lastTransactionWithoutSellerTaxNumber = requests.OrderByDescending(x => x.InvoiceRequestDto.InvoiceRequest.Invoice.ReferenceInvoice.FirstOrDefault()?.ReferenceInvoiceIssueDateTime).FirstOrDefault();
        var vatNumberDateFrom = lastTransactionWithoutSellerTaxNumber?.InvoiceRequestDto.InvoiceRequest.Invoice.ReferenceInvoice.FirstOrDefault().ReferenceInvoiceIssueDateTime;
        vatNumberDateFrom = _appSettings.VatNumberDateFrom.HasValue ? _appSettings.VatNumberDateFrom : vatNumberDateFrom;
        var vatNubmerDateTo = _appSettings.VatNumberDateTo is null ? DateTime.Now : _appSettings.VatNumberDateTo.Value;

        var merchantTaxNumber = await _queryService.GetMerchatnTaxIdentificationNumberAsync();
        var salesTransactionsToChangeVatNumber = await _queryService.GetSalesTransactionWithoutVatNumberAsync(vatNumberDateFrom.GetValueOrDefault(),
                                                                                                              vatNubmerDateTo,
                                                                                                              includedSalesTransaction);

        Console.WriteLine($"\r\nThere are {salesTransactionsToChangeVatNumber.Count()} sales transactions without VAT number between {vatNumberDateFrom} and {vatNubmerDateTo} for merchant tax number {merchantTaxNumber}. These sales transactions will not have VAT number updated in the repaired receipts.\r\n");

        var vatNumberChangeRequestst = GetVatNumberChangeRequests(salesTransactionsToChangeVatNumber,
                                                                  merchantTaxNumber.GetTaxNumber(),
                                                                  sellerTaxNumber,
                                                                  ref lastReceiptNumber).ToList();
        PrintRequests(vatNumberChangeRequestst);
        repairRequests.AddRange(vatNumberChangeRequestst);

        Console.WriteLine($"\r\nPath {_appSettings.RequestsDirectory} contains {repairRequests.Count} requests. Last receipt number is {receiptNumber}. Do you want to proceed? y\\n");
        var proceed = Console.ReadKey(true);
        if (proceed.Key != ConsoleKey.Y)
        {
            return;
        }

#if DEBUG
        string? ou = _certificate.Subject
                    .Split(',')
                    .Select(part => part.Trim())
                    .Where(part => part.StartsWith("OU=", StringComparison.OrdinalIgnoreCase))
                    .Select(part => part.Substring(3))
                    .FirstOrDefault();
        var taxNumber = int.Parse(ou);

        foreach (var req in repairRequests)
        {
            req.InvoiceRequestDto.InvoiceRequest.Invoice.TaxNumber = taxNumber;
            req.InvoiceRequestDto.InvoiceRequest.Invoice.InvoiceIdentifier.BusinessPremiseID = "136";
        }
#endif



        if (_certificate is null)
        {
            Console.WriteLine("Certificate not found.");
            return;
        }

        (var success, var failed, var failedST) = await SendRequestsAsync(repairRequests,
                                                                          _certificate,
                                                                          receiptNumber);
        Console.WriteLine($"\r\nFinished processing requests. Success: {success}, Failed: {failed}");
        if (failedST.Any())
        {
            Console.WriteLine(string.Join(",", failedST));
        }
    }

    private static List<string>? GetIncludeOnlySalesTransactions(string[] args)
    {
        var index = Array.IndexOf(args, "-includeOnly");
        if (index == -1)
        {
            return null;
        }

        return args[index + 1].Split(",").ToList();
    }

    private static IEnumerable<InvoiceRequest> GetVatNumberChangeRequests(IEnumerable<ReceiptInfo> salesTransactionsToChangeVatNumber,
                                                                          int merchantTaxNumber,
                                                                          int? sellerTaxNumber,
                                                                          ref int lastReceiptNumber)
    {
        var list = new List<InvoiceRequest>();
        foreach (var salesTransaction in salesTransactionsToChangeVatNumber)
        {
            var dateTimeNow = DateTime.Now;
            list.Add(new InvoiceRequest
            {
                OriginalGlobalSalesTransactionId = salesTransaction.GlobalSalesTransactionId,
                InvoiceRequestDto = new InvoiceRequestDto
                {
                    InvoiceRequest = new Datapac.Posybe.POS.Fiscal.SLO.Api.Model.InvoiceRequest
                    {
                        Header = new PayloadHeaderDto { DateTime = dateTimeNow, MessageID = Guid.NewGuid().ToString() },
                        Invoice = new InvoiceDto
                        {
                            IssueDateTime = dateTimeNow,
                            TaxNumber = merchantTaxNumber,
                            NumberingStructure = "B",
                            InvoiceIdentifier = new InvoiceIdentifierDto
                            {
                                BusinessPremiseID = salesTransaction.BusinessPremiseID,
                                ElectronicDeviceID = salesTransaction.DeviceId,
                                InvoiceNumber = (++lastReceiptNumber).ToString()
                            },
                            InvoiceAmount = salesTransaction.TotalAmount,
                            PaymentAmount = salesTransaction.TotalAmount,
                            SubsequentSubmit = false,
                            ReturnsAmount = null,
                            TaxesPerSeller = new List<TaxesPerSellerDto>
                            {
                                new TaxesPerSellerDto
                                {
                                    SellerTaxNumber = sellerTaxNumber,
                                    VAT = salesTransaction.VatAmounts.Select(vatAmount => new VATDto
                                    {
                                        TaxRate = vatAmount.VatValue,
                                        TaxableAmount = vatAmount.BaseAmount,
                                        TaxAmount = vatAmount.TaxAmount
                                    }).ToList()
                                }
                            },
                            OperatorTaxNumber = salesTransaction.OperatorTaxNumber.GetTaxNumber(),
                            ProtectedID = _certificate.GetProtectiveMark(
                                new Datapac.Posybe.POS.Fiscal.SLO.Model.FiscalInformation
                                {
                                    BusinessPremiseID = salesTransaction.BusinessPremiseID,
                                    ElectronicDeviceID = salesTransaction.DeviceId,
                                    InvoiceNumberingStructure = "B",
                                    TaxNumber = merchantTaxNumber,
                                    CashierTaxNumber = salesTransaction.OperatorTaxNumber.GetTaxNumber(),
                                },
                                dateTimeNow,
                                lastReceiptNumber.ToString(),
                                salesTransaction.TotalAmount),
                            CustomerVATNumber = !string.IsNullOrEmpty(salesTransaction.CustomerVatIdentificationNumber) ?
                                                salesTransaction.CustomerVatIdentificationNumber :
                                                salesTransaction.CustomerTaxIdentificationNumber,
                            ReferenceInvoice = new ReferenceInvoice[]
                            {
                                new ReferenceInvoice
                                {
                                    ReferenceInvoiceIdentifier = new InvoiceIdentifierDto
                                    {
                                        BusinessPremiseID = salesTransaction.BusinessPremiseID,
                                        ElectronicDeviceID = salesTransaction.DeviceId,
                                        InvoiceNumber = salesTransaction.ReceiptId.ToString()
                                    },
                                    ReferenceInvoiceIssueDateTime = salesTransaction.Date
                                }
                            },
                            SpecialNotes = "Naknadna sprememba podatkov: poprava CustomerVATNumber."
                        }
                    }
                }
            });
        }
        return list;
    }

    private static void PrintRequests(List<InvoiceRequest> repairRequests)
    {
        foreach (var request in repairRequests)
        {
            Console.Write($"Original receipt id: {GetReferencInvoiceNumber(request)}");
            if (!string.IsNullOrEmpty(request.InvoiceRequestDto.InvoiceRequest.Invoice.CustomerVATNumber))
            {
                Console.WriteLine($", CustomerVatNumber: {request.InvoiceRequestDto.InvoiceRequest.Invoice.CustomerVATNumber}");
            }
            else
            {
                Console.WriteLine("");
            }
        }
    }

    private static async Task<int?> GetSellerTaxNumberAsync()
    {
        int bpTaxIdentification = await _queryService.GetBusinessPremiseTaxIdentificationNumberAsync();
        var merchantTaxNumberString = await _queryService.GetMerchatnTaxIdentificationNumberAsync();
        int merchantTaxNumber = merchantTaxNumberString.GetTaxNumber();

        return bpTaxIdentification != merchantTaxNumber ? merchantTaxNumber : null;
    }

    private static async Task<(int, int, List<string>)> SendRequestsAsync(List<InvoiceRequest> repairRequests, X509Certificate2 certificate, int receiptNumber)
    {
        int i = 0;
        int success = 0;
        int failed = 0;
        List<string> failedST = new List<string>();
        foreach (var requestDto in repairRequests)
        {
            requestDto.InvoiceRequestDto.InvoiceRequest.Invoice.InvoiceIdentifier.InvoiceNumber = (++receiptNumber).ToString();
            Console.Write($"Sending receipt {++i}/{repairRequests.Count}\t{GetReferencInvoiceNumber(requestDto)}\t{GetInvoiceNumber(requestDto)}");

            var signedRequest = certificate.SignObject(requestDto.InvoiceRequestDto);
            var response = await _eDavkiApiClient.PostReceiptAsync(signedRequest);

            var fiscalizationResult = await response.GetFiscalizationResultAsync();
            if (fiscalizationResult.IsFailed)
            {
                receiptNumber--;
                var reasonCode = fiscalizationResult.GetReasonCode();
                Console.WriteLine($"\tFailed to fiscalize receipt {requestDto.InvoiceRequestDto.InvoiceRequest.Invoice.InvoiceIdentifier}: {reasonCode}");
                failed++;
                if (!string.IsNullOrEmpty(requestDto.OriginalGlobalSalesTransactionId))
                {
                    failedST.Add(requestDto.OriginalGlobalSalesTransactionId);
                }
                continue;
            }
            else
            {

                Console.WriteLine($"\tSuccessfully fiscalized");
                success++;
                if (string.IsNullOrEmpty(requestDto.FileName))
                {
                    SaveResult(Path.Combine(_appSettings.ResultPath, "Success"),
                               GetReferencInvoiceNumber(requestDto),
                               requestDto.InvoiceRequestDto,
                               (await response.Content.ReadAsStringAsync()).GetPayload());
                }
                else
                {
                    MoveSucessTo(Path.Combine(_appSettings.ResultPath, "Success"),
                                 requestDto.FileName,
                                 GetReferencInvoiceNumber(requestDto),
                                 requestDto.InvoiceRequestDto,
                                 (await response.Content.ReadAsStringAsync()).GetPayload());
                }
                await _queryService.UpdateInvoiceNumber(int.Parse(requestDto.InvoiceRequestDto.InvoiceRequest.Invoice.InvoiceIdentifier.InvoiceNumber));
            }
        }
        return (success, failed, failedST);
    }

    private static object GetInvoiceNumber(InvoiceRequest requestDto)
    {
        return $"{requestDto.InvoiceRequestDto.InvoiceRequest.Invoice.InvoiceIdentifier.BusinessPremiseID}-{requestDto.InvoiceRequestDto.InvoiceRequest.Invoice.InvoiceIdentifier.ElectronicDeviceID}-{requestDto.InvoiceRequestDto.InvoiceRequest.Invoice.InvoiceIdentifier.InvoiceNumber}";
    }

    private static void SaveResult(string directory,
                                   string receiptNumber,
                                   InvoiceRequestDto invoiceRequestDto,
                                   string responseBody)
    {
        string subDirectory = EnsureDirectory(directory, receiptNumber);
        File.WriteAllText(Path.Combine(subDirectory, $"repaired.json"), JsonSerializer.Serialize(invoiceRequestDto, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(subDirectory, $"repairedResponse.json"), responseBody);
    }

    private static void MoveSucessTo(string directory,
                                     string fileName,
                                     string receiptNumber,
                                     InvoiceRequestDto invoiceRequestDto,
                                     string responseBody)
    {
        string subDirectory = EnsureDirectory(directory, receiptNumber);

        File.WriteAllText(Path.Combine(subDirectory, $"repaired.json"), JsonSerializer.Serialize(invoiceRequestDto, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(subDirectory, $"repairedResponse.json"), responseBody);
        File.Move(fileName, Path.Combine(subDirectory, "original.json"));
    }

    private static string EnsureDirectory(string directory, string receiptNumber)
    {
        if (Directory.Exists(directory) == false)
        {
            Directory.CreateDirectory(directory);
        }
        var subDirectory = Path.Combine(directory, receiptNumber);
        if (Directory.Exists(subDirectory) == false)
        {
            Directory.CreateDirectory(subDirectory);
        }

        return subDirectory;
    }

    private static void PairReqeutsWithVatCustomers(List<InvoiceRequest> repairRequests, List<VatCustomer> vatCustomers)
    {
        foreach (var customer in vatCustomers)
        {
            var request = repairRequests.FirstOrDefault(x => x.InvoiceRequestDto.InvoiceRequest.Invoice.InvoiceIdentifier.InvoiceNumber.Trim() == customer.FiscalizationResult?.ReceiptNumber.ToString());
            if (request is null)
            {
                continue;
            }

            if (string.IsNullOrEmpty(customer.VatNumber))
            {
                var f = customer.TaxNumber;
            }

            request.InvoiceRequestDto.InvoiceRequest.Invoice.CustomerVATNumber = !string.IsNullOrEmpty(customer.VatNumber) ?
                customer.VatNumber :
                customer.TaxNumber;
        }
    }

    private static InvoiceRequest CreateRepairRequest(int lastReceiptNumber, int? sellerTaxNumber, InvoiceRequest request)
    {
        request.InvoiceRequestDto.InvoiceRequest.Invoice.ReferenceInvoice =
                        [new ReferenceInvoice
                {
                    ReferenceInvoiceIdentifier = new InvoiceIdentifierDto
                    {
                        BusinessPremiseID = request.InvoiceRequestDto.InvoiceRequest.Invoice.InvoiceIdentifier.BusinessPremiseID,
                        ElectronicDeviceID = request.InvoiceRequestDto.InvoiceRequest.Invoice.InvoiceIdentifier.ElectronicDeviceID,
                        InvoiceNumber = request.InvoiceRequestDto.InvoiceRequest.Invoice.InvoiceIdentifier.InvoiceNumber
                    },
                    ReferenceInvoiceIssueDateTime = request.InvoiceRequestDto.InvoiceRequest.Invoice.IssueDateTime
                }];

        request.InvoiceRequestDto.InvoiceRequest.Invoice.InvoiceIdentifier.InvoiceNumber = lastReceiptNumber.ToString();
        request.InvoiceRequestDto.InvoiceRequest.Invoice.IssueDateTime = DateTime.Now;

        foreach (var taxPerSeller in request.InvoiceRequestDto.InvoiceRequest.Invoice.TaxesPerSeller)
        {
            taxPerSeller.SellerTaxNumber = sellerTaxNumber;
        }

        request.InvoiceRequestDto.InvoiceRequest.Header.MessageID = Guid.NewGuid().ToString();
        request.InvoiceRequestDto.InvoiceRequest.Header.DateTime = DateTime.Now;
        request.InvoiceRequestDto.InvoiceRequest.Invoice.ProtectedID = _certificate.GetProtectiveMark(
            new Datapac.Posybe.POS.Fiscal.SLO.Model.FiscalInformation
            {
                BusinessPremiseID = request.InvoiceRequestDto.InvoiceRequest.Invoice.InvoiceIdentifier.BusinessPremiseID,
                ElectronicDeviceID = request.InvoiceRequestDto.InvoiceRequest.Invoice.InvoiceIdentifier.ElectronicDeviceID,
                InvoiceNumberingStructure = request.InvoiceRequestDto.InvoiceRequest.Invoice.NumberingStructure,
                TaxNumber = request.InvoiceRequestDto.InvoiceRequest.Invoice.TaxNumber,
                CashierTaxNumber = request.InvoiceRequestDto.InvoiceRequest.Invoice.OperatorTaxNumber,
            },
            request.InvoiceRequestDto.InvoiceRequest.Header.DateTime,
            request.InvoiceRequestDto.InvoiceRequest.Invoice.InvoiceIdentifier.InvoiceNumber,
            request.InvoiceRequestDto.InvoiceRequest.Invoice.InvoiceAmount);

        var specialNote = !string.IsNullOrEmpty(request.InvoiceRequestDto.InvoiceRequest.Invoice.CustomerVATNumber) ?
            "Naknadna sprememba podatkov: poprava SellerTaxNumber in CustomerVATNumber." :
            "Naknadna sprememba podatkov: poprava SellerTaxNumber.";

        request.InvoiceRequestDto.InvoiceRequest.Invoice.SpecialNotes = specialNote;

        return request;
    }

    private static IEnumerable<InvoiceRequest> GetOriginalRequests(string path)
    {
        return Directory.EnumerateFiles(path, "*.json")
            .Select(file => new InvoiceRequest
            {
                FileName = file,
                InvoiceRequestDto = System.Text.Json.JsonSerializer.Deserialize<InvoiceRequestDto>(File.ReadAllText(file))
            })
            .OrderBy(x => x.InvoiceRequestDto.InvoiceRequest.Invoice.IssueDateTime)
            .Where(dto => dto is not null && dto.InvoiceRequestDto.InvoiceRequest.Invoice.TaxesPerSeller.Any(y => y.SellerTaxNumber == null));
    }

    private static string GetReferencInvoiceNumber(InvoiceRequest requestDto)
    {
        return $"{requestDto.InvoiceRequestDto.InvoiceRequest.Invoice.ReferenceInvoice.FirstOrDefault().ReferenceInvoiceIdentifier.BusinessPremiseID}-{requestDto.InvoiceRequestDto.InvoiceRequest.Invoice.ReferenceInvoice.FirstOrDefault().ReferenceInvoiceIdentifier.ElectronicDeviceID}-{requestDto.InvoiceRequestDto.InvoiceRequest.Invoice.ReferenceInvoice.FirstOrDefault().ReferenceInvoiceIdentifier.InvoiceNumber}";
    }
}