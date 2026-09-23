namespace Olaf.Core;

public sealed record Dependency(
    string Ecosystem,
    string Name,
    string Version,
    bool IsTransitive)
{
    public bool Direct => !IsTransitive;
}
