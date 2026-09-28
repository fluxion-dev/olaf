namespace Olaf.Core;

public sealed record ResolvedLicense(
    Dependency Dependency,
    string? SpdxId,
    string? LicenseText,
    string? SourceUrl,
    string Status,
    string? Reason,
    Enrichment? Enrichment = null);
