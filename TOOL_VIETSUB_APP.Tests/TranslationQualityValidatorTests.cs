using TOOL_VIETSUB_APP.LocalAi;

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
}
