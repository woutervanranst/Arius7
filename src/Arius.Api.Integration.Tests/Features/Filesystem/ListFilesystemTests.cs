using System.Net;
using System.Net.Http.Json;
using Arius.Api.Features.Filesystem;
using Arius.Api.Integration.Tests.Harness;

namespace Arius.Api.Integration.Tests.Features.Filesystem;

/// <summary><c>GET /fs/list</c> — the local-path picker browses directories as the Api host sees them.</summary>
public class ListFilesystemTests
{
    [Test]
    public async Task Lists_the_immediate_subdirectories_in_name_order_with_the_parent()
    {
        await using var factory = new AriusApiFactory();
        using var client = factory.CreateClient();
        var root = new DirectoryInfo(Path.Combine(Path.GetTempPath(), $"arius-itest-fs-{Guid.NewGuid():N}"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root.FullName, "beta"));
            Directory.CreateDirectory(Path.Combine(root.FullName, "Alpha"));
            Directory.CreateDirectory(Path.Combine(root.FullName, "gamma", "nested"));
            await File.WriteAllTextAsync(Path.Combine(root.FullName, "file.txt"), "not a directory");

            // Surrounding whitespace (e.g. a pasted path) is ignored.
            var listing = await client.GetFromJsonAsync<FsListDto>($"/api/fs/list?path={Uri.EscapeDataString($"  {root.FullName}  ")}");

            await Assert.That(listing!.Path).IsEqualTo(root.FullName);
            await Assert.That(listing.Parent).IsEqualTo(root.Parent!.FullName);
            string[] names = ["Alpha", "beta", "gamma"];   // case-insensitive name order; the file is not listed
            await Assert.That(listing.Entries.Select(e => e.Name).SequenceEqual(names)).IsTrue();
            await Assert.That(listing.Entries.Select(e => e.Path).SequenceEqual(names.Select(name => Path.Combine(root.FullName, name)))).IsTrue();
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Test]
    public async Task Without_a_path_it_lists_the_filesystem_root()
    {
        await using var factory = new AriusApiFactory();
        using var client = factory.CreateClient();

        var listing = await client.GetFromJsonAsync<FsListDto>("/api/fs/list");

        await Assert.That(listing!.Path).IsEqualTo(Path.GetPathRoot(Environment.CurrentDirectory));
        await Assert.That(listing.Parent).IsNull();
    }

    [Test]
    public async Task A_missing_directory_is_404_and_an_invalid_path_is_400()
    {
        await using var factory = new AriusApiFactory();
        using var client = factory.CreateClient();
        var missing = Path.Combine(Path.GetTempPath(), $"arius-itest-missing-{Guid.NewGuid():N}");

        using var notFound = await client.GetAsync($"/api/fs/list?path={Uri.EscapeDataString(missing)}");
        using var invalid = await client.GetAsync($"/api/fs/list?path={Uri.EscapeDataString("bad\0path")}");

        await Assert.That(notFound.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(invalid.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }
}
