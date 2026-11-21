using Arius.Api.Shared.Downloads;
using Shouldly;

namespace Arius.Api.Tests.Shared.Downloads;

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
    public async Task Delete_of_an_already_removed_job_folder_succeeds()
    {
        var (staging, _) = Create();

        Should.NotThrow(() => staging.Delete(Guid.NewGuid().ToString()));
    }

    [Test]
    public async Task Delete_ListJobIds_and_SizeOf_reflect_the_folders_on_disk()
    {
        var (staging, root) = Create();
        var job1 = Guid.NewGuid().ToString();
        var job2 = Guid.NewGuid().ToString();
        try
        {
            Stage(staging, job1, "a.txt", "12345");
            Stage(staging, job1, "sub/b.txt", "123");
            Stage(staging, job2, "c.txt", "1");
            Stage(staging, "Downloads", "not-a-job.txt", "1");   // a non-GUID folder is not a staging folder

            staging.Exists(job1).ShouldBeTrue();
            staging.ListJobIds().OrderBy(x => x).ToArray().ShouldBe(new[] { job1, job2 }.OrderBy(x => x).ToArray());
            staging.SizeOf(job1).ShouldBe(8);

            staging.Delete(job1);

            staging.Exists(job1).ShouldBeFalse();
            staging.ListJobIds().ShouldBe([job2]);
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
