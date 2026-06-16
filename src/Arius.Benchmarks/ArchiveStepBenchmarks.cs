using Arius.Core.Features.ArchiveCommand;
using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.FileSystem;
using Arius.E2E.Tests.Datasets;
using Arius.E2E.Tests.Fixtures;
using Arius.E2E.Tests.Workflows;
using Arius.Tests.Shared;
using Arius.Tests.Shared.Encryption;
using Arius.Tests.Shared.Fixtures;
using Arius.Tests.Shared.IO;
using BenchmarkDotNet.Attributes;

namespace Arius.Benchmarks;

[MemoryDiagnoser]
[ThreadingDiagnoser]
public class ArchiveStepBenchmarks
{
    private AzuriteE2EBackendFixture? _backend;
    private SyntheticRepositoryDefinition? _definition;
    private LocalDirectory _preparedSourceRoot;
    private E2EStorageBackendContext? _context;
    private RepositoryTestFixture? _fixture;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _backend = new AzuriteE2EBackendFixture();
        await _backend.InitializeAsync();

        _definition         = SyntheticRepositoryDefinitionFactory.Create(SyntheticRepositoryProfile.Representative);
        _preparedSourceRoot = TestTempRoots.CreateDirectory("benchmark-source");
        RelativeFileSystem.CreateDirectory(_preparedSourceRoot, RelativePath.Root);

        await SyntheticRepositoryMaterializer.MaterializeV1Async(
            _definition,
            RepresentativeWorkflowCatalog.Canonical.Seed,
            _preparedSourceRoot,
            IEncryptionService.EncryptedInstance);
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        if (_backend is not null)
            await _backend.DisposeAsync();

        RelativeFileSystem.DeleteDirectory(_preparedSourceRoot, RelativePath.Root, recursive: true);
    }

    [IterationSetup]
    public void IterationSetup()
        => IterationSetupCoreAsync().GetAwaiter().GetResult();

    async Task IterationSetupCoreAsync()
    {
        if (_backend is null)
            throw new InvalidOperationException("Benchmark state was not initialized.");

        _context = await _backend.CreateContextAsync();
        _fixture = await RepositoryTestFixture.CreateWithPassphraseAsync(
            _context.BlobContainer,
            _context.AccountName,
            _context.ContainerName);

        RelativeFileSystem.DeleteDirectory(_fixture.LocalDirectory, RelativePath.Root, true);

        FileSystemHelper.CopyDirectory(_preparedSourceRoot, _fixture.LocalDirectory);
    }

    [IterationCleanup]
    public void IterationCleanup()
        => IterationCleanupCoreAsync().GetAwaiter().GetResult();

    async Task IterationCleanupCoreAsync()
    {
        if (_fixture is not null)
        {
            await _fixture.DisposeAsync();
            _fixture = null;
        }

        if (_context is not null)
        {
            await _context.DisposeAsync();
            _context = null;
        }
    }

    [Benchmark(Description = "Archive_Step_V1_Representative_Azurite")]
    public async Task Archive_Step_V1_Representative_Azurite()
    {
        if (_fixture is null)
            throw new InvalidOperationException("Benchmark iteration state was not initialized.");

        var result = await _fixture.CreateArchiveHandler()
            .Handle(
                new ArchiveCommand(new ArchiveCommandOptions
                {
                    RootDirectory = _fixture.LocalDirectory.ToString(),
                }),
                CancellationToken.None)
            .AsTask();

        if (!result.Success)
            throw new InvalidOperationException(result.ErrorMessage ?? "Archive benchmark failed.");
    }
}
