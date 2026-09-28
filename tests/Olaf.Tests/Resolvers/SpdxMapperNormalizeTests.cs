using Olaf.Resolvers;

namespace Olaf.Tests.Resolvers;

// Issue #77 (D4) normalize gaps: case-insensitive -or-later/-only suffixes,
// LicenseRef-* passthrough (valid as-is, bare/malformed null), bare
// gpl|lgpl|agpl stays null. The 120/121-char boundary and OR/AND/WITH
// passthrough pins from SpdxMapperTests hold (re-asserted, not replaced).
public class SpdxMapperNormalizeTests
{
    // Normalize row 1/4 — -or-later/-only match case-insensitively.
    // (tail: 5 arms in one Fact.)
    [Fact]
    public void Normalize_OrLaterSuffix_IsCaseInsensitive()
    {
        Assert.Equal("GPL-2.0-or-later", SpdxMapper.Normalize("GPL-2.0-OR-LATER"));
        Assert.Equal("GPL-3.0-or-later", SpdxMapper.Normalize("gpl-3.0-or-later"));
        Assert.Equal("LGPL-2.1-or-later", SpdxMapper.Normalize("LGPL-2.1-Or-Later"));
        Assert.Equal("AGPL-3.0-or-later", SpdxMapper.Normalize("agpl-3.0-or-LATER"));
        Assert.Equal("GPL-2.0-only", SpdxMapper.Normalize("GPL-2.0-ONLY"));
    }

    // Normalize row 2/4 — valid LicenseRef-* ids pass through untouched.
    // (tail: 3 arms in one Fact.)
    [Fact]
    public void Normalize_ValidLicenseRef_PassesThrough()
    {
        Assert.Equal("LicenseRef-MyCode-1.0", SpdxMapper.Normalize("LicenseRef-MyCode-1.0"));
        Assert.Equal("LicenseRef-foo.bar+baz-2", SpdxMapper.Normalize("LicenseRef-foo.bar+baz-2"));
        Assert.Equal("LicenseRef-A", SpdxMapper.Normalize("LicenseRef-A"));
    }

    // Normalize row 3/4 — bare "LicenseRef-" and malformed refs return null
    // (never an invented mapping, never an exception).
    [Fact]
    public void Normalize_BareOrMalformedLicenseRef_ReturnsNull()
    {
        Assert.Null(SpdxMapper.Normalize("LicenseRef-"));
        Assert.Null(SpdxMapper.Normalize("LicenseRef- foo"));
        Assert.Null(SpdxMapper.Normalize("LicenseRef-/bad"));
        Assert.Null(SpdxMapper.Normalize("LicenseRef-a b"));
    }

    // Normalize row 4/4 — old pins hold: bare family names stay null, the
    // -only pins still map, and the pre-existing 120/121-char + compound
    // guards are unchanged.
    [Fact]
    public void Normalize_OldPinsHold()
    {
        Assert.Null(SpdxMapper.Normalize("GPL"));
        Assert.Null(SpdxMapper.Normalize("gpl"));
        Assert.Null(SpdxMapper.Normalize("LGPL"));
        Assert.Null(SpdxMapper.Normalize("LGPL-2"));
        Assert.Null(SpdxMapper.Normalize("agpl3"));
        Assert.Equal("GPL-2.0-only", SpdxMapper.Normalize("gplv2"));
        Assert.Equal("LGPL-3.0-only", SpdxMapper.Normalize("lgpl-3.0"));

        var at120 = new string('x', 120);
        Assert.Equal(at120, SpdxMapper.Normalize(at120));
        Assert.Null(SpdxMapper.Normalize(new string('x', 121)));

        Assert.Equal("MIT OR Apache-2.0", SpdxMapper.Normalize("MIT OR Apache-2.0"));
        Assert.Equal("MIT AND Apache-2.0", SpdxMapper.Normalize("MIT AND Apache-2.0"));
        Assert.Equal(
            "GPL-2.0-only WITH Classpath-exception-2.0",
            SpdxMapper.Normalize("GPL-2.0-only WITH Classpath-exception-2.0"));
    }
}
