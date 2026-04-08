using Arius.Cli;
using Arius.AzureBlob;
using Serilog;
using Spectre.Console;

try
{
    return await CliBuilder.BuildRootCommand(blobServiceFactory: new AzureBlobServiceFactory()).Parse(args).InvokeAsync();
}
catch (Exception ex)
{
    AnsiConsole.MarkupLine(CliBuilder.FormatUnhandledExceptionMessage(ex));
    Log.Fatal(ex, "Unhandled exception");
    return 1;
}
finally
{
    Log.CloseAndFlush();
}
