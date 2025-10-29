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
        var services = new ServiceCollection();
        services.Configure<SloFiscalOptions>(opt => opt.BaseUrl = "https://blagajne-test.fu.gov.si:9002");
        using var provider = services.BuildServiceProvider();
        var monitor = provider.GetRequiredService<IOptionsMonitor<SloFiscalOptions>>();

        _eDavkiApiClient = new EDavkiApiClient(new LoggerFactory().CreateLogger<EDavkiApiClient>(), new HttpClientFactory(() => _certificate), monitor);

        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .Build();
        _appSettings = config.GetSection("eDavkiRepairerOptions").Get<eDavkiRepairerOptions>();

        _queryService = new QueryService(_appSettings);
    }

    public static async Task Main(string[] args)
    {
        int? sellerTaxNumber = await GetSellerTaxNumberAsync(); //57536163;
        int lastReceiptNumber = await _queryService.GetLastReceiptNumberAsync(); //TODO overide from parameter.
        int receiptNumber = lastReceiptNumber;
        _certificate = await _queryService.GetCertificateAsync();

        var requests = GetOriginalRequests(_appSettings.RequestsDirectory).ToList();

        var vatCustomers = await _queryService.GetCustomerVatNumbersAsync(_appSettings.From, _appSettings.To);
        PairReqeustsWithVatCustomers(requests, vatCustomers);

        var repairRequests = requests.Select(x => CreateRepairRequest(++lastReceiptNumber, sellerTaxNumber, x)).ToList();

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

        Console.WriteLine($"\r\nPath {_appSettings.RequestsDirectory} contains {requests.Count} requests. Last receipt number is {receiptNumber}. Do you want to proceed? y\\n");
        var proceed = Console.ReadKey(true);
        if (proceed.Key != ConsoleKey.Y)
        {
            return;
        }

//#if DEBUG
//        string? ou = _certificate.Subject
//                    .Split(',')
//                    .Select(part => part.Trim())
//                    .Where(part => part.StartsWith("OU=", StringComparison.OrdinalIgnoreCase))
//                    .Select(part => part.Substring(3))
//                    .FirstOrDefault();
//        var taxNumber = int.Parse(ou);

//        foreach (var req in repairRequests)
//        {
//            req.InvoiceRequestDto.InvoiceRequest.Invoice.TaxNumber = taxNumber;
//            req.InvoiceRequestDto.InvoiceRequest.Invoice.InvoiceIdentifier.BusinessPremiseID = "136";
//        }
//#endif



        if (_certificate is null)
        {
            Console.WriteLine("Certificate not found.");
            return;
        }

        (var success, var failed) = await SendRequestsAsync(repairRequests, _certificate);
        Console.WriteLine($"\r\nFinished processing requests. Success: {success}, Failed: {failed}");
    }

    private static async Task<int?> GetSellerTaxNumberAsync()
    {
        int bpTaxIdentification = await _queryService.GetBusinessPremiseTaxIdentificationNumberAsync();
        var merchantTaxNumberString = await _queryService.GetMerchatnTaxIdentificationNumberAsync();
        int merchantTaxNumber = merchantTaxNumberString.GetTaxNumber();

        return bpTaxIdentification != merchantTaxNumber ? merchantTaxNumber : null;
    }

    private static async Task<(int, int)> SendRequestsAsync(List<InvoiceRequest> repairRequests, X509Certificate2 certificate)
    {
        int i = 0;
        int success = 0;
        int failed = 0;
        foreach (var requestDto in repairRequests)
        {
            Console.Write($"Sending receipt {++i}/{repairRequests.Count}\t{GetReferencInvoiceNumber(requestDto)}");

            var signedRequest = certificate.SignObject(requestDto.InvoiceRequestDto);
            var response = await _eDavkiApiClient.PostReceiptAsync(signedRequest);

            var fiscalizationResult = await response.GetFiscalizationResultAsync();
            if (fiscalizationResult.IsFailed)
            {
                var reasonCode = fiscalizationResult.GetReasonCode();
                Console.WriteLine($"\tFailed to fiscalize receipt {requestDto.InvoiceRequestDto.InvoiceRequest.Invoice.InvoiceIdentifier}: {reasonCode}");
                MoveTo(Path.Combine(_appSettings.ResultPath, "Failed"), requestDto.FileName);
                failed++;
                continue;
            }
            else
            {

                Console.WriteLine($"\tSuccessfully fiscalized");
                success++;
                MoveSucessTo(Path.Combine(_appSettings.ResultPath, "Success"),
                             requestDto.FileName,
                             GetReferencInvoiceNumber(requestDto),
                             requestDto.InvoiceRequestDto,
                             (await response.Content.ReadAsStringAsync()).GetPayload());
                await _queryService.UpdateInvoiceNumber(int.Parse(requestDto.InvoiceRequestDto.InvoiceRequest.Invoice.InvoiceIdentifier.InvoiceNumber));
            }
        }
        return (success, failed);
    }

    private static void MoveSucessTo(string directory,
                                     string fileName,
                                     string receiptNumber,
                                     InvoiceRequestDto invoiceRequestDto,
                                     string responseBody)
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

        File.WriteAllText(Path.Combine(subDirectory, $"repaired.json"), JsonSerializer.Serialize(invoiceRequestDto, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(subDirectory, $"repairedResponse.json"), responseBody);
        File.Move(fileName, Path.Combine(subDirectory, "original.json"));
    }

    private static void MoveTo(string directory, string fileName)
    {
        if (Directory.Exists(directory) == false)
        {
            Directory.CreateDirectory(directory);
        }

        File.Move(fileName, Path.Combine(directory, Path.GetFileName(fileName)));
    }

    private static void PairReqeustsWithVatCustomers(List<InvoiceRequest> repairRequests, List<VatCustomer> vatCustomers)
    {
        foreach (var customer in vatCustomers)
        {
            var request = repairRequests.FirstOrDefault(x => x.InvoiceRequestDto.InvoiceRequest.Invoice.InvoiceIdentifier.InvoiceNumber.Trim() == customer.FiscalizationResult?.ReceiptNumber.ToString());
            if (request is null)
            {
                continue;
            }
            request.InvoiceRequestDto.InvoiceRequest.Invoice.CustomerVATNumber = customer.VatNumber;
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