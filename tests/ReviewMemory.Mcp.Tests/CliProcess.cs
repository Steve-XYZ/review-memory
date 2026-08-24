using System.Diagnostics;

namespace ReviewMemory.Mcp.Tests;

/// <summary>
/// Ejecuta el binario real del CLI como subproceso, resolviendo la ruta del DLL
/// relativa al ensamblado de test (patrón dotnet build → bin/Debug/net10.0).
/// </summary>
internal static class CliProcess
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private static readonly string CliDll = Path.GetFullPath(
        Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "src", "ReviewMemory.Cli", "bin", "Debug", "net10.0", "ReviewMemory.Cli.dll"));

    /// <summary>Resultado de una invocación completa del CLI.</summary>
    internal sealed record Result(int ExitCode, string StdOut, string StdErr);

    internal static Result Run(params string[] args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add(CliDll);
        foreach (var argument in args)
        {
            startInfo.ArgumentList.Add(argument);
        }
        startInfo.Environment.Remove("REVIEWMEMORY_CONNECTIONSTRING");

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("no se pudo iniciar el proceso dotnet");
        var stdOut = process.StandardOutput.ReadToEndAsync();
        var stdErr = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(Timeout))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"el CLI no terminó en {Timeout.TotalSeconds}s");
        }

        return new Result(process.ExitCode, stdOut.GetAwaiter().GetResult(), stdErr.GetAwaiter().GetResult());
    }
}
