using Arius.Api.Downloads;
using Shouldly;

namespace Arius.Api.Tests.Downloads;

public sealed class DownloadStagingTests
{
    private const string Job = "job-1";

    private static (DownloadStaging Staging, string Root) Create()
    {
        var root = Path.Combine(Path.GetTempPath(), "arius-staging-" + Guid.NewGuid().ToString("N"));
        return (new DownloadStaging(root), root);
    }

    private static void Stage(DownloadStaging staging, string jobId, string relativePath, string content = "x")
    {
        var path = staging.FilePathFor(jobId, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Test]
    public async Task PruneUnready_keeps_ready_files_and_deletes_the_rest()
    {
        var (staging, root) = Create();
        try
        {
            Stage(staging, Job, "photos/a.jpg");
            Stage(staging, Job, "photos/b.jpg");
            Stage(staging, Job, "docs/ü c.txt");

            staging.PruneUnready(Job, new HashSet<string> { "photos/a.jpg", "docs/ü c.txt" });

            File.Exists(staging.FilePathFor(Job, "photos/a.jpg")).ShouldBeTrue();
            File.Exists(staging.FilePathFor(Job, "docs/ü c.txt")).ShouldBeTrue();
            File.Exists(staging.FilePathFor(Job, "photos/b.jpg")).ShouldBeFalse();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Test]
    public async Task FilePathFor_rejects_a_path_outside_the_job_folder()
    {
        var (staging, _) = Create();

        Should.Throw<ArgumentException>(() => staging.FilePathFor(Job, "../other/x"));
    }

    [Test]
    public async Task Delete_ListJobIds_and_SizeOf_reflect_the_folders_on_disk()
    {
        var (staging, root) = Create();
        try
        {
            Stage(staging, "job-1", "a.txt", "12345");
            Stage(staging, "job-1", "sub/b.txt", "123");
            Stage(staging, "job-2", "c.txt", "1");

            staging.Exists("job-1").ShouldBeTrue();
            staging.ListJobIds().OrderBy(x => x).ToArray().ShouldBe(["job-1", "job-2"]);
            staging.SizeOf("job-1").ShouldBe(8);

            staging.Delete("job-1");

            staging.Exists("job-1").ShouldBeFalse();
            staging.ListJobIds().ShouldBe(["job-2"]);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Test]
    public async Task A_missing_folder_is_not_an_error()
    {
        var (staging, _) = Create();

        staging.PruneUnready("unknown", new HashSet<string>());
        staging.Delete("unknown");
        staging.SizeOf("unknown").ShouldBe(0);
        staging.Exists("unknown").ShouldBeFalse();
        staging.ListJobIds().ShouldBeEmpty();
    }
}
