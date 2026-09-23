namespace Olaf.Core;

public interface ILicenseResolver
{
    Task<ResolvedLicense> ResolveAsync(Dependency dependency, CancellationToken cancellationToken = default);
}
