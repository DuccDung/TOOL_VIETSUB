using System.Collections.Concurrent;
using TOOL_VIETSUB_APP.Api;
using TOOL_VIETSUB_APP.Core;
using TOOL_VIETSUB_APP.Jobs;

namespace TOOL_VIETSUB_APP.Usage;

public sealed class QuotaProtectedJobService
{
    private readonly IDesktopQuotaGateway _quota;
    private readonly PersistentJobManager _jobs;
    private readonly ProjectWorkspaceService _workspace;
    private readonly ConcurrentDictionary<Guid, Task> _settlementTasks = new();

    public QuotaProtectedJobService(
        IDesktopQuotaGateway quota,
        PersistentJobManager jobs,
        ProjectWorkspaceService workspace)
    {
        _quota = quota;
        _jobs = jobs;
        _workspace = workspace;
    }

    public async Task<LocalJob> StartAsync(
        ProjectManifest project,
        string jobType,
        string featureCode,
        IReadOnlyList<string> steps,
        decimal estimatedMinutes,
        ILocalJobExecutor executor,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? parameters = null)
    {
        if (estimatedMinutes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(estimatedMinutes));
        }

        var jobId = Guid.NewGuid();
        var reservation = await _quota.ReserveAsync(
            new ReserveQuotaApiRequest(
                jobId,
                project.ProjectId,
                jobId,
                featureCode,
                estimatedMinutes),
            cancellationToken);
        LocalJob? job = null;
        try
        {
            job = await _jobs.EnqueueAsync(
                project,
                jobType,
                steps,
                cancellationToken,
                jobId);
            job.QuotaReservationId = reservation.ReservationId;
            job.QuotaReservationExpiresAtUtc = reservation.ExpiresAtUtc;
            job.QuotaEstimatedMinutes = estimatedMinutes;
            job.QuotaSettlementStatus = "HELD";
            if (parameters is not null)
            {
                foreach (var (key, value) in parameters)
                {
                    job.Parameters[key] = value;
                }
            }
            await _workspace.SaveAsync(project, cancellationToken);
            await _jobs.StartAsync(project, job.JobId, executor, cancellationToken);
            StartSettlementMonitor(project, job, estimatedMinutes);
            return job;
        }
        catch
        {
            try
            {
                await _quota.ReleaseAsync(reservation.ReservationId, CancellationToken.None);
            }
            catch
            {
                if (job is not null)
                {
                    job.QuotaSettlementStatus = "PENDING_RELEASE";
                }
            }

            throw;
        }
    }

    public void MonitorResumedJob(ProjectManifest project, LocalJob job, decimal actualMinutes)
    {
        if (job.QuotaReservationId is not null)
        {
            StartSettlementMonitor(project, job, actualMinutes);
        }
    }

    public async Task<LocalJob> RestartAsync(
        ProjectManifest project,
        LocalJob job,
        string featureCode,
        decimal estimatedMinutes,
        ILocalJobExecutor executor,
        CancellationToken cancellationToken)
    {
        if (job.QuotaReservationId is null)
        {
            await RestartExistingJobAsync(project, job, executor, cancellationToken);
            return job;
        }

        // The settlement monitor may still be releasing a just-failed job.
        // Waiting avoids reusing a reservation while its state is changing.
        await WaitForSettlementAsync(job.JobId, cancellationToken);
        if (job.QuotaSettlementStatus is "PENDING_COMMIT" or "PENDING_RELEASE")
        {
            await ReconcilePendingSettlementsAsync(project, cancellationToken);
        }

        if (job.QuotaSettlementStatus is "PENDING_COMMIT" or "PENDING_RELEASE")
        {
            throw new InvalidOperationException(
                "ChÆ°a thá»ƒ thá»­ láº¡i vÃ¬ giao dá»‹ch háº¡n má»©c trÆ°á»›c Ä‘Ã³ chÆ°a Ä‘á»“ng bá»™ vá»›i Server.");
        }

        var reservationIsUsable = job.QuotaSettlementStatus == "HELD"
            && job.QuotaReservationExpiresAtUtc is DateTime expiresAtUtc
            && expiresAtUtc > DateTime.UtcNow.AddMinutes(1);
        if (reservationIsUsable)
        {
            await RestartExistingJobAsync(project, job, executor, cancellationToken);
            StartSettlementMonitor(project, job, estimatedMinutes);
            return job;
        }

        if (job.Status is LocalJobStatus.Paused or LocalJobStatus.Interrupted)
        {
            await _jobs.CancelAsync(project, job.JobId, cancellationToken);
        }

        if (job.QuotaSettlementStatus == "HELD")
        {
            job.QuotaSettlementStatus = "EXPIRED";
            await _workspace.SaveAsync(project, cancellationToken);
        }

        return await StartAsync(
            project,
            job.JobType,
            featureCode,
            job.Steps.Select(step => step.Code).ToArray(),
            estimatedMinutes,
            executor,
            cancellationToken,
            new Dictionary<string, string>(job.Parameters, StringComparer.OrdinalIgnoreCase));
    }

    public Task WaitForSettlementAsync(Guid jobId, CancellationToken cancellationToken = default) =>
        _settlementTasks.TryGetValue(jobId, out var task)
            ? task.WaitAsync(cancellationToken)
            : Task.CompletedTask;

    public async Task ReconcilePendingSettlementsAsync(
        ProjectManifest project,
        CancellationToken cancellationToken)
    {
        foreach (var job in project.Jobs.Where(item =>
            item.QuotaReservationId is not null
            && item.QuotaSettlementStatus is "PENDING_COMMIT" or "PENDING_RELEASE"))
        {
            try
            {
                if (job.QuotaSettlementStatus == "PENDING_COMMIT")
                {
                    var actual = job.QuotaEstimatedMinutes
                        ?? throw new InvalidDataException("Job thiếu thời lượng quota.");
                    await _quota.CommitAsync(job.QuotaReservationId!.Value, actual, cancellationToken);
                    job.QuotaSettlementStatus = "COMMITTED";
                }
                else
                {
                    await _quota.ReleaseAsync(job.QuotaReservationId!.Value, cancellationToken);
                    job.QuotaSettlementStatus = "RELEASED";
                }

                job.QuotaSettlementError = null;
                await _workspace.SaveAsync(project, cancellationToken);
            }
            catch (Exception exception)
            {
                job.QuotaSettlementError = exception.Message;
                await _workspace.SaveAsync(project, cancellationToken);
            }
        }
    }

    private void StartSettlementMonitor(ProjectManifest project, LocalJob job, decimal actualMinutes)
    {
        var task = MonitorAndSettleAsync(project, job, actualMinutes);
        _settlementTasks[job.JobId] = task;
        _ = task.ContinueWith(
            _ => RemoveSettlementTask(job.JobId),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void RemoveSettlementTask(Guid jobId)
    {
        _settlementTasks.TryRemove(jobId, out var ignored);
        _ = ignored;
    }

    private async Task RestartExistingJobAsync(
        ProjectManifest project,
        LocalJob job,
        ILocalJobExecutor executor,
        CancellationToken cancellationToken)
    {
        if (job.Status == LocalJobStatus.Paused)
        {
            await _jobs.StartAsync(project, job.JobId, executor, cancellationToken);
            return;
        }

        await _jobs.RetryAsync(project, job.JobId, executor, cancellationToken);
    }

    private async Task MonitorAndSettleAsync(
        ProjectManifest project,
        LocalJob job,
        decimal actualMinutes)
    {
        await _jobs.WaitForCompletionAsync(job.JobId);
        if (job.QuotaReservationId is not Guid reservationId)
        {
            return;
        }

        try
        {
            if (job.Status == LocalJobStatus.Completed)
            {
                var committed = Math.Min(
                    actualMinutes,
                    job.QuotaEstimatedMinutes ?? actualMinutes);
                await _quota.CommitAsync(reservationId, committed, CancellationToken.None);
                job.QuotaSettlementStatus = "COMMITTED";
            }
            else if (job.Status is LocalJobStatus.Failed or LocalJobStatus.Cancelled)
            {
                await _quota.ReleaseAsync(reservationId, CancellationToken.None);
                job.QuotaSettlementStatus = "RELEASED";
            }
            else
            {
                return;
            }

            job.QuotaSettlementError = null;
        }
        catch (Exception exception)
        {
            job.QuotaSettlementStatus = job.Status == LocalJobStatus.Completed
                ? "PENDING_COMMIT"
                : "PENDING_RELEASE";
            job.QuotaSettlementError = exception.Message;
        }

        await _workspace.SaveAsync(project);
    }
}
