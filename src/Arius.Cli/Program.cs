using Arius.Cli;
using Serilog;
using Spectre.Console;

try
{
    return await CliBuilder.BuildRootCommand().Parse(args).InvokeAsync();
}
catch (Exception ex)
{
    AnsiConsole.WriteException(ex, ExceptionFormats.ShortenEverything | ExceptionFormats.ShowLinks);
    Log.Fatal(ex, "Unhandled exception");
    return 1;
}
finally
{
    Log.CloseAndFlush();
}
