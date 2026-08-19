namespace SubVid.App.Core;

internal static class DesktopJobHistorySelector
{
    internal const int MaximumTerminalJobs = 20;

    internal static IReadOnlyList<LocalJob> Select(IReadOnlyList<LocalJob> jobs)
    {
        var activeJobs = jobs.Where(job => job.Status is
            LocalJobStatus.Pending or
            LocalJobStatus.Running or
            LocalJobStatus.Paused or
            LocalJobStatus.Interrupted);
        var recentTerminalJobs = jobs
            .Where(job => job.Status is
                LocalJobStatus.Completed or
                LocalJobStatus.Failed or
                LocalJobStatus.Cancelled)
            .OrderByDescending(job => job.UpdatedAtUtc)
            .ThenByDescending(job => job.CreatedAtUtc)
            .Take(MaximumTerminalJobs);

        return activeJobs
            .Concat(recentTerminalJobs)
            .DistinctBy(job => job.JobId)
            .OrderBy(job => job.CreatedAtUtc)
            .ThenBy(job => job.UpdatedAtUtc)
            .ToArray();
    }
}
