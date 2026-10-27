using Microsoft.Extensions.DependencyInjection;

namespace Calldown.AppHost;

internal static class RunOnceCommands
{
    public static IResourceBuilder<ProjectResource> WithDbSetupCommands(
        this IResourceBuilder<ProjectResource> dbSetup
    )
    {
        var nextRun = dbSetup.WithNextRunArgs();

        dbSetup.WithCommand(
            "reimport-sdb",
            "Re-import SDB",
            ctx => nextRun.StartWith(ctx, "--force-sdb"),
            new CommandOptions
            {
                Description = "Drops the sdb schema and imports clientdb.sd2 again",
                IconName = "ArrowSync",
                UpdateState = OnlyWhenStopped,
            }
        );

        dbSetup.WithCommand(
            "reset-database",
            "Reset database",
            ctx => nextRun.StartWith(ctx, "--reset"),
            new CommandOptions
            {
                Description =
                    "Drops the webapi, sdb and calldown schemas and sets everything up from scratch",
                ConfirmationMessage =
                    "This deletes all accounts and characters. Restart rin-webapi and rin-internal afterwards. Continue?",
                IconName = "DatabaseWarning",
                UpdateState = OnlyWhenStopped,
            }
        );

        return dbSetup;
    }

    public static IResourceBuilder<ProjectResource> WithCollisionCommands(
        this IResourceBuilder<ProjectResource> collisionCache
    )
    {
        var nextRun = collisionCache.WithNextRunArgs();

        collisionCache.WithCommand(
            "rebuild-collision",
            "Rebuild collision cache",
            ctx => nextRun.StartWith(ctx, "--force"),
            new CommandOptions
            {
                Description =
                    "Regenerates the collision cache even if it looks current. Restart pin-gameserver afterwards to load it.",
                IconName = "ArrowSync",
                UpdateState = OnlyWhenStopped,
            }
        );

        return collisionCache;
    }

    public static NextRun WithNextRunArgs(
        this IResourceBuilder<ProjectResource> resource,
        params string[] defaults
    )
    {
        var nextRun = new NextRun(resource.Resource);
        resource.WithArgs(ctx =>
        {
            foreach (var arg in nextRun.Take() ?? defaults)
            {
                ctx.Args.Add(arg);
            }
        });

        return nextRun;
    }

    public static ResourceCommandState OnlyWhenStopped(UpdateCommandStateContext ctx)
    {
        var state = ctx.ResourceSnapshot.State?.Text;
        return
            state == KnownResourceStates.Finished
            || state == KnownResourceStates.Exited
            || state == KnownResourceStates.FailedToStart
            ? ResourceCommandState.Enabled
            : ResourceCommandState.Disabled;
    }

    public sealed class NextRun(IResource resource)
    {
        private string[]? _args;

        public string[]? Take() => Interlocked.Exchange(ref _args, null);

        public Task<ExecuteCommandResult> StartWith(ExecuteCommandContext ctx, params string[] args)
        {
            _args = args;
            var commands = ctx.Services.GetRequiredService<ResourceCommandService>();
            return commands.ExecuteCommandAsync(
                resource,
                KnownResourceCommands.StartCommand,
                ctx.CancellationToken
            );
        }
    }
}
