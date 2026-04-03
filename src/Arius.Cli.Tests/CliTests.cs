using Arius.Core.Archive;
using Arius.Core.Ls;
using Arius.Core.Restore;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Serilog;
using Shouldly;
using System.CommandLine;

namespace Arius.Cli.Tests;

// ── Test helper ───────────────────────────────────────────────────────────────

/// <summary>
/// Builds a CLI invocation harness with mock command handlers.
/// The factory passed to <see cref="CliBuilder.BuildRootCommand"/> registers
/// <c>AddMediator()</c> then overrides all three handler interfaces with
/// NSubstitute mocks that capture the command objects for assertion.
/// </summary>
internal sealed class CliHarness
{
    public ICommandHandler<ArchiveCommand, ArchiveResult> ArchiveHandler { get; }
    public ICommandHandler<RestoreCommand, RestoreResult> RestoreHandler { get; }
    public ICommandHandler<LsCommand, LsResult>           LsHandler      { get; }

    /// <summary>
    /// Account name resolved and passed to the factory (set on first invocation).
    /// </summary>
    public string? ResolvedAccount { get; private set; }

    /// <summary>
    /// Account key resolved and passed to the factory (set on first invocation).
    /// </summary>
    public string? ResolvedKey { get; private set; }

    private readonly RootCommand _rootCommand;

    public CliHarness()
    {
        var archiveHandler = Substitute.For<ICommandHandler<ArchiveCommand, ArchiveResult>>();
        var restoreHandler = Substitute.For<ICommandHandler<RestoreCommand, RestoreResult>>();
        var lsHandler      = Substitute.For<ICommandHandler<LsCommand, LsResult>>();

        archiveHandler
            .Handle(Arg.Any<ArchiveCommand>(), Arg.Any<CancellationToken>())
            .Returns(new ArchiveResult
            {
                Success       = true,
                FilesScanned  = 0,
                FilesUploaded = 0,
                FilesDeduped  = 0,
                TotalSize     = 0,
                RootHash      = null,
                SnapshotTime  = DateTimeOffset.UtcNow,
            });

        restoreHandler
            .Handle(Arg.Any<RestoreCommand>(), Arg.Any<CancellationToken>())
            .Returns(new RestoreResult
            {
                Success                  = true,
                FilesRestored            = 0,
                FilesSkipped             = 0,
                ChunksPendingRehydration = 0,
            });

        lsHandler
            .Handle(Arg.Any<LsCommand>(), Arg.Any<CancellationToken>())
            .Returns(new LsResult
            {
                Success = true,
                Entries = Array.Empty<LsEntry>(),
            });

        ArchiveHandler = archiveHandler;
        RestoreHandler = restoreHandler;
        LsHandler      = lsHandler;

        _rootCommand = CliBuilder.BuildRootCommand(serviceProviderFactory: (account, key, passphrase, container, _) =>
        {
            ResolvedAccount = account;
            ResolvedKey     = key;

            var services = new ServiceCollection();
            services.AddMediator();
            services.AddSingleton<ProgressState>();
            // Override all three handlers with mocks
            services.AddSingleton(archiveHandler);
            services.AddSingleton(restoreHandler);
            services.AddSingleton(lsHandler);
            return Task.FromResult<IServiceProvider>(services.BuildServiceProvider());
        });
    }

    /// <summary>
    /// Invokes the CLI with the given arguments and returns the exit code.
    /// </summary>
    public async Task<int> InvokeAsync(string args) => await _rootCommand.Parse(args).InvokeAsync();
}

// ── 6.1 Archive command tests ─────────────────────────────────────────────────

