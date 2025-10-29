using Datapac.Posybe.POS.Domain.Extensions;
using Datapac.Posybe.POS.Fiscal.SLO.Api.Model;
using Datapac.Posybe.POS.Fiscal.SLO.Model;
using System.Numerics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Datapac.Posybe.POS.Fiscal.SLO.Extensions;

internal static class CertificateExtensions
{
    /// <summary>
    /// ZOI (protective mark) service.
    /// </summary>
    internal static string GetProtectiveMark(this X509Certificate2 certificate,
                                             FiscalInformation fiscalInformation,
                                             DateTime invoiceIssueDateTime,
                                             string invoiceNumber,
                                             decimal invoiceAmount)
    {
        string baseString = fiscalInformation.TaxNumber.ToString();
        baseString = baseString + invoiceIssueDateTime.ToString("dd.MM.yyyy HH:mm:ss");
        baseString = baseString + invoiceNumber;
        baseString = baseString + fiscalInformation.BusinessPremiseID;
        baseString = baseString + fiscalInformation.ElectronicDeviceID;
        baseString = baseString + invoiceAmount.ToString();

        byte[] baseStringBytes = Encoding.ASCII.GetBytes(baseString);
        byte[] signature = certificate.SignData(baseStringBytes);
        string protectectiveMark = GetMd5Hash(signature); //ZOI - zaščitna oznaka izdajatelja računa
        return protectectiveMark;
    }

    internal static string SignObject(this X509Certificate2 certificate, object payloadDto)
    {
        JwtHeaderDto jwtHeader = new JwtHeaderDto()
        {
            Alg = "RS256",
            SubjectName = certificate.SubjectName.Name,
            IssuerName = certificate.IssuerName.Name,
            Serial = GetSerialNumber(certificate.SerialNumber)
        };


        // Serialize header and payload to JSON
        string jsonHeader = jwtHeader.Serialize();
        string jsonPayload = payloadDto.Serialize();
        //Logger.Log(this, LogLevel.Info, "eDavki", $"GenerateJwt message payload:{jsonPayload}");

        // Base64Url encode header and payload
        string encodedHeader = Encoding.UTF8.GetBytes(jsonHeader).Base64UrlEncode();
        string encodedPayload = jsonPayload.Base64Encode();

        // Create the string to sign
        string message = $"{encodedHeader}.{encodedPayload}";

        // Sign the message
        string signature = certificate.SignData(message);

        // Combine all parts to get the final token
        string token = $"{message}.{signature}";

        // Create final JSON payload
        var finalPayload = new
        {
            token
        };

        return finalPayload.Serialize();
    }

    public static string Base64Encode(this string plainText)
    {
        var plainTextBytes = Encoding.UTF8.GetBytes(plainText);
        return Convert.ToBase64String(plainTextBytes);
    }

    /// <summary>
    /// Verify signature with public key of cert.
    /// </summary>
    /// <param name="data">Data which was signed.</param>
    /// <param name="signature">Signature.</param>
    /// <param name="certificate">Certificate with public key.</param>
    /// <returns>Validation result in bool.</returns>
    public static bool VerifyMessage(this X509Certificate2 certificate, byte[] data, byte[] signature)
    {
        using RSA rsaPK = certificate.GetRSAPublicKey();
        bool signedData = rsaPK.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return signedData;
    }

    internal static string Base64UrlEncode(this byte[] input)
    {
        var output = Convert.ToBase64String(input);
        output = output.Split('=')[0]; // Remove any trailing '='s
        output = output.Replace('+', '-'); // 62nd char of encoding
        output = output.Replace('/', '_'); // 63rd char of encoding
        return output;
    }

    /// <summary>
    /// Get serial number in right format.
    /// </summary>
    /// <param name="certSerialNumber">Cert serial number.</param>
    /// <returns>Serial number in BigInteger.</returns>
    private static BigInteger GetSerialNumber(string certSerialNumber)
    {
        int length = certSerialNumber.Length;
        byte[] bytes = new byte[length / 2];

        for (int i = 0; i < length; i += 2)
        {
            bytes[i / 2] = Convert.ToByte(certSerialNumber.Substring(i, 2), 16);
        }

        //Ensure that the byte array is in the correct endianness (byte order) for BigInteger.
        //If your serial number is in big-endian format, reverse the byte array using Array.Reverse(serialNumberBytes)
        //before passing it to the BigInteger constructor.
        Array.Reverse(bytes);
        BigInteger serialNumberBigInteger = new BigInteger(bytes);

        return serialNumberBigInteger;
    }

    internal static byte[] SignData(this X509Certificate2 certificate, byte[] input)
    {
        using RSA? privateKey = certificate.GetRSAPrivateKey();
        if (privateKey is null)
        {
            throw new ArgumentException("Client certificate does not contain private key.");
        }
        return privateKey.SignData(input, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    }

    public static string SignData(this X509Certificate2 certificate, string message)
    {
        byte[] data = Encoding.UTF8.GetBytes(message);
        byte[] signedData;

        using RSA rsaPK = certificate.GetRSAPrivateKey();
        signedData = rsaPK.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return Base64UrlEncode(signedData);
    }

    private static string GetMd5Hash(byte[] input)
    {
        using MD5 md5Hash = MD5.Create();
        byte[] data = md5Hash.ComputeHash(input);
        StringBuilder sBuilder = new StringBuilder();
        for (int i = 0; i < data.Length; i++)
        {
            sBuilder.Append(data[i].ToString("x2"));
        }
        return sBuilder.ToString();
    }
}
