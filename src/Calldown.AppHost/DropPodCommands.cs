using System.Globalization;

namespace Calldown.AppHost;

/// <summary>
/// DropPod's dashboard commands. They only build its command line, so this compiles without a DropPod checkout, the
/// resource itself is added behind HAS_DROPPOD.
/// </summary>
internal static class DropPodCommands
{
    private static readonly string[] Behaviors = ["idle", "wander", "chat", "churn", "hop"];

    public static IResourceBuilder<ProjectResource> WithDropPodCommands(
        this IResourceBuilder<ProjectResource> dropPod,
        string dropPodRoot,
        string operatorHost
    )
    {
        // A plain start drops a single pod, the commands put their own command line in front
        var nextRun = dropPod.WithNextRunArgs("drop");

        // The stack runs on a development certificate unless public-host brings its own
        dropPod.WithArgs("--operator", operatorHost, "--insecure").WithExplicitStart();

        dropPod.WithCommand(
            "seed-pods",
            "Seed pods",
            ctx =>
            {
                if (!TryGetCount(ctx, "pods", out var pods, out var error))
                {
                    return Task.FromResult(error);
                }

                return nextRun.StartWith(ctx, "seed", "--pods", pods);
            },
            new CommandOptions
            {
                Description =
                    "Creates the pod accounts droppod001@droppod.local and so on in RIN, each with a character. Only needed once per database, safe to run again.",
                IconName = "PeopleAdd",
                Arguments =
                [
                    new InteractionInput
                    {
                        Name = "pods",
                        Label = "Pods",
                        InputType = InputType.Number,
                        Required = true,
                        Value = "200",
                    },
                ],
                UpdateState = WhileNotRunning,
            }
        );

        dropPod.WithCommand(
            "drop-pods",
            "Drop pods",
            ctx => DropPods(nextRun, ctx),
            new CommandOptions
            {
                Description =
                    "Logs pods in through RIN, holds once all of them are in the world, then logs them out again. Stop the resource to end early.",
                IconName = "Rocket",
                IsHighlighted = true,
                Arguments =
                [
                    new InteractionInput
                    {
                        Name = "pods",
                        Label = "Pods",
                        InputType = InputType.Number,
                        Required = true,
                        Value = "50",
                    },
                    new InteractionInput
                    {
                        Name = "behavior",
                        Label = "Behavior",
                        InputType = InputType.Choice,
                        Required = true,
                        Value = "wander",
                        Options = [.. Behaviors.Select(b => KeyValuePair.Create(b, b))],
                    },
                    new InteractionInput
                    {
                        Name = "zone",
                        Label = "Zone",
                        Description =
                            "Pods in another zone move there first with the zone admin command. Empty keeps them where they are.",
                        InputType = InputType.Text,
                        Placeholder = "448",
                    },
                    new InteractionInput
                    {
                        Name = "seconds",
                        Label = "Hold for (s)",
                        InputType = InputType.Number,
                        Required = true,
                        Value = "300",
                    },
                    new InteractionInput
                    {
                        Name = "ramp",
                        Label = "Ramp",
                        Description = "Pods per second (5/s) or per minute (30/m), up and down",
                        InputType = InputType.Text,
                        Required = true,
                        Value = "5/s",
                    },
                ],
                UpdateState = WhileNotRunning,
            }
        );

        var invasions = Path.Combine(dropPodRoot, "invasions");
        dropPod.WithCommand(
            "start-invasion",
            "Start invasion",
            ctx =>
            {
                var scenario = ctx.Arguments.GetString("scenario")?.Trim();
                if (string.IsNullOrEmpty(scenario))
                {
                    return Task.FromResult(CommandResults.Failure("Pick an invasion"));
                }

                // DropPod looks for names relative to its working directory, which isn't the repository root here
                var path = File.Exists(scenario) ? scenario : Path.Combine(invasions, $"{scenario}.json");
                return nextRun.StartWith(ctx, "invade", "--scenario", path);
            },
            new CommandOptions
            {
                Description = $"Drops the groups of an invasion file from {invasions}",
                IconName = "Flash",
                Arguments =
                [
                    new InteractionInput
                    {
                        Name = "scenario",
                        Label = "Invasion",
                        Description = "A file name from the invasions folder or a full path",
                        InputType = InputType.Choice,
                        AllowCustomChoice = true,
                        Required = true,
                        Options = [.. InvasionNames(invasions).Select(n => KeyValuePair.Create(n, n))],
                    },
                ],
                UpdateState = WhileNotRunning,
            }
        );

        return dropPod;
    }

    private static Task<ExecuteCommandResult> DropPods(RunOnceCommands.NextRun nextRun, ExecuteCommandContext ctx)
    {
        if (!TryGetCount(ctx, "pods", out var pods, out var error) || !TryGetCount(ctx, "seconds", out var seconds, out error))
        {
            return Task.FromResult(error);
        }

        var behavior = ctx.Arguments.GetString("behavior") ?? "idle";
        if (!Behaviors.Contains(behavior))
        {
            return Task.FromResult(CommandResults.Failure($"'{behavior}' isn't a behavior"));
        }

        var args = new List<string> { "drop", "--pods", pods, "--behavior", behavior, "--seconds", seconds, "--ramp", ctx.Arguments.GetString("ramp") ?? "5/s" };

        var zone = ctx.Arguments.GetString("zone")?.Trim();
        if (!string.IsNullOrEmpty(zone))
        {
            if (!uint.TryParse(zone, CultureInfo.InvariantCulture, out _))
            {
                return Task.FromResult(CommandResults.Failure($"'{zone}' isn't a zone id"));
            }

            args.AddRange(["--zone", zone]);
        }

        return nextRun.StartWith(ctx, [.. args]);
    }

    private static bool TryGetCount(ExecuteCommandContext ctx, string name, out string value, out ExecuteCommandResult error)
    {
        value = ctx.Arguments.GetString(name)?.Trim() ?? string.Empty;
        error = CommandResults.Failure($"'{value}' isn't a positive number for {name}");
        return int.TryParse(value, CultureInfo.InvariantCulture, out var number) && number > 0;
    }

    private static IEnumerable<string> InvasionNames(string folder) =>
        Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*.json").Select(Path.GetFileNameWithoutExtension).OfType<string>().Order()
            : [];

    // Unlike the run-once resources this one starts out NotStarted, the commands start it the first time too
    private static ResourceCommandState WhileNotRunning(UpdateCommandStateContext ctx)
    {
        var state = ctx.ResourceSnapshot.State?.Text;
        return state == KnownResourceStates.NotStarted ? ResourceCommandState.Enabled : RunOnceCommands.OnlyWhenStopped(ctx);
    }
}
