using Datapac.Posybe.POS.Model.Fiscal.Results.SLO;

public static partial class Program
{
    private class VatCustomer
    {
        public string VatNumber { get; set; }
        public string AdditionalInfo { get; set; }
        public FiscalizationResult? FiscalizationResult { get; set; }
    }
}