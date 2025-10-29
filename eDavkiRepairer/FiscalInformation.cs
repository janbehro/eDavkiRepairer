namespace Datapac.Posybe.POS.Fiscal.SLO.Model;

internal class FiscalInformation
{
    public long TaxNumber { get; set; }
    public long? CashierTaxNumber { get; set; }
    public string InvoiceNumberingStructure { get; set; }
    public string BusinessPremiseID { get; set; }
    public string ElectronicDeviceID { get; set; }
}