// AnsiConsole.Console is a static property; parallel tests that both set up a
// Recorder would race on that shared state, causing "Collection was modified"
// during Recorder.ExportText().  Serialise all three command-test classes under
// one key so they never overlap.
[NotInParallel("AnsiConsoleRecorder")]
public class ArchiveCommandTests
{
    [Test]
    public async Task Archive_AllOptions_ParsedCorrectly()
    {
        var harness = new CliHarness();
        var exitCode = await harness.InvokeAsync("archive /data -a acct -k key -c ctr -t Hot --remove-local");

        exitCode.ShouldBe(0);

        var call = harness.ArchiveHandler.ReceivedCalls().Single();
        var cmd  = (ArchiveCommand)call.GetArguments()[0]!;
        cmd.Options.UploadTier.ShouldBe(Core.Storage.BlobTier.Hot);
        cmd.Options.RemoveLocal.ShouldBeTrue();
        cmd.Options.NoPointers.ShouldBeFalse();
    }

    [Test]
    public async Task Archive_Defaults_Applied()
    {
        var harness  = new CliHarness();
        var exitCode = await harness.InvokeAsync("archive /data -a acct -k key -c ctr");

        exitCode.ShouldBe(0);

        var call = harness.ArchiveHandler.ReceivedCalls().Single();
        var cmd  = (ArchiveCommand)call.GetArguments()[0]!;
        cmd.Options.UploadTier.ShouldBe(Core.Storage.BlobTier.Archive);
        cmd.Options.RemoveLocal.ShouldBeFalse();
        cmd.Options.NoPointers.ShouldBeFalse();
    }

    [Test]
    public async Task Archive_RemoveLocalPlusNoPointers_ReturnsExitCode1()
    {
        var harness  = new CliHarness();
        var exitCode = await harness.InvokeAsync("archive /data -a acct -k key -c ctr --remove-local --no-pointers");

        exitCode.ShouldBe(1);
        // Handler must NOT have been called
        harness.ArchiveHandler.ReceivedCalls().ShouldBeEmpty();
    }

    [Test]
    public async Task Archive_MockHandlerCapturesPath()
    {
        var harness  = new CliHarness();
        var exitCode = await harness.InvokeAsync("archive /tmp -a acct -k key -c ctr");

        exitCode.ShouldBe(0);

        var call = harness.ArchiveHandler.ReceivedCalls().Single();
        var cmd  = (ArchiveCommand)call.GetArguments()[0]!;
        cmd.Options.RootDirectory.ShouldEndWith("tmp");
    }
}

// ── 6.2 Restore command tests ─────────────────────────────────────────────────

[NotInParallel("AnsiConsoleRecorder")]
public class RestoreCommandTests
{
    [Test]
    public async Task Restore_WithVersion_ParsedCorrectly()
    {
        var harness  = new CliHarness();
        var exitCode = await harness.InvokeAsync("restore /data -a acct -k key -c ctr -v 2026-03-21T140000.000Z");

        exitCode.ShouldBe(0);

        var call = harness.RestoreHandler.ReceivedCalls().Single();
        var cmd  = (RestoreCommand)call.GetArguments()[0]!;
        cmd.Options.Version.ShouldBe("2026-03-21T140000.000Z");
    }

    [Test]
    public async Task Restore_Defaults_Applied()
    {
        var harness  = new CliHarness();
        var exitCode = await harness.InvokeAsync("restore /data -a acct -k key -c ctr");

        exitCode.ShouldBe(0);

        var call = harness.RestoreHandler.ReceivedCalls().Single();
        var cmd  = (RestoreCommand)call.GetArguments()[0]!;
        cmd.Options.Version.ShouldBeNull();
        cmd.Options.NoPointers.ShouldBeFalse();
        cmd.Options.Overwrite.ShouldBeFalse();
    }

    [Test]
    public async Task Restore_LongVersionAlias_ParsedCorrectly()
    {
        var harness  = new CliHarness();
        var exitCode = await harness.InvokeAsync("restore /data -a acct -k key -c ctr --version 2026-01-01");

        exitCode.ShouldBe(0);

        var call = harness.RestoreHandler.ReceivedCalls().Single();
        var cmd  = (RestoreCommand)call.GetArguments()[0]!;
        cmd.Options.Version.ShouldBe("2026-01-01");
    }

