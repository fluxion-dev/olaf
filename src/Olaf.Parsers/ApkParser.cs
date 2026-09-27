using Olaf.Core;

namespace Olaf.Parsers;

/// <summary>
/// Alpine apk database parser (thin, standalone-capable).
/// Reads the <c>installed</c> text database (<c>lib/apk/db/installed</c>):
/// blank-line-separated stanzas with <c>P:</c> (name) and <c>V:</c> (version) fields.
/// Versions are kept VERBATIM (whole, including the <c>-r0</c> suffix).
/// <c>L:</c> (declared license) and <c>A:</c> (arch) lines are validated-but-deferred:
/// their presence must not break parsing and their values are not stored;
/// declared-license + arch + PURL enrichment is deferred to issue #70.
/// OS dependencies resolve to <c>Unknown</c> downstream with reason
/// <c>license-unknown: unsupported ecosystem.</c> (emitted by
/// <c>CachingLicenseResolver</c>; no registry-backed SPDX source for OS DBs,
/// no resolver changes here).
/// Malformed input yields <c>[]</c>, never throws.
/// </summary>
public sealed class ApkParser : IEcosystemParser
{
    public string Ecosystem => "apk";

    public bool CanHandle(string fileName)
    {
        var name = Path.GetFileName(fileName);
        return string.Equals(name, "installed", StringComparison.OrdinalIgnoreCase);
    }

    public IReadOnlyList<Dependency> Parse(string inputPath)
    {
        if (Directory.Exists(inputPath))
        {
            var installedPath = Path.Combine(inputPath, "installed");
            if (File.Exists(installedPath))
            {
                return ParseInstalledFile(installedPath);
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
                return ParseInstalledFile(candidate);
            }

            return Array.Empty<Dependency>();
        }

        return ParseInstalledFile(inputPath);
    }

    internal static IReadOnlyList<Dependency> ParseInstalledFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return Array.Empty<Dependency>();
            }

            return ParseInstalledText(File.ReadAllText(path));
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

    internal static IReadOnlyList<Dependency> ParseInstalledText(string text)
    {
        var deps = new List<Dependency>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? name = null;
        string? version = null;

        void Flush()
        {
            if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(version) && seen.Add(name))
            {
                deps.Add(new Dependency("apk", name, version, IsTransitive: false));
            }

            name = null;
            version = null;
        }

        foreach (var raw in text.Split('\n'))
        {
            // Folded-continuation rule: key detection runs on the raw line with
            // only trailing whitespace trimmed — lines starting with space/tab
            // (multi-line value continuations) must NOT false-match P:/V:.
            var line = raw.TrimEnd('\r', ' ', '\t');
            if (string.IsNullOrWhiteSpace(line))
            {
                Flush();
                continue;
            }

            if (line.StartsWith("P:", StringComparison.Ordinal))
            {
                var value = line.Substring(2).Trim();
                if (value.Length > 0)
                {
                    name = value;
                }
            }
            else if (line.StartsWith("V:", StringComparison.Ordinal))
            {
                var value = line.Substring(2).Trim();
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
