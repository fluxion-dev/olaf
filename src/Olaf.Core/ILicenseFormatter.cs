namespace Olaf.Core;

public interface ILicenseFormatter
{
    string Format { get; }

    string FormatResult(ScanResult result);
}