    [Test]
    public async Task Restore_OverwriteAndNoPointers_ParsedCorrectly()
    {
        var harness  = new CliHarness();
        var exitCode = await harness.InvokeAsync("restore /data -a acct -k key -c ctr --overwrite --no-pointers");

        exitCode.ShouldBe(0);

        var call = harness.RestoreHandler.ReceivedCalls().Single();
        var cmd  = (RestoreCommand)call.GetArguments()[0]!;
        cmd.Options.Overwrite.ShouldBeTrue();
        cmd.Options.NoPointers.ShouldBeTrue();
    }
}

// ── 6.3 Ls command tests ──────────────────────────────────────────────────────

[NotInParallel("AnsiConsoleRecorder")]
public class LsCommandTests
{
    [Test]
    public async Task Ls_AllFilters_ParsedCorrectly()
    {
        var harness  = new CliHarness();
        var exitCode = await harness.InvokeAsync("ls -a acct -k key -c ctr -v 2026-01-01 --prefix docs/ -f .pdf");

        exitCode.ShouldBe(0);

        var call = harness.LsHandler.ReceivedCalls().Single();
        var cmd  = (LsCommand)call.GetArguments()[0]!;
        cmd.Options.Version.ShouldBe("2026-01-01");
        cmd.Options.Prefix.ShouldBe("docs/");
        cmd.Options.Filter.ShouldBe(".pdf");
    }

    [Test]
    public async Task Ls_Defaults_Applied()
    {
        var harness  = new CliHarness();
        var exitCode = await harness.InvokeAsync("ls -a acct -k key -c ctr");

        exitCode.ShouldBe(0);

        var call = harness.LsHandler.ReceivedCalls().Single();
        var cmd  = (LsCommand)call.GetArguments()[0]!;
        cmd.Options.Version.ShouldBeNull();
        cmd.Options.Prefix.ShouldBeNull();
        cmd.Options.Filter.ShouldBeNull();
    }

    [Test]
    public async Task Ls_MockHandlerCaptures_PrefixAndFilter()
    {
        var harness  = new CliHarness();
        var exitCode = await harness.InvokeAsync("ls -a acct -k key -c ctr --prefix photos/ -f .jpg");

        exitCode.ShouldBe(0);

        var call = harness.LsHandler.ReceivedCalls().Single();
        var cmd  = (LsCommand)call.GetArguments()[0]!;
        cmd.Options.Prefix.ShouldBe("photos/");
        cmd.Options.Filter.ShouldBe(".jpg");
    }
}

// ── 7.x Account and key resolution tests ─────────────────────────────────────

[NotInParallel("EnvVarTests")]
public class AccountResolutionTests
{
    [Test]
    public void ResolveAccount_CliFlagOverridesEnvVar()
    {
        Environment.SetEnvironmentVariable("ARIUS_ACCOUNT", "envacct");
        try
        {
            var resolved = CliBuilder.ResolveAccount("cliacct");
            resolved.ShouldBe("cliacct");
        }
        finally
        {
            Environment.SetEnvironmentVariable("ARIUS_ACCOUNT", null);
        }
    }

    [Test]
    public void ResolveAccount_EnvVarUsedWhenCliFlagOmitted()
    {
        Environment.SetEnvironmentVariable("ARIUS_ACCOUNT", "envacct");
        try
        {
            var resolved = CliBuilder.ResolveAccount(null);
            resolved.ShouldBe("envacct");
        }
        finally
        {
            Environment.SetEnvironmentVariable("ARIUS_ACCOUNT", null);
        }
    }

