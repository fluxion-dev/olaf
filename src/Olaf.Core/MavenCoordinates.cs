namespace Olaf.Core;

/// <summary>
/// Single owner for JVM "group:artifact" first-colon splits. Raw split only:
/// no trimming, no empty/reject validation — each call-site wrapper preserves
/// its own validation (strict-null, bare-fallback, ??=). Never throws.
/// </summary>
public static class MavenCoordinates
{
    public static bool TrySplit(string? name, out string? group, out string? artifact)
    {
        group = null;
        artifact = null;
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        var sep = name.IndexOf(':');
        if (sep < 0)
        {
            return false;
        }

        group = name.Substring(0, sep);
        artifact = name.Substring(sep + 1);
        return true;
    }
}
