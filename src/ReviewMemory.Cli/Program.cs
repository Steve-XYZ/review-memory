using System.CommandLine;
using ReviewMemory.Core;
using ReviewMemory.Core.Reporting;
using ReviewMemory.GitHub;
using ReviewMemory.Storage;

var lastOption = new Option<int>("--last") { DefaultValueFactory = _ => 200 };
var tokenOption = new Option<string?>("--token") { Description = "GitHub token; defaults to the GITHUB_TOKEN environment variable" };
var connectionStringOption = new Option<string?>("--connection-string")
{
    Description = "Postgres connection; defaults to REVIEWMEMORY_CONNECTIONSTRING or the docker-compose local database",
};
var repoOption = new Option<string?>("--repo") { Description = "Filters by owner/name repository" };
var filesOption = new Option<string?>("--files") { Description = "Comma-separated paths for overlap-based search" };
var limitOption = new Option<int>("--limit") { DefaultValueFactory = _ => 10 };
var prOption = new Option<int>("--pr") { Required = true, Description = "Number of the PR to contextualize" };
var formatOption = new Option<string>("--format") { DefaultValueFactory = _ => "console" };

var repoArgument = new Argument<string>("repo") { Description = "Repository in owner/name format" };
var queryArgument = new Argument<string>("query") { Description = "Free text to search in the review history" };

var indexCommand = new Command("index", "Downloads and indexes the recent PRs of a repository");
indexCommand.Add(repoArgument);
indexCommand.Add(lastOption);
indexCommand.Add(tokenOption);
indexCommand.Add(connectionStringOption);

var searchCommand = new Command("search", "Searches historical review discussions by text and/or files");
searchCommand.Add(queryArgument);
searchCommand.Add(repoOption);
searchCommand.Add(filesOption);
searchCommand.Add(limitOption);
searchCommand.Add(formatOption);
searchCommand.Add(connectionStringOption);

var contextCommand = new Command("context", "Retrieves the relevant historical context for a specific PR");
contextCommand.Add(repoArgument);
contextCommand.Add(prOption);
contextCommand.Add(limitOption);
contextCommand.Add(formatOption);
contextCommand.Add(connectionStringOption);

var rootCommand = new RootCommand("ReviewMemory: institutional code review memory for any reviewer");
rootCommand.Add(indexCommand);
rootCommand.Add(searchCommand);
rootCommand.Add(contextCommand);

const int usageError = 2;
const int runtimeError = 1;

static string ResolveConnectionString(string? optionValue) =>
    optionValue
    ?? Environment.GetEnvironmentVariable("REVIEWMEMORY_CONNECTIONSTRING")
    ?? "Host=localhost;Port=5433;Database=reviewmemory;Username=reviewmemory;Password=reviewmemory";

static string? ResolveToken(string? optionValue) =>
    optionValue ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN");

static (string Owner, string Name)? ParseRepo(string value)
{
    var parts = value.Split('/');
    return parts.Length == 2 && parts[0].Length > 0 && parts[1].Length > 0
        ? (parts[0], parts[1])
        : null;
}

static string[] ParseFiles(string? csv) =>
    string.IsNullOrWhiteSpace(csv)
        ? []
        : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

static bool ValidFormat(string format) => format is "console" or "json";

async Task<Npgsql.NpgsqlDataSource> OpenDatabaseAsync(string? optionValue)
{
    var dataSource = Npgsql.NpgsqlDataSource.Create(ResolveConnectionString(optionValue));
    await DbMigrations.ApplyAsync(dataSource);
    return dataSource;
}

