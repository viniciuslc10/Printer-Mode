namespace PrinterMode.Core.Models;

public class PaperConfig
{
    public string Name { get; set; } = string.Empty;
    public double WidthMm { get; set; }
    public double PrintableWidthMm { get; set; }
    public double? HeightMm { get; set; }
    public double MarginTopMm { get; set; } = 0;
    public double MarginBottomMm { get; set; } = 0;
    public double MarginLeftMm { get; set; } = 0;
    public double MarginRightMm { get; set; } = 0;
    public bool IsAutoLength { get; set; } = true;

    public string DisplayName => HeightMm.HasValue
        ? $"{Name} ({WidthMm}mm x {HeightMm}mm)"
        : $"{Name} ({WidthMm}mm x Auto)";
}
