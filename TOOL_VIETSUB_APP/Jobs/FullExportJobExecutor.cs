using TOOL_VIETSUB_APP.Core;

namespace TOOL_VIETSUB_APP.Jobs;

public sealed class FullExportJobExecutor(
    ILocalJobExecutor timelineExecutor,
    ILocalJobExecutor exportExecutor) : ILocalJobExecutor
{
    public async Task ExecuteAsync(
        LocalJob job,
        Func<JobProgressUpdate, ValueTask> reportProgress,
        CancellationToken cancellationToken)
    {
        await timelineExecutor.ExecuteAsync(
            job,
            update => reportProgress(update with
            {
                JobProgressPercent = update.StepProgressPercent * 0.4,
            }),
            cancellationToken);

        await exportExecutor.ExecuteAsync(
            job,
            update => reportProgress(update with
            {
                JobProgressPercent = 40 + update.StepProgressPercent * 0.6,
            }),
            cancellationToken);
    }
}
