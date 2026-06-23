namespace PrinterMode.Core.Models;

public class InstallResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public List<string> Steps { get; set; } = [];
    public string? ErrorDetails { get; set; }
    public string? PrinterName { get; set; }

    public static InstallResult Ok(string message, string printerName, IEnumerable<string>? steps = null) =>
        new() { Success = true, Message = message, PrinterName = printerName, Steps = steps?.ToList() ?? [] };

    public static InstallResult Fail(string message, string? details = null, IEnumerable<string>? steps = null) =>
        new() { Success = false, Message = message, ErrorDetails = details, Steps = steps?.ToList() ?? [] };
}
