using SubVid.App.Core;

namespace SubVid.App.LocalAi;

public static class LocalVoiceEngines
{
    public const string Piper = "piper";
    public const string VieNeu = "vieneu";
    public const string Fpt = "fpt";

    public static bool IsCloud(string? engine) =>
        string.Equals(engine, Fpt, StringComparison.OrdinalIgnoreCase);
}

public static class LocalVoiceInstallStates
{
    public const string Online = "ONLINE";
    public const string Ready = "READY";
    public const string Missing = "MISSING";
    public const string RepairRequired = "REPAIR_REQUIRED";
}

public sealed record LocalVoiceDefinition(
    string VoiceId,
    string Engine,
    string ProviderVoiceId,
    string DisplayName,
    string Gender,
    string Region,
    string Style,
    string ModelId,
    string ModelVersion,
    string License)
{
    public bool IsCloud => LocalVoiceEngines.IsCloud(Engine);

    public bool RequiresInstall => !IsCloud;
}

public static class LocalVoiceCatalog
{
    public const string DefaultVoiceId = "piper:vi-vn-vais1000";
    public const string VieNeuModelId = "vieneu-v3-turbo";
    public const string VieNeuModelVersion = "3.2.5";
    public const string FptModelId = "fpt-ai-tts";
    public const string FptModelVersion = "v5";

    public static readonly IReadOnlyList<LocalVoiceDefinition> All =
    [
        new(DefaultVoiceId, LocalVoiceEngines.Piper, "vi_VN-vais1000-medium", "VAIS-1000", "Nữ", "Việt Nam", "Tự nhiên", PiperLocalVoiceSynthesizer.ModelId, "medium", "CC BY 4.0"),
        VieNeu("minh-duc", "Minh Đức", "Nam", "Bắc", "Tin tức"),
        VieNeu("pham-tuyen", "Phạm Tuyên", "Nam", "Bắc", "Tự nhiên"),
        VieNeu("thai-son", "Thái Sơn", "Nam", "Nam", "Kể chuyện"),
        VieNeu("xuan-vinh", "Xuân Vĩnh", "Nam", "Nam", "Tự nhiên"),
        VieNeu("thanh-binh", "Thanh Bình", "Nam", "Bắc", "Kể chuyện"),
        VieNeu("truc-ly", "Trúc Ly", "Nữ", "Bắc", "Tự nhiên"),
        VieNeu("ngoc-linh", "Ngọc Linh", "Nữ", "Bắc", "Kể chuyện"),
        VieNeu("doan-trang", "Đoan Trang", "Nữ", "Bắc", "Tự nhiên"),
        VieNeu("mai-anh", "Mai Anh", "Nữ", "Bắc", "Tin tức"),
        VieNeu("thuc-doan", "Thục Đoan", "Nữ", "Nam", "Kể chuyện"),
        VieNeu("minh-triet", "Minh Triết", "Nam", "Nam", "Tin tức"),
        VieNeu("thuy-dung", "Thùy Dung", "Nữ", "Nam", "Tin tức"),
        VieNeu("quang-son", "Quang Sơn", "Nam", "Trung", "Tự nhiên"),
        VieNeu("ngoc-tran", "Ngọc Trân", "Nữ", "Trung", "Tự nhiên"),
        Fpt("banmai", "Ban Mai", "Nữ", "Bắc"),
        Fpt("lannhi", "Lan Nhi", "Nữ", "Nam"),
        Fpt("leminh", "Lê Minh", "Nam", "Bắc"),
        Fpt("myan", "Mỹ An", "Nữ", "Trung"),
        Fpt("thuminh", "Thu Minh", "Nữ", "Bắc"),
        Fpt("giahuy", "Gia Huy", "Nam", "Trung"),
        Fpt("linhsan", "Linh San", "Nữ", "Nam"),
    ];

    public static LocalVoiceDefinition Resolve(string? voiceId) =>
        Find(voiceId) ?? All[0];

    public static LocalVoiceDefinition? Find(string? voiceId) =>
        string.IsNullOrWhiteSpace(voiceId)
            ? null
            : All.FirstOrDefault(voice => string.Equals(
                voice.VoiceId,
                voiceId.Trim(),
                StringComparison.OrdinalIgnoreCase));

    public static LocalVoiceDefinition Resolve(ProjectManifest project, SubtitleCue cue)
    {
        if (Find(cue.VoiceId) is { } cueVoice)
        {
            return cueVoice;
        }

        project.Settings.SpeakerVoiceIds ??= new(StringComparer.OrdinalIgnoreCase);
        var speakerVoiceId = !string.IsNullOrWhiteSpace(cue.Speaker)
            ? project.Settings.SpeakerVoiceIds
                .FirstOrDefault(mapping => string.Equals(
                    mapping.Key,
                    cue.Speaker.Trim(),
                    StringComparison.OrdinalIgnoreCase))
                .Value
            : null;
        if (Find(speakerVoiceId) is { } speakerVoice)
        {
            return speakerVoice;
        }

        return Resolve(project.Settings.VoiceId);
    }

    private static LocalVoiceDefinition VieNeu(
        string id,
        string displayName,
        string gender,
        string region,
        string style) =>
        new(
            $"vieneu:{id}",
            LocalVoiceEngines.VieNeu,
            displayName,
            displayName,
            gender,
            region,
            style,
            VieNeuModelId,
            VieNeuModelVersion,
            "Apache-2.0");

    private static LocalVoiceDefinition Fpt(
        string id,
        string displayName,
        string gender,
        string region) =>
        new(
            $"fpt:{id}",
            LocalVoiceEngines.Fpt,
            id,
            displayName,
            gender,
            region,
            "Tự nhiên",
            FptModelId,
            FptModelVersion,
            "FPT.AI Terms");
}
