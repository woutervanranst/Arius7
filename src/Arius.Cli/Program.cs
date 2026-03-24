using Arius.Cli;

return await CliBuilder.BuildRootCommand().Parse(args).InvokeAsync();
