using Olaf.Cli;

namespace Olaf.Tests.Cli;

/// <summary>
/// Issue #75 policy-file loader unit tests (no subprocess, no network).
/// L1 unknown-key + line, L2 wrong-type (+ valid-schema tail), L3 malformed
/// YAML throws. The injectable-clock boundary (RulesLoader.UtcToday) is
/// pinned in L1's tail because CLI-subprocess e2e cannot inject the
/// in-process clock — frozen dates 2099/2000 in e2e never touch wall-clock.
/// </summary>
public sealed class RulesLoaderTests
{
    [Fact]
    public void L1_UnknownTopLevelKey_ReturnsErrorWithLine()
    {
        var (rules, errors) = RulesLoader.TryParse("bogusKey: true\ndeny: [Unknown]\n", "rules.yaml");

        Assert.Null(rules);
        Assert.Single(errors);
        Assert.Contains("rules.yaml", errors[0], StringComparison.Ordinal);
        Assert.Contains("unknown key 'bogusKey'", errors[0], StringComparison.Ordinal);
        Assert.Matches(@":\d+:\d+", errors[0]);

        // Empty document tail: no errors, empty rules (never null-rules + no-errors).
        var (emptyRules, emptyErrors) = RulesLoader.TryParse(string.Empty, "empty.yaml");
        Assert.NotNull(emptyRules);
        Assert.Empty(emptyErrors);

        // Injectable-clock tail: expiry boundary is date >= today (UTC date),
        // missing expires = perpetual. Pinned here (not e2e) because the
        // subprocess CLI cannot see the test process's UtcToday override.
        var saved = RulesLoader.UtcToday;
        try
        {
            var pinned = new DateOnly(2025, 6, 15);
            RulesLoader.UtcToday = () => pinned;
            var today = RulesLoader.UtcToday();
            Assert.True(
                RulesLoader.IsExceptionActive(
                    new RulesExceptionRow { License = "MIT", Reason = "r", Expires = today },
                    today));
            Assert.False(
                RulesLoader.IsExceptionActive(
                    new RulesExceptionRow { License = "MIT", Reason = "r", Expires = today.AddDays(-1) },
                    today));
            Assert.True(
                RulesLoader.IsExceptionActive(
                    new RulesExceptionRow { License = "MIT", Reason = "r" },
                    today));
        }
        finally
        {
            RulesLoader.UtcToday = saved;
        }
    }

    [Fact]
    public void L2_WrongTypeDenyString_ReturnsError()
    {
        var (rules, errors) = RulesLoader.TryParse("deny: \"MIT\"\n", "rules.yaml");

        Assert.Null(rules);
        Assert.Single(errors);
        Assert.Contains("rules.yaml", errors[0], StringComparison.Ordinal);
        Assert.Contains("'deny' must be a list of strings", errors[0], StringComparison.Ordinal);

        // Valid-schema tail: every documented key parses with zero errors.
        var valid = """
            allow: [MIT]
            deny: [GPL-3.0-only]
            excludeEcosystems: [npm]
            failOnUnknown: true
            failOnUnresolved: false
            exceptions:
              - name: foo
                license: MIT
                reason: approved
                expires: 2099-01-01
            """;
        var (validRules, validErrors) = RulesLoader.TryParse(valid, "rules.yaml");
        Assert.Empty(validErrors);
        Assert.NotNull(validRules);
        Assert.Contains("MIT", validRules.Allow);
        Assert.Contains("GPL-3.0-only", validRules.Deny);
        Assert.Contains("npm", validRules.ExcludeEcosystems);
        Assert.True(validRules.FailOnUnknown);
        Assert.False(validRules.FailOnUnresolved);
        var row = Assert.Single(validRules.Exceptions);
        Assert.Equal("foo", row.Name);
        Assert.Equal(new DateOnly(2099, 1, 1), row.Expires);
    }

    [Fact]
    public void L3_MalformedYaml_ThrowsYamlException()
    {
        // Same bad-indent shape the CLI maps to {file}:{line}:{col} + exit 2
        // (X1 pins the CLI rendering of this throw). ThrowsAny: YamlDotNet
        // surfaces this shape as SemanticErrorException (a YamlException),
        // which is what Program.cs catches.
        Assert.ThrowsAny<YamlDotNet.Core.YamlException>(
            () => RulesLoader.TryParse("deny:\n - MIT\n  - BADINDENT\n   : : :\n", "bad.yaml"));
    }
}
