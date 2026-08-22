using System.Reflection;
using Npgsql;

namespace ReviewMemory.Storage;

public static class DbMigrations
{
    private const string EnsureMigrationsTableSql = """
        CREATE TABLE IF NOT EXISTS schema_migrations (
            name       text PRIMARY KEY,
            applied_at timestamptz NOT NULL DEFAULT now()
        )
        """;

    /// <summary>
    /// Aplica en orden todas las migraciones embebidas que falten.
    /// Devuelve los nombres de las migraciones aplicadas en esta llamada.
    /// </summary>
    public static async Task<IReadOnlyList<string>> ApplyAsync(
        NpgsqlDataSource dataSource, CancellationToken cancellationToken = default)
    {
        var applied = new List<string>();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        await using (var ensure = new NpgsqlCommand(EnsureMigrationsTableSql, connection))
        {
            await ensure.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var (name, sql) in ReadEmbeddedMigrations())
        {
            var alreadyApplied = false;
            await using (var check = new NpgsqlCommand(
                             "SELECT COUNT(*) FROM schema_migrations WHERE name = @name", connection))
            {
                check.Parameters.AddWithValue("name", name);
                alreadyApplied = Convert.ToInt64(await check.ExecuteScalarAsync(cancellationToken)) > 0;
            }

            if (alreadyApplied)
            {
                continue;
            }

            await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
            {
                await using (var apply = new NpgsqlCommand(sql, connection, transaction))
                {
                    await apply.ExecuteNonQueryAsync(cancellationToken);
                }

                await using (var record = new NpgsqlCommand(
                                 "INSERT INTO schema_migrations (name) VALUES (@name)", connection, transaction))
                {
                    record.Parameters.AddWithValue("name", name);
                    await record.ExecuteNonQueryAsync(cancellationToken);
                }

                await transaction.CommitAsync(cancellationToken);
            }

            applied.Add(name);
        }

        return applied;
    }

    private static IEnumerable<(string Name, string Sql)> ReadEmbeddedMigrations() =>
        typeof(DbMigrations).Assembly
            .GetManifestResourceNames()
            .Where(name => name.Contains(".Migrations.") && name.EndsWith(".sql"))
            .OrderBy(name => name, StringComparer.Ordinal)
            .Select(name =>
            {
                const string marker = ".Migrations.";
                using var stream = typeof(DbMigrations).Assembly.GetManifestResourceStream(name)!
                    ?? throw new InvalidOperationException($"Embedded resource not found: {name}");
                using var reader = new StreamReader(stream);
                return (Name: name[(name.IndexOf(marker) + marker.Length)..], Sql: reader.ReadToEnd());
            });
}
