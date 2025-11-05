using Datapac.Posybe.POS.Model.SalesTransaction;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace eDavkiRepairer;

internal class ReceiptInfo
{
    public string GlobalSalesTransactionId { get; set; }
	public string BusinessPremiseID { get; set; }
	public string DeviceId { get; set; }
	public string ReceiptId { get; set; }
	public DateTime Date { get; set; }
	public string CustomerVatIdentificationNumber { get; set; }
	public string CustomerTaxIdentificationNumber { get; set; }
	public decimal TotalAmount { get; set; }
	public string OperatorTaxNumber { get; set; }
	public List<VatAmount> VatAmounts { get; set; } = new();
}
