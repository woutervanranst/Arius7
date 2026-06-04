using ArchUnitNET.Fluent;
using ArchUnitNET.Loader;
using Arius.Core.Features.ArchiveCommand;
using Arius.Core.Shared.ChunkIndex;
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

    private static readonly ArchUnitNET.Domain.Assembly CoreAssembly = GetAssembly(typeof(Core.AssemblyMarker));
    private static readonly ArchUnitNET.Domain.Assembly AzureBlobAssembly = GetAssembly(typeof(AzureBlob.AssemblyMarker));
    private static readonly ArchUnitNET.Domain.Assembly CliAssembly = GetAssembly(typeof(Cli.AssemblyMarker));


    [Test]
    public void Core_Should_Not_Reference_Azure()
    {
        // Core must not depend on any Azure namespace types (Azure.Storage, Azure.Identity, Azure.Core, etc.)
        IArchRule rule = Classes().That().ResideInAssembly(CoreAssembly)
            .Should().NotDependOnAnyTypesThat().ResideInNamespace("Azure");

        rule.HasNoViolations(Architecture).ShouldBeTrue(
            $"Arius.Core must not depend on Azure types. Violations: {DescribeViolations(rule)}");
    }

    [Test]
    public void Core_Should_Not_Depend_On_AzureBlob()
    {
        IArchRule rule = Classes().That().ResideInAssembly(CoreAssembly)
            .Should().NotDependOnAnyTypesThat().ResideInAssembly(AzureBlobAssembly);

        rule.HasNoViolations(Architecture).ShouldBeTrue(
            $"Arius.Core must not depend on Arius.AzureBlob. Violations: {DescribeViolations(rule)}");
    }

    [Test]
    public void Cli_Should_Not_Reference_Azure()
    {
        // CLI must not depend on any Azure namespace types directly;
        // all Azure interactions are mediated through Arius.AzureBlob types.
        IArchRule rule = Classes().That().ResideInAssembly(CliAssembly)
            .Should().NotDependOnAnyTypesThat().ResideInNamespace("Azure");

        rule.HasNoViolations(Architecture).ShouldBeTrue(
            $"Arius.Cli must not depend on Azure types. Violations: {DescribeViolations(rule)}");
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

    [Test]
    public void Selected_Core_Implementation_Types_Should_Remain_Internal()
    {
        var internalTypes = new[]
        {
            // FileSystem types
            typeof(LocalDirectory),
            typeof(LocalDirectoryEntry),
            typeof(LocalFileEntry),
            typeof(RelativeFileSystem),

            // Archive feature types
            typeof(BinaryFile),
            typeof(PointerFile),
            typeof(FilePair)
        };

        foreach (var type in internalTypes)
        {
            type.IsNotPublic.ShouldBeTrue($"Type '{type.FullName}' should remain internal.");
        }
    }

    [Test]
    public void Core_Is_Exposed_Primarily_Through_Mediator()
    {
        // See ADR0010 for more context.
        // NOTE: this test is NOT exhaustive: it does NOT validate lambdas / async state machines.

        var contractTypes = GetContractTypes();

        // RULE: Every non-contract Core class is treated as Core-owned implementation detail unless explicitly exempted below.
        var coreSharedServiceTypes = typeof(Core.AssemblyMarker).Assembly
            .GetTypes()
            .Where(type => type is { IsClass: true, Namespace: not null })
            // EXCEPTION RULE: Public contracts and the Core DTO/value types reachable from those contracts are allowed across assembly boundaries.
            .Where(type => !contractTypes.Contains(type))
            // EXCEPTION RULE: Notification messages are public cross-boundary events; they are allowed outside Core as event contracts, not implementation services.
            .Where(type => !type.GetInterfaces().Any(IsNotification))
            // EXCEPTION RULE: Core-defined exception types are boundary contracts; non-Core adapters may throw, catch, or translate them.
            .Where(type => !type.IsAssignableTo(typeof(Exception)))
            // EXCEPTION RULE: These Core entry-point or boundary helper types are intentionally callable from composition roots or storage adapters.
            .Except([
                typeof(Core.ServiceCollectionExtensions),
                typeof(Core.Shared.RepositoryLocalStatePaths) // for GetLogsDirectory
                ])
            .ToList();

        // RULE: Command and query handlers remain Core implementation details, but Mediator source-generated infrastructure must reference them for generated dispatch/registration metadata.
        var mediatorHandlerNames = coreSharedServiceTypes
            .Where(type => type.GetInterfaces().Any(IsCommandHandler) || type.GetInterfaces().Any(IsQueryHandler))
            .Select(type => type.FullName!)
            .ToHashSet(StringComparer.Ordinal);

        // RULE: Validate dependencies by full name so ArchUnit reports one precise forbidden Core type per failure.
        var coreSharedServiceNames = coreSharedServiceTypes
            .Select(type => type.FullName!)
            .Order()
            .ToList();

        foreach (var coreSharedServiceName in coreSharedServiceNames)
        {
            foreach (var nonCoreAssembly in new[] { AzureBlobAssembly, CliAssembly })
            {
                // RULE: Non-Core assemblies must not depend on Core implementation types by default.
                var classes = Classes().That().ResideInAssembly(nonCoreAssembly);

                if (mediatorHandlerNames.Contains(coreSharedServiceName))
                {
                    // RULE: Only Mediator-generated infrastructure may depend on Core command/query handlers outside Core.
                    classes = classes
                        .And().DoNotHaveFullName("Microsoft.Extensions.DependencyInjection.MediatorDependencyInjectionExtensions")
                        .And().DoNotHaveFullName("Mediator.Mediator")
                        .And().DoNotHaveFullName("Mediator.Internals.ContainerMetadata");
                }

                // RULE: After applying the narrow contract/source-generator exceptions, no remaining non-Core class may reference the Core implementation type under test.
                IArchRule rule = classes
                    .Should().NotDependOnAnyTypesThat().HaveFullName(coreSharedServiceName);

                rule.HasNoViolations(Architecture).ShouldBeTrue(
                    $"Only Arius.Core may depend on Core shared services. {nonCoreAssembly.Name} depends on {coreSharedServiceName}. Violations: {DescribeViolations(rule)}");
            }
        }

        static bool IsNotification(System.Type type) => type.FullName == "Mediator.INotification";

        static bool IsCommandHandler(System.Type type) => type.IsGenericType && type.GetGenericTypeDefinition().FullName == "Mediator.ICommandHandler`2";

        static bool IsQueryHandler(System.Type type) => type.IsGenericType && type.GetGenericTypeDefinition().FullName is "Mediator.IQueryHandler`2" or "Mediator.IStreamQueryHandler`2";

        static HashSet<System.Type> GetContractTypes()
        {
            var coreAssembly = typeof(Core.AssemblyMarker).Assembly;
            var contractTypes = new HashSet<System.Type>();
            // RULE: Start from every public Core contract surface that non-Core callers, adapters, or observers are allowed to know about.
            var pending = new Queue<System.Type>(coreAssembly
                .GetTypes()
                .SelectMany(type => GetContractSeedTypes(type)));

            // RULE: Treat contract DTO graphs transitively so nested Core value objects used by allowed contracts are allowed too.
            while (pending.TryDequeue(out var type))
            {
                foreach (var dependency in GetCoreTypes(type, coreAssembly))
                {
                    // RULE: Each Core type in the contract graph is processed once to avoid cycles between DTOs and value objects.
                    if (!contractTypes.Add(dependency))
                    {
                        continue;
                    }

                    // RULE: Any Core type used by a contract type's public construction/storage surface becomes part of the allowed contract graph.
                    foreach (var usedType in GetUsedTypes(dependency))
                    {
                        pending.Enqueue(usedType);
                    }
                }
            }

            return contractTypes;
        }

        static IEnumerable<System.Type> GetContractSeedTypes(System.Type type)
        {
            if (type.GetInterfaces().Any(IsNotification))
            {
                // RULE: Notification classes are message contracts because non-Core observers subscribe to them.
                yield return type;
            }

            // RULE: Command and command-result types are message contracts because non-Core callers send commands and receive results through Mediator.
            foreach (var commandHandlerArgument in type.GetInterfaces()
                .Where(IsCommandHandler)
                .SelectMany(handlerInterface => handlerInterface.GetGenericArguments()))
            {
                yield return commandHandlerArgument;
            }

            // RULE: Query and query-result types are message contracts because non-Core callers send queries and enumerate/read results through Mediator.
            foreach (var queryHandlerArgument in type.GetInterfaces()
                .Where(IsQueryHandler)
                .SelectMany(handlerInterface => handlerInterface.GetGenericArguments()))
            {
                yield return queryHandlerArgument;
            }

            if (type is { IsInterface: true, IsPublic: true })
            {
                // RULE: Public interface member signatures are boundary contracts because non-Core implementations and callers must compile against their DTO/value types.
                foreach (var usedType in GetUsedTypes(type))
                {
                    yield return usedType;
                }
            }
        }

        static IEnumerable<System.Type> GetUsedTypes(System.Type type)
        {
            // RULE: Public DTO properties define part of the cross-boundary contract surface.
            foreach (var property in type.GetProperties())
            {
                yield return property.PropertyType;
            }

            // RULE: Public DTO fields, if any, define part of the cross-boundary contract surface.
            foreach (var field in type.GetFields())
            {
                yield return field.FieldType;
            }

            // RULE: Constructor parameter types define positional record and DTO creation contracts.
            foreach (var constructor in type.GetConstructors())
            {
                foreach (var parameter in constructor.GetParameters())
                {
                    yield return parameter.ParameterType;
                }
            }

            // RULE: Method return and parameter types define public interface contracts.
            foreach (var method in type.GetMethods())
            {
                yield return method.ReturnType;

                foreach (var parameter in method.GetParameters())
                {
                    yield return parameter.ParameterType;
                }
            }
        }

        static IEnumerable<System.Type> GetCoreTypes(System.Type type, System.Reflection.Assembly coreAssembly)
        {
            if (type.IsArray || type.IsByRef || type.IsPointer)
            {
                // RULE: Wrapper shapes are not contracts themselves; their Core element type is the contract dependency.
                foreach (var coreType in GetCoreTypes(type.GetElementType()!, coreAssembly))
                {
                    yield return coreType;
                }

                yield break;
            }

            if (type.IsGenericType)
            {
                // RULE: Generic containers are allowed only insofar as their Core generic arguments are allowed contract dependencies.
                foreach (var genericArgument in type.GetGenericArguments())
                {
                    foreach (var coreType in GetCoreTypes(genericArgument, coreAssembly))
                    {
                        yield return coreType;
                    }
                }
            }

            if (type.Assembly == coreAssembly)
            {
                // RULE: Only Arius.Core-owned types can be added to the Core contract allow-list; framework and third-party types are irrelevant to this rule.
                yield return type;
            }
        }
    }

    [Test]
    public void Archive_Local_File_Models_Should_Only_Be_Used_By_ArchiveCommand()
    {
        var archiveNamespace = typeof(ArchiveCommandHandler).Namespace!;
        var archiveLocalFileModelNames = new[]
        {
            typeof(BinaryFile).FullName!,
            typeof(PointerFile).FullName!,
            typeof(FilePair).FullName!
        };

        foreach (var modelName in archiveLocalFileModelNames)
        {
            IArchRule rule = Classes().That().ResideInAssembly(CoreAssembly)
                .And().DoNotResideInNamespace(archiveNamespace)
                .Should().NotDependOnAnyTypesThat().HaveFullName(modelName);

            rule.HasNoViolations(Architecture).ShouldBeTrue(
                $"Only {archiveNamespace} may depend on {modelName}. Violations: {DescribeViolations(rule)}");
        }
    }

    [Test]
    public void Chunk_Index_Internals_Should_Remain_Behind_Service_Facade()
    {
        var chunkIndexNamespace = typeof(ChunkIndexService).Namespace!;
        var internalComponentTypes = new[]
        {
            typeof(ChunkIndexLocalStore),
            typeof(ChunkIndexRouter),
            typeof(LoadedPrefixState),
        };

        typeof(ChunkIndexService).IsPublic.ShouldBeTrue("ChunkIndexService remains the public chunk-index facade for this split.");

        foreach (var componentType in internalComponentTypes)
        {
            componentType.IsNotPublic.ShouldBeTrue($"Type '{componentType.FullName}' should remain an internal chunk-index implementation detail.");

            IArchRule rule = Classes().That().ResideInAssembly(CoreAssembly)
                .And().DoNotResideInNamespace(chunkIndexNamespace)
                .Should().NotDependOnAnyTypesThat().HaveFullName(componentType.FullName!);

            rule.HasNoViolations(Architecture).ShouldBeTrue(
                $"Only {chunkIndexNamespace} may depend on {componentType.FullName}. Violations: {DescribeViolations(rule)}");
        }
    }

    [Test]
    public void Only_ChunkIndexLocalStore_Should_Depend_On_Sqlite()
    {
        IArchRule rule = Classes().That().ResideInAssembly(CoreAssembly)
            .And().DoNotHaveFullName(typeof(ChunkIndexLocalStore).FullName!)
            .Should().NotDependOnAnyTypesThat().ResideInNamespace("Microsoft.Data.Sqlite");

        rule.HasNoViolations(Architecture).ShouldBeTrue(
            $"Only {typeof(ChunkIndexLocalStore).FullName} should know the local store uses SQLite. Violations: {DescribeViolations(rule)}");
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

    private static ArchUnitNET.Domain.Assembly GetAssembly(System.Type assemblyMarker)
    {
        var assemblyName = assemblyMarker.Assembly.GetName().Name;
        return Architecture.Assemblies.First(a => a.Name == assemblyName);
    }
}
