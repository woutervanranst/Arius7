using Arius.Core.Shared;

namespace Arius.Core.Tests.Shared;

public class AriusLogConfigTests
{
    [Test]
    [Arguments("Verbose", "Verbose")]
    [Arguments("Debug", "Debug")]
    [Arguments("debug", "Debug")]           // case-insensitive
    [Arguments("  Warning  ", "Warning")]   // trimmed
    [Arguments("Error", "Error")]
    [Arguments("Fatal", "Fatal")]
    public void ResolveLevelName_returns_the_canonical_name_for_a_valid_value(string raw, string expected) =>
        AriusLogConfig.ResolveLevelName(raw).ShouldBe(expected);

    [Test]
    [Arguments(null)]        // unset
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("6")]         // numeric — Enum.TryParse<LogEventLevel> used to accept this as (LogEventLevel)6, silencing ALL logging
    [Arguments("99")]
    [Arguments("Trace")]     // an MEL level name, not a Serilog level
    [Arguments("Critical")]  // ditto
    [Arguments("nonsense")]
    public void ResolveLevelName_falls_back_to_Information_for_unset_or_invalid_values(string? raw) =>
        AriusLogConfig.ResolveLevelName(raw).ShouldBe(AriusLogConfig.DefaultLevelName);
}
