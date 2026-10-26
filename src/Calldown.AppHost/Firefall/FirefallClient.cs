using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Calldown.AppHost.Firefall;

internal static class FirefallClient
{
    private const string BackupSuffix = ".calldown.bak";

    private static readonly InteractionInput[] OptionArguments =
    [
        new()
        {
            Name = "screen-mode",
            Label = "Screen mode",
            InputType = InputType.Choice,
            Required = true,
            Value = "windowed",
            Options =
            [
                new("windowed", "Windowed"),
                new("borderless", "Borderless, fills the screen"),
                new("fullscreen", "Fullscreen, takes the screen exclusively"),
                new("keep", "Leave as it is"),
            ],
        },
        new()
        {
            Name = "window-size",
            Label = "Window size",
            Description = "Only used by the windowed mode",
            InputType = InputType.Choice,
            AllowCustomChoice = true,
            Value = "1600x900",
            Options =
            [
                new("1280x720", "1280x720"),
                new("1600x900", "1600x900"),
                new("1920x1080", "1920x1080"),
                new("2560x1440", "2560x1440"),
            ],
        },
        new()
        {
            Name = "exit-vehicle-key",
            Label = "Leave vehicle",
            Description =
                "1962 ships without a binding for this, so there's no way off an LGV. Takes a key name or a virtual key code.",
            InputType = InputType.Choice,
            AllowCustomChoice = true,
            Value = "E",
            Options =
            [
                new("E", "E"),
                new("F", "F"),
                new("X", "X"),
                new("Backspace", "Backspace"),
                new("keep", "Leave as it is"),
            ],
        },
    ];

    public static IResourceBuilder<ExecutableResource> AddFirefallClient(
        this IDistributedApplicationBuilder builder,
        FirefallInstall install,
        string operatorHost
    )
    {
        return builder
            .AddExecutable("firefall", install.ClientExe, install.BinDir)
            .WithEnvironment("_NO_DEBUG_HEAP", "1")
            .WithEnvironment("OMP_WAIT_POLICY", "PASSIVE")
            .WithExplicitStart()
            .WithCommand(
                "configure-client",
                "Configure client",
                ctx => Task.FromResult(Configure(install, operatorHost, ctx.Logger)),
                new CommandOptions
                {
                    Description =
                        $"Points {install.FirefallIni} at this stack's RIN.WebAPI and patches FirefallClient.exe to bypass RedHanded. The original files are kept with a {BackupSuffix} suffix",
                    IconName = "Settings",
                    IsHighlighted = true,
                }
            )
            .WithCommand(
                "client-options",
                "Client options",
                ctx => Task.FromResult(ApplyOptions(install, ctx)),
                new CommandOptions
                {
                    Description =
                        "Sets the screen mode and the key that leaves a vehicle in the client's own settings.con and keybinds.json",
                    IconName = "Window",
                    Arguments = OptionArguments,
                    UpdateState = OnlyWhileStopped,
                }
            )
            .WithCommand(
                "restore-client",
                "Restore client",
                ctx => Task.FromResult(Restore(install, ctx.Logger)),
                new CommandOptions
                {
                    Description =
                        "Puts back the files from before 'Configure client' and 'Client options', including the unpatched FirefallClient.exe",
                    IconName = "ArrowUndo",
                    UpdateState = OnlyWhileStopped,
                }
            );
    }

    public static IResourceBuilder<ProjectResource> WithCreateAccountCommand(
        this IResourceBuilder<ProjectResource> rinWebApi
    )
    {
        var endpoint = rinWebApi.GetEndpoint("https");

        return rinWebApi.WithCommand(
            "create-account",
            "Create account",
            ctx => CreateAccountAsync(endpoint.Url, ctx),
            new CommandOptions
            {
                Description = "Registers an account you can log in with",
                IconName = "PersonAdd",
                Arguments =
                [
                    new InteractionInput
                    {
                        Name = "email",
                        Label = "Email",
                        InputType = InputType.Text,
                        Required = true,
                        Placeholder = "you@example.com",
                    },
                    new InteractionInput
                    {
                        Name = "password",
                        Label = "Password",
                        InputType = InputType.SecretText,
                        Required = true,
                    },
                    new InteractionInput
                    {
                        Name = "country",
                        Label = "Country",
                        InputType = InputType.Text,
                        Required = true,
                        Value = "US",
                        MaxLength = 2,
                    },
                ],
                UpdateState = ctx =>
                    ctx.ResourceSnapshot.HealthStatus
                    == Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy
                        ? ResourceCommandState.Enabled
                        : ResourceCommandState.Disabled,
            }
        );
    }

