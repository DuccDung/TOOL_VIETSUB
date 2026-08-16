using SubVid.App.Api;
using SubVid.App.Core;
using SubVid.App.Jobs;
using SubVid.App.Usage;

namespace SubVid.App.Tests;

public sealed class QuotaProtectedJobServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SUBVID_TESTS", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CompletedJob_ReservesBeforeStartAndCommitsOnce()
    {
        var paths = new AppPaths(_root);
        var workspace = new ProjectWorkspaceService(paths);
        var project = await workspace.CreateAsync(Guid.NewGuid(), "Quota protected");
        await using var jobs = new PersistentJobManager(workspace, paths);
        var gateway = new FakeQuotaGateway();
        var service = new QuotaProtectedJobService(gateway, jobs, workspace);

        var job = await service.StartAsync(
            project,
            "TEST_BILLED",
            "subtitle.transcribe",
            ["EXTRACT_AUDIO"],
            2.5m,
            new CompletingExecutor(),
            CancellationToken.None);
        await service.WaitForSettlementAsync(job.JobId).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(["reserve", "commit"], gateway.Operations);
        Assert.Equal("COMMITTED", job.QuotaSettlementStatus);
        Assert.Equal(2.5m, gateway.CommittedMinutes);
    }

    [Fact]
    public async Task FailedJob_ReleasesReservationWithoutCommit()
    {
        var paths = new AppPaths(_root);
        var workspace = new ProjectWorkspaceService(paths);
        var project = await workspace.CreateAsync(Guid.NewGuid(), "Quota failure");
        await using var jobs = new PersistentJobManager(workspace, paths);
        var gateway = new FakeQuotaGateway();
        var service = new QuotaProtectedJobService(gateway, jobs, workspace);

        var job = await service.StartAsync(
            project,
            "TEST_BILLED",
            "subtitle.transcribe",
            ["EXTRACT_AUDIO"],
            3m,
            new FailingExecutor(),
            CancellationToken.None);
        await service.WaitForSettlementAsync(job.JobId).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(["reserve", "release"], gateway.Operations);
        Assert.Equal("RELEASED", job.QuotaSettlementStatus);
    }

    [Fact]
    public async Task LostConnectionDuringCommit_IsPersistedAndReconciledOnNextOpen()
    {
        var paths = new AppPaths(_root);
        var workspace = new ProjectWorkspaceService(paths);
        var project = await workspace.CreateAsync(Guid.NewGuid(), "Quota offline recovery");
        await using var jobs = new PersistentJobManager(workspace, paths);
        var gateway = new FakeQuotaGateway(failFirstCommit: true);
        var service = new QuotaProtectedJobService(gateway, jobs, workspace);

        var job = await service.StartAsync(
            project,
            "TEST_BILLED",
            "subtitle.transcribe",
            ["EXTRACT_AUDIO"],
            4m,
            new CompletingExecutor(),
            CancellationToken.None);
        await service.WaitForSettlementAsync(job.JobId).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("PENDING_COMMIT", job.QuotaSettlementStatus);
        await service.ReconcilePendingSettlementsAsync(project, CancellationToken.None);

        Assert.Equal("COMMITTED", job.QuotaSettlementStatus);
        Assert.Null(job.QuotaSettlementError);
        Assert.Equal(["reserve", "commit", "commit"], gateway.Operations);
    }

    [Fact]
    public async Task RetryAfterReleasedFailure_CreatesANewReservationAndKeepsParameters()
    {
        var paths = new AppPaths(_root);
        var workspace = new ProjectWorkspaceService(paths);
        var project = await workspace.CreateAsync(Guid.NewGuid(), "Quota retry");
        await using var jobs = new PersistentJobManager(workspace, paths);
        var gateway = new FakeQuotaGateway();
        var service = new QuotaProtectedJobService(gateway, jobs, workspace);

        var failed = await service.StartAsync(
            project,
            "TEST_BILLED",
            "subtitle.transcribe",
            ["EXTRACT_AUDIO"],
            3m,
            new FailingExecutor(),
            CancellationToken.None,
            new Dictionary<string, string> { ["destination"] = "output.mp4" });
        await service.WaitForSettlementAsync(failed.JobId).WaitAsync(TimeSpan.FromSeconds(2));

        var replacement = await service.RestartAsync(
            project,
            failed,
            "subtitle.transcribe",
            3m,
            new CompletingExecutor(),
            CancellationToken.None);
        await service.WaitForSettlementAsync(replacement.JobId).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.NotEqual(failed.JobId, replacement.JobId);
        Assert.Equal("output.mp4", replacement.Parameters["destination"]);
        Assert.Equal("COMMITTED", replacement.QuotaSettlementStatus);
        Assert.Equal(["reserve", "release", "reserve", "commit"], gateway.Operations);
    }

    private sealed class FakeQuotaGateway(bool failFirstCommit = false) : IDesktopQuotaGateway
    {
        private readonly Guid _reservationId = Guid.NewGuid();
        private bool _failNextCommit = failFirstCommit;

        public List<string> Operations { get; } = [];

        public decimal? CommittedMinutes { get; private set; }

        public Task<QuotaReservationApiResponse> ReserveAsync(
            ReserveQuotaApiRequest request,
            CancellationToken cancellationToken)
        {
            Operations.Add("reserve");
            return Task.FromResult(new QuotaReservationApiResponse(
                _reservationId,
                "HELD",
                request.EstimatedMinutes,
                null,
                DateTime.UtcNow.AddHours(2),
                100,
                false));
        }

        public Task<QuotaReservationApiResponse> CommitAsync(
            Guid reservationId,
            decimal actualMinutes,
            CancellationToken cancellationToken)
        {
            Operations.Add("commit");
            if (_failNextCommit)
            {
                _failNextCommit = false;
                throw new HttpRequestException("Simulated offline settlement.");
            }

            CommittedMinutes = actualMinutes;
            return Task.FromResult(new QuotaReservationApiResponse(
                reservationId, "COMMITTED", actualMinutes, actualMinutes,
                DateTime.UtcNow.AddHours(2), 100, false));
        }

        public Task<QuotaReservationApiResponse> ReleaseAsync(
            Guid reservationId,
            CancellationToken cancellationToken)
        {
            Operations.Add("release");
            return Task.FromResult(new QuotaReservationApiResponse(
                reservationId, "RELEASED", 1, null,
                DateTime.UtcNow.AddHours(2), 100, false));
        }
    }

    private sealed class CompletingExecutor : ILocalJobExecutor
    {
        public async Task ExecuteAsync(
            LocalJob job,
            Func<JobProgressUpdate, ValueTask> reportProgress,
            CancellationToken cancellationToken) =>
            await reportProgress(new JobProgressUpdate("EXTRACT_AUDIO", 100, 100));
    }

    private sealed class FailingExecutor : ILocalJobExecutor
    {
        public Task ExecuteAsync(
            LocalJob job,
            Func<JobProgressUpdate, ValueTask> reportProgress,
            CancellationToken cancellationToken) =>
            throw new LocalJobException("TEST_FAILURE", "Test failure");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
