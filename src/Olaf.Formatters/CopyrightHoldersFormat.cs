using System.Diagnostics.CodeAnalysis;

namespace Olaf.Formatters;

/// <summary>
/// Issue #72: single home for the "; "-joined holders shape (omit-when-empty).
/// Null/empty yields null so call sites double as the guard.
/// </summary>
public static class CopyrightHoldersFormat
{
    public static string? Join(string[]? holders) =>
        holders is { Length: > 0 } ? string.Join("; ", holders) : null;

    public static bool HasHolders([NotNullWhen(true)] string[]? holders) =>
        holders is { Length: > 0 };
}
