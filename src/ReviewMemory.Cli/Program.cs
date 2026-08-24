using System.CommandLine;
using ReviewMemory.Core;
using ReviewMemory.Core.Reporting;
using ReviewMemory.GitHub;
using ReviewMemory.Storage;

var lastOption = new Option<int>("--last") { DefaultValueFactory = _ => 200 };
var tokenOption = new Option<string?>("--token") { Description = "GitHub token; por defecto usa la variable GITHUB_TOKEN" };
var connectionStringOption = new Option<string?>("--connection-string")
{
    Description = "Conexión Postgres; por defecto usa REVIEWMEMORY_CONNECTIONSTRING o la BD local de docker-compose",
};
var repoOption = new Option<string?>("--repo") { Description = "Filtra por repositorio owner/name" };
var filesOption = new Option<string?>("--files") { Description = "Rutas separadas por coma para búsqueda por solapamiento" };
var limitOption = new Option<int>("--limit") { DefaultValueFactory = _ => 10 };
var prOption = new Option<int>("--pr") { Required = true, Description = "Número del PR a contextualizar" };
var formatOption = new Option<string>("--format") { DefaultValueFactory = _ => "console" };

var repoArgument = new Argument<string>("repo") { Description = "Repositorio en formato owner/name" };
var queryArgument = new Argument<string>("query") { Description = "Texto libre a buscar en el historial de reviews" };

var indexCommand = new Command("index", "Descarga y indexa los PRs recientes de un repositorio");
indexCommand.Add(repoArgument);
indexCommand.Add(lastOption);
indexCommand.Add(tokenOption);
indexCommand.Add(connectionStringOption);

var searchCommand = new Command("search", "Busca discusiones históricas de review por texto y/o archivos");
searchCommand.Add(queryArgument);
searchCommand.Add(repoOption);
searchCommand.Add(filesOption);
searchCommand.Add(limitOption);
searchCommand.Add(formatOption);
searchCommand.Add(connectionStringOption);

var contextCommand = new Command("context", "Recupera el contexto histórico relevante para un PR concreto");
contextCommand.Add(repoArgument);
contextCommand.Add(prOption);
contextCommand.Add(limitOption);
contextCommand.Add(formatOption);
contextCommand.Add(connectionStringOption);

var rootCommand = new RootCommand("ReviewMemory: memoria institucional de code review para cualquier reviewer");
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
        Console.Error.WriteLine("error: el repositorio debe tener el formato owner/name");
        return usageError;
    }

    try
    {
        await using var dataSource = await OpenDatabaseAsync(parseResult.GetValue(connectionStringOption));
        var source = new GitHubPullRequestSource(
            ResolveToken(parseResult.GetValue(tokenOption)),
            reason => Console.Error.WriteLine($"aviso: no se pudo obtener resolved vía GraphQL: {reason}"));
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
            Console.Out.WriteLine($"#{pullRequest.Number} {pullRequest.Title} → {threadCount} discusión(es)");
        }

        Console.Out.WriteLine($"""
            Indexados {pullRequests} PRs · {threads} discusiones · {decisions} decisiones con desenlace
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
        Console.Error.WriteLine($"error: --format desconocido '{format}' (console|json)");
        return usageError;
    }

    if (queryText.Trim().Length == 0 && paths.Length == 0)
    {
        Console.Error.WriteLine("error: la búsqueda requiere texto o --files");
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
        Console.Error.WriteLine($"error: --format desconocido '{format}' (console|json)");
        return usageError;
    }

    if (ParseRepo(parseResult.GetValue(repoArgument)!) is not { } repo)
    {
        Console.Error.WriteLine("error: el repositorio debe tener el formato owner/name");
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
            Console.Error.WriteLine($"error: el PR {repo.Owner}/{repo.Name}#{number} no está indexado; ejecuta 'reviewmemory index' primero");
            return usageError;
        }

        var hits = await search.ContextForPullRequestAsync(
            repo.Owner + "/" + repo.Name,
            number,
            new SearchQuery("", Limit: parseResult.GetValue(limitOption)),
            cancellationToken);

        if (format is "console")
        {
            Console.Out.WriteLine($"{summary.State.ToUpperInvariant()} PR #{summary.Number} «{summary.Title}» ({summary.Author})");
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
