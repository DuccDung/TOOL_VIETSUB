namespace SubVid.App.Core;

public static class VoiceTimelinePreviewState
{
    public const string TimelineRole = "VOICE_TIMELINE";

    public static void MarkStale(ProjectManifest project)
    {
        foreach (var timeline in project.AudioTracks.Where(item =>
            item.Role == TimelineRole))
        {
            timeline.IsStale = true;
        }
    }
}
