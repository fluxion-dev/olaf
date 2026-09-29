using System.Text.Json;

namespace Olaf.Resolvers;

// Issue #166: self-contained npm-range evaluator (NO new NuGet dependency, zero HttpClient).
// Faithful port of the node-semver comparator subset olaf needs: exact/=, ^ (incl. ^0.x
// special-casing), ~, x/* partials, hyphen ranges (+ partials), compounds (>=a <b),
// || unions, v-prefix + whitespace trimming, and node prerelease-exclusion.
// Deliberate deviations from node-semver (documented, out of scope): no build-metadata
// precedence (ignored for ordering), no leading-zero rejection on numerics, hyphen
// requires whitespace around '-' (else it is a prerelease dash), '*' inside >=/<=
// pairs is not supported (unparseable -> latest fallback in the resolver).
internal static class NpmSemverRange
{
    // Exact-shape gate (issue #166 fidelity/validation contract): optional v/= prefix +
    // surrounding whitespace tolerated, core MUST be X.Y.Z numerics with an optional
    // -prerelease tail. Returns the normalized form (no v-prefix, trimmed, build
    // metadata stripped) that is the ONLY string ever interpolated into a registry URL.
    public static bool IsExactVersion(string? value, out string? exact)
    {
        exact = null;
        if (!TryParseVersion(value, out var parsed) || parsed is null)
        {
            return false;
        }

        exact = parsed.Normalized;
        return true;
    }

    // npm:alias split (issue #166): "npm:<target>@<range>" resolves against TARGET name
    // + range while the resolver attributes everything to the declared alias dependency.
    // Scoped targets ("npm:@scope/pkg@^1.0.0") split at the LAST '@'. A bare "npm:pkg"
    // (no '@') means range "*" (pacote-equivalent default).
    public static bool TrySplitAlias(string? version, out string targetName, out string range)
    {
        targetName = string.Empty;
        range = string.Empty;
        if (string.IsNullOrWhiteSpace(version))
        {
            return false;
        }

        var spec = version.Trim();
        if (!spec.StartsWith("npm:", StringComparison.Ordinal))
        {
            return false;
        }

        var remainder = spec.Substring("npm:".Length);
        var at = remainder.LastIndexOf('@');
        if (at < 0)
        {
            if (remainder.Length == 0)
            {
                return false;
            }

            targetName = remainder;
            range = "*";
            return true;
        }

        targetName = remainder.Substring(0, at).Trim();
        range = remainder.Substring(at + 1).Trim();
        if (targetName.Length == 0 || range.Length == 0)
        {
            return false;
        }

        return true;
    }

    // True when the range string parses (incl. "" -> "*" and "*"); false means the
    // resolver must take the dist-tags.latest fallback, NOT the unsatisfiable path.
    // Dist-tag names ("latest", "next", ...) are NOT semver ranges -> false here; the
    // resolver consults the packument dist-tags map before calling this.
    public static bool IsSupportedRange(string? range)
    {
        if (string.IsNullOrWhiteSpace(range))
        {
            return true;
        }

        return TryParseRange(range.Trim(), out _);
    }

    // Max-satisfying version for the range, or false when unparseable/unsatisfiable.
    // Prerelease exclusion per node rule: a range WITHOUT a prerelease comparator never
    // matches a prerelease version; a range WITH a prerelease comparator matches only
    // prereleases on the same [major,minor,patch] tuple as that comparator.
    public static bool TryResolve(IReadOnlyList<string>? versions, string? range, out string? resolved)
    {
        resolved = null;
        var spec = string.IsNullOrWhiteSpace(range) ? "*" : range.Trim();
        if (!TryParseRange(spec, out var arms) || arms is null)
        {
            return false;
        }

        SemVersion? best = null;
        string? bestRaw = null;
        if (versions is null)
        {
            return false;
        }

        foreach (var raw in versions)
        {
            if (!TryParseVersion(raw, out var candidate) || candidate is null)
            {
                continue;
            }

            foreach (var arm in arms)
            {
                if (Satisfies(candidate, arm))
                {
                    if (best is null || Compare(candidate, best) > 0)
                    {
                        best = candidate;
                        bestRaw = raw.Trim();
                    }

                    break;
                }
            }
        }

        if (best is null)
        {
            return false;
        }

        resolved = bestRaw;
        return true;
    }