indexCommand.SetAction(async (parseResult, cancellationToken) =>
{
    if (ParseRepo(parseResult.GetValue(repoArgument)!) is not { } repo)
    {
        Console.Error.WriteLine("error: repository must be in owner/name format");
        return usageError;
    }

    try
    {
        await using var dataSource = await OpenDatabaseAsync(parseResult.GetValue(connectionStringOption));
        var source = new GitHubPullRequestSource(
            ResolveToken(parseResult.GetValue(tokenOption)),
            reason => Console.Error.WriteLine($"warning: could not fetch resolved state via GraphQL: {reason}"));
        var index = new IndexRepository(dataSource);

        var pullRequests = 0;
        var threads = 0;
        var decisions = 0;

        await foreach (var pullRequest in source.GetRecentPullRequestsAsync(
                           repo.Owner, repo.Name, parseResult.GetValue(lastOption), cancellationToken))
        {
            var (threadCount, decisionCount) = await index.UpsertAsync(pullRequest, cancellationToken);
            pullRequests++;
            threads += threadCount;
            decisions += decisionCount;
            Console.Out.WriteLine($"#{pullRequest.Number} {pullRequest.Title} → {threadCount} discussion(s)");
        }

        Console.Out.WriteLine($"""
            Indexed {pullRequests} PRs · {threads} discussions · {decisions} decisions with outcome
            """);
        return 0;
    }
    catch (Exception ex) when (ex is Octokit.ApiException or HttpRequestException or Npgsql.NpgsqlException)
    {
        Console.Error.WriteLine($"error: {ex.Message}");
        return runtimeError;
    }
});

searchCommand.SetAction(async (parseResult, cancellationToken) =>
{
    var format = parseResult.GetValue(formatOption)!;
    var queryText = parseResult.GetValue(queryArgument)!;
    var paths = ParseFiles(parseResult.GetValue(filesOption));

    if (!ValidFormat(format))
    {
        Console.Error.WriteLine($"error: unknown --format '{format}' (console|json)");
        return usageError;
    }

    if (queryText.Trim().Length == 0 && paths.Length == 0)
    {
        Console.Error.WriteLine("error: search requires text or --files");
        return usageError;
    }

    try
    {
        await using var dataSource = await OpenDatabaseAsync(parseResult.GetValue(connectionStringOption));
        var search = new SearchRepository(dataSource);
        var hits = await search.SearchAsync(new SearchQuery(
            queryText,
            parseResult.GetValue(repoOption),
            paths,
            parseResult.GetValue(limitOption)), cancellationToken);

        Console.Out.WriteLine(FormatHits(hits, format));
        return 0;
    }
    catch (Npgsql.NpgsqlException ex)
    {
        Console.Error.WriteLine($"error: {ex.Message}");
        return runtimeError;
    }
});

contextCommand.SetAction(async (parseResult, cancellationToken) =>
{
    var format = parseResult.GetValue(formatOption)!;

    if (!ValidFormat(format))
    {
        Console.Error.WriteLine($"error: unknown --format '{format}' (console|json)");
        return usageError;
    }

    if (ParseRepo(parseResult.GetValue(repoArgument)!) is not { } repo)
    {
        Console.Error.WriteLine("error: repository must be in owner/name format");
        return usageError;
    }

    try
    {
        await using var dataSource = await OpenDatabaseAsync(parseResult.GetValue(connectionStringOption));
        var search = new SearchRepository(dataSource);

        var number = parseResult.GetValue(prOption);
        var summary = await search.GetPullRequestAsync(repo.Owner + "/" + repo.Name, number, cancellationToken);
        if (summary is null)
        {
            Console.Error.WriteLine($"error: PR {repo.Owner}/{repo.Name}#{number} is not indexed; run 'reviewmemory index' first");
            return usageError;
        }

        var hits = await search.ContextForPullRequestAsync(
            repo.Owner + "/" + repo.Name,
            number,
            new SearchQuery("", Limit: parseResult.GetValue(limitOption)),
            cancellationToken);

        if (format is "console")
        {
            Console.Out.WriteLine($"{summary.State.ToUpperInvariant()} PR #{summary.Number} \"{summary.Title}\" ({summary.Author})");
            Console.Out.WriteLine();
        }

        Console.Out.WriteLine(FormatHits(hits, format));
        return 0;
    }
    catch (Npgsql.NpgsqlException ex)
    {
        Console.Error.WriteLine($"error: {ex.Message}");
        return runtimeError;
    }
});

static string FormatHits(IReadOnlyList<SearchHit> hits, string format) =>
    format is "json"
        ? SearchRenderer.RenderJson(hits)
        : SearchRenderer.RenderConsole(hits);

return await rootCommand.Parse(args).InvokeAsync();
