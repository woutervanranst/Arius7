using System.CommandLine;
using Arius.Core.Application.Tag;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;

namespace Arius.Cli.Commands;

internal static class TagCommand
{
    public static Command Build(IServiceProvider services)
    {
        var cmd = new Command("tag", "Modify tags on a snapshot");

        var repoOpt         = new Option<string?>("--repo", "-r")       { Description = "Azure connection string" };
        var containerOpt    = new Option<string?>("--container", "-c")   { Description = "Container name" };
        var passwordFileOpt = new Option<string?>("--password-file")     { Description = "Password file path" };
        var snapshotArg     = new Argument<string>("snapshot-id")        { Description = "Snapshot ID" };
        var addTagOpt       = new Option<string[]?>("--add")             { Description = "Add tag(s)", Arity = ArgumentArity.ZeroOrMore };
        var removeTagOpt    = new Option<string[]?>("--remove")          { Description = "Remove tag(s)", Arity = ArgumentArity.ZeroOrMore };
        var setTagOpt       = new Option<string[]?>("--set")             { Description = "Set tags (replaces all)", Arity = ArgumentArity.ZeroOrMore };

        cmd.Options.Add(repoOpt);
        cmd.Options.Add(containerOpt);
        cmd.Options.Add(passwordFileOpt);
        cmd.Options.Add(addTagOpt);
        cmd.Options.Add(removeTagOpt);
        cmd.Options.Add(setTagOpt);
        cmd.Arguments.Add(snapshotArg);

        cmd.SetAction(async (parseResult, ct) =>
        {
            var repo = GlobalOptions.ResolveRepo(parseResult.GetValue(repoOpt));
            if (string.IsNullOrEmpty(repo)) { AnsiConsole.MarkupLine("[red]Error:[/] No --repo specified."); return; }
            var container = parseResult.GetValue(containerOpt) ?? Environment.GetEnvironmentVariable("ARIUS_CONTAINER");
            if (string.IsNullOrEmpty(container)) { AnsiConsole.MarkupLine("[red]Error:[/] No --container specified."); return; }

            var passphrase  = GlobalOptions.ResolvePassphrase(parseResult.GetValue(passwordFileOpt));
            var snapshotId  = parseResult.GetValue(snapshotArg)!;
            var addTags     = parseResult.GetValue(addTagOpt);
            var removeTags  = parseResult.GetValue(removeTagOpt);
            var setTags     = parseResult.GetValue(setTagOpt);

            var (operation, tags) = (setTags, addTags, removeTags) switch
            {
                ({ Length: > 0 }, _, _) => (TagOperation.Set,    (IReadOnlyList<string>)setTags!),
                (_, { Length: > 0 }, _) => (TagOperation.Add,    (IReadOnlyList<string>)addTags!),
                (_, _, { Length: > 0 }) => (TagOperation.Remove, (IReadOnlyList<string>)removeTags!),
                _                       => throw new InvalidOperationException("Specify --set, --add, or --remove.")
            };

            var handler = services.GetRequiredService<TagHandler>();
            var result  = await handler.Handle(
                new TagRequest(repo, container, passphrase, snapshotId, operation, tags), ct);

            AnsiConsole.MarkupLine(
                $"[green]{Markup.Escape(result.Message)}[/]  Tags: {string.Join(", ", result.Tags)}");
        });

        return cmd;
    }
}
