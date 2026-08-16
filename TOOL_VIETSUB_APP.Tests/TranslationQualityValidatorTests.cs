using TOOL_VIETSUB_APP.LocalAi;
using TOOL_VIETSUB_APP.Core;

namespace TOOL_VIETSUB_APP.Tests;

public sealed class TranslationQualityValidatorTests
{
    [Theory]
    [InlineData("这是小米", "Đây là So So So So So So So So So So So So")]
    [InlineData("首先，它观察整个绿植的结构。", "Đầu tiên nó quan sát toàn toàn toàn toàn toàn toàn toàn toàn")]
    [InlineData("它从窗户旁边绕过去。", "Nó đi vòng qua cửa sổ pha pha pha pha pha pha pha pha pha pha")]
    public void DetectsPathologicalRepetition(string source, string translation)
    {
        Assert.True(TranslationQualityValidator.LooksPathological(source, translation));
    }

    [Theory]
    [InlineData("这是小米", "Đây là Xiaomi.")]
    [InlineData("首先，它观察整个绿植的结构。", "Đầu tiên, nó quan sát toàn bộ cấu trúc của cây xanh.")]
    [InlineData("它从窗户旁边绕过去。", "Nó quyết định đi vòng qua bên cạnh cửa sổ.")]
    public void AcceptsNormalVietnamese(string source, string translation)
    {
        Assert.False(TranslationQualityValidator.LooksPathological(source, translation));
    }

    [Theory]
    [InlineData("\u5bf9\u5bf9\u5bf9\u5bf9", "\u0110\u00fang, \u0111\u00fang, \u0111\u00fang, \u0111\u00fang.")]
    [InlineData("\u5bf9\u5bf9\u5bf9\u5bf9", "\u0110\u00fang r\u1ed3i, \u0111\u00fang r\u1ed3i, \u0111\u00fang r\u1ed3i, \u0111\u00fang r\u1ed3i.")]
    [InlineData("\u5bf9\uff0c\u5bf9\uff0c\u5bf9\uff0c\u5bf9", "\u0110\u00fang, \u0111\u00fang, \u0111\u00fang, \u0111\u00fang.")]
    [InlineData("no no no no", "Kh\u00f4ng, kh\u00f4ng, kh\u00f4ng, kh\u00f4ng.")]
    [InlineData("\u597d\u597d\u597d", "\u0110\u01b0\u1ee3c, \u0111\u01b0\u1ee3c, \u0111\u01b0\u1ee3c.")]
    public void AcceptsIntentionalRepetitionPresentInSource(string source, string translation)
    {
        Assert.False(TranslationQualityValidator.LooksPathological(source, translation));
    }

    [Fact]
    public void RejectsOutputThatGreatlyExceedsIntentionalSourceRepetition()
    {
        Assert.True(TranslationQualityValidator.LooksPathological(
            "\u5bf9\u5bf9\u5bf9\u5bf9",
            "\u0110\u00fang \u0111\u00fang \u0111\u00fang \u0111\u00fang \u0111\u00fang \u0111\u00fang \u0111\u00fang \u0111\u00fang."));
    }

    [Fact]
    public void RejectsOutputWithoutEndToken()
    {
        var result = TranslationQualityValidator.Validate(
            "这是小米",
            "Đây là Xiaomi.",
            endedWithEos: false,
            generatedTokenCount: 12,
            maxGeneratedTokens: 64);

        Assert.False(result.IsValid);
        Assert.Equal("MISSING_EOS", result.Code);
    }

    [Fact]
    public void AssessCue_FlagsNumbersGlossaryAndReadingSpeedWithoutDestroyingUsableText()
    {
        var assessment = TranslationQualityValidator.AssessCue(
            "Xiaomi sold 2024 devices",
            "Công ty đã bán rất nhiều thiết bị trong năm nay với một câu cố ý quá dài",
            durationMilliseconds: 1000,
            glossary:
            [
                new TranslationGlossaryEntry
                {
                    SourceText = "Xiaomi",
                    TargetText = "Xiaomi",
                },
            ],
            maximumCharactersPerSecond: 18,
            providerConfidence: 0.6);

        Assert.True(assessment.IsValid);
        Assert.Contains("NUMBER_MISMATCH", assessment.Warnings);
        Assert.Contains("GLOSSARY_MISSING:Xiaomi", assessment.Warnings);
        Assert.Contains("READING_SPEED_HIGH", assessment.Warnings);
        Assert.Contains("LOW_CONFIDENCE", assessment.Warnings);
    }
}
