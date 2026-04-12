using Serilog;

namespace Arius.Cli.Tests;

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
