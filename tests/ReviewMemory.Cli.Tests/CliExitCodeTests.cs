namespace ReviewMemory.Cli.Tests;

/// <summary>
/// Contrato de exit codes y salida según docs/specs/04-cli.md (§Exit codes,
/// §Criterios de aceptación 1-2) y docs/specs/03-recuperacion.md (§Contrato para agentes).
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
    [InlineData(new[] { "index", "owner" }, "error: el repositorio debe tener el formato owner/name")]
    [InlineData(new[] { "search", "" }, "error: la búsqueda requiere texto o --files")]
    [InlineData(new[] { "search", "x", "--format", "xml" }, "error: --format desconocido 'xml' (console|json)")]
    [InlineData(new[] { "context", "badrepo", "--pr", "1" }, "error: el repositorio debe tener el formato owner/name")]
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
        Assert.Equal($"error: el PR {repo}#999999 no está indexado; ejecuta 'reviewmemory index' primero{Environment.NewLine}", result.StdErr);
        Assert.Equal(string.Empty, result.StdOut);
    }
}
