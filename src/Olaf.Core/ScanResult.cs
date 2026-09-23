namespace Olaf.Core;

public sealed record ScanResult(IReadOnlyList<ResolvedLicense> Licenses)
{
    public static ScanResult Empty { get; } = new ScanResult(Array.Empty<ResolvedLicense>());
}
