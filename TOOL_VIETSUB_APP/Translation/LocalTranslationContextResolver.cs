using System.Text;
using TOOL_VIETSUB_APP.Core;
using TOOL_VIETSUB_APP.LocalAi;

namespace TOOL_VIETSUB_APP.Translation;

internal sealed record LocalTranslationMatch(string Text, double Confidence);

internal sealed class LocalTranslationContextResolver
{
    private readonly Dictionary<string, string> _sessionMemory = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, LocalTranslationMatch> BuildExactMatches(
        TranslationSceneRequest request)
    {
        var matches = new Dictionary<string, LocalTranslationMatch>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in request.TranslationMemory
                     .Where(entry => IsCompatible(entry, request))
                     .OrderByDescending(entry => entry.UpdatedAtUtc))
        {
            AddIfValid(matches, entry.SourceText, entry.TranslatedText, 0.98);
        }

        foreach (var cue in request.Cues.Where(cue =>
                     !cue.IsTarget && !string.IsNullOrWhiteSpace(cue.CandidateTranslation)))
        {
            SetIfValid(matches, cue.OriginalText, cue.CandidateTranslation!, 0.99);
        }

        foreach (var entry in request.Glossary.OrderByDescending(entry => entry.SourceText.Length))
        {
            AddIfValid(matches, entry.SourceText, entry.TargetText, 0.97);
        }

        foreach (var pair in _sessionMemory)
        {
            AddIfValid(matches, pair.Key, pair.Value, 0.9);
        }

        return matches;
    }

    public void Remember(string sourceText, string translatedText)
    {
        var key = NormalizeKey(sourceText);
        var value = NormalizeOutput(translatedText);
        if (key.Length > 0 && value.Length > 0)
        {
            _sessionMemory[key] = value;
        }
    }

    public string ApplyGlossary(
        string sourceText,
        string translatedText,
        IReadOnlyList<TranslationGlossaryEntry> glossary)
    {
        var result = NormalizeOutput(translatedText);
        foreach (var entry in glossary
                     .Where(entry => !string.IsNullOrWhiteSpace(entry.SourceText)
                         && !string.IsNullOrWhiteSpace(entry.TargetText)
                         && sourceText.Contains(entry.SourceText, StringComparison.OrdinalIgnoreCase))
                     .OrderByDescending(entry => entry.SourceText.Length))
        {
            if (result.Contains(entry.TargetText, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (result.Contains(entry.SourceText, StringComparison.OrdinalIgnoreCase))
            {
                result = result.Replace(
                    entry.SourceText,
                    entry.TargetText,
                    StringComparison.OrdinalIgnoreCase);
            }
        }

        return result;
    }

    public static string NormalizeKey(string value)
    {
        var builder = new StringBuilder(value.Length);
        var pendingWhitespace = false;
        foreach (var character in value.Trim())
        {
            if (char.IsWhiteSpace(character))
            {
                pendingWhitespace = builder.Length > 0;
                continue;
            }

            if (pendingWhitespace)
            {
                builder.Append(' ');
                pendingWhitespace = false;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }

    private static bool IsCompatible(
        TranslationMemoryEntry entry,
        TranslationSceneRequest request) =>
        string.Equals(
            LocalLanguageCodes.NormalizeSource(entry.SourceLanguageCode),
            LocalLanguageCodes.NormalizeSource(request.SourceLanguage),
            StringComparison.OrdinalIgnoreCase)
        && string.Equals(
            entry.TargetLanguageCode.Trim(),
            request.TargetLanguage.Trim(),
            StringComparison.OrdinalIgnoreCase);

    private static void AddIfValid(
        IDictionary<string, LocalTranslationMatch> matches,
        string sourceText,
        string translatedText,
        double confidence)
    {
        var key = NormalizeKey(sourceText);
        var value = NormalizeOutput(translatedText);
        if (key.Length > 0 && value.Length > 0)
        {
            matches.TryAdd(key, new LocalTranslationMatch(value, confidence));
        }
    }

    private static void SetIfValid(
        IDictionary<string, LocalTranslationMatch> matches,
        string sourceText,
        string translatedText,
        double confidence)
    {
        var key = NormalizeKey(sourceText);
        var value = NormalizeOutput(translatedText);
        if (key.Length > 0 && value.Length > 0)
        {
            matches[key] = new LocalTranslationMatch(value, confidence);
        }
    }

    private static string NormalizeOutput(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
}
