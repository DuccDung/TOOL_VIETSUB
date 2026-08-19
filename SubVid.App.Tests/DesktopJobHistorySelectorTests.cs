using SubVid.App.Core;

namespace SubVid.App.Tests;

public sealed class DesktopJobHistorySelectorTests
{
    [Fact]
    public void Select_KeepsAllActiveJobsAndOnlyRecentTerminalHistory()
    {
        var origin = new DateTime(2026, 8, 19, 0, 0, 0, DateTimeKind.Utc);
        var terminalJobs = Enumerable.Range(0, 35)
            .Select(index => CreateJob(LocalJobStatus.Completed, origin.AddMinutes(index)))
            .ToArray();
        var activeJobs = new[]
        {
            CreateJob(LocalJobStatus.Pending, origin.AddMinutes(-4)),
            CreateJob(LocalJobStatus.Running, origin.AddMinutes(-3)),
            CreateJob(LocalJobStatus.Paused, origin.AddMinutes(-2)),
            CreateJob(LocalJobStatus.Interrupted, origin.AddMinutes(-1)),
        };

        var selected = DesktopJobHistorySelector.Select([.. terminalJobs, .. activeJobs]);

        Assert.Equal(DesktopJobHistorySelector.MaximumTerminalJobs + activeJobs.Length, selected.Count);
        Assert.All(activeJobs, active => Assert.Contains(selected, job => job.JobId == active.JobId));
        Assert.DoesNotContain(selected, job => job.JobId == terminalJobs[0].JobId);
        Assert.Contains(selected, job => job.JobId == terminalJobs[^1].JobId);
        Assert.True(selected.SequenceEqual(selected.OrderBy(job => job.CreatedAtUtc).ThenBy(job => job.UpdatedAtUtc)));
    }

    [Fact]
    public void Select_DoesNotMutateManifestJobHistory()
    {
        var jobs = Enumerable.Range(0, 25)
            .Select(index => CreateJob(LocalJobStatus.Failed, DateTime.UtcNow.AddMinutes(index)))
            .ToList();

        var selected = DesktopJobHistorySelector.Select(jobs);

        Assert.Equal(25, jobs.Count);
        Assert.Equal(DesktopJobHistorySelector.MaximumTerminalJobs, selected.Count);
    }

    private static LocalJob CreateJob(LocalJobStatus status, DateTime updatedAtUtc) => new()
    {
        JobId = Guid.NewGuid(),
        Status = status,
        CreatedAtUtc = updatedAtUtc,
        UpdatedAtUtc = updatedAtUtc,
    };
}