    private static ExecuteCommandResult Configure(
        FirefallInstall install,
        string operatorHost,
        ILogger logger
    )
    {
        if (!File.Exists(install.FirefallIni))
        {
            return CommandResults.Failure(
                $"{install.FirefallIni} doesn't exist, start the client once through Steam first"
            );
        }

        try
        {
            Backup(install.FirefallIni, logger);

            var lines = File.ReadAllLines(install.FirefallIni).ToList();
            lines = IniFile.SetValue(lines, "Config", "OperatorHost", $"\"{operatorHost}\"");
            lines = IniFile.SetValue(lines, "UI", "PlayIntroMovie", "false");
            foreach (var (key, value) in StreamingAssets.IniValues(operatorHost))
            {
                lines = IniFile.SetValue(lines, "FilePaths", key, $"\"{value}\"");
            }

            File.WriteAllLines(install.FirefallIni, lines);
            logger.LogInformation("Set OperatorHost and the streaming asset paths to {OperatorHost}", operatorHost);
        }
        catch (UnauthorizedAccessException)
        {
            return CommandResults.Failure(
                $"No write access to {install.Root}. Give your user write access to that folder, or run the AppHost elevated."
            );
        }

        var redHanded = RedHandedPatch.Detect(install.ClientExe);
        logger.LogInformation("RedHanded check of {Exe}: {State}", install.ClientExe, redHanded);

        return redHanded switch
        {
            RedHandedState.Patched => CommandResults.Success($"Client now uses {operatorHost}"),
            RedHandedState.Unpatched => PatchRedHanded(install, operatorHost, logger),
            _ => CommandResults.Success(
                $"Client now uses {operatorHost}. Couldn't tell whether FirefallClient.exe bypasses RedHanded."
            ),
        };
    }

    private static ExecuteCommandResult PatchRedHanded(
        FirefallInstall install,
        string operatorHost,
        ILogger logger
    )
    {
        try
        {
            Backup(install.ClientExe, logger);

            var result = RedHandedPatch.Apply(install.ClientExe);
            if (!result.Success)
            {
                return CommandResults.Failure(
                    $"Client now uses {operatorHost}, but FirefallClient.exe couldn't be patched to bypass RedHanded: {result.Message}"
                );
            }
        }
        catch (UnauthorizedAccessException)
        {
            return CommandResults.Failure(
                $"Client now uses {operatorHost}, but there's no write access to {install.BinDir} to patch out RedHanded. Give your user write access to that folder, or run the AppHost elevated."
            );
        }
        catch (IOException e)
        {
            return CommandResults.Failure(
                $"Client now uses {operatorHost}, but FirefallClient.exe couldn't be patched to bypass RedHanded, close the client and try again: {e.Message}"
            );
        }

        logger.LogInformation("Patched {Exe} to bypass RedHanded", install.ClientExe);
        return CommandResults.Success($"Client now uses {operatorHost} and bypasses RedHanded");
    }

    private static ExecuteCommandResult ApplyOptions(
        FirefallInstall install,
        ExecuteCommandContext ctx
    )
    {
        var screenModeArgument = ctx.Arguments.GetString("screen-mode") ?? "keep";
        if (!Enum.TryParse<ScreenMode>(screenModeArgument, ignoreCase: true, out var screenMode))
        {
            return CommandResults.Failure($"'{screenModeArgument}' isn't a screen mode");
        }

        var sizeArgument = ctx.Arguments.GetString("window-size");
        var size = ClientSettings.ToSize(sizeArgument);
        if (screenMode == ScreenMode.Windowed && size is null)
        {
            return CommandResults.Failure(
                $"'{sizeArgument}' isn't a window size, it has to look like 1600x900"
            );
        }

        var keyArgument = ctx.Arguments.GetString("exit-vehicle-key") ?? "keep";
        var keepKey = keyArgument.Trim().Equals("keep", StringComparison.OrdinalIgnoreCase);
        var keyCode = keepKey ? null : ClientSettings.ToKeyCode(keyArgument);
        if (!keepKey && keyCode is null)
        {
            return CommandResults.Failure(
                $"'{keyArgument}' isn't a key, use a letter, a digit, F1 to F12, a name such as Backspace, or a virtual key code"
            );
        }

        var done = new List<string>();

        try
        {
            if (screenMode != ScreenMode.Keep)
            {
                Backup(install.SettingsCon, ctx.Logger);
                ClientSettings.SetScreenMode(
                    install.SettingsCon,
                    screenMode,
                    size?.Width ?? 0,
                    size?.Height ?? 0
                );

                done.Add(
                    screenMode == ScreenMode.Windowed
                        ? $"windowed at {size!.Value.Width}x{size.Value.Height}"
                        : screenMode.ToString().ToLowerInvariant()
                );
                ctx.Logger.LogInformation(
                    "Set the screen mode to {ScreenMode} in {SettingsCon}",
                    screenMode,
                    install.SettingsCon
                );
            }

            if (keyCode is not null)
            {
                Backup(install.Keybinds, ctx.Logger);
                ClientSettings.SetExitVehicleKey(install.Keybinds, keyCode.Value);

                done.Add($"leave vehicle on {keyArgument.Trim()}");
                ctx.Logger.LogInformation(
                    "Bound Vehicle/ExitVehicle to key code {KeyCode} in {Keybinds}",
                    keyCode,
                    install.Keybinds
                );
            }
        }
        catch (UnauthorizedAccessException)
        {
            return CommandResults.Failure($"No write access to {install.WritePath}");
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            return CommandResults.Failure(
                $"Couldn't write the client settings in {install.WritePath}: {e.Message}"
            );
        }

        return done.Count == 0
            ? CommandResults.Success("Left the client settings alone")
            : CommandResults.Success($"Client set to {string.Join(", ", done)}");
    }

