using System.Text.RegularExpressions;
using TOOL_VIETSUB_APP.Core;

namespace TOOL_VIETSUB_APP.LocalAi;

public sealed record TranslationQualityResult(bool IsValid, string? Code = null)
{
    public static TranslationQualityResult Valid { get; } = new(true);

    public static TranslationQualityResult Invalid(string code) => new(false, code);
}

public sealed record TranslationCueQualityAssessment(
    bool IsValid,
    string? FailureCode,
    IReadOnlyList<string> Warnings,
    double CharactersPerSecond);

public static partial class TranslationQualityValidator
{
    public static TranslationCueQualityAssessment AssessCue(
        string source,
        string translation,
        long durationMilliseconds,
        IReadOnlyList<TranslationGlossaryEntry>? glossary = null,
        double maximumCharactersPerSecond = 18,
        double? providerConfidence = null,
        IReadOnlyList<string>? providerWarnings = null)
    {
        var safeSource = source ?? string.Empty;
        var safeTranslation = translation ?? string.Empty;
        var fatal = ValidateText(safeSource, safeTranslation);
        var durationSeconds = Math.Max(0.25, durationMilliseconds / 1000d);
        var charactersPerSecond = safeTranslation.Count(character => !char.IsWhiteSpace(character)) / durationSeconds;
        if (!fatal.IsValid)
        {
            return new TranslationCueQualityAssessment(
                false,
                fatal.Code,
                [],
                charactersPerSecond);
        }

        var warnings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (providerWarnings is not null)
        {
            foreach (var warning in providerWarnings.Where(value => !string.IsNullOrWhiteSpace(value)))
            {
                warnings.Add(warning.Trim());
            }
        }

        if (providerConfidence is < 0.7)
        {
            warnings.Add("LOW_CONFIDENCE");
        }

        var sourceNumbers = NumberRegex().Matches(safeSource).Select(match => match.Value).ToArray();
        var translatedNumbers = NumberRegex().Matches(safeTranslation).Select(match => match.Value).ToArray();
        if (!sourceNumbers.SequenceEqual(translatedNumbers, StringComparer.Ordinal))
        {
            warnings.Add("NUMBER_MISMATCH");
        }

        foreach (var entry in glossary ?? [])
        {
            var glossarySource = entry.SourceText;
            var glossaryTarget = entry.TargetText;
            if (string.IsNullOrWhiteSpace(glossarySource)
                || string.IsNullOrWhiteSpace(glossaryTarget)
                || !safeSource.Contains(glossarySource, StringComparison.OrdinalIgnoreCase)
                || safeTranslation.Contains(glossaryTarget, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            warnings.Add($"GLOSSARY_MISSING:{LimitCode(glossarySource)}");
        }

        var normalizedMaximum = double.IsFinite(maximumCharactersPerSecond)
            ? Math.Clamp(maximumCharactersPerSecond, 8, 30)
            : 18;
        if (charactersPerSecond > normalizedMaximum)
        {
            warnings.Add("READING_SPEED_HIGH");
        }

        return new TranslationCueQualityAssessment(
            true,
            null,
            warnings.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            charactersPerSecond);
    }

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

        var sourceRepeatRun = FindMaximumIntentionalRepeatRun(source ?? string.Empty);
        var maximumAllowedConsecutive = sourceRepeatRun >= 3
            ? sourceRepeatRun + 1
            : 3;
        var consecutive = 1;
        for (var index = 1; index < tokens.Length; index++)
        {
            consecutive = tokens[index] == tokens[index - 1] ? consecutive + 1 : 1;
            if (consecutive > maximumAllowedConsecutive)
            {
                return TranslationQualityResult.Invalid("REPEATED_TOKEN_RUN");
            }
        }

        var dominantCount = tokens
            .GroupBy(token => token, StringComparer.Ordinal)
            .Max(group => group.Count());
        if (dominantCount >= 6
            && dominantCount * 100 >= tokens.Length * 35
            && (sourceRepeatRun < 3 || dominantCount > sourceRepeatRun + 1))
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

            if (frequencies.Values.Any(count => count >= 4
                && count * size * 100 >= tokens.Length * 55
                && (sourceRepeatRun < 3 || count > sourceRepeatRun + 1)))
            {
                return TranslationQualityResult.Invalid("REPEATED_PHRASE");
            }
        }

        return TranslationQualityResult.Valid;
    }

    public static bool LooksPathological(string source, string translation) =>
        !string.IsNullOrWhiteSpace(translation)
        && !ValidateText(source, translation).IsValid;

    private static int FindMaximumIntentionalRepeatRun(string source)
    {
        var normalized = WhitespaceRegex().Replace(source, " ").Trim().ToLowerInvariant();
        var sourceTokens = WordRegex()
            .Matches(normalized)
            .Select(match => match.Value)
            .ToArray();
        var maximumTokenRun = FindMaximumRun(sourceTokens);

        var maximumHanRun = 1;
        var currentHanRun = 1;
        char? previousHan = null;
        foreach (var character in normalized)
        {
            if (!IsHanCharacter(character))
            {
                continue;
            }

            currentHanRun = previousHan == character ? currentHanRun + 1 : 1;
            previousHan = character;
            maximumHanRun = Math.Max(maximumHanRun, currentHanRun);
        }

        return Math.Max(maximumTokenRun, maximumHanRun);
    }

    private static int FindMaximumRun(IReadOnlyList<string> values)
    {
        if (values.Count == 0)
        {
            return 1;
        }

        var maximum = 1;
        var current = 1;
        for (var index = 1; index < values.Count; index++)
        {
            current = string.Equals(values[index], values[index - 1], StringComparison.Ordinal)
                ? current + 1
                : 1;
            maximum = Math.Max(maximum, current);
        }

        return maximum;
    }

    private static bool IsHanCharacter(char character) =>
        character is >= '\u3400' and <= '\u4DBF'
        or >= '\u4E00' and <= '\u9FFF'
        or >= '\uF900' and <= '\uFAFF';

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"[\p{L}\p{M}\p{N}]+", RegexOptions.CultureInvariant)]
    private static partial Regex WordRegex();

    [GeneratedRegex(@"\d+", RegexOptions.CultureInvariant)]
    private static partial Regex NumberRegex();

    private static string LimitCode(string value)
    {
        var normalized = WhitespaceRegex().Replace(value.Trim(), "_");
        return normalized.Length <= 40 ? normalized : normalized[..40];
    }
}
