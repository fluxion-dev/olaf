namespace Olaf.Core;

public sealed record ScanResult(IReadOnlyList<ResolvedLicense> Licenses)
{
    public static ScanResult Empty { get; } = new ScanResult(Array.Empty<ResolvedLicense>());

    public int TotalCount => Licenses.Count;

    public int ResolvedCount => Licenses.Count(l =>
        string.Equals(l.Status, "Resolved", StringComparison.OrdinalIgnoreCase));

    public int UnknownCount => Licenses.Count(l =>
        string.Equals(l.Status, "Unknown", StringComparison.OrdinalIgnoreCase));
}
