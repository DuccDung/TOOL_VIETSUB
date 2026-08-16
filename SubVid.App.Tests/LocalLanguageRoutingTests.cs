using SubVid.App.Core;
using SubVid.App.LocalAi;

namespace SubVid.App.Tests;

public sealed class LocalLanguageRoutingTests
{
    [Theory]
    [InlineData("zh", "zh")]
    [InlineData("zh-CN", "zh")]
    [InlineData("zh_Hant", "zh")]
    [InlineData("cmn", "zh")]
    [InlineData("en-US", "en")]
    [InlineData("auto", null)]
    [InlineData("und", null)]
    public void NormalizeSource_MapsSupportedLanguageVariants(string input, string? expected)
    {
        Assert.Equal(expected, LocalLanguageCodes.NormalizeSource(input));
    }

    [Fact]
    public void GetModelId_SelectsDirectChineseVietnameseModel()
    {
        Assert.Equal(
            OpusMtChineseVietnameseTranslator.ModelId,
            LocalTranslatorFactory.GetModelId("zh-CN"));
        Assert.Equal(ArgosLocalTranslator.ModelId, LocalTranslatorFactory.GetModelId("en"));
    }

    [Fact]
    public void ResolveProjectSource_UsesDetectedTrackWhenProjectIsAutomatic()
    {
        var project = new ProjectManifest
        {
            SourceLanguageCode = null,
            SubtitleTracks =
            [
                new SubtitleDocument
                {
                    LanguageCode = "zh-Hans",
                    Cues = [new SubtitleCue { OriginalText = "你好" }],
                },
            ],
        };

        Assert.Equal("zh", LocalLanguageCodes.ResolveProjectSource(project));
    }
}
