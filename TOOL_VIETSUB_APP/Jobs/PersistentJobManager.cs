using System.Collections.Concurrent;
using System.Text.Json;
using TOOL_VIETSUB_APP.Core;

namespace TOOL_VIETSUB_APP.Jobs;

public sealed record JobProgressUpdate(
    string StepCode,
    double StepProgressPercent,
    double JobProgressPercent,
    string? Message = null);

public interface ILocalJobExecutor
{
    Task ExecuteAsync(
        LocalJob job,
        Func<JobProgressUpdate, ValueTask> reportProgress,
        CancellationToken cancellationToken);
}

public sealed class LocalJobException(string code, string message, bool retryable = true)
    : Exception(message)
{
    public string Code { get; } = code;

    public bool Retryable { get; } = retryable;
}

public sealed class PersistentJobManager : IAsyncDisposable
{
    private readonly ProjectWorkspaceService _workspace;
    private readonly AppPaths _paths;
    private readonly SemaphoreSlim _globalSlot;
    private readonly ConcurrentDictionary<Guid, ActiveJob> _activeJobs = new();
    private bool _disposed;

    public PersistentJobManager(
        ProjectWorkspaceService workspace,
        AppPaths paths,
        int maxParallelJobs = 1)
    {
        if (maxParallelJobs < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxParallelJobs));
        }

        _workspace = workspace;
        _paths = paths;
        _globalSlot = new SemaphoreSlim(maxParallelJobs, maxParallelJobs);
    }

    public event EventHandler<LocalJob>? JobChanged;

    public async Task<LocalJob> EnqueueAsync(
        ProjectManifest project,
        string jobType,
        IReadOnlyList<string> steps,
        CancellationToken cancellationToken = default,
        Guid? jobId = null)
    {
        ThrowIfDisposed();
        if (steps.Count == 0 || steps.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Job phải có ít nhất một bước hợp lệ.", nameof(steps));
        }

        if (project.Jobs.Any(item =>
            item.Status is LocalJobStatus.Pending or LocalJobStatus.Running or LocalJobStatus.Paused))
        {
            throw new InvalidOperationException("Một công việc cùng loại đang chờ hoặc đang chạy trong dự án.");
        }

        var job = new LocalJob
        {
            JobId = jobId ?? Guid.NewGuid(),
            JobType = jobType.Trim().ToUpperInvariant(),
            Steps = steps.Select(code => new LocalJobStep
            {
                Code = code.Trim().ToUpperInvariant(),
            }).ToList(),
        };
        project.Jobs.Add(job);
        project.Status = ProjectStates.Processing;
        await _workspace.SaveAsync(project, cancellationToken);
        await AppendEventAsync(project.ProjectId, job, "QUEUED", "Job đã được đưa vào hàng đợi.", cancellationToken);
        JobChanged?.Invoke(this, job);
        return job;
    }

    public async Task StartAsync(
        ProjectManifest project,
        Guid jobId,
        ILocalJobExecutor executor,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var job = FindJob(project, jobId);
        if (job.Status is not (LocalJobStatus.Pending or LocalJobStatus.Paused))
        {
            throw new InvalidOperationException("Chỉ có thể chạy job đang chờ hoặc tạm dừng.");
        }

        var active = new ActiveJob(CancellationTokenSource.CreateLinkedTokenSource(cancellationToken));
        if (!_activeJobs.TryAdd(jobId, active))
        {
            active.Dispose();
            throw new InvalidOperationException("Job đang được thực thi.");
        }

        JobStateMachine.Transition(job, LocalJobStatus.Running);
        await _workspace.SaveAsync(project, cancellationToken);
        await AppendEventAsync(project.ProjectId, job, "STARTED", "Bắt đầu thực thi job.", cancellationToken);
        JobChanged?.Invoke(this, job);
        active.ExecutionTask = RunAsync(project, job, executor, active);
    }

    public async Task PauseAsync(
        ProjectManifest project,
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        var job = FindJob(project, jobId);
        if (job.Status != LocalJobStatus.Running || !_activeJobs.TryGetValue(jobId, out var active))
        {
            throw new InvalidOperationException("Job không ở trạng thái có thể tạm dừng.");
        }

        active.RequestedTerminalState = LocalJobStatus.Paused;
        active.Cancellation.Cancel();
        await active.ExecutionTask.WaitAsync(cancellationToken);
    }

    public async Task CancelAsync(
        ProjectManifest project,
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        var job = FindJob(project, jobId);
        if (job.Status is LocalJobStatus.Completed or LocalJobStatus.Cancelled)
        {
            return;
        }

        if (_activeJobs.TryGetValue(jobId, out var active))
        {
            active.RequestedTerminalState = LocalJobStatus.Cancelled;
            active.Cancellation.Cancel();
            await active.ExecutionTask.WaitAsync(cancellationToken);
            return;
        }

        JobStateMachine.Transition(job, LocalJobStatus.Cancelled);
        await _workspace.SaveAsync(project, cancellationToken);
        await AppendEventAsync(project.ProjectId, job, "CANCELLED", "Job đã bị hủy.", cancellationToken);
        JobChanged?.Invoke(this, job);
    }

    public async Task RetryAsync(
        ProjectManifest project,
        Guid jobId,
        ILocalJobExecutor executor,
        CancellationToken cancellationToken = default)
    {
        var job = FindJob(project, jobId);
        if (job.Status is not (LocalJobStatus.Failed or LocalJobStatus.Interrupted))
        {
            throw new InvalidOperationException("Chỉ có thể thử lại job lỗi hoặc bị gián đoạn.");
        }

        if (job.AttemptCount >= job.MaxAttempts)
        {
            throw new InvalidOperationException("Job đã đạt số lần thử tối đa.");
        }

        JobStateMachine.Transition(job, LocalJobStatus.Pending);
        foreach (var step in job.Steps.Where(item => item.Status != LocalJobStatus.Completed))
        {
            step.Status = LocalJobStatus.Pending;
            step.ErrorCode = null;
            step.ErrorMessage = null;
        }

        await _workspace.SaveAsync(project, cancellationToken);
        await StartAsync(project, jobId, executor, cancellationToken);
    }

    public async Task<int> RestoreInterruptedJobsAsync(
        ProjectManifest project,
        CancellationToken cancellationToken = default)
    {
        var restored = 0;
        foreach (var job in project.Jobs.Where(item => item.Status == LocalJobStatus.Running))
        {
            JobStateMachine.Transition(job, LocalJobStatus.Interrupted);
            job.ErrorCode = "JOB_INTERRUPTED";
            job.ErrorMessage = "App đã đóng trước khi công việc hoàn thành.";
            foreach (var step in job.Steps.Where(item => item.Status == LocalJobStatus.Running))
            {
                step.Status = LocalJobStatus.Interrupted;
                step.ErrorCode = "JOB_INTERRUPTED";
                step.ErrorMessage = job.ErrorMessage;
            }
            restored++;
        }

        if (restored > 0)
        {
            await _workspace.SaveAsync(project, cancellationToken);
        }

        return restored;
    }

    public Task WaitForCompletionAsync(Guid jobId, CancellationToken cancellationToken = default) =>
        _activeJobs.TryGetValue(jobId, out var active)
            ? active.ExecutionTask.WaitAsync(cancellationToken)
            : Task.CompletedTask;

    private async Task RunAsync(
        ProjectManifest project,
        LocalJob job,
        ILocalJobExecutor executor,
        ActiveJob active)
    {
        var acquiredSlot = false;
        try
        {
            await _globalSlot.WaitAsync(active.Cancellation.Token);
            acquiredSlot = true;
            await executor.ExecuteAsync(
                job,
                update => ReportProgressAsync(project, job, update, active.Cancellation.Token),
                active.Cancellation.Token);
            JobStateMachine.Transition(job, LocalJobStatus.Completed);
            job.ProgressPercent = 100;
            foreach (var step in job.Steps)
            {
                if (step.Status is LocalJobStatus.Pending or LocalJobStatus.Running)
                {
                    step.Status = LocalJobStatus.Completed;
                    step.ProgressPercent = 100;
                }
            }

            project.Status = ProjectStates.Completed;
            await _workspace.SaveAsync(project);
            await AppendEventAsync(project.ProjectId, job, "COMPLETED", "Job hoàn thành.", CancellationToken.None);
        }
        catch (OperationCanceledException) when (active.Cancellation.IsCancellationRequested)
        {
            var target = active.RequestedTerminalState ?? LocalJobStatus.Interrupted;
            JobStateMachine.Transition(job, target);
            foreach (var step in job.Steps.Where(item => item.Status == LocalJobStatus.Running))
            {
                step.Status = target;
            }
            project.Status = target == LocalJobStatus.Cancelled ? ProjectStates.Ready : ProjectStates.Processing;
            await _workspace.SaveAsync(project);
            await AppendEventAsync(project.ProjectId, job, target.ToString().ToUpperInvariant(),
                target == LocalJobStatus.Paused ? "Job đã tạm dừng tại checkpoint an toàn." : "Job đã bị hủy hoặc gián đoạn.",
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            JobStateMachine.Transition(job, LocalJobStatus.Failed);
            job.ErrorCode = exception is LocalJobException jobException ? jobException.Code : "JOB_UNEXPECTED_ERROR";
            job.ErrorMessage = exception.Message;
            var failedStep = job.Steps.SingleOrDefault(item => item.Code == job.CurrentStep)
                ?? job.Steps.FirstOrDefault(item => item.Status == LocalJobStatus.Running);
            if (failedStep is not null)
            {
                failedStep.Status = LocalJobStatus.Failed;
                failedStep.ErrorCode = job.ErrorCode;
                failedStep.ErrorMessage = job.ErrorMessage;
            }
            project.Status = ProjectStates.Failed;
            await _workspace.SaveAsync(project);
            await AppendEventAsync(project.ProjectId, job, "FAILED", exception.Message, CancellationToken.None);
        }
        finally
        {
            if (acquiredSlot)
            {
                _globalSlot.Release();
            }
            _activeJobs.TryRemove(job.JobId, out _);
            active.Dispose();
            JobChanged?.Invoke(this, job);
        }
    }

    private async ValueTask ReportProgressAsync(
        ProjectManifest project,
        LocalJob job,
        JobProgressUpdate update,
        CancellationToken cancellationToken)
    {
        var step = job.Steps.SingleOrDefault(item => item.Code == update.StepCode)
            ?? throw new InvalidOperationException($"Job không có bước {update.StepCode}.");
        if (step.Status is LocalJobStatus.Pending or LocalJobStatus.Paused or LocalJobStatus.Interrupted)
        {
            step.Status = LocalJobStatus.Running;
            step.AttemptCount++;
        }

        step.ProgressPercent = Math.Clamp(update.StepProgressPercent, 0, 100);
        if (step.ProgressPercent >= 100)
        {
            step.Status = LocalJobStatus.Completed;
        }

        job.CurrentStep = update.StepCode;
        job.ProgressPercent = Math.Clamp(update.JobProgressPercent, 0, 100);
        job.UpdatedAtUtc = DateTime.UtcNow;
        await _workspace.SaveAsync(project, cancellationToken);
        if (!string.IsNullOrWhiteSpace(update.Message))
        {
            await AppendEventAsync(project.ProjectId, job, "PROGRESS", update.Message, cancellationToken);
        }

        JobChanged?.Invoke(this, job);
    }

    private async Task AppendEventAsync(
        Guid projectId,
        LocalJob job,
        string eventType,
        string message,
        CancellationToken cancellationToken)
    {
        var path = _paths.GetProjectPath(projectId, "logs", $"job-{job.JobId:N}.jsonl");
        var line = JsonSerializer.Serialize(new
        {
            timestampUtc = DateTime.UtcNow,
            jobId = job.JobId,
            eventType,
            job.Status,
            job.ProgressPercent,
            message = message.Length <= 2000 ? message : message[..2000],
        });
        await File.AppendAllTextAsync(path, line + Environment.NewLine, cancellationToken);
    }

    private static LocalJob FindJob(ProjectManifest project, Guid jobId) =>
        project.Jobs.SingleOrDefault(item => item.JobId == jobId)
        ?? throw new KeyNotFoundException("Không tìm thấy job trong dự án.");

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var active = _activeJobs.Values.ToArray();
        foreach (var item in active)
        {
            item.RequestedTerminalState = LocalJobStatus.Interrupted;
            item.Cancellation.Cancel();
        }

        await Task.WhenAll(active.Select(item => item.ExecutionTask));
        _globalSlot.Dispose();
    }

    private sealed class ActiveJob(CancellationTokenSource cancellation) : IDisposable
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;

        public LocalJobStatus? RequestedTerminalState { get; set; }

        public Task ExecutionTask { get; set; } = Task.CompletedTask;

        public void Dispose() => Cancellation.Dispose();
    }
}
