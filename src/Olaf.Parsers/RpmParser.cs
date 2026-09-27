using System.Text;
using Olaf.Core;

namespace Olaf.Parsers;

/// <summary>
/// RPM package database parser (text-dump, standalone-capable).
/// Reads the <c>Packages</c> text dump (<c>var/lib/rpm/Packages</c> or
/// <c>usr/lib/sysimage/rpm/Packages</c>): one <c>name-ver-rel.arch</c> NVRA
/// line per package (<c>rpm -qa</c> default output). The native BerkeleyDB
/// binary format is out of scope — binary input (NUL byte in the first 8KB
/// or UTF-8 decode failure) yields <c>[]</c>, never throws.
/// Malformed lines are skipped; empty input yields <c>[]</c>, never throws.
/// Versions are kept VERBATIM (<c>ver-rel</c> joined, e.g. <c>5.2-5</c>);
/// PURL normalization is deferred to issue #70.
/// OS dependencies resolve to <c>Unknown</c> downstream with reason
/// <c>license-unknown: unsupported ecosystem.</c> (emitted by
/// <c>CachingLicenseResolver</c>; no registry-backed SPDX source for OS DBs)
/// — no resolver changes here.
/// </summary>
public sealed class RpmParser : IEcosystemParser
{
    public string Ecosystem => "rpm";

    public bool CanHandle(string fileName)
    {
        var name = Path.GetFileName(fileName);
        return string.Equals(name, "Packages", StringComparison.OrdinalIgnoreCase);
    }

    public IReadOnlyList<Dependency> Parse(string inputPath)
    {
        if (Directory.Exists(inputPath))
        {
            var packagesPath = Path.Combine(inputPath, "Packages");
            if (File.Exists(packagesPath))
            {
                return ParsePackagesFile(packagesPath);
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
                return ParsePackagesFile(candidate);
            }

            return Array.Empty<Dependency>();
        }

        return ParsePackagesFile(inputPath);
    }

    internal static IReadOnlyList<Dependency> ParsePackagesFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return Array.Empty<Dependency>();
            }

            var bytes = File.ReadAllBytes(path);

            // Binary sniff inside Parse (never crash): NUL byte in the first
            // 8KB means BerkeleyDB binary — deferred, yield [].
            var probeLength = Math.Min(bytes.Length, 8192);
            for (var i = 0; i < probeLength; i++)
            {
                if (bytes[i] == 0)
                {
                    return Array.Empty<Dependency>();
                }
            }

            string text;
            try
            {
                text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                    .GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                // Undecodable bytes are treated as binary — yield [], never throw.
                return Array.Empty<Dependency>();
            }

            return ParsePackagesText(text);
        }
        catch (IOException)
        {
            // Covers File/DirectoryNotFound by inheritance (CS0160) — never catch them separately.
            return Array.Empty<Dependency>();
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<Dependency>();
        }
    }

    internal static IReadOnlyList<Dependency> ParsePackagesText(string text)
    {
        var deps = new List<Dependency>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r', ' ', '\t');
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            // (a) Strip arch: substring after the LAST '.'. No dot → malformed, skip.
            var dot = line.LastIndexOf('.');
            if (dot < 0)
            {
                continue;
            }

            var remainder = line.Substring(0, dot).Trim();
            var segments = remainder.Split('-');

            // (b) Right-split: the cut counts from the RIGHT so hyphenated
            // names (e.g. gpg-pubkey-style) keep all their hyphens.
            string name;
            string version;
            if (segments.Length >= 3)
            {
                name = string.Join("-", segments.Take(segments.Length - 2));
                version = string.Join("-", segments.Skip(segments.Length - 2));
            }
            else if (segments.Length == 2)
            {
                name = segments[0];
                version = segments[1];
            }
            else
            {
                continue;
            }

            // (c) Empty name/version after trim → skip.
            name = name.Trim();
            version = version.Trim();
            if (name.Length == 0 || version.Length == 0)
            {
                continue;
            }

            if (seen.Add(name))
            {
                deps.Add(new Dependency("rpm", name, version, IsTransitive: false));
            }
        }

        return deps;
    }
}