    // Packument projection the resolver needs: "versions" keys + "dist-tags" map.
    // Returns false ONLY on malformed JSON (resolver maps that to parse-error);
    // missing members yield empty collections (unsatisfiable -> not-found, or latest
    // fallback), never false.
    internal static bool TryParsePackument(string body, out List<string> versions, out Dictionary<string, string> distTags)
    {
        versions = new List<string>();
        distTags = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (root.TryGetProperty("versions", out var vers) && vers.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in vers.EnumerateObject())
                {
                    versions.Add(prop.Name);
                }
            }

            if (root.TryGetProperty("dist-tags", out var tags) && tags.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in tags.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.String)
                    {
                        var tagValue = prop.Value.GetString();
                        if (!string.IsNullOrWhiteSpace(tagValue))
                        {
                            distTags[prop.Name] = tagValue.Trim();
                        }
                    }
                }
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private sealed record SemVersion(
        int Major,
        int Minor,
        int Patch,
        string[] Prerelease,
        string Normalized)
    {
        public bool HasPrerelease => Prerelease.Length > 0;
    }

    private sealed record Comparator(string Op, SemVersion Version);

    private sealed record RangeArm(IReadOnlyList<Comparator> Comparators)
    {
        public bool HasPrereleaseComparator(out List<Comparator> prereleaseComparators)
        {
            prereleaseComparators = new List<Comparator>();
            foreach (var c in Comparators)
            {
                if (c.Version.HasPrerelease)
                {
                    prereleaseComparators.Add(c);
                }
            }

            return prereleaseComparators.Count > 0;
        }
    }

    private static bool TryParseVersion(string? s, out SemVersion? parsed)
    {
        parsed = null;
        if (string.IsNullOrWhiteSpace(s))
        {
            return false;
        }

        var text = s.Trim();
        if (text.StartsWith("v", StringComparison.Ordinal) || text.StartsWith("=", StringComparison.Ordinal))
        {
            text = text.Substring(1).Trim();
        }

        var plus = text.IndexOf('+');
        if (plus >= 0)
        {
            text = text.Substring(0, plus);
        }

        string prerelease = string.Empty;
        var dash = text.IndexOf('-');
        var core = text;
        if (dash >= 0)
        {
            core = text.Substring(0, dash);
            prerelease = text.Substring(dash + 1);
            if (prerelease.Length == 0)
            {
                return false;
            }
        }

        var parts = core.Split('.');
        if (parts.Length != 3)
        {
            return false;
        }

        var numbers = new int[3];
        for (var i = 0; i < 3; i++)
        {
            if (parts[i].Length == 0 || !IsDigits(parts[i]) || !int.TryParse(parts[i], out numbers[i]))
            {
                return false;
            }
        }

        string[] pre;
        if (prerelease.Length == 0)
        {
            pre = Array.Empty<string>();
        }
        else
        {
            pre = prerelease.Split('.');
            foreach (var id in pre)
            {
                if (id.Length == 0 || !IsPrereleaseIdent(id))
                {
                    return false;
                }
            }
        }

        var normalized = string.Join('.', numbers[0].ToString(), numbers[1].ToString(), numbers[2].ToString());
        if (pre.Length > 0)
        {
            normalized += "-" + string.Join('.', pre);
        }

        parsed = new SemVersion(numbers[0], numbers[1], numbers[2], pre, normalized);
        return true;
    }

    private static bool IsDigits(string s)
    {
        foreach (var ch in s)
        {
            if (ch < '0' || ch > '9')
            {
                return false;
            }
        }

        return s.Length > 0;
    }

    private static bool IsPrereleaseIdent(string s)
    {
        foreach (var ch in s)
        {
            var ok = (ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || ch == '-';
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }

    private static int Compare(SemVersion a, SemVersion b)
    {
        if (a.Major != b.Major)
        {
            return a.Major.CompareTo(b.Major);
        }

        if (a.Minor != b.Minor)
        {
            return a.Minor.CompareTo(b.Minor);
        }

        if (a.Patch != b.Patch)
        {
            return a.Patch.CompareTo(b.Patch);
        }

        if (!a.HasPrerelease && !b.HasPrerelease)
        {
            return 0;
        }

        if (!a.HasPrerelease)
        {
            return 1;
        }

        if (!b.HasPrerelease)
        {
            return -1;
        }

        var len = Math.Min(a.Prerelease.Length, b.Prerelease.Length);
        for (var i = 0; i < len; i++)
        {
            var x = a.Prerelease[i];
            var y = b.Prerelease[i];
            if (string.Equals(x, y, StringComparison.Ordinal))
            {
                continue;
            }

            var xNum = IsDigits(x);
            var yNum = IsDigits(y);
            if (xNum && yNum)
            {
                var cmp = int.Parse(x, System.Globalization.CultureInfo.InvariantCulture)
                    .CompareTo(int.Parse(y, System.Globalization.CultureInfo.InvariantCulture));
                if (cmp != 0)
                {
                    return cmp;
                }

                continue;
            }

            if (xNum)
            {
                return -1;
            }

            if (yNum)
            {
                return 1;
            }

            return string.Compare(x, y, StringComparison.Ordinal);
        }

        return a.Prerelease.Length.CompareTo(b.Prerelease.Length);
    }

    private static bool TryParseRange(string spec, out List<RangeArm>? arms)
    {
        arms = null;
        var parsed = new List<RangeArm>();
        foreach (var union in spec.Split(new[] { "||" }, StringSplitOptions.None))
        {
            if (!TryParseArm(union.Trim(), out var arm) || arm is null)
            {
                return false;
            }

            parsed.Add(arm);
        }

        arms = parsed;
        return true;
    }

    private static bool TryParseArm(string arm, out RangeArm? result)
    {
        result = null;
        if (arm.Length == 0 || string.Equals(arm, "*", StringComparison.Ordinal))
        {
            result = new RangeArm(Array.Empty<Comparator>());
            return true;
        }

        // Hyphen range requires whitespace around '-': "1.2.3 - 2.3.4" (+ partials).
        var hyphen = arm.IndexOf(" - ", StringComparison.Ordinal);
        if (hyphen >= 0)
        {
            var from = arm.Substring(0, hyphen).Trim();
            var to = arm.Substring(hyphen + 3).Trim();
            if (from.Length == 0 || to.Length == 0)
            {
                return false;
            }

            var comparators = new List<Comparator>();
            if (!AddHyphenLower(from, comparators) || !AddHyphenUpper(to, comparators))
            {
                return false;
            }

            result = new RangeArm(comparators);
            return true;
        }

        var comparatorsAnd = new List<Comparator>();
        foreach (var token in arm.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!AddToken(token.Trim(), comparatorsAnd))
            {
                return false;
            }
        }

        result = new RangeArm(comparatorsAnd);
        return true;
    }

    private static bool AddHyphenLower(string spec, List<Comparator> out_)
    {
        // Partial lower: missing pieces pin to zero ("1.2 - ..." := >=1.2.0).
        if (!TryParsePartial(spec, out var major, out var minor, out var patch, out var pre) || pre.Length > 0)
        {
            return false;
        }

        out_.Add(new Comparator(">=", new SemVersion(
            major ?? 0, minor ?? 0, patch ?? 0, Array.Empty<string>(),
            $"{major ?? 0}.{minor ?? 0}.{patch ?? 0}")));
        return true;
    }

    private static bool AddHyphenUpper(string spec, List<Comparator> out_)
    {
        // Partial upper is exclusive at the partial level ("- 2.3" := <2.4.0).
        if (!TryParsePartial(spec, out var major, out var minor, out var patch, out var pre))
        {
            return false;
        }

        if (major is null)
        {
            return true;
        }

        if (pre.Length > 0)
        {
            if (minor is null || patch is null)
            {
                return false;
            }

            out_.Add(new Comparator("<=", new SemVersion(
                major.Value, minor.Value, patch.Value, pre,
                $"{major.Value}.{minor.Value}.{patch.Value}-{string.Join('.', pre)}")));
            return true;
        }

        if (minor is null)
        {
            out_.Add(new Comparator("<", Exact(major.Value + 1, 0, 0)));
            return true;
        }

        if (patch is null)
        {
            out_.Add(new Comparator("<", Exact(major.Value, minor.Value + 1, 0)));
            return true;
        }

        out_.Add(new Comparator("<=", new SemVersion(
            major.Value, minor.Value, patch.Value, Array.Empty<string>(),
            $"{major.Value}.{minor.Value}.{patch.Value}")));
        return true;
    }

    private static SemVersion Exact(int major, int minor, int patch) =>
        new(major, minor, patch, Array.Empty<string>(), $"{major}.{minor}.{patch}");

    private static bool AddToken(string token, List<Comparator> out_)
    {
        if (token.Length == 0)
        {
            return true;
        }

        if (string.Equals(token, "*", StringComparison.Ordinal))
        {
            return true;
        }

        if (token.StartsWith("v", StringComparison.Ordinal))
        {
            token = token.Substring(1);
        }

        if (token.StartsWith("^", StringComparison.Ordinal))
        {
            return AddCaret(token.Substring(1), out_);
        }

        if (token.StartsWith("~", StringComparison.Ordinal))
        {
            return AddTilde(token.Substring(1), out_);
        }

        string op = "=";
        var rest = token;
        if (token.StartsWith(">=", StringComparison.Ordinal) || token.StartsWith("<=", StringComparison.Ordinal))
        {
            op = token.Substring(0, 2);
            rest = token.Substring(2);
        }
        else if (token.StartsWith(">", StringComparison.Ordinal)
            || token.StartsWith("<", StringComparison.Ordinal)
            || token.StartsWith("=", StringComparison.Ordinal))
        {
            op = token.Substring(0, 1);
            rest = token.Substring(1);
        }

        if (rest.StartsWith("v", StringComparison.Ordinal))
        {
            rest = rest.Substring(1);
        }

        if (!TryParsePartial(rest, out var major, out var minor, out var patch, out var pre))
        {
            return false;
        }

        if (major is null)
        {
            // Bare "*" consumed above; "*"-with-op is unsupported -> unparseable.
            return false;
        }

        if (pre.Length > 0 && (minor is null || patch is null))
        {
            return false;
        }

        if (minor is null || patch is null)
        {
            return AddPartialBounds(op, major.Value, minor, patch, pre, out_);
        }

        var version = new SemVersion(
            major.Value, minor.Value, patch.Value, pre,
            pre.Length > 0
                ? $"{major.Value}.{minor.Value}.{patch.Value}-{string.Join('.', pre)}"
                : $"{major.Value}.{minor.Value}.{patch.Value}");
        out_.Add(new Comparator(op, version));
        return true;
    }

    // Partial inequality desugar (node xRange rules): ">1.2" := >=1.3.0,
    // "<=1.2" := <1.3.0, "<1.2" := <1.2.0, ">=1.2" := >=1.2.0, "=1.2"/bare
    // "1.2" := >=1.2.0 <1.3.0, "=1"/"1" := >=1.0.0 <2.0.0.
    private static bool AddPartialBounds(string op, int major, int? minor, int? patch, string[] pre, List<Comparator> out_)
    {
        var startMinor = minor ?? 0;
        if (op == "=")
        {
            out_.Add(new Comparator(">=", Exact(major, startMinor, 0)));
            out_.Add(minor is null
                ? new Comparator("<", Exact(major + 1, 0, 0))
                : new Comparator("<", Exact(major, startMinor + 1, 0)));
            return true;
        }

        if (op == ">=")
        {
            out_.Add(new Comparator(">=", Exact(major, startMinor, 0)));
            return true;
        }

        if (op == ">")
        {
            out_.Add(minor is null
                ? new Comparator(">=", Exact(major + 1, 0, 0))
                : new Comparator(">=", Exact(major, startMinor + 1, 0)));
            return true;
        }

        if (op == "<=")
        {
            out_.Add(minor is null
                ? new Comparator("<", Exact(major + 1, 0, 0))
                : new Comparator("<", Exact(major, startMinor + 1, 0)));
            return true;
        }

        if (op == "<")
        {
            out_.Add(new Comparator("<", Exact(major, startMinor, 0)));
            return true;
        }

        return false;
    }

    private static bool AddCaret(string spec, List<Comparator> out_)
    {
        // ^0.2.3 := >=0.2.3 <0.3.0; ^0.0.3 := >=0.0.3 <0.0.4; ^1.2.3 := >=1.2.3 <2.0.0.
        if (!TryParsePartial(spec, out var major, out var minor, out var patch, out var pre))
        {
            return false;
        }

        if (major is null)
        {
            return true;
        }

        if (pre.Length > 0 && (minor is null || patch is null))
        {
            return false;
        }

        var lower = pre.Length > 0
            ? new SemVersion(major.Value, minor!.Value, patch!.Value, pre,
                $"{major.Value}.{minor.Value}.{patch.Value}-{string.Join('.', pre)}")
            : Exact(major.Value, minor ?? 0, patch ?? 0);
        out_.Add(new Comparator(">=", lower));

        if (major.Value > 0)
        {
            out_.Add(new Comparator("<", Exact(major.Value + 1, 0, 0)));
            return true;
        }

        if (minor is null)
        {
            out_.Add(new Comparator("<", Exact(1, 0, 0)));
            return true;
        }

        if (minor.Value > 0)
        {
            out_.Add(new Comparator("<", Exact(0, minor.Value + 1, 0)));
            return true;
        }

        if (patch is null)
        {
            out_.Add(new Comparator("<", Exact(0, 1, 0)));
            return true;
        }

        out_.Add(new Comparator("<", Exact(0, 0, patch.Value + 1)));
        return true;
    }

    private static bool AddTilde(string spec, List<Comparator> out_)
    {
        // ~1.2.3 := >=1.2.3 <1.3.0; ~1.2 := >=1.2.0 <1.3.0; ~1 := >=1.0.0 <2.0.0.
        if (!TryParsePartial(spec, out var major, out var minor, out var patch, out var pre))
        {
            return false;
        }

        if (major is null)
        {
            return true;
        }

        if (pre.Length > 0 && (minor is null || patch is null))
        {
            return false;
        }

        var lower = pre.Length > 0
            ? new SemVersion(major.Value, minor!.Value, patch!.Value, pre,
                $"{major.Value}.{minor.Value}.{patch.Value}-{string.Join('.', pre)}")
            : Exact(major.Value, minor ?? 0, patch ?? 0);
        out_.Add(new Comparator(">=", lower));

        if (minor is null)
        {
            out_.Add(new Comparator("<", Exact(major.Value + 1, 0, 0)));
            return true;
        }

        out_.Add(new Comparator("<", Exact(major.Value, minor.Value + 1, 0)));
        return true;
    }

    // Partial core: "1" / "1.x" / "1.*" / "*" (+ 2-part forms); missing/wildcard
    // pieces are null. Prerelease tail allowed only when all three parts are numeric.
    private static bool TryParsePartial(string spec, out int? major, out int? minor, out int? patch, out string[] pre)
    {
        major = null;
        minor = null;
        patch = null;
        pre = Array.Empty<string>();
        if (string.IsNullOrWhiteSpace(spec))
        {
            return false;
        }

        var text = spec.Trim();
        var dash = text.IndexOf('-');
        var core = text;
        if (dash >= 0)
        {
            core = text.Substring(0, dash);
            var preText = text.Substring(dash + 1);
            if (preText.Length == 0)
            {
                return false;
            }

            pre = preText.Split('.');
            foreach (var id in pre)
            {
                if (id.Length == 0 || !IsPrereleaseIdent(id))
                {
                    return false;
                }
            }
        }

        var parts = core.Split('.');
        if (parts.Length > 3)
        {
            return false;
        }

        var numbers = new int?[3];
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i].Trim();
            if (part.Length == 0
                || string.Equals(part, "x", StringComparison.OrdinalIgnoreCase)
                || string.Equals(part, "*", StringComparison.Ordinal))
            {
                numbers[i] = null;
                continue;
            }

            if (!IsDigits(part) || !int.TryParse(part, out var n))
            {
                return false;
            }

            numbers[i] = n;
        }

        // A wildcard above a numeric is unparseable ("1.*.3").
        for (var i = 0; i < 2; i++)
        {
            if (numbers[i] is null && numbers[i + 1] is not null)
            {
                return false;
            }
        }

        major = numbers[0];
        minor = parts.Length > 1 ? numbers[1] : null;
        patch = parts.Length > 2 ? numbers[2] : null;
        return true;
    }

    private static bool Satisfies(SemVersion candidate, RangeArm arm)
    {
        foreach (var c in arm.Comparators)
        {
            var cmp = Compare(candidate, c.Version);
            var ok = c.Op switch
            {
                "=" => cmp == 0,
                ">=" => cmp >= 0,
                ">" => cmp > 0,
                "<=" => cmp <= 0,
                "<" => cmp < 0,
                _ => false,
            };

            if (!ok)
            {
                return false;
            }
        }

        if (candidate.HasPrerelease)
        {
            // Node rule: a prerelease candidate satisfies only when some comparator
            // in the SAME arm carries a prerelease on the same [major,minor,patch].
            if (!arm.HasPrereleaseComparator(out var pres))
            {
                return false;
            }

            foreach (var c in pres)
            {
                if (c.Version.Major == candidate.Major
                    && c.Version.Minor == candidate.Minor
                    && c.Version.Patch == candidate.Patch)
                {
                    return true;
                }
            }

            return false;
        }

        return true;
    }
}
