using ArchUnitNET.Fluent;
using ArchUnitNET.Loader;
using Arius.Core.Features.ArchiveCommand;
using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.HashCache;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

namespace Arius.Architecture.Tests;

/// <summary>
/// Dependency rules across the Arius projects. Uses <c>HasNoViolations()</c> because the base ArchUnitNET package
/// has no <c>Check(Architecture)</c>; that comes with the test-framework extension packages.
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
        // Azure access goes through Arius.AzureBlob.
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
            typeof(Api.AssemblyMarker).Assembly,
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
            typeof(LocalDirectory),
            typeof(RelativeFileSystem),
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
        // See ADR-0010. Not exhaustive: ArchUnitNET does not see dependencies inside lambdas or async state machines.

        var contractTypes = GetContractTypes();

        // Every Core class is an implementation detail unless exempted below.
        var coreSharedServiceTypes = typeof(Core.AssemblyMarker).Assembly
            .GetTypes()
            .Where(type => type is { IsClass: true, Namespace: not null })
            // Exempt: public contracts and the Core types reachable from them.
            .Where(type => !contractTypes.Contains(type))
            // Exempt: notifications, the events non-Core observers subscribe to.
            .Where(type => !type.GetInterfaces().Any(IsNotification))
            // Exempt: Core exceptions, which adapters may throw, catch or translate.
            .Where(type => !type.IsAssignableTo(typeof(Exception)))
            // Exempt: entry points and helpers that composition roots and storage adapters call.
            .Except([
                typeof(Core.ServiceCollectionExtensions),
                typeof(Core.Shared.RepositoryLocalStatePaths), // for GetLogsDirectory
                typeof(Core.Shared.AriusLogConfig)             // the log level and line format every host configures Serilog with
                ])
            .ToList();

        // Handlers are Core implementation details, but the Mediator-generated infrastructure must reference them.
        var mediatorHandlerNames = coreSharedServiceTypes
            .Where(type => type.GetInterfaces().Any(IsCommandHandler) || type.GetInterfaces().Any(IsQueryHandler))
            .Select(type => type.FullName!)
            .ToHashSet(StringComparer.Ordinal);

        // Checked one type at a time so each failure names the forbidden Core type.
        var coreSharedServiceNames = coreSharedServiceTypes
            .Select(type => type.FullName!)
            .Order()
            .ToList();

        foreach (var coreSharedServiceName in coreSharedServiceNames)
        {
            foreach (var nonCoreAssembly in new[] { AzureBlobAssembly, CliAssembly })
            {
                var classes = Classes().That().ResideInAssembly(nonCoreAssembly);

                if (mediatorHandlerNames.Contains(coreSharedServiceName))
                {
                    // Outside Core, only the Mediator-generated infrastructure may depend on handlers.
                    classes = classes
                        .And().DoNotHaveFullName("Microsoft.Extensions.DependencyInjection.MediatorDependencyInjectionExtensions")
                        .And().DoNotHaveFullName("Mediator.Mediator")
                        .And().DoNotHaveFullName("Mediator.Internals.ContainerMetadata");
                }

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
            // Seeded with the Core surface that non-Core callers, adapters and observers may know about.
            var pending = new Queue<System.Type>(coreAssembly
                .GetTypes()
                .SelectMany(type => GetContractSeedTypes(type)));

            // Transitive, so the Core types that allowed contracts use are allowed too.
            while (pending.TryDequeue(out var type))
            {
                foreach (var dependency in GetCoreTypes(type, coreAssembly))
                {
                    // Visit each type once: the graph can contain cycles.
                    if (!contractTypes.Add(dependency))
                    {
                        continue;
                    }

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
                // Notifications: non-Core observers subscribe to them.
                yield return type;
            }

            // Commands and their results: non-Core callers send them through Mediator.
            foreach (var commandHandlerArgument in type.GetInterfaces()
                .Where(IsCommandHandler)
                .SelectMany(handlerInterface => handlerInterface.GetGenericArguments()))
            {
                yield return commandHandlerArgument;
            }

            // Queries and their results: non-Core callers send them through Mediator.
            foreach (var queryHandlerArgument in type.GetInterfaces()
                .Where(IsQueryHandler)
                .SelectMany(handlerInterface => handlerInterface.GetGenericArguments()))
            {
                yield return queryHandlerArgument;
            }

            if (type is { IsInterface: true, IsPublic: true })
            {
                // Public interface signatures: non-Core implementations and callers compile against their types.
                foreach (var usedType in GetUsedTypes(type))
                {
                    yield return usedType;
                }
            }
        }

        static IEnumerable<System.Type> GetUsedTypes(System.Type type)
        {
            foreach (var property in type.GetProperties())
            {
                yield return property.PropertyType;
            }

            foreach (var field in type.GetFields())
            {
                yield return field.FieldType;
            }

            foreach (var constructor in type.GetConstructors())
            {
                foreach (var parameter in constructor.GetParameters())
                {
                    yield return parameter.ParameterType;
                }
            }

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
                // For arrays, by-refs and pointers, the element type is the dependency.
                foreach (var coreType in GetCoreTypes(type.GetElementType()!, coreAssembly))
                {
                    yield return coreType;
                }

                yield break;
            }

            if (type.IsGenericType)
            {
                // For generic types, the Core type arguments are the dependencies.
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
        };

        typeof(IChunkIndexService).IsPublic.ShouldBeTrue("IChunkIndexService is the public chunk-index facade for this split.");
        typeof(ChunkIndexService).IsNotPublic.ShouldBeTrue("ChunkIndexService is an internal implementation detail behind IChunkIndexService.");

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
    public void Only_LocalStores_Should_Depend_On_Sqlite()
    {
        IArchRule rule = Classes().That().ResideInAssembly(CoreAssembly)
            .And().DoNotHaveFullName(typeof(ChunkIndexLocalStore).FullName!)
            .And().DoNotHaveFullName(typeof(HashCacheLocalStore).FullName!)
            .Should().NotDependOnAnyTypesThat().ResideInNamespace("Microsoft.Data.Sqlite");

        rule.HasNoViolations(Architecture).ShouldBeTrue(
            $"Only {typeof(ChunkIndexLocalStore).FullName} and {typeof(HashCacheLocalStore).FullName} should know the local stores use SQLite. Violations: {DescribeViolations(rule)}");
    }

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
