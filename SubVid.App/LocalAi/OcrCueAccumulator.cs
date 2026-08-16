using SubVid.App.Core;

namespace SubVid.App.LocalAi;

public sealed class OcrCueAccumulator(int sampleIntervalMilliseconds)
{
    private readonly int _interval = Math.Clamp(sampleIntervalMilliseconds, 200, 5000);
    private PendingCue? _pending;

    public List<SubtitleCue> Completed { get; } = [];

    public void Add(long timestampMilliseconds, string text, float confidence)
    {
        var normalized = Normalize(text);
        if (normalized.Length == 0 || confidence < 0.45f)
        {
            Flush();
            return;
        }

        if (_pending is not null && IsSimilar(_pending.NormalizedText, normalized))
        {
            _pending.EndMilliseconds = timestampMilliseconds + _interval;
            _pending.Text = text.Trim();
            _pending.NormalizedText = normalized;
            _pending.ConfidenceTotal += confidence;
            _pending.Samples++;
            return;
        }

        Flush();
        _pending = new PendingCue
        {
            StartMilliseconds = timestampMilliseconds,
            EndMilliseconds = timestampMilliseconds + _interval,
            Text = text.Trim(),
            NormalizedText = normalized,
            ConfidenceTotal = confidence,
            Samples = 1,
        };
    }

    public void Complete() => Flush();

    private void Flush()
    {
        if (_pending is null)
        {
            return;
        }

        var averageConfidence = _pending.ConfidenceTotal / _pending.Samples;
        if (_pending.Samples >= 2 || averageConfidence >= 0.72f)
        {
            Completed.Add(new SubtitleCue
            {
                StartMilliseconds = _pending.StartMilliseconds,
                EndMilliseconds = _pending.EndMilliseconds,
                OriginalText = _pending.Text,
            });
        }

        _pending = null;
    }

    internal static bool IsSimilar(string left, string right)
    {
        if (string.Equals(left, right, StringComparison.Ordinal))
        {
            return true;
        }

        var longest = Math.Max(left.Length, right.Length);
        if (longest == 0)
        {
            return true;
        }

        return Levenshtein(left, right) <= Math.Max(1, (int)Math.Ceiling(longest * 0.16));
    }

    private static string Normalize(string value) =>
        string.Concat((value ?? string.Empty)
            .Normalize()
            .Where(character => char.IsLetterOrDigit(character) || char.IsWhiteSpace(character)))
            .ToLowerInvariant()
            .Trim();

    private static int Levenshtein(string left, string right)
    {
        var previous = Enumerable.Range(0, right.Length + 1).ToArray();
        var current = new int[right.Length + 1];
        for (var row = 1; row <= left.Length; row++)
        {
            current[0] = row;
            for (var column = 1; column <= right.Length; column++)
            {
                var cost = left[row - 1] == right[column - 1] ? 0 : 1;
                current[column] = Math.Min(
                    Math.Min(current[column - 1] + 1, previous[column] + 1),
                    previous[column - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }

    private sealed class PendingCue
    {
        public long StartMilliseconds { get; set; }
        public long EndMilliseconds { get; set; }
        public string Text { get; set; } = string.Empty;
        public string NormalizedText { get; set; } = string.Empty;
        public float ConfidenceTotal { get; set; }
        public int Samples { get; set; }
    }
}
