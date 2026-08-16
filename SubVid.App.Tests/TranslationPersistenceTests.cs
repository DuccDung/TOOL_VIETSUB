using System.Text;
using SubVid.App.Api;
using SubVid.App.Core;
using SubVid.App.Subtitles;

namespace SubVid.App.Tests;

public sealed class TranslationPersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "SUBVID_TESTS",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void CredentialStore_EncryptsAndDeletesProviderKey()
    {
        var paths = new AppPaths(_root);
        var store = new ProtectedTranslationCredentialStore(paths);

        store.SaveKey(TranslationProviders.OpenAi, "secret-openai-key-value");

        Assert.True(store.HasKey(TranslationProviders.OpenAi));
        Assert.Equal("secret-openai-key-value", store.GetKey(TranslationProviders.OpenAi));
        var raw = File.ReadAllBytes(Path.Combine(paths.RootDirectory, "translation.credentials"));
        Assert.DoesNotContain("secret-openai-key-value", Encoding.UTF8.GetString(raw));

        store.DeleteKey(TranslationProviders.OpenAi);
        Assert.False(store.HasKey(TranslationProviders.OpenAi));
    }

    [Fact]
    public void CredentialStore_SeparatesDeepSeekFromOtherProviderKeys()
    {
        var paths = new AppPaths(_root);
        var store = new ProtectedTranslationCredentialStore(paths);

        store.SaveKey(TranslationProviders.OpenAi, "secret-openai-key-value");
        store.SaveKey(TranslationProviders.DeepSeek, "secret-deepseek-key-value");

        Assert.Equal("secret-openai-key-value", store.GetKey(TranslationProviders.OpenAi));
        Assert.Equal("secret-deepseek-key-value", store.GetKey(TranslationProviders.DeepSeek));
        var raw = File.ReadAllBytes(Path.Combine(paths.RootDirectory, "translation.credentials"));
        Assert.DoesNotContain("secret-deepseek-key-value", Encoding.UTF8.GetString(raw));

        store.DeleteKey(TranslationProviders.DeepSeek);
        Assert.False(store.HasKey(TranslationProviders.DeepSeek));
        Assert.True(store.HasKey(TranslationProviders.OpenAi));
    }

    [Fact]
    public void CredentialStore_EncryptsAndSeparatesGroqKey()
    {
        var paths = new AppPaths(_root);
        var store = new ProtectedTranslationCredentialStore(paths);

        store.SaveKey(TranslationProviders.OpenAi, "secret-openai-key-value");
        store.SaveKey(TranslationProviders.Groq, "secret-groq-key-value");

        Assert.Equal("secret-groq-key-value", store.GetKey(TranslationProviders.Groq));
        var raw = File.ReadAllBytes(Path.Combine(paths.RootDirectory, "translation.credentials"));
        Assert.DoesNotContain("secret-groq-key-value", Encoding.UTF8.GetString(raw));

        store.DeleteKey(TranslationProviders.Groq);
        Assert.False(store.HasKey(TranslationProviders.Groq));
        Assert.True(store.HasKey(TranslationProviders.OpenAi));
    }

    [Fact]
    public async Task ManualSubtitleEdit_UpdatesTranslationMemoryAndKeepsLatestVersion()
    {
        var paths = new AppPaths(_root);
        var workspace = new ProjectWorkspaceService(paths);
        var project = await workspace.CreateAsync(Guid.NewGuid(), "Translation memory");
        project.SourceLanguageCode = "en";
        var cue = new SubtitleCue
        {
            OriginalText = "Master",
            StartMilliseconds = 0,
            EndMilliseconds = 1000,
        };
        project.SubtitleTracks.Add(new SubtitleDocument { LanguageCode = "en", Cues = [cue] });
        var service = new SrtService(paths, workspace);

        await service.UpdateCueAsync(project, cue.CueId, "Master", "Sư phụ", CancellationToken.None);
        await service.UpdateCueAsync(project, cue.CueId, "Master", "Thầy", CancellationToken.None);

        var memory = Assert.Single(project.TranslationMemory);
        Assert.Equal("Master", memory.SourceText);
        Assert.Equal("Thầy", memory.TranslatedText);
        Assert.True(cue.TranslationLocked);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
