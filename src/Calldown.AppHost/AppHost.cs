using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Calldown.AppHost;
using Calldown.AppHost.Firefall;

var builder = DistributedApplication.CreateBuilder(args);
var paths = TmwPaths.FromAssembly();

// Fixed ports, the client gets them handed out by the oracle ticket and the Matrix server
const int MatrixPort = 25000;
const int GameServerPort = 25001;
const int MatrixHealthPort = 25080;
const int GameServerHealthPort = 25081;

// Parameters:firefall-path in user secrets wins over the detected install, and without either the dashboard asks for it
var detectedInstall = FirefallInstall.Find();
var firefallPath = (
    detectedInstall is null
        ? builder.AddParameter("firefall-path")
        : builder.AddParameter("firefall-path", detectedInstall)
).WithDescription(
    "Root of the Firefall client install, the folder that contains system\\ and firefall.ini"
);
var firefall = firefallPath.Resource;

// Address the clients reach the stack at. localhost keeps everything on this PC, a LAN address
// makes rin-webapi listen on all interfaces and hands that address out in place of localhost
var publicHost = builder.Configuration["Parameters:public-host"] ?? "localhost";
var isRemote = !string.Equals(publicHost, "localhost", StringComparison.OrdinalIgnoreCase);
builder
    .AddParameter("public-host", publicHost)
    .WithDescription(
        "Address the clients reach the stack at. Set it in user secrets and restart the AppHost to change it."
    );

var zoneId = builder
    .AddParameter("zone-id", "448")
    .WithDescription("Default zone of the game server, for characters without a last zone");
var preloadZones = builder
    .AddParameter("preload-zones", "1030")
    .WithDescription("Comma separated zones the game server hosts from startup, other zones get loaded on first use");

// Decides which resources exist, so it's read once at startup: off, zone or zone+assets
var collision = CollisionScope.Parse(builder.Configuration["Parameters:collision"]);
builder
    .AddParameter("collision", collision.Name)
    .WithDescription(
        "Collision pre-generation: off, zone or zone+assets. Set it in user secrets and restart the AppHost to change it."
    );

var streamingAssets = builder
    .AddParameter("streaming-assets-path", StreamingAssets.DefaultPath)
    .WithDescription(
        "Drop folder RIN serves in place of dl.firefall.com: vtex\\static.vtex<N> and AssetStream\\<block>\\<id>.bnk"
    );

// RIN's schema dump assigns every object to tmwadmin, so the role has to exist
var pgUser = builder.AddParameter("pg-user", "tmwadmin");

var postgres = builder
    .AddPostgres("postgres", userName: pgUser)
    // Keep in sync with RIN.WebAPI/docker/docker-compose.yml
    .WithImageTag("18.6-alpine")
    .WithDataVolume("calldown-postgres")
    .WithLifetime(ContainerLifetime.Persistent)
    .WithPgWeb(pgweb => pgweb.WithLifetime(ContainerLifetime.Persistent));

var tmw = postgres.AddDatabase("tmw", "TMW");

var dbSetup = builder
    .AddProject<Projects.Calldown_DbSetup>("db-setup")
    .WithReference(tmw)
    .WaitFor(tmw)
    .WithEnvironment("Calldown__RinSqlPath", Path.Combine(paths.RinRoot, "sql"))
    .WithEnvironment(
        "Calldown__StaticDbPath",
        ReferenceExpression.Create($"{firefall}\\system\\db\\clientdb.sd2")
    )
    .WithDbSetupCommands();

var webApiDb = ReferenceExpression.Create(
    $"{tmw.Resource.ConnectionStringExpression};Search Path=webapi"
);
var sdbDb = ReferenceExpression.Create(
    $"{tmw.Resource.ConnectionStringExpression};Search Path=sdb"
);

var rinInternal = builder
    .AddProject<Projects.RIN_InternalAPI>("rin-internal")
    .WithEnvironment("DBConnConfig__DBConnStr", webApiDb)
    .WithEnvironment("DBConnConfig__SDBConnStr", sdbDb)
    .WithHttp2ToCleartextHealthCheck("http", "/health")
    .WaitForCompletion(dbSetup);

var rinWebApi = builder
    .AddProject<Projects.RIN_WebAPI>("rin-webapi")
    .WithEnvironment("DBConnConfig__DBConnStr", webApiDb)
    .WithEnvironment("DBConnConfig__SDBConnStr", sdbDb)
    .WithEnvironment("DevServer__EnableLocalDev", "true")
    .WithEnvironment("DevServer__DevGameServerURL", publicHost + ":" + MatrixPort)
    .WithEnvironment("WebApiConfig__StreamingAssetsPath", streamingAssets)
    .WithHttpHealthCheck("/health", endpointName: "https")
    .WaitForCompletion(dbSetup);

