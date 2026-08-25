namespace ReviewMemory.Cli.Tests;

/// <summary>
/// Exit code and output contract per docs/specs/04-cli.md (§Exit codes,
/// §Acceptance criteria 1-2) and docs/specs/03-ranking.md (§Agent contract).
/// </summary>
public sealed class CliExitCodeTests
{
    [Theory]
    [InlineData("--help")]
    [InlineData("index", "--help")]
    [InlineData("search", "--help")]
    [InlineData("context", "--help")]
    public void Help_exits_zero_and_prints_to_stdout(params string[] args)
    {
        var result = CliProcess.Run(args);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Usage:", result.StdOut);
        Assert.DoesNotContain("error: ", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
    }

    [Fact]
    public void Missing_required_pr_is_a_parse_error_exit_one()
    {
        var result = CliProcess.Run("context", "a/b");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("'--pr' is required", result.StdErr);
        Assert.Contains("Usage:", result.StdOut);
    }

    [Theory]
    [InlineData(new[] { "index", "owner" }, "error: repository must be in owner/name format")]
    [InlineData(new[] { "search", "" }, "error: search requires text or --files")]
    [InlineData(new[] { "search", "x", "--format", "xml" }, "error: unknown --format 'xml' (console|json)")]
    [InlineData(new[] { "context", "badrepo", "--pr", "1" }, "error: repository must be in owner/name format")]
    public void Invalid_usage_exits_two_with_literal_message_on_stderr(string[] args, string expected)
    {
        var result = CliProcess.Run(args);

        Assert.Equal(2, result.ExitCode);
        Assert.Equal(expected + Environment.NewLine, result.StdErr);
        Assert.Equal(string.Empty, result.StdOut);
    }

    [Fact]
    public void Unreachable_database_is_a_runtime_error_exit_one()
    {
        var result = CliProcess.Run(
            "search", "x",
            "--connection-string", "Host=localhost;Port=5999;Database=reviewmemory;Username=reviewmemory;Password=reviewmemory");

        Assert.Equal(1, result.ExitCode);
        Assert.StartsWith("error: ", result.StdErr);
        Assert.Equal(string.Empty, result.StdOut);
    }

    [Fact]
    public void Context_over_unindexed_pr_exits_two_with_literal_message()
    {
        var connectionString = Environment.GetEnvironmentVariable("REVIEWMEMORY_TEST_CONNECTIONSTRING");
        if (connectionString is null)
        {
            return;
        }

        var repo = $"smoke/{Guid.NewGuid():N}";
        var result = CliProcess.Run("context", repo, "--pr", "999999", "--connection-string", connectionString);

        Assert.Equal(2, result.ExitCode);
        Assert.Equal($"error: PR {repo}#999999 is not indexed; run 'reviewmemory index' first{Environment.NewLine}", result.StdErr);
        Assert.Equal(string.Empty, result.StdOut);
    }
}
