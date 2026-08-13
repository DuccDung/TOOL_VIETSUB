using TOOL_VIETSUB_APP.Core;

namespace TOOL_VIETSUB_APP.Jobs;

public sealed class VoiceGenerationJobExecutor(
    ILocalJobExecutor synthesisExecutor,
    ILocalJobExecutor timelineExecutor) : ILocalJobExecutor
{
    public async Task ExecuteAsync(
        LocalJob job,
        Func<JobProgressUpdate, ValueTask> reportProgress,
        CancellationToken cancellationToken)
    {
        await synthesisExecutor.ExecuteAsync(
            job,
            update => reportProgress(update with
            {
                JobProgressPercent = update.StepProgressPercent * 0.85,
            }),
            cancellationToken);

        await timelineExecutor.ExecuteAsync(
            job,
            update => reportProgress(update with
            {
                JobProgressPercent = 85 + update.StepProgressPercent * 0.15,
            }),
            cancellationToken);
    }
}
