using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace eDavkiRepairer.Extensions;

internal static class StringsExtensions
{
    public static int GetTaxNumber(this string number)
    {
        var merchantTaxIdentification = number;
        if (int.TryParse(merchantTaxIdentification, out int taxIdentification))
        {
            return taxIdentification;
        }
        if (merchantTaxIdentification.StartsWith("SI"))
        {
            if (int.TryParse(merchantTaxIdentification.Substring(2), out int taxNumber))
            {
                return taxNumber;
            }
        }
        throw new ArgumentException($"Could not parse merchant tax identification number {merchantTaxIdentification}");
    }
}
