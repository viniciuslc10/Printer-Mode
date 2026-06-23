namespace PrinterMode.Core.Models;

public class SerialConfig
{
    public int BaudRate { get; set; } = 9600;
    public int DataBits { get; set; } = 8;
    public string Parity { get; set; } = "None";
    public string StopBits { get; set; } = "One";
    public string FlowControl { get; set; } = "None";

    public string DisplayName => $"{BaudRate},{DataBits},{Parity[0]},{StopBits}";
}
