using TOOL_VIETSUB_APP.Core;
using TOOL_VIETSUB_APP.Jobs;

namespace TOOL_VIETSUB_APP.Tests;

public sealed class PersistentJobManagerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TOOL_VIETSUB_TESTS", Guid.NewGuid().ToString("N"));

    [Fact]
    public void StateMachine_RejectsInvalidTransition()
    {
        var job = new LocalJob { Status = LocalJobStatus.Completed };

        Assert.Throws<InvalidOperationException>(() =>
            JobStateMachine.Transition(job, LocalJobStatus.Running));
    }

    [Fact]
    public async Task Start_DoesNotRunSameJobTwiceAndCanCancel()
    {
        var paths = new AppPaths(_root);
        var workspace = new ProjectWorkspaceService(paths);
        var manifest = await workspace.CreateAsync(Guid.NewGuid(), "Job test");
        await using var manager = new PersistentJobManager(workspace, paths);
        var executor = new BlockingExecutor();
        var job = await manager.EnqueueAsync(manifest, "FULL_PIPELINE", ["EXTRACT_AUDIO"]);

        await manager.StartAsync(manifest, job.JobId, executor);
        await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.StartAsync(manifest, job.JobId, executor));
        await manager.CancelAsync(manifest, job.JobId);

        Assert.Equal(LocalJobStatus.Cancelled, job.Status);
        Assert.False(File.Exists(paths.GetProjectPath(manifest.ProjectId, "temp", "unfinished.tmp")));
    }

    [Fact]
    public async Task Restore_ChangesRunningJobToInterrupted()
    {
        var paths = new AppPaths(_root);
        var workspace = new ProjectWorkspaceService(paths);
        var manifest = await workspace.CreateAsync(Guid.NewGuid(), "Recovery job");
        var job = new LocalJob { Status = LocalJobStatus.Running };
        manifest.Jobs.Add(job);
        await workspace.SaveAsync(manifest);
        await using var manager = new PersistentJobManager(workspace, paths);

        var count = await manager.RestoreInterruptedJobsAsync(manifest);

        Assert.Equal(1, count);
        Assert.Equal(LocalJobStatus.Interrupted, job.Status);
        Assert.Equal("JOB_INTERRUPTED", job.ErrorCode);
    }

    [Fact]
    public async Task Enqueue_RejectsASecondActiveJobOfAnotherType()
    {
        var paths = new AppPaths(_root);
        var workspace = new ProjectWorkspaceService(paths);
        var manifest = await workspace.CreateAsync(Guid.NewGuid(), "One active job");
        await using var manager = new PersistentJobManager(workspace, paths);
        _ = await manager.EnqueueAsync(manifest, "TRANSCRIBE_LOCAL", ["TRANSCRIBE"]);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.EnqueueAsync(manifest, "OCR_LOCAL", ["OCR_RECOGNIZE"]));
    }

    [Fact]
    public async Task PauseAndResume_RerunsSafeStepAndCompletes()
    {
        var paths = new AppPaths(_root);
        var workspace = new ProjectWorkspaceService(paths);
        var manifest = await workspace.CreateAsync(Guid.NewGuid(), "Pause resume");
        await using var manager = new PersistentJobManager(workspace, paths);
        var blocking = new BlockingExecutor();
        var job = await manager.EnqueueAsync(manifest, "EXTRACT_AUDIO", ["EXTRACT_AUDIO"]);
        await manager.StartAsync(manifest, job.JobId, blocking);
        await blocking.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await manager.PauseAsync(manifest, job.JobId);
        Assert.Equal(LocalJobStatus.Paused, job.Status);

        await manager.StartAsync(manifest, job.JobId, new CompletingExecutor());
        await manager.WaitForCompletionAsync(job.JobId).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(LocalJobStatus.Completed, job.Status);
        Assert.Equal(100, job.ProgressPercent);
        Assert.Equal(2, job.AttemptCount);
    }

    [Fact]
    public async Task Retry_AllowsFailureOnlyUntilMaxAttempts()
    {
        var paths = new AppPaths(_root);
        var workspace = new ProjectWorkspaceService(paths);
        var manifest = await workspace.CreateAsync(Guid.NewGuid(), "Retry job");
        await using var manager = new PersistentJobManager(workspace, paths);
        var job = await manager.EnqueueAsync(manifest, "EXTRACT_AUDIO", ["EXTRACT_AUDIO"]);

        await manager.StartAsync(manifest, job.JobId, new FailingExecutor());
        await manager.WaitForCompletionAsync(job.JobId).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(LocalJobStatus.Failed, job.Status);
        Assert.Equal("TEST_RETRYABLE", job.ErrorCode);

        await manager.RetryAsync(manifest, job.JobId, new CompletingExecutor());
        await manager.WaitForCompletionAsync(job.JobId).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(LocalJobStatus.Completed, job.Status);
        Assert.Equal(2, job.AttemptCount);
    }

    private sealed class BlockingExecutor : ILocalJobExecutor
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task ExecuteAsync(
            LocalJob job,
            Func<JobProgressUpdate, ValueTask> reportProgress,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await reportProgress(new JobProgressUpdate("EXTRACT_AUDIO", 1, 1));
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class CompletingExecutor : ILocalJobExecutor
    {
        public async Task ExecuteAsync(
            LocalJob job,
            Func<JobProgressUpdate, ValueTask> reportProgress,
            CancellationToken cancellationToken)
        {
            await reportProgress(new JobProgressUpdate("EXTRACT_AUDIO", 100, 100));
        }
    }

    private sealed class FailingExecutor : ILocalJobExecutor
    {
        public Task ExecuteAsync(
            LocalJob job,
            Func<JobProgressUpdate, ValueTask> reportProgress,
            CancellationToken cancellationToken) =>
            throw new LocalJobException("TEST_RETRYABLE", "Lỗi thử nghiệm có thể chạy lại.");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
