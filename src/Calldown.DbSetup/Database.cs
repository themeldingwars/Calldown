using System.Text;
using Npgsql;

namespace Calldown.DbSetup;

internal static class Database
{
    extension(NpgsqlDataSource dataSource)
    {
        public async Task ExecuteAsync(string sql)
        {
            await using var command = dataSource.CreateCommand(sql);
            await command.ExecuteNonQueryAsync();
        }

        public async Task<T?> ScalarAsync<T>(string sql)
        {
            await using var command = dataSource.CreateCommand(sql);
            return (T?)await command.ExecuteScalarAsync();
        }

        public async Task<bool> SchemaExistsAsync(string schema)
        {
            await using var command = dataSource.CreateCommand(
                "SELECT EXISTS (SELECT 1 FROM pg_namespace WHERE nspname = $1)"
            );
            command.Parameters.AddWithValue(schema);
            return (bool)(await command.ExecuteScalarAsync())!;
        }

        /// <summary>
        ///     Runs a pg_dump file the way psql would. Npgsql only speaks SQL, so the psql parts of a dump are
        ///     handled here: <c>\restrict</c> / <c>\unrestrict</c> are dropped and <c>COPY ... FROM stdin</c>
        ///     blocks are streamed through a text import.
        /// </summary>
        public async Task ExecuteFileInTransactionAsync(string path)
        {
            var lines = await File.ReadAllLinesAsync(path);

            // pg_dump output changes session settings like search_path, so every file gets a fresh connection
            await using var connection = await dataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            var sql = new StringBuilder();
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];

                if (line.StartsWith(@"\restrict ") || line.StartsWith(@"\unrestrict "))
                {
                    continue;
                }

                if (line.StartsWith("COPY ") && line.EndsWith(" FROM stdin;"))
                {
                    await ExecuteAsync(connection, transaction, sql.ToString());
                    sql.Clear();

                    // The rows are already in COPY's text format, they go to the server unchanged up to the \. line
                    await using var writer = await connection.BeginTextImportAsync(line[..^1]);
                    for (i++; i < lines.Length && lines[i] != @"\."; i++)
                    {
                        await writer.WriteAsync(lines[i]);
                        await writer.WriteAsync('\n');
                    }

                    continue;
                }

                sql.AppendLine(line);
            }

            await ExecuteAsync(connection, transaction, sql.ToString());
            await transaction.CommitAsync();
        }
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql
    )
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return;
        }

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.CommandTimeout = 0;
        await command.ExecuteNonQueryAsync();
    }
}

internal sealed class SetupState(NpgsqlDataSource dataSource)
{
    public Task EnsureCreatedAsync() =>
        dataSource.ExecuteAsync(
            """
            CREATE SCHEMA IF NOT EXISTS calldown;
            CREATE TABLE IF NOT EXISTS calldown.setup_state (
                step        text PRIMARY KEY,
                fingerprint text NOT NULL,
                applied_at  timestamptz NOT NULL DEFAULT now()
            );
            """
        );

    public async Task<string?> GetAsync(string step)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT fingerprint FROM calldown.setup_state WHERE step = $1"
        );
        command.Parameters.AddWithValue(step);
        return (string?)await command.ExecuteScalarAsync();
    }

    public async Task SetAsync(string step, string fingerprint)
    {
        await using var command = dataSource.CreateCommand(
            """
            INSERT INTO calldown.setup_state (step, fingerprint) VALUES ($1, $2)
            ON CONFLICT (step) DO UPDATE SET fingerprint = excluded.fingerprint, applied_at = now()
            """
        );
        command.Parameters.AddWithValue(step);
        command.Parameters.AddWithValue(fingerprint);
        await command.ExecuteNonQueryAsync();
    }

    public async Task ClearAsync(string step)
    {
        await using var command = dataSource.CreateCommand(
            "DELETE FROM calldown.setup_state WHERE step = $1"
        );
        command.Parameters.AddWithValue(step);
        await command.ExecuteNonQueryAsync();
    }
}

internal static class Log
{
    public static void Info(string message) =>
        Console.WriteLine($"{DateTime.Now:HH:mm:ss} [INF] {message}");

    public static void Warn(string message) =>
        Console.WriteLine($"{DateTime.Now:HH:mm:ss} [WRN] {message}");

    public static void Error(string message) =>
        Console.Error.WriteLine($"{DateTime.Now:HH:mm:ss} [ERR] {message}");
}
