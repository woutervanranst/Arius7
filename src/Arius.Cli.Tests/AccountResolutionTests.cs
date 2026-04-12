using Arius.Cli.Tests.TestSupport;
using NSubstitute;

namespace Arius.Cli.Tests;

[NotInParallel("EnvVarTests")]
public class AccountResolutionTests
{
    [Test]
    public void ResolveAccount_CliFlagOverridesEnvVar()
    {
        var original = Environment.GetEnvironmentVariable("ARIUS_ACCOUNT");
        Environment.SetEnvironmentVariable("ARIUS_ACCOUNT", "envacct");
        try
        {
            var resolved = CliBuilder.ResolveAccount("cliacct");
            resolved.ShouldBe("cliacct");
        }
        finally
        {
            Environment.SetEnvironmentVariable("ARIUS_ACCOUNT", original);
        }
    }

    [Test]
    public void ResolveAccount_EnvVarUsedWhenCliFlagOmitted()
    {
        var original = Environment.GetEnvironmentVariable("ARIUS_ACCOUNT");
        Environment.SetEnvironmentVariable("ARIUS_ACCOUNT", "envacct");
        try
        {
            var resolved = CliBuilder.ResolveAccount(null);
            resolved.ShouldBe("envacct");
        }
        finally
        {
            Environment.SetEnvironmentVariable("ARIUS_ACCOUNT", original);
        }
    }

    [Test]
    public async Task Archive_MissingAccountFromAllSources_ReturnsExitCode1()
    {
        var original = Environment.GetEnvironmentVariable("ARIUS_ACCOUNT");
        Environment.SetEnvironmentVariable("ARIUS_ACCOUNT", null);
        try
        {
            var harness = new CliHarness();
            var exitCode = await harness.InvokeAsync("archive /data -k key -c ctr");

            exitCode.ShouldBe(1);
            harness.ArchiveHandler.ReceivedCalls().ShouldBeEmpty();
        }
        finally
        {
            Environment.SetEnvironmentVariable("ARIUS_ACCOUNT", original);
        }
    }

    [Test]
    public async Task ListQuery_EnvVarAccountUsedWhenCliFlagOmitted()
    {
        var original = Environment.GetEnvironmentVariable("ARIUS_ACCOUNT");
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
            Environment.SetEnvironmentVariable("ARIUS_ACCOUNT", original);
        }
    }
}
