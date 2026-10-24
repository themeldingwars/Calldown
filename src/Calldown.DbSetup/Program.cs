using System.Security.Cryptography;
using Calldown.DbSetup;
using Npgsql;

// Brings the TMW database into the state RIN expects: webapi schema, seed data and the sdb schema.
// Every step records a fingerprint in calldown.setup_state and is skipped on the next run if nothing changed.

var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__tmw");
var rinSqlPath = Environment.GetEnvironmentVariable("Calldown__RinSqlPath");
var staticDbPath = Environment.GetEnvironmentVariable("Calldown__StaticDbPath");

if (
    string.IsNullOrEmpty(connectionString)
    || string.IsNullOrEmpty(rinSqlPath)
    || string.IsNullOrEmpty(staticDbPath)
)
{
    Log.Error(
        "ConnectionStrings__tmw, Calldown__RinSqlPath and Calldown__StaticDbPath have to be set"
    );
    return 1;
}

var reset = args.Contains("--reset");
var forceSdb = args.Contains("--force-sdb");

try
{
    await using var dataSource = NpgsqlDataSource.Create(connectionString);
    var state = new SetupState(dataSource);

    if (reset)
    {
        Log.Info("Resetting database: dropping schemas webapi, sdb and calldown");
        await dataSource.ExecuteAsync(
            "DROP SCHEMA IF EXISTS webapi CASCADE; DROP SCHEMA IF EXISTS sdb CASCADE; DROP SCHEMA IF EXISTS calldown CASCADE;"
        );
    }

    await state.EnsureCreatedAsync();

    await ApplyWebApiSchemaAsync(dataSource, state, rinSqlPath);
    await ApplySeedDataAsync(dataSource, state, rinSqlPath);
    await SdbImport.RunAsync(dataSource, state, connectionString, staticDbPath, forceSdb);

    Log.Info("Database is ready");
    return 0;
}
catch (Exception e)
{
    Log.Error($"Database setup failed: {e}");
    return 1;
}

static async Task ApplyWebApiSchemaAsync(
    NpgsqlDataSource dataSource,
    SetupState state,
    string rinSqlPath
)
{
    const string step = "webapi-schema";
    var file = Path.Combine(rinSqlPath, "webapi_schema.sql");
    var fingerprint = Fingerprint(file);

    var applied = await state.GetAsync(step);
    if (applied is not null)
    {
        if (applied != fingerprint)
        {
            Log.Warn(
                $"{file} changed since it was applied. The schema is left untouched, run 'Reset database' to recreate it"
            );
        }
        else
        {
            Log.Info("webapi schema is up to date");
        }

        return;
    }

    if (await dataSource.SchemaExistsAsync("webapi"))
    {
        // A database that was set up by hand before Calldown took over
        Log.Info("webapi schema already exists, adopting it");
        await state.SetAsync(step, fingerprint);
        return;
    }

    Log.Info($"Applying {file}");
    await dataSource.ExecuteFileInTransactionAsync(file);
    await state.SetAsync(step, fingerprint);
}

static async Task ApplySeedDataAsync(
    NpgsqlDataSource dataSource,
    SetupState state,
    string rinSqlPath
)
{
    const string step = "webapi-seed";

    // Certificates and difficulties reference zone settings, so the order matters.
    // Without the character templates CreateNewCharacter seeds an empty inventory.
    string[] files =
    [
        "ZoneSettings.sql",
        "ZoneCertificates.sql",
        "ZoneDifficulty.sql",
        "Leaderboards.sql",
        "NewCharacterTemplate.sql",
    ];
    var paths = files.Select(f => Path.Combine(rinSqlPath, "Data", f)).ToArray();
    var fingerprint = Fingerprint(paths);

    if (await state.GetAsync(step) is not null)
    {
        Log.Info("Seed data is already applied");
        return;
    }

    if (await dataSource.ScalarAsync<long>("SELECT count(*) FROM webapi.\"ZoneSettings\"") > 0)
    {
        Log.Info("Seed data already present, adopting it");
        await state.SetAsync(step, fingerprint);
        return;
    }

    foreach (var path in paths)
    {
        Log.Info($"Applying {path}");
        await dataSource.ExecuteFileInTransactionAsync(path);
    }

    await state.SetAsync(step, fingerprint);
}

static string Fingerprint(params string[] files)
{
    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    foreach (var file in files)
    {
        hash.AppendData(File.ReadAllBytes(file));
    }

    return Convert.ToHexString(hash.GetHashAndReset());
}
