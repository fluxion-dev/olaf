using static Olaf.Tests.Cli.CliTestHelpers;

namespace Olaf.Tests.Cli;

/// <summary>
/// Issue #73 Step 2: errors 3 + CLI 2 (offline only — committed template
/// fixtures + temp files, npm fixture input; resolvers stay offline-safe).
/// </summary>
public sealed class TemplateCliTests
{
    [Fact]
    public void Should_Exit2_When_TemplateFileIsMissing()
    {
        var input = FixturePath("npm", "package.json");

        var result = RunCli("--input", input, "--template", FixturePath("templates", "does-not-exist.scriban"));

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("Template not found", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_Exit2_When_TemplatePathIsDirectory()
    {
        var input = FixturePath("npm", "package.json");

        var result = RunCli("--input", input, "--template", FixturePath("templates"));

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("Template not found", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_Exit2WithLineNumber_When_TemplateHasSyntaxError()
    {
        var dir = CreateTempDir();
        try
        {
            var template = Path.Combine(dir, "bad.scriban");
            File.WriteAllText(template, "header\n{{#each licenses}}\nno-close\n");
            var input = FixturePath("npm", "package.json");

            var result = RunCli("--input", input, "--template", template);

            Assert.Equal(2, result.ExitCode);
            Assert.Contains("line 2", result.Stderr, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public void Should_ListTemplateFlag_When_HelpRequested()
    {
        var result = RunCli("--help");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("--template", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_OverrideFormat_When_TemplateAndFormatGiven()
    {
        var input = FixturePath("npm", "package.json");
        var template = FixturePath("templates", "header-footer-loop.scriban");

        var result = RunCli("--input", input, "--format", "txt", "--template", template, "--verbose");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Third-Party Attribution", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("Template overrides --format 'txt'.", result.Stderr, StringComparison.Ordinal);
    }
}
