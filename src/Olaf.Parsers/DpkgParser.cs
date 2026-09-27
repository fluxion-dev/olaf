using Olaf.Core;

namespace Olaf.Parsers;

/// <summary>
/// Debian dpkg status parser (thin, standalone-capable).
/// Reads the <c>status</c> text database (<c>var/lib/dpkg/status</c>):
/// blank-line-separated stanzas with <c>Package:</c> and <c>Version:</c> fields.
/// Versions are kept VERBATIM (whole, including any epoch prefix).
/// <c>Architecture:</c> lines are validated-but-deferred: their presence must
/// not break parsing and their values are not stored; arch + PURL enrichment
/// is deferred to issue #70.
/// OS dependencies resolve to <c>Unknown</c> downstream with reason
/// <c>license-unknown: unsupported ecosystem.</c> (emitted by
/// <c>CachingLicenseResolver</c>; no registry-backed SPDX source for OS DBs,
/// no resolver changes here).
/// Malformed input yields <c>[]</c>, never throws.
/// </summary>
public sealed class DpkgParser : IEcosystemParser
{
    public string Ecosystem => "dpkg";

    public bool CanHandle(string fileName)
    {
        var name = Path.GetFileName(fileName);
        return string.Equals(name, "status", StringComparison.OrdinalIgnoreCase);
    }

    public IReadOnlyList<Dependency> Parse(string inputPath)
    {
        if (Directory.Exists(inputPath))
        {
            var statusPath = Path.Combine(inputPath, "status");
            if (File.Exists(statusPath))
            {
                return ParseStatusFile(statusPath);
            }

            string[] files;
            try
            {
                files = Directory.GetFiles(inputPath);
            }
            catch (IOException)
            {
                // Covers FileNotFound/DirectoryNotFound by inheritance (CS0160) — never catch them separately.
                return Array.Empty<Dependency>();
            }

            var candidate = files.FirstOrDefault(CanHandle);
            if (candidate is not null)
            {
                return ParseStatusFile(candidate);
            }

            return Array.Empty<Dependency>();
        }

        return ParseStatusFile(inputPath);
    }

    internal static IReadOnlyList<Dependency> ParseStatusFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return Array.Empty<Dependency>();
            }

            return ParseStatusText(File.ReadAllText(path));
        }
        catch (IOException)
        {
            // Covers FileNotFound/DirectoryNotFound by inheritance (CS0160) — never catch them separately.
            return Array.Empty<Dependency>();
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<Dependency>();
        }
    }

    internal static IReadOnlyList<Dependency> ParseStatusText(string text)
    {
        var deps = new List<Dependency>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? name = null;
        string? version = null;

        void Flush()
        {
            if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(version) && seen.Add(name))
            {
                deps.Add(new Dependency("dpkg", name, version, IsTransitive: false));
            }

            name = null;
            version = null;
        }

        foreach (var raw in text.Split('\n'))
        {
            // Folded-continuation rule: key detection runs on the raw line with
            // only trailing whitespace trimmed — lines starting with space/tab
            // (e.g. Description: bodies) must NOT false-match Package:/Version:.
            var line = raw.TrimEnd('\r', ' ', '\t');
            if (string.IsNullOrWhiteSpace(line))
            {
                Flush();
                continue;
            }

            if (line.StartsWith("Package:", StringComparison.Ordinal))
            {
                var value = line.Substring("Package:".Length).Trim();
                if (value.Length > 0)
                {
                    name = value;
                }
            }
            else if (line.StartsWith("Version:", StringComparison.Ordinal))
            {
                var value = line.Substring("Version:".Length).Trim();
                if (value.Length > 0)
                {
                    version = value;
                }
            }
        }

        Flush();
        return deps;
    }
}