    private static ExecuteCommandResult Restore(FirefallInstall install, ILogger logger)
    {
        var restored = new List<string>();

        try
        {
            foreach (
                var file in new[]
                {
                    install.FirefallIni,
                    install.ClientExe,
                    install.SettingsCon,
                    install.Keybinds,
                }
            )
            {
                var backup = file + BackupSuffix;
                if (!File.Exists(backup))
                {
                    continue;
                }

                File.Copy(backup, file, overwrite: true);
                File.Delete(backup);
                restored.Add(Path.GetFileName(file));
                logger.LogInformation("Restored {File} from {Backup}", file, backup);
            }
        }
        catch (UnauthorizedAccessException)
        {
            return CommandResults.Failure(
                $"No write access to {install.Root} or {install.WritePath}"
            );
        }

        return restored.Count == 0
            ? CommandResults.Failure(
                "There are no backups, the client commands haven't changed anything yet"
            )
            : CommandResults.Success($"Restored {string.Join(", ", restored)}");
    }

    private static ResourceCommandState OnlyWhileStopped(UpdateCommandStateContext ctx)
    {
        var state = ctx.ResourceSnapshot.State?.Text;
        return
            state == KnownResourceStates.Starting
            || state == KnownResourceStates.Running
            || state == KnownResourceStates.Stopping
            ? ResourceCommandState.Disabled
            : ResourceCommandState.Enabled;
    }

    private static void Backup(string file, ILogger logger)
    {
        var backup = file + BackupSuffix;
        if (!File.Exists(file) || File.Exists(backup))
        {
            return;
        }

        File.Copy(file, backup);
        logger.LogInformation("Backed up {File} to {Backup}", file, backup);
    }

    private static async Task<ExecuteCommandResult> CreateAccountAsync(
        string baseUrl,
        ExecuteCommandContext ctx
    )
    {
        var email = ctx.Arguments.GetString("email");
        var password = ctx.Arguments.GetString("password");
        var country = ctx.Arguments.GetString("country");

        // RIN runs with the ASP.NET development certificate, which doesn't have to be trusted for this to work
        using var handler = new HttpClientHandler();
        handler.ServerCertificateCustomValidationCallback =
            HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        using var http = new HttpClient(handler);
        http.BaseAddress = new Uri(baseUrl);

        var query =
            $"DevTestController/RegisterAccount?email={Uri.EscapeDataString(email ?? "")}&password={Uri.EscapeDataString(password ?? "")}&country={Uri.EscapeDataString(country ?? "")}";
        using var response = await http.GetAsync(query, ctx.CancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return CommandResults.Failure(
                $"RIN answered {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(ctx.CancellationToken)}"
            );
        }

        var result = await response.Content.ReadFromJsonAsync<RegisterResult>(
            ctx.CancellationToken
        );
        if (result is null || !string.IsNullOrEmpty(result.Error) || result.AccountId <= 0)
        {
            // RIN swallows database exceptions and returns an empty result, the details only end up in its log
            return CommandResults.Failure(
                string.IsNullOrEmpty(result?.Error)
                    ? "RIN couldn't create the account, check the rin-webapi logs"
                    : $"Couldn't create the account: {result.Error}"
            );
        }

        ctx.Logger.LogInformation(
            "Created account {AccountId} for {Email}",
            result.AccountId,
            email
        );
        return CommandResults.Success($"Created account {result.AccountId} for {email}");
    }

    private sealed record RegisterResult(
        [property: JsonPropertyName("account_id")] long AccountId,
        [property: JsonPropertyName("error")] string? Error
    );
}
