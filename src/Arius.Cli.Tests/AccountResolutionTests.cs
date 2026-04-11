using NSubstitute;
using Shouldly;

namespace Arius.Cli.Tests;

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
        var harness = new CliHarness();
        var exitCode = await harness.InvokeAsync("archive /data -k key -c ctr");

        exitCode.ShouldBe(1);
        harness.ArchiveHandler.ReceivedCalls().ShouldBeEmpty();
    }

    [Test]
    public async Task ListQuery_EnvVarAccountUsedWhenCliFlagOmitted()
    {
        Environment.SetEnvironmentVariable("ARIUS_ACCOUNT", "envacct");
        try
        {
            var harness = new CliHarness();
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
