namespace Arius.Cli.Tests;

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
        Environment.SetEnvironmentVariable("ARIUS_KEY", null);
        var resolved = CliBuilder.ResolveKey(null, "acct");
        resolved.ShouldBeNull();
    }
}
