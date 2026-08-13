using TOOL_VIETSUB_APP.Core;

namespace TOOL_VIETSUB_APP.Jobs;

public static class JobStateMachine
{
    private static readonly IReadOnlyDictionary<LocalJobStatus, HashSet<LocalJobStatus>> AllowedTransitions =
        new Dictionary<LocalJobStatus, HashSet<LocalJobStatus>>
        {
            [LocalJobStatus.Pending] = [LocalJobStatus.Running, LocalJobStatus.Cancelled],
            [LocalJobStatus.Running] =
                [LocalJobStatus.Paused, LocalJobStatus.Interrupted, LocalJobStatus.Completed, LocalJobStatus.Failed, LocalJobStatus.Cancelled],
            [LocalJobStatus.Paused] = [LocalJobStatus.Running, LocalJobStatus.Cancelled],
            [LocalJobStatus.Interrupted] = [LocalJobStatus.Pending, LocalJobStatus.Cancelled],
            [LocalJobStatus.Failed] = [LocalJobStatus.Pending, LocalJobStatus.Cancelled],
            [LocalJobStatus.Completed] = [],
            [LocalJobStatus.Cancelled] = [],
        };

    public static bool CanTransition(LocalJobStatus current, LocalJobStatus next) =>
        current == next || AllowedTransitions[current].Contains(next);

    public static void Transition(LocalJob job, LocalJobStatus next, DateTime? nowUtc = null)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (!CanTransition(job.Status, next))
        {
            throw new InvalidOperationException($"Không thể chuyển job từ {job.Status} sang {next}.");
        }

        if (job.Status == next)
        {
            return;
        }

        var now = nowUtc ?? DateTime.UtcNow;
        job.Status = next;
        job.UpdatedAtUtc = now;
        if (next == LocalJobStatus.Running)
        {
            job.StartedAtUtc ??= now;
            job.AttemptCount++;
            job.ErrorCode = null;
            job.ErrorMessage = null;
        }

        if (next is LocalJobStatus.Completed or LocalJobStatus.Failed or LocalJobStatus.Cancelled)
        {
            job.CompletedAtUtc = now;
        }

        if (next == LocalJobStatus.Pending)
        {
            job.CompletedAtUtc = null;
            job.ErrorCode = null;
            job.ErrorMessage = null;
        }
    }
}
