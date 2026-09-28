using System.Reflection;
using Olaf.Resolvers;

namespace Olaf.Tests.Resolvers;

// Issue #77: embedded offline SPDX DB (primary) over SpdxLicenseTexts
// (fallback seed). Checked-in file ships the D3 seed variant: metadata for
// the 35 seed ids pinned to SPDX License List 3.29 (license-list-data tag
// v3.29.0, per the SpdxLicenseTexts header + tools/update-spdx-db.sh
// SPDX_VERSION), texts hydrated later by the maintainer script. All lookups
// are in-memory — zero HTTP by construction (no live network in tests).
public class SpdxDbTests
{
    // DB row 1/5 — id presence: every sampled seed id resolves metadata.
    // (tail: 8 ids in one Fact; full 35-id set pinned in Db_PinnedToV329SeedSet.)
    [Fact]
    public void Db_ContainsSeedIds()
    {
        var ids = new[] { "MIT", "Apache-2.0", "ISC", "GPL-3.0-only", "LGPL-2.1-or-later", "AGPL-3.0-only", "0BSD", "AAL" };

        foreach (var id in ids)
        {
            Assert.True(SpdxLicenseDb.TryGet(id, out var entry), $"seed id missing: {id}");
            Assert.NotNull(entry);
            Assert.Equal(id, entry.Id);
        }
    }

    // DB row 2/5 — v3.29.0 pin: exact 35-id seed set + count. Any added or
    // dropped id (hydration, list update) must update this pin explicitly.
    [Fact]
    public void Db_PinnedToV329SeedSet()
    {
        var expected = new[]
        {
            "0BSD", "AAL", "AGPL-1.0-only", "AGPL-3.0-only", "AGPL-3.0-or-later",
            "Apache-1.1", "Apache-2.0", "Artistic-2.0", "BSD-2-Clause", "BSD-3-Clause",
            "BSD-4-Clause", "BSL-1.0", "CC0-1.0", "CDDL-1.0", "EPL-1.0", "EPL-2.0",
            "GPL-1.0-only", "GPL-2.0-only", "GPL-2.0-or-later", "GPL-3.0-only", "GPL-3.0-or-later",
            "ISC", "LGPL-2.0-only", "LGPL-2.1-only", "LGPL-2.1-or-later", "LGPL-3.0-only",
            "LGPL-3.0-or-later", "MIT", "MIT-0", "MPL-1.0", "MPL-1.1", "MPL-2.0",
            "OFL-1.1", "Unlicense", "Zlib",
        };

        Assert.Equal(expected.Length, SpdxLicenseDb.Count);
        foreach (var id in expected)
        {
            Assert.True(SpdxLicenseDb.TryGet(id, out _), $"seed id missing: {id}");
        }
    }

    // DB row 3/5 — lazy/cached: singleton loads via Lazy<> (parse-once) and
    // lookups are case-insensitive + deterministic across repeated calls.
    [Fact]
    public void Db_LookupIsCaseInsensitiveAndCached()
    {
        var field = typeof(SpdxLicenseDb).GetField("Entries", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        Assert.True(
            field.FieldType.IsGenericType && field.FieldType.GetGenericTypeDefinition() == typeof(Lazy<>),
            "SpdxLicenseDb must load the DB through Lazy<> (parse-once singleton).");

        Assert.True(SpdxLicenseDb.TryGet("mit", out var lower));
        Assert.True(SpdxLicenseDb.TryGet("MIT", out var upper));
        Assert.True(SpdxLicenseDb.TryGet("  Apache-2.0  ", out var padded));
        Assert.NotNull(lower);
        Assert.NotNull(upper);
        Assert.NotNull(padded);
        Assert.Equal(upper.Id, lower.Id);
        Assert.Equal("Apache-2.0", padded.Id);
        Assert.Equal(SpdxLicenseDb.Count, SpdxLicenseDb.Count);
    }

    // DB row 4/5 — zero-HTTP proof by reflection: no HttpClient in fields or
    // method signatures (load path is pure embedded-resource JSON parse).
    [Fact]
    public void Db_UsesZeroHttpClient()
    {
        var type = typeof(SpdxLicenseDb);
        var fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
        Assert.DoesNotContain(fields, f => f.FieldType == typeof(HttpClient));

        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
        {
            Assert.DoesNotContain(method.GetParameters(), p => p.ParameterType == typeof(HttpClient));
            Assert.NotEqual(typeof(HttpClient), method.ReturnType);
        }
    }

    // DB row 5/5 — D3 size gate: checked-in DB must stay under 5MB
    // (5_242_880 bytes). Current seed variant is ~6KB; a tripped gate ships
    // the seed variant + files a follow-up issue instead of growing the binary.
    [Fact]
    public void Db_SizeUnder5MB()
    {
        var assembly = typeof(SpdxLicenseDb).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("spdx-licenses.json", StringComparison.Ordinal));
        Assert.NotNull(resourceName);

        using var stream = assembly.GetManifestResourceStream(resourceName);
        Assert.NotNull(stream);
        Assert.True(stream.Length > 0, "embedded spdx-licenses.json must be non-empty.");
        Assert.True(stream.Length < 5_242_880, $"DB size {stream.Length} bytes exceeds the 5MB gate.");
    }
}