if (isRemote)
{
    // The http port collides with other services on a shared host and nothing needs it, so it gets a free one
    rinWebApi
        .WithEndpoint("https", e => e.TargetHost = "0.0.0.0")
        .WithEndpoint("http", e => e.Port = null)
        .WithEnvironment("WebApiConfig__BaseURL", "https://" + publicHost + ":5001");

    // The client talks to RIN through WinHTTP, which rejects the dev certificate (CN=localhost, untrusted) on
    // another PC. A certificate issued for public-host and trusted on each client replaces it. Aspire writes
    // Kestrel's certificate settings itself, so it has to come in through Aspire rather than environment variables.
    var tlsCertPath = builder.Configuration["Parameters:tls-cert-path"];
    if (!string.IsNullOrEmpty(tlsCertPath))
    {
        var tlsCert = X509CertificateLoader.LoadPkcs12FromFile(
            tlsCertPath,
            builder.Configuration["Parameters:tls-cert-password"],
            X509KeyStorageFlags.Exportable
        );
#pragma warning disable ASPIRECERTIFICATES001 // Experimental in Aspire 13.5
        rinWebApi.WithHttpsCertificate(tlsCert);
#pragma warning restore ASPIRECERTIFICATES001
    }
}
else
{
    rinWebApi.WithEnvironment("WebApiConfig__BaseURL", rinWebApi.GetEndpoint("https"));
}

var gameServer = builder
    .AddProject<Projects.GameServer>("pin-gameserver")
    .WithEndpoint(
        targetPort: GameServerPort,
        port: GameServerPort,
        name: "udp",
        scheme: "udp",
        protocol: ProtocolType.Udp,
        isProxied: false
    )
    .WithHttpEndpoint(
        targetPort: GameServerHealthPort,
        port: GameServerHealthPort,
        name: "health",
        isProxied: false
    )
    .WithHttpHealthCheck("/", endpointName: "health")
    .WithEnvironment("GameServer__Port", GameServerPort.ToString())
    .WithEnvironment("GameServer__HealthUrl", "http://localhost:" + GameServerHealthPort + "/")
    .WithEnvironment("GameServer__GrpcChannelAddress", rinInternal.GetEndpoint("http"))
    .WithEnvironment(
        "GameServer__StaticDBPath",
        ReferenceExpression.Create($"{firefall}\\system\\db\\clientdb.sd2")
    )
    .WithEnvironment(
        "GameServer__MapsPath",
        ReferenceExpression.Create($"{firefall}\\system\\maps")
    )
    .WithEnvironment(
        "GameServer__AssetDBPath",
        ReferenceExpression.Create($"{firefall}\\system\\assetdb")
    )
    .WithEnvironment("GameServer__ZoneId", zoneId)
    .WithEnvironment("GameServer__PreloadZones", preloadZones)
    .WithEnvironment("GameServer__LoadMapsCollision", collision.Enabled ? "true" : "false")
    .WithEnvironment("GameServer__LogOutputs", "Console")
    .WaitFor(rinInternal);

if (collision.Enabled)
{
    var cacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Calldown",
        "collision",
        "1962"
    );

    var collisionCache = builder
        .AddProject<Projects.CollisionGenerator>("collision-cache")
        .WithArgs(ctx =>
        {
            ctx.Args.Add("--cache-path");
            ctx.Args.Add(cacheDir);
            ctx.Args.Add("--maps-path");
            ctx.Args.Add(ReferenceExpression.Create($"{firefall}\\system\\maps"));
            ctx.Args.Add("--zone-id");
            ctx.Args.Add(zoneId.Resource);

            if (collision.IncludeAssets)
            {
                ctx.Args.Add("--asset-db-path");
                ctx.Args.Add(ReferenceExpression.Create($"{firefall}\\system\\assetdb"));
                ctx.Args.Add("--all-assets");
            }
        })
        .WithCollisionCommands();

    gameServer.WithEnvironment("GameServer__CachePath", cacheDir).WaitForCompletion(collisionCache);
}

var matrix = builder
    .AddProject<Projects.MatrixServer>("pin-matrix")
    .WithEndpoint(
        targetPort: MatrixPort,
        port: MatrixPort,
        name: "udp",
        scheme: "udp",
        protocol: ProtocolType.Udp,
        isProxied: false
    )
    .WithHttpEndpoint(
        targetPort: MatrixHealthPort,
        port: MatrixHealthPort,
        name: "health",
        isProxied: false
    )
    .WithHttpHealthCheck("/", endpointName: "health")
    .WithEnvironment("MatrixServer__Port", MatrixPort.ToString())
    .WithEnvironment("MatrixServer__GameServerPort", GameServerPort.ToString())
    .WithEnvironment("MatrixServer__HealthUrl", "http://localhost:" + MatrixHealthPort + "/")
    .WaitFor(gameServer);

// The client's executable path is needed up front, so this uses the configured or detected install directly
var installRoot = builder.Configuration["Parameters:firefall-path"] ?? detectedInstall;
var install = installRoot is null ? null : new FirefallInstall(installRoot);

if (install is { IsValid: true })
{
    builder.AddFirefallClient(install, rinWebApi).WaitFor(rinWebApi).WaitFor(matrix);
}

rinWebApi.WithCreateAccountCommand();
builder.AddFirefallPreflight(install, "https://localhost:5001");

builder.Build().Run();
