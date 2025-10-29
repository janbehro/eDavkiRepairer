using Datapac.Posybe.POS.Domain;
using Datapac.Posybe.POS.Domain.Extensions;
using Datapac.Posybe.POS.Fiscal.SLO.Api.Model;
using Datapac.Posybe.POS.Model.Fiscal.Results.SLO;
using FluentResults;
using Microsoft.IdentityModel.Tokens;
using Newtonsoft.Json.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Datapac.Posybe.POS.Fiscal.SLO.Extensions;

internal static class HttpResponseMessageExtensions
{
    internal static async Task<Result<FiscalizationResult>> GetFiscalizationResultAsync(this HttpResponseMessage httpResponseMessage)
    {
        if (!httpResponseMessage.IsSuccessStatusCode)
        {
            return await GetErrorResultAsync(httpResponseMessage);
        }
        var content = await httpResponseMessage.Content.ReadAsStringAsync();
        var jwtValidationResult = ParseAndValidateJwt(content);
        if (jwtValidationResult.IsFailed)
        {
            return jwtValidationResult.ToResult();
        }

        var responseJson = jwtValidationResult.Value;
        var receiptResponse = responseJson.DeserializeOrDefault<InvoiceResponseDto>();
        if (receiptResponse is null)
        {
            return Result.Fail(new Error("Could not parse response")
                .SetOrUpdateReasonCode(ReasonCodes.FiscalServices.StatusServices.ResponseParsingError));
        }

        return new FiscalizationResult
        {
            FiscalRegistrationCode = receiptResponse?.InvoiceResponse?.UniqueInvoiceID ?? string.Empty
        };
    }

    public static string GetPayload(this string jwt)
    {
        var jsonObject = JObject.Parse(jwt);
        jwt = jsonObject["token"]?.ToString();

        if (string.IsNullOrEmpty(jwt))
        {
            return null;
        }

        // Split the JWT into its components
        var parts = jwt.Split('.');
        if (parts.Length != 3)
        {
            return null;
        }

        var header = parts[0];
        var payload = parts[1];
        var signature = parts[2];

        // Decode and parse the header
        var headerJson = Encoding.UTF8.GetString(Base64UrlEncoder.DecodeBytes(header));
        var headerObj = JObject.Parse(headerJson);

        // Extract the certificate from the header's x5c field
        var x5c = headerObj["x5c"]?.FirstOrDefault()?.ToString();
        if (string.IsNullOrEmpty(x5c))
        {
            return null;
        }

        // Decode the base64 certificate
        var certificateBytes = Convert.FromBase64String(x5c);
        using var certificate = new X509Certificate2(certificateBytes);
        var signatureBytes = Base64UrlEncoder.DecodeBytes(signature);
        var jwtVerified = certificate.VerifyMessage(Encoding.UTF8.GetBytes($"{header}.{payload}"), signatureBytes);

        if (!jwtVerified)
        {
            return null;
        }

        // Decode the payload
        return Encoding.UTF8.GetString(Base64UrlEncoder.DecodeBytes(payload));
    }

    private static Result<string> ParseAndValidateJwt(string jwt)
    {
        // Parse the JSON string to extract the token
        var jsonObject = JObject.Parse(jwt);
        jwt = jsonObject["token"]?.ToString();

        if (string.IsNullOrEmpty(jwt))
        {
            return Result.Fail(new Error("Token not found in JSON.")
                .SetOrUpdateReasonCode(ReasonCodes.FiscalServices.StatusServices.CertificateNotFound));
        }

        // Split the JWT into its components
        var parts = jwt.Split('.');
        if (parts.Length != 3)
        {
            return Result.Fail(new Error("Invalid JWT format.")
                .SetOrUpdateReasonCode(ReasonCodes.FiscalServices.StatusServices.InvalidJwtFormat));
        }

        var header = parts[0];
        var payload = parts[1];
        var signature = parts[2];

        // Decode and parse the header
        var headerJson = Encoding.UTF8.GetString(Base64UrlEncoder.DecodeBytes(header));
        var headerObj = JObject.Parse(headerJson);

        // Extract the certificate from the header's x5c field
        var x5c = headerObj["x5c"]?.FirstOrDefault()?.ToString();
        if (string.IsNullOrEmpty(x5c))
        {
            return Result.Fail(new Error("Certificate not found in JWT header.")
                .SetOrUpdateReasonCode(ReasonCodes.FiscalServices.StatusServices.CertificateNotFound));
        }

        // Decode the base64 certificate
        var certificateBytes = Convert.FromBase64String(x5c);
        using var certificate = new X509Certificate2(certificateBytes);
        var signatureBytes = Base64UrlEncoder.DecodeBytes(signature);
        var jwtVerified = certificate.VerifyMessage(Encoding.UTF8.GetBytes($"{header}.{payload}"), signatureBytes);

        if (!jwtVerified)
        {
            return Result.Fail(new Error("Ivalid response signature.")
                .SetOrUpdateReasonCode(ReasonCodes.FiscalServices.StatusServices.InvalidResponseSignature));
        }

        // Decode the payload
        var payloadJson = Encoding.UTF8.GetString(Base64UrlEncoder.DecodeBytes(payload));

        // Check for error messages in the payload
        var payloadObj = JObject.Parse(payloadJson);
        foreach (var property in payloadObj.Properties())
        {
            var error = property.Value["Error"];
            if (error != null)
            {
                var errorCode = error["ErrorCode"];
                var statusCode = GetStatusCode(errorCode.ToString());
                return Result.Fail(new Error($"eDavki error response(check request) ErrorCode:{errorCode} ErrorMessage:{error["ErrorMessage"]}")
                    .SetOrUpdateReasonCode(statusCode));
            }
        }

        return payloadJson;
    }

    private static string GetStatusCode(string errorCode)
    {
        return errorCode switch
        {
            "S001" => ReasonCodes.FiscalServices.EDavkiService.MessageNotByTheSchema,
            "S002" => ReasonCodes.FiscalServices.EDavkiService.MessageNotByTheSchema,
            "S003" => ReasonCodes.FiscalServices.EDavkiService.IncorrectDigitalSignature,
            "S004" => ReasonCodes.FiscalServices.EDavkiService.IncorrectDigitalCertificateIdentifier,
            "S005" => ReasonCodes.FiscalServices.EDavkiService.DifferentTaxNumberInCertificate,
            "S006" => ReasonCodes.FiscalServices.EDavkiService.BusinessPremisesNotSubmitted,
            "S007" => ReasonCodes.FiscalServices.EDavkiService.DigitalCertificateWithdrawn,
            "S008" => ReasonCodes.FiscalServices.EDavkiService.CertificateExpired,
            "S100" => ReasonCodes.FiscalServices.EDavkiService.MessageProcessingSystemError,
            _ => ReasonCodes.FiscalServices.EDavkiService.EDavkiResponseError,
        };
    }

    private static async Task<FiscalizationResult> GetErrorResultAsync(HttpResponseMessage httpResponseMessage) => throw new NotImplementedException();
}
