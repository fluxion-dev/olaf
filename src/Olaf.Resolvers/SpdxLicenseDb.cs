using System.Reflection;
using System.Text.Json;

namespace Olaf.Resolvers;

// Issue #77: embedded offline SPDX DB (primary) over SpdxLicenseTexts
// (fallback seed). Shape per entry (metadata-first key order):
// {id, name, osi, fsf, deprecated, text} where text is the full license
// text where hydrated, else "" (checked-in file ships the D3 seed
// variant: metadata for the 35 seed ids, texts resolved at runtime via
// SpdxLicenseTexts until tools/update-spdx-db.sh hydrates full texts).
// Lazy parse-once, zero network (no HTTP client in this file — air-gap safe).
internal sealed record SpdxLicenseEntry(string Id, string Name, bool Osi, bool Fsf, bool Deprecated, string Text);

internal static class SpdxLicenseDb
{
    private static readonly Lazy<IReadOnlyDictionary<string, SpdxLicenseEntry>> Entries =
        new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    public static int Count => Entries.Value.Count;

    public static bool TryGet(string? spdxId, out SpdxLicenseEntry? entry)
    {
        entry = null;
        if (string.IsNullOrWhiteSpace(spdxId))
        {
            return false;
        }

        var candidates = Entries.Value;
        var trimmed = spdxId.Trim();
        if (candidates.TryGetValue(trimmed, out entry))
        {
            return true;
        }

        var normalized = SpdxMapper.Normalize(trimmed);
        if (normalized is not null && candidates.TryGetValue(normalized, out entry))
        {
            return true;
        }

        entry = null;
        return false;
    }

    public static bool TryGetText(string? spdxId, out string? text)
    {
        text = null;
        if (!TryGet(spdxId, out var entry) || entry is null)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(entry.Text))
        {
            return false;
        }

        text = entry.Text;
        return true;
    }

    private static IReadOnlyDictionary<string, SpdxLicenseEntry> Load()
    {
        var empty = new Dictionary<string, SpdxLicenseEntry>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var assembly = typeof(SpdxLicenseDb).Assembly;
            var resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("spdx-licenses.json", StringComparison.Ordinal));
            if (resourceName is null)
            {
                return empty;
            }

            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is null)
            {
                return empty;
            }

            using var doc = JsonDocument.Parse(stream);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return empty;
            }

            var entries = new Dictionary<string, SpdxLicenseEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (!element.TryGetProperty("id", out var idProp) || idProp.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var id = idProp.GetString();
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                entries[id] = new SpdxLicenseEntry(
                    Id: id,
                    Name: GetString(element, "name"),
                    Osi: GetBool(element, "osi"),
                    Fsf: GetBool(element, "fsf"),
                    Deprecated: GetBool(element, "deprecated"),
                    Text: GetString(element, "text"));
            }

            return entries;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Covers FileNotFound/DirectoryNotFound by inheritance (CS0160) — never catch them separately.
            return empty;
        }
    }

    private static string GetString(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString() ?? string.Empty
            : string.Empty;
    }

    private static bool GetBool(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var prop)
            && prop.ValueKind is JsonValueKind.True or JsonValueKind.False
            && prop.GetBoolean();
    }
}
