namespace PrinterMode.Core.Interfaces;

public interface ILogService
{
    void Info(string message);
    void Warning(string message);
    void Error(string message, Exception? ex = null);
    void Debug(string message);
    string GetLogPath();
}
