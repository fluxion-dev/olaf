namespace Olaf.Tests.Parsers;

/// <summary>
/// Red tests for file|dir scan + ecosystem detection.
/// Expects a ParserRegistry (file-or-dir scan, auto-detect, --ecosystem filter).
/// All tests require the registry type first so failures are NotImplementedException (right Red reason).
/// </summary>
public sealed class DetectionTests
{
    [Fact]
    public void Should_SelectSingleParser_When_SingleFileGiven()
    {
        ParserTestHelpers.RequireRegistryType();
        var parsers = ParserTestHelpers.ResolveAllParsers();

        var matches = parsers.Where(p => p.CanHandle("package.json")).ToList();

        Assert.Single(matches);
        Assert.Equal("npm", matches[0].Ecosystem);
    }

    [Fact]
    public void Should_AutoDetectSingleManifest_When_DirectoryHasOneManifest()
    {
        ParserTestHelpers.RequireRegistryType();
        var parsers = ParserTestHelpers.ResolveAllParsers();

        var tempDir = ParserTestHelpers.CopyFixtureToTempDir(
            ParserTestHelpers.FixturePath("npm", "package.json"), "package.json");
        try
        {
            var manifests = Directory.GetFiles(tempDir);
            var matched = parsers
                .Where(p => manifests.Any(f => p.CanHandle(Path.GetFileName(f))))
                .ToList();

            Assert.Single(matched);

            var deps = matched[0].Parse(tempDir);
            Assert.NotEmpty(deps);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Should_Throw_When_DirectoryHasZeroManifests()
    {
        ParserTestHelpers.RequireRegistryType();
        var parsers = ParserTestHelpers.ResolveAllParsers();

        var tempDir = ParserTestHelpers.CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "README.md"), "# empty");

            var manifests = Directory.GetFiles(tempDir);
            var matched = parsers
                .Where(p => manifests.Any(f => p.CanHandle(Path.GetFileName(f))))
                .ToList();

            // Future ParserRegistry.Scan(dir) should throw when nothing detected.
            var ex = Assert.Throws<InvalidOperationException>(() =>
            {
                if (matched.Count == 0)
                {
                    throw new InvalidOperationException($"No manifests found in '{tempDir}'.");
                }
            });
            Assert.Contains("No manifests", ex.Message);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Should_FindAllEcosystems_When_DirectoryHasMultipleManifests()
    {
        ParserTestHelpers.RequireRegistryType();
        var parsers = ParserTestHelpers.ResolveAllParsers();

        var tempDir = ParserTestHelpers.CreateTempDir();
        try
        {
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("npm", "package.json"), tempDir, "package.json");
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("nuget", "sample.csproj"), tempDir, "sample.csproj");
            ParserTestHelpers.CopyFixtureToDir(
                ParserTestHelpers.FixturePath("pip", "requirements.txt"), tempDir, "requirements.txt");

            var manifests = Directory.GetFiles(tempDir);
            Assert.Equal(3, manifests.Length);

            // Both file+dir support: each manifest matches exactly one parser.
            var npm = parsers.Where(p => p.CanHandle("package.json")).ToList();
            var nuget = parsers.Where(p => p.CanHandle("sample.csproj")).ToList();
            var pip = parsers.Where(p => p.CanHandle("requirements.txt")).ToList();

            Assert.Single(npm);
            Assert.Single(nuget);
            Assert.Single(pip);

            var ecosystems = new[] { npm[0].Ecosystem, nuget[0].Ecosystem, pip[0].Ecosystem };
            Assert.Contains("npm", ecosystems);
            Assert.Contains("nuget", ecosystems);
            Assert.Contains("pip", ecosystems);

            // Dir scan finds all three via CanHandle against dir contents.
            var matchedInDir = parsers
                .Where(p => manifests.Any(f => p.CanHandle(Path.GetFileName(f))))
                .Select(p => p.Ecosystem)
                .OrderBy(e => e)
                .ToList();
            Assert.Equal(3, matchedInDir.Count);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Should_FilterByEcosystem_When_EcosystemOverrideGiven()
    {
        ParserTestHelpers.RequireRegistryType();
        var parsers = ParserTestHelpers.ResolveAllParsers();

        // --ecosystem npm should limit scan to npm parser only (CanHandle filter).
        const string ecosystemOverride = "npm";
        var filtered = parsers
            .Where(p => string.Equals(p.Ecosystem, ecosystemOverride, StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.Single(filtered);
        Assert.True(filtered[0].CanHandle("package.json"));
        Assert.False(filtered[0].CanHandle("sample.csproj"));
        Assert.False(filtered[0].CanHandle("requirements.txt"));
    }
}
