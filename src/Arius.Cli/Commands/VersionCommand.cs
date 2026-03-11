using System.CommandLine;
using System.Reflection;

namespace Arius.Cli.Commands;

internal static class VersionCommand
{
    public static Command Build()
    {
        var cmd = new Command("version", "Show Arius version information");

        cmd.SetAction((parseResult, ct) =>
        {
            var version = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion ?? "unknown";
            Console.WriteLine($"arius {version}");
            return Task.CompletedTask;
        });

        return cmd;
    }
}
