namespace Olaf.Core;

public interface IEcosystemParser
{
    string Ecosystem { get; }

    bool CanHandle(string fileName);

    IReadOnlyList<Dependency> Parse(string inputPath);
}
