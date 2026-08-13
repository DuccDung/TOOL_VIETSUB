using System.Text.RegularExpressions;

namespace TOOL_VIETSUB_APP.LocalAi;

public sealed record TranslationQualityResult(bool IsValid, string? Code = null)
{
    public static TranslationQualityResult Valid { get; } = new(true);

    public static TranslationQualityResult Invalid(string code) => new(false, code);
}

public static partial class TranslationQualityValidator
{
    public static TranslationQualityResult Validate(
        string source,
        string translation,
        bool endedWithEos,
        int generatedTokenCount,
        int maxGeneratedTokens)
    {
        if (!endedWithEos)
        {
            return TranslationQualityResult.Invalid("MISSING_EOS");
        }

        if (generatedTokenCount >= maxGeneratedTokens)
        {
            return TranslationQualityResult.Invalid("DECODING_LIMIT_REACHED");
        }

        return ValidateText(source, translation);
    }

    public static TranslationQualityResult ValidateText(string source, string translation)
    {
        var normalized = WhitespaceRegex().Replace(translation ?? string.Empty, " ").Trim();
        if (normalized.Length == 0)
        {
            return TranslationQualityResult.Invalid("EMPTY_TRANSLATION");
        }

        var sourceLength = Math.Max(1, source.Count(character => !char.IsWhiteSpace(character)));
        if (normalized.Length > Math.Max(200, sourceLength * 14))
        {
            return TranslationQualityResult.Invalid("EXCESSIVE_LENGTH");
        }

        var tokens = WordRegex()
            .Matches(normalized.ToLowerInvariant())
            .Select(match => match.Value)
            .ToArray();
        if (tokens.Length < 4)
        {
            return TranslationQualityResult.Valid;
        }

        var consecutive = 1;
        for (var index = 1; index < tokens.Length; index++)
        {
            consecutive = tokens[index] == tokens[index - 1] ? consecutive + 1 : 1;
            if (consecutive >= 4)
            {
                return TranslationQualityResult.Invalid("REPEATED_TOKEN_RUN");
            }
        }

        var dominantCount = tokens
            .GroupBy(token => token, StringComparer.Ordinal)
            .Max(group => group.Count());
        if (dominantCount >= 6 && dominantCount * 100 >= tokens.Length * 35)
        {
            return TranslationQualityResult.Invalid("DOMINANT_REPEATED_TOKEN");
        }

        for (var size = 2; size <= Math.Min(5, tokens.Length / 3); size++)
        {
            var frequencies = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var index = 0; index <= tokens.Length - size; index++)
            {
                var key = string.Join('\u001f', tokens.AsSpan(index, size).ToArray());
                frequencies[key] = frequencies.GetValueOrDefault(key) + 1;
            }

            if (frequencies.Values.Any(count => count >= 4 && count * size * 100 >= tokens.Length * 55))
            {
                return TranslationQualityResult.Invalid("REPEATED_PHRASE");
            }
        }

        return TranslationQualityResult.Valid;
    }

    public static bool LooksPathological(string source, string translation) =>
        !string.IsNullOrWhiteSpace(translation)
        && !ValidateText(source, translation).IsValid;

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"[\p{L}\p{M}\p{N}]+", RegexOptions.CultureInvariant)]
    private static partial Regex WordRegex();
}
