using Dapper;
using Datapac.Posybe.Common.Encryption;
using Datapac.Posybe.POS.Domain.Extensions;
using Datapac.Posybe.POS.Model.Fiscal.EDavki;
using Datapac.Posybe.POS.Model.Fiscal.Results.SLO;
using Datapac.Posybe.POS.Model.Pos;
using Datapac.Posybe.POS.Model.SalesTransaction;
using Microsoft.Data.Sqlite;
using System.Security.Cryptography.X509Certificates;

namespace eDavkiRepairer.Service;

internal class QueryService
{
    private eDavkiRepairerOptions _options;

    public QueryService(eDavkiRepairerOptions options)
    {
        _options = options;
    }

    public async Task<X509Certificate2?> GetCertificateAsync()
    {
        string connectionString = $"Data Source={_options.PosDBFileName};";

        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        string sql = @$"select s.Value from Storage s where s.Key = 'EDavkiInfo'";
        var eDavkiInfo = await connection.QueryFirstOrDefaultAsync<string>(sql);
        var eDavki = eDavkiInfo?.DeserializeOrDefault<EDavkiInfo>();

        return eDavki is null ? null : new X509Certificate2(Path.Combine(_options.PosDirectory, eDavki.ClientCertificateFileName), EncryptionHelper.Decrypt(eDavki.ClientCertificatePassword, EncryptionKeys.Password));
    }

    public async Task<List<VatCustomer>> GetCustomerVatNumbersAsync(DateTime from, DateTime to)
    {
        string connectionString = $"Data Source={_options.PosDBFileName};";

        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        string sql = @$"select st.CustomerVatIdentificationNumber as VatNumber, st.CustomerTaxIdentificationNumber as TaxNumber, st.FRegAdditionalInfo as AdditionalInfo 
                        from SalesTransactions st 
                        where (st.CustomerVatIdentificationNumber not null OR st.CustomerTaxIdentificationNumber not null)
                        and st.FRegRegistrationDate BETWEEN @from AND @to";
        var vatCustomers = await connection.QueryAsync<VatCustomer>(sql, param: new { from, to });

        foreach (var customer in vatCustomers)
        {
            customer.FiscalizationResult = customer.AdditionalInfo.DeserializeOrDefault<FiscalizationResult>();
        }

        return vatCustomers.ToList();
    }

    public async Task<int> GetLastReceiptNumberAsync()
    {
        string connectionString = $"Data Source={_options.PosDBFileName};";
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        string sql = @$"select st.FRegAdditionalInfo from SalesTransactions st
                        join Pos p on p.Device_Id = st.DeviceId 
                        where st.FRegAdditionalInfo not null
                        order by SalesTransactionId desc";

        var fiscalData = await connection.QueryFirstOrDefaultAsync<string>(sql);
        var fRegAdditionalData = fiscalData?.DeserializeOrDefault<FiscalizationResult>();
        return fRegAdditionalData?.ReceiptNumber ?? throw new Exception("Last receipt number not found");
    }

    public async Task<string> GetMerchatnTaxIdentificationNumberAsync()
    {
        string connectionString = $"Data Source={_options.PosDBFileName};";

        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        string sql = @$"select s.Value from Storage s where s.Key = 'Merchant'";
        var merchantData = await connection.QueryFirstOrDefaultAsync<string>(sql);
        var merchant = merchantData?.DeserializeOrDefault<Merchant>();
        if (merchant is null)
        {
            throw new Exception("Merchant tax identification number is not available");
        }

        return merchant.TaxIdentificationNumber;
    }

    public async Task<int> GetBusinessPremiseTaxIdentificationNumberAsync()
    {
        string connectionString = $"Data Source={_options.PosDBFileName};";

        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        string sql = @$"select s.Value from Storage s where s.Key = 'EDavkiInfo'";
        var eDavkiInfo = await connection.QueryFirstOrDefaultAsync<string>(sql);
        var eDavki = eDavkiInfo?.DeserializeOrDefault<EDavkiInfo>();

        return eDavki is null ?
            throw new Exception("Business premise tax identification number is not available") :
            int.Parse(eDavki.TaxNumber);
    }