    [Test]
    public async Task Archive_MissingAccountFromAllSources_ReturnsExitCode1()
    {
        Environment.SetEnvironmentVariable("ARIUS_ACCOUNT", null);
        var harness  = new CliHarness();
        var exitCode = await harness.InvokeAsync("archive /data -k key -c ctr");

        exitCode.ShouldBe(1);
        harness.ArchiveHandler.ReceivedCalls().ShouldBeEmpty();
    }

    [Test]
    public async Task Ls_EnvVarAccountUsedWhenCliFlagOmitted()
    {
        Environment.SetEnvironmentVariable("ARIUS_ACCOUNT", "envacct");
        try
        {
            var harness  = new CliHarness();
            var exitCode = await harness.InvokeAsync("ls -k key -c ctr");

            exitCode.ShouldBe(0);
            harness.ResolvedAccount.ShouldBe("envacct");
        }
        finally
        {
            Environment.SetEnvironmentVariable("ARIUS_ACCOUNT", null);
        }
    }
}

[NotInParallel("EnvVarTests")]
public class KeyResolutionTests
{
    [Test]
    public void ResolveKey_EnvVarUsedWhenCliFlagOmitted()
    {
        Environment.SetEnvironmentVariable("ARIUS_KEY", "envkey");
        try
        {
            var resolved = CliBuilder.ResolveKey(null, "acct");
            resolved.ShouldBe("envkey");
        }
        finally
        {
            Environment.SetEnvironmentVariable("ARIUS_KEY", null);
        }
    }

    [Test]
    public void ResolveKey_ReturnsNullWhenNoSourceAvailable()
    {
        // When no key is available from any source, ResolveKey returns null
        // (the caller falls back to AzureCliCredential — no error is thrown here).
        Environment.SetEnvironmentVariable("ARIUS_KEY", null);
        var resolved = CliBuilder.ResolveKey(null, "acct");
        resolved.ShouldBeNull();
    }
}

[NotInParallel("AnsiConsoleRecorder")]
public class CrashLoggingTests
{
    [Test]
    public void FormatUnhandledExceptionMessage_UsesOnlyEscapedMessage()
    {
        var message = CliBuilder.FormatUnhandledExceptionMessage(new InvalidOperationException("boom [x]"));

        message.ShouldBe("[red]Error:[/] boom [[x]]");
    }

    [Test]
    public void ConfigureAuditLogging_WritesFullExceptionDetailsToLogFile()
    {
        var logFile = CliBuilder.ConfigureAuditLogging("acct", "ctr", "test");

        try
        {
            try
            {
                throw new InvalidOperationException("top level failure",
                    new ArgumentException("inner failure"));
            }
            catch (InvalidOperationException exception)
            {
                Log.Fatal(exception, "Unhandled exception");
            }
        }
        finally
        {
            Log.CloseAndFlush();
        }

        var logContents = File.ReadAllText(logFile);

        logContents.ShouldContain("Unhandled exception");
        logContents.ShouldContain("System.InvalidOperationException: top level failure");
        logContents.ShouldContain("System.ArgumentException: inner failure");
        logContents.ShouldContain(" at ");

        File.Delete(logFile);
    }

    [Test]
    public void ConfigureAuditLogging_DoesNotWriteToConsole()
    {
#pragma warning disable TUnit0055
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var stdOut = new StringWriter();
        using var stdErr = new StringWriter();

        Console.SetOut(stdOut);
        Console.SetError(stdErr);

        var logFile = CliBuilder.ConfigureAuditLogging("acct", "ctr", "test");

        try
        {
            Log.Warning("Visible warning");
            Log.Fatal(new InvalidOperationException("top level failure"), "Unhandled exception");
        }
        finally
        {
            Log.CloseAndFlush();
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }

        var consoleOutput = stdOut.ToString() + stdErr.ToString();

        consoleOutput.ShouldBeEmpty();
        consoleOutput.ShouldNotContain("Unhandled exception");
        consoleOutput.ShouldNotContain("top level failure");

        File.Delete(logFile);
#pragma warning restore TUnit0055
    }
}
