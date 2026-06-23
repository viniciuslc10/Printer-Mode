using System.IO;
using PrinterMode.Core.Interfaces;

namespace PrinterMode.UI.Services;

public class LogService : ILogService
{
    private readonly string _logPath;
    private readonly object _lock = new();

    public LogService(string logDirectory)
    {
        Directory.CreateDirectory(logDirectory);
        _logPath = Path.Combine(logDirectory, $"printermode_{DateTime.Now:yyyyMMdd}.log");
    }

    public void Info(string message) => Write("INFO", message);
    public void Warning(string message) => Write("WARN", message);
    public void Error(string message, Exception? ex = null) => Write("ERROR", ex == null ? message : $"{message} | {ex}");
    public void Debug(string message) => Write("DEBUG", message);
    public string GetLogPath() => _logPath;

    private void Write(string level, string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {message}";
        lock (_lock)
        {
            try { File.AppendAllText(_logPath, line + Environment.NewLine); }
            catch { /* non-blocking */ }
        }

        System.Diagnostics.Debug.WriteLine(line);
    }
}
