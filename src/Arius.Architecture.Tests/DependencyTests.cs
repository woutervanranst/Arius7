using ArchUnitNET.Fluent;
using ArchUnitNET.Loader;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

namespace Arius.Architecture.Tests;

/// <summary>
/// Architecture tests enforcing dependency rules across projects.
/// Note: uses HasNoViolations() since the base TngTech.ArchUnitNET package
/// does not expose Check(Architecture) on IArchRule (that is added by framework
/// extension packages like ArchUnitNET.xUnit or ArchUnitNET.NUnit).
/// </summary>
public class DependencyTests
{
    private static readonly ArchUnitNET.Domain.Architecture Architecture = new ArchLoader()
        .LoadAssemblies(
            typeof(Core.AssemblyMarker).Assembly,
            typeof(AzureBlob.AssemblyMarker).Assembly,
            typeof(Cli.AssemblyMarker).Assembly
        )
        .Build();

    [Test]
    public void Core_Should_Not_Reference_Azure()
    {
        // Core must not depend on any Azure namespace types (Azure.Storage, Azure.Identity, Azure.Core, etc.)
        IArchRule rule = Classes().That().ResideInAssembly(
                Architecture.Assemblies.First(a => a.FullName.Contains("Arius.Core")))
            .Should().NotDependOnAnyTypesThat().ResideInNamespace("Azure");

        rule.HasNoViolations(Architecture).ShouldBeTrue(
            $"Arius.Core must not depend on Azure types. Violations: {DescribeViolations(rule)}");
    }

    [Test]
    public void Cli_Should_Not_Reference_Azure()
    {
        // CLI must not depend on any Azure namespace types directly;
        // all Azure interactions are mediated through Arius.AzureBlob types.
        IArchRule rule = Classes().That().ResideInAssembly(
                Architecture.Assemblies.First(a => a.FullName.Contains("Arius.Cli") && !a.FullName.Contains("Tests")))
            .Should().NotDependOnAnyTypesThat().ResideInNamespace("Azure");

        rule.HasNoViolations(Architecture).ShouldBeTrue(
            $"Arius.Cli must not depend on Azure types. Violations: {DescribeViolations(rule)}");
    }

    [Test]
    public void Core_Should_Not_Depend_On_Cli()
    {
        var coreAssembly = Architecture.Assemblies.First(a => a.FullName.Contains("Arius.Core") && !a.FullName.Contains("Tests"));
        var cliAssembly  = Architecture.Assemblies.First(a => a.FullName.Contains("Arius.Cli")  && !a.FullName.Contains("Tests"));

        IArchRule rule = Classes().That().ResideInAssembly(coreAssembly)
            .Should().NotDependOnAnyTypesThat().ResideInAssembly(cliAssembly);

        rule.HasNoViolations(Architecture).ShouldBeTrue(
            $"Arius.Core must not depend on Arius.Cli. Violations: {DescribeViolations(rule)}");
    }

    [Test]
    public void Core_Should_Not_Depend_On_AzureBlob()
    {
        var coreAssembly      = Architecture.Assemblies.First(a => a.FullName.Contains("Arius.Core")      && !a.FullName.Contains("Tests"));
        var azureBlobAssembly = Architecture.Assemblies.First(a => a.FullName.Contains("Arius.AzureBlob") && !a.FullName.Contains("Tests"));

        IArchRule rule = Classes().That().ResideInAssembly(coreAssembly)
            .Should().NotDependOnAnyTypesThat().ResideInAssembly(azureBlobAssembly);

        rule.HasNoViolations(Architecture).ShouldBeTrue(
            $"Arius.Core must not depend on Arius.AzureBlob. Violations: {DescribeViolations(rule)}");
    }

    [Test]
    public void Mediator_Command_And_Stream_Handlers_Should_Live_In_Core_Only()
    {
        var nonCoreHandlerTypes = new[]
        {
            typeof(AzureBlob.AssemblyMarker).Assembly,
            typeof(Cli.AssemblyMarker).Assembly,
        }
        .SelectMany(assembly => assembly.GetTypes())
        .Where(type => type is { IsClass: true, IsAbstract: false })
        .Where(type => type.GetInterfaces().Any(i =>
            i.FullName is "Mediator.ICommandHandler`2"
                or "Mediator.IStreamQueryHandler`2"))
        .Select(type => $"{type.Assembly.GetName().Name}:{type.FullName}")
        .ToList();

        nonCoreHandlerTypes.ShouldBeEmpty(
            $"Mediator command/query handlers must live in Arius.Core. Violations: {string.Join(", ", nonCoreHandlerTypes)}");
    }

    // Helper: produce a human-readable summary of rule violations for failure messages
    private static string DescribeViolations(IArchRule rule)
    {
        var results = rule.Evaluate(Architecture)
            .Where(r => !r.Passed)
            .Select(r => r.Description)
            .Take(10);
        return string.Join("; ", results);
    }
}