    public async Task UpdateInvoiceNumber(int invoiceNumber)
    {
        string connectionString = $"Data Source={_options.PosDBFileName};";
        using var connection = new SqliteConnection(connectionString);
        connection.Open();

        var record = await connection.QueryFirstOrDefaultAsync<Record>(@"select st.Id, st.FRegAdditionalInfo as Value from SalesTransactions st
                                                            where st.FRegAdditionalInfo not null
                                                            order by SalesTransactionId desc
                                                            LIMIT 1;");

        var fRegAdditionalInfo = record.Value.DeserializeOrDefault<FiscalizationResult>();
        if (fRegAdditionalInfo is null)
        {
            throw new Exception("Cannot update invoice number, fiscalization result not found.");
        }

        fRegAdditionalInfo.ReceiptNumber = invoiceNumber;
        var result = await connection.ExecuteAsync(@"UPDATE SalesTransactions
                                                     SET FRegAdditionalInfo = json_set(FRegAdditionalInfo, '$.ReceiptNumber', @ReceiptNumber)
                                                     WHERE Id = @Id;", param: new { ReceiptNumber = invoiceNumber, Id = record.Id });
    }

    public async Task<IEnumerable<ReceiptInfo>> GetSalesTransactionWithoutVatNumberAsync(DateTime dateFrom, DateTime dateTo, List<string> includeOnlySalesTransactions = null)
    {
        string connectionString = $"Data Source={_options.PosDBFileName};";

        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        string sql = @$"select
	                        st.GlobalSalesTransactionId,
	                        json_extract(st.FRegAdditionalInfo, '$.LocationCode') as BusinessPremiseID,
	                        json_extract(st.FRegAdditionalInfo, '$.PosNumber') as DeviceId,
	                        json_extract(st.FRegAdditionalInfo, '$.ReceiptNumber') as ReceiptId,
	                        st.Date, st.CustomerVatIdentificationNumber, st.CustomerTaxIdentificationNumber, st.TotalAmount,
	                        u.TaxIdentificationNumber as OperatorTaxNumber,
	                        va.Id, 	va.Vat_Value as VatValue, va.BaseAmount, va.TaxAmount
                        from SalesTransactions st
                        join VatAmount va on va.SalesTransactionId = st.Id
                        join Users u on u.ExternalId = st.CashierId
                        join Pos p on p.Device_Id = st.DeviceId 
                        where
	                        st.CustomerVatIdentificationNumber is NULL
	                        and st.CustomerTaxIdentificationNumber is not NULL";

        if (includeOnlySalesTransactions is null || !includeOnlySalesTransactions.Any())
        {
            sql += " and st.Date BETWEEN @dateFrom and @dateTo";
        }
        else
        {
            sql += " and st.GlobalSalesTransactionId in @includeOnlySalesTransactions";
        }

        Dictionary<string, ReceiptInfo> receiptDict = new();
        return (await connection.QueryAsync<ReceiptInfo>(sql,
            new Type[]
            {
                typeof(ReceiptInfo),
                typeof(VatAmount)
            },
            (obj) =>
            {
                var receiptInfo = obj[0] as ReceiptInfo;
                var vatAmount = obj[1] as VatAmount;

                if (receiptDict.ContainsKey(receiptInfo!.GlobalSalesTransactionId))
                {
                    receiptDict[receiptInfo.GlobalSalesTransactionId].VatAmounts.Add(vatAmount!);
                    return null;
                }
                else
                {
                    receiptInfo!.VatAmounts = new List<VatAmount> { vatAmount! };
                    receiptDict.Add(receiptInfo.GlobalSalesTransactionId, receiptInfo);
                }
                return receiptDict[receiptInfo.GlobalSalesTransactionId];
            },
            param: new { dateFrom, dateTo, includeOnlySalesTransactions },
            splitOn: "GlobalSalesTransactionId,Id")).Where(x => x != null).ToList();
    }

    private class Record
    {
        public int Id { get; set; }
        public string Value { get; set; }
    }
}
