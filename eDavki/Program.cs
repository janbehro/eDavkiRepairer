using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

class Program
{
    static async Task Main(string[] args)
    {
        // Root directory containing log files (can include subfolders)
        string rootDir = "C:\\Users\\behro\\Downloads\\N670\\N670";
        string outputRoot = Path.Combine(rootDir, "C:\\Users\\behro\\DownloadsExtractedInvoices");
        Directory.CreateDirectory(outputRoot);

        // Regex to capture JSON inside quotes after "Fiscal receipt request:"
        var jsonRegex = new Regex(@"\{\\""InvoiceRequest\\"".*?\}\}", RegexOptions.Compiled);

        // Enumerate all .log files recursively
        var logFiles = Directory.EnumerateFiles(rootDir, "*.log", SearchOption.AllDirectories);

        foreach (var logPath in logFiles)
        {
            Console.WriteLine($"Processing: {logPath}");
            using var reader = new StreamReader(logPath, Encoding.UTF8);
            string? line;
            int lineNumber = 0;

            while ((line = await reader.ReadLineAsync()) != null)
            {
                lineNumber++;
                if (!line.Contains("InvoiceRequest"))
                    continue;

                var match = jsonRegex.Match(line);
                if (!match.Success)
                    continue;

                try
                {
                    string rawJson = match.Value;

                    // Unescape JSON string
                    string cleanJson = rawJson
                        .Replace("\\\"", "\"")
                        .Replace("\\\\", "\\") + '}';

                    // Parse to extract identifiers
                    using var doc = JsonDocument.Parse(cleanJson);
                    var root = doc.RootElement
                        .GetProperty("InvoiceRequest")
                        .GetProperty("Invoice");

                    string businessPremiseId = root
                        .GetProperty("InvoiceIdentifier")
                        .GetProperty("BusinessPremiseID").GetString()!;
                    string electronicDeviceId = root
                        .GetProperty("InvoiceIdentifier")
                        .GetProperty("ElectronicDeviceID").GetString()!;
                    string invoiceNumber = root
                        .GetProperty("InvoiceIdentifier")
                        .GetProperty("InvoiceNumber").GetString()!;

                    // Create subdirectory for this premise/device
                    string subDir = Path.Combine(outputRoot, $"{businessPremiseId}-{electronicDeviceId}");
                    Directory.CreateDirectory(subDir);

                    string fileName = $"{invoiceNumber}.json";
                    string outputPath = Path.Combine(subDir, fileName);

                    // Pretty print JSON
                    var options = new JsonSerializerOptions { WriteIndented = true };
                    string pretty = JsonSerializer.Serialize(
                        JsonSerializer.Deserialize<object>(cleanJson), options);

                    await File.WriteAllTextAsync(outputPath, pretty, Encoding.UTF8);
                    Console.WriteLine($"  Saved {outputPath}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  Error on line {lineNumber}: {ex.Message}");
                }
            }
        }

        Console.WriteLine("All done.");
    }
}
