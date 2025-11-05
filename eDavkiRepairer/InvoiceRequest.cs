using Datapac.Posybe.POS.Fiscal.SLO.Api.Model;

public static partial class Program
{
    internal class InvoiceRequest
    {
        public string FileName { get; set; }
        public InvoiceRequestDto InvoiceRequestDto { get; set; }
        public string? OriginalGlobalSalesTransactionId { get; set; }
    }
}