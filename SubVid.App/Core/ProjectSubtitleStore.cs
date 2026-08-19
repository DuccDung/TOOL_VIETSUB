using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace SubVid.App.Core;

/// <summary>
/// Persists the unbounded subtitle portion of a project separately from the
/// small project manifest. The manifest remains the source of metadata while
/// this store provides transactional cue checkpoints for long-form projects.
/// </summary>
internal sealed class ProjectSubtitleStore
{
    private const string DatabaseFileName = "project.db";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly AppPaths _paths;
    private readonly ConcurrentDictionary<Guid, StoreSnapshot> _snapshots = new();

    public ProjectSubtitleStore(AppPaths paths)
    {
        _paths = paths;
    }

    public async Task<IReadOnlyList<SubtitleDocument>?> TryLoadAsync(
        Guid projectId,
        CancellationToken cancellationToken)
    {
        var path = GetDatabasePath(projectId);
        if (!File.Exists(path))
        {
            return null;
        }

        await using var connection = CreateConnection(path);
        await connection.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);

        var tracks = new List<SubtitleDocument>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT track_id, language_code, source
                FROM subtitle_tracks
                ORDER BY track_index;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                tracks.Add(new SubtitleDocument
                {
                    TrackId = Guid.Parse(reader.GetString(0)),
                    LanguageCode = reader.GetString(1),
                    Source = reader.GetString(2),
                });
            }
        }

        var tracksById = tracks.ToDictionary(item => item.TrackId);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT cue_id, track_id, start_ms, end_ms, speaker, voice_id,
                       original_text, translated_text, translation_model_id,
                       translation_model_version, translation_source_fingerprint,
                       translation_quality_status, translation_confidence,
                       translation_warnings_json, translation_reviewed_at_utc,
                       original_locked, translation_locked, voice_timing_json
                FROM subtitle_cues
                ORDER BY track_id, cue_index;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var trackId = Guid.Parse(reader.GetString(1));
                if (!tracksById.TryGetValue(trackId, out var track))
                {
                    continue;
                }

                track.Cues.Add(new SubtitleCue
                {
                    CueId = Guid.Parse(reader.GetString(0)),
                    StartMilliseconds = reader.GetInt64(2),
                    EndMilliseconds = reader.GetInt64(3),
                    Speaker = reader.GetString(4),
                    VoiceId = GetNullableString(reader, 5),
                    OriginalText = reader.GetString(6),
                    TranslatedText = reader.GetString(7),
                    TranslationModelId = GetNullableString(reader, 8),
                    TranslationModelVersion = GetNullableString(reader, 9),
                    TranslationSourceFingerprint = GetNullableString(reader, 10),
                    TranslationQualityStatus = GetNullableString(reader, 11),
                    TranslationConfidence = reader.IsDBNull(12) ? null : reader.GetDouble(12),
                    TranslationWarnings = DeserializeOrDefault<List<string>>(reader.GetString(13)) ?? [],
                    TranslationReviewedAtUtc = reader.IsDBNull(14)
                        ? null
                        : DateTime.Parse(
                            reader.GetString(14),
                            System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.RoundtripKind),
                    OriginalLocked = reader.GetBoolean(15),
                    TranslationLocked = reader.GetBoolean(16),
                    VoiceTiming = reader.IsDBNull(17)
                        ? null
                        : DeserializeOrDefault<VoiceTimingAnalysis>(reader.GetString(17)),
                });
            }
        }

        _snapshots[projectId] = StoreSnapshot.Create(tracks);
        return tracks;
    }

    public async Task SaveAsync(ProjectManifest project, CancellationToken cancellationToken)
    {
        var current = StoreSnapshot.Create(project.SubtitleTracks);
        _snapshots.TryGetValue(project.ProjectId, out var previous);
        if (previous is not null && previous.Equals(current))
        {
            return;
        }

        var path = GetDatabasePath(project.ProjectId);
        await using var connection = CreateConnection(path);
        await connection.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);
        await using var transaction = connection.BeginTransaction();

        var currentTrackIds = current.Tracks.Keys.ToHashSet();
        var currentCueIds = current.Cues.Keys.ToHashSet();
        if (previous is not null)
        {
            foreach (var cueId in previous.Cues.Keys.Where(id => !currentCueIds.Contains(id)))
            {
                await ExecuteDeleteAsync(connection, transaction, "subtitle_cues", "cue_id", cueId, cancellationToken);
            }

            foreach (var trackId in previous.Tracks.Keys.Where(id => !currentTrackIds.Contains(id)))
            {
                await ExecuteDeleteAsync(connection, transaction, "subtitle_tracks", "track_id", trackId, cancellationToken);
            }
        }

        for (var trackIndex = 0; trackIndex < project.SubtitleTracks.Count; trackIndex++)
        {
            var track = project.SubtitleTracks[trackIndex];
            var snapshot = current.Tracks[track.TrackId];
            if (previous is null
                || !previous.Tracks.TryGetValue(track.TrackId, out var oldTrack)
                || oldTrack != snapshot)
            {
                await UpsertTrackAsync(connection, transaction, track, trackIndex, cancellationToken);
            }

            for (var cueIndex = 0; cueIndex < track.Cues.Count; cueIndex++)
            {
                var cue = track.Cues[cueIndex];
                var cueSnapshot = current.Cues[cue.CueId];
                if (previous is null
                    || !previous.Cues.TryGetValue(cue.CueId, out var oldCue)
                    || oldCue != cueSnapshot)
                {
                    await UpsertCueAsync(
                        connection,
                        transaction,
                        track.TrackId,
                        cue,
                        cueIndex,
                        cueSnapshot,
                        cancellationToken);
                }
            }
        }

        await transaction.CommitAsync(cancellationToken);
        _snapshots[project.ProjectId] = current;
    }

    private string GetDatabasePath(Guid projectId) =>
        _paths.GetProjectPath(projectId, DatabaseFileName);

    private static SqliteConnection CreateConnection(string path) => new(
        new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString());

    private static async Task EnsureSchemaAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            PRAGMA foreign_keys=ON;

            CREATE TABLE IF NOT EXISTS subtitle_tracks (
                track_id TEXT NOT NULL PRIMARY KEY,
                track_index INTEGER NOT NULL,
                language_code TEXT NOT NULL,
                source TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS subtitle_cues (
                cue_id TEXT NOT NULL PRIMARY KEY,
                track_id TEXT NOT NULL,
                cue_index INTEGER NOT NULL,
                start_ms INTEGER NOT NULL,
                end_ms INTEGER NOT NULL,
                speaker TEXT NOT NULL,
                voice_id TEXT NULL,
                original_text TEXT NOT NULL,
                translated_text TEXT NOT NULL,
                translation_model_id TEXT NULL,
                translation_model_version TEXT NULL,
                translation_source_fingerprint TEXT NULL,
                translation_quality_status TEXT NULL,
                translation_confidence REAL NULL,
                translation_warnings_json TEXT NOT NULL,
                translation_reviewed_at_utc TEXT NULL,
                original_locked INTEGER NOT NULL,
                translation_locked INTEGER NOT NULL,
                voice_timing_json TEXT NULL,
                FOREIGN KEY(track_id) REFERENCES subtitle_tracks(track_id) ON DELETE CASCADE
            );

            DROP INDEX IF EXISTS ix_subtitle_tracks_order;
            DROP INDEX IF EXISTS ix_subtitle_cues_track_order;

            CREATE INDEX IF NOT EXISTS ix_subtitle_tracks_order_v2
                ON subtitle_tracks(track_index);
            CREATE INDEX IF NOT EXISTS ix_subtitle_cues_track_order_v2
                ON subtitle_cues(track_id, cue_index);
            CREATE INDEX IF NOT EXISTS ix_subtitle_cues_timeline
                ON subtitle_cues(track_id, start_ms, end_ms);
            CREATE INDEX IF NOT EXISTS ix_subtitle_cues_quality
                ON subtitle_cues(track_id, translation_quality_status);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpsertTrackAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SubtitleDocument track,
        int trackIndex,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO subtitle_tracks(track_id, track_index, language_code, source)
            VALUES ($trackId, $trackIndex, $languageCode, $source)
            ON CONFLICT(track_id) DO UPDATE SET
                track_index = excluded.track_index,
                language_code = excluded.language_code,
                source = excluded.source;
            """;
        command.Parameters.AddWithValue("$trackId", track.TrackId.ToString("D"));
        command.Parameters.AddWithValue("$trackIndex", trackIndex);
        command.Parameters.AddWithValue("$languageCode", track.LanguageCode ?? string.Empty);
        command.Parameters.AddWithValue("$source", track.Source ?? string.Empty);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpsertCueAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid trackId,
        SubtitleCue cue,
        int cueIndex,
        CueSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO subtitle_cues(
                cue_id, track_id, cue_index, start_ms, end_ms, speaker, voice_id,
                original_text, translated_text, translation_model_id,
                translation_model_version, translation_source_fingerprint,
                translation_quality_status, translation_confidence,
                translation_warnings_json, translation_reviewed_at_utc,
                original_locked, translation_locked, voice_timing_json)
            VALUES (
                $cueId, $trackId, $cueIndex, $startMs, $endMs, $speaker, $voiceId,
                $originalText, $translatedText, $modelId, $modelVersion, $fingerprint,
                $qualityStatus, $confidence, $warnings, $reviewedAt,
                $originalLocked, $translationLocked, $voiceTiming)
            ON CONFLICT(cue_id) DO UPDATE SET
                track_id = excluded.track_id,
                cue_index = excluded.cue_index,
                start_ms = excluded.start_ms,
                end_ms = excluded.end_ms,
                speaker = excluded.speaker,
                voice_id = excluded.voice_id,
                original_text = excluded.original_text,
                translated_text = excluded.translated_text,
                translation_model_id = excluded.translation_model_id,
                translation_model_version = excluded.translation_model_version,
                translation_source_fingerprint = excluded.translation_source_fingerprint,
                translation_quality_status = excluded.translation_quality_status,
                translation_confidence = excluded.translation_confidence,
                translation_warnings_json = excluded.translation_warnings_json,
                translation_reviewed_at_utc = excluded.translation_reviewed_at_utc,
                original_locked = excluded.original_locked,
                translation_locked = excluded.translation_locked,
                voice_timing_json = excluded.voice_timing_json;
            """;
        command.Parameters.AddWithValue("$cueId", cue.CueId.ToString("D"));
        command.Parameters.AddWithValue("$trackId", trackId.ToString("D"));
        command.Parameters.AddWithValue("$cueIndex", cueIndex);
        command.Parameters.AddWithValue("$startMs", cue.StartMilliseconds);
        command.Parameters.AddWithValue("$endMs", cue.EndMilliseconds);
        command.Parameters.AddWithValue("$speaker", cue.Speaker ?? string.Empty);
        command.Parameters.AddWithValue("$voiceId", DbValue(cue.VoiceId));
        command.Parameters.AddWithValue("$originalText", cue.OriginalText ?? string.Empty);
        command.Parameters.AddWithValue("$translatedText", cue.TranslatedText ?? string.Empty);
        command.Parameters.AddWithValue("$modelId", DbValue(cue.TranslationModelId));
        command.Parameters.AddWithValue("$modelVersion", DbValue(cue.TranslationModelVersion));
        command.Parameters.AddWithValue("$fingerprint", DbValue(cue.TranslationSourceFingerprint));
        command.Parameters.AddWithValue("$qualityStatus", DbValue(cue.TranslationQualityStatus));
        command.Parameters.AddWithValue("$confidence", cue.TranslationConfidence is { } value ? value : DBNull.Value);
        command.Parameters.AddWithValue("$warnings", snapshot.WarningsJson);
        command.Parameters.AddWithValue(
            "$reviewedAt",
            cue.TranslationReviewedAtUtc is { } reviewedAt
                ? reviewedAt.ToUniversalTime().ToString("O")
                : DBNull.Value);
        command.Parameters.AddWithValue("$originalLocked", cue.OriginalLocked);
        command.Parameters.AddWithValue("$translationLocked", cue.TranslationLocked);
        command.Parameters.AddWithValue("$voiceTiming", DbValue(snapshot.VoiceTimingJson));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ExecuteDeleteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string column,
        Guid id,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"DELETE FROM {table} WHERE {column} = $id;";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static object DbValue(string? value) => value is null ? DBNull.Value : value;

    private static string? GetNullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static T? DeserializeOrDefault<T>(string value)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(value, JsonOptions);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private sealed record TrackSnapshot(int Index, string LanguageCode, string Source);

    private sealed record CueSnapshot(
        Guid TrackId,
        int Index,
        long StartMilliseconds,
        long EndMilliseconds,
        string Speaker,
        string? VoiceId,
        string OriginalText,
        string TranslatedText,
        string? TranslationModelId,
        string? TranslationModelVersion,
        string? TranslationSourceFingerprint,
        string? TranslationQualityStatus,
        double? TranslationConfidence,
        string WarningsJson,
        DateTime? TranslationReviewedAtUtc,
        bool OriginalLocked,
        bool TranslationLocked,
        string? VoiceTimingJson)
    {
        public static CueSnapshot Create(Guid trackId, int index, SubtitleCue cue) => new(
            trackId,
            index,
            cue.StartMilliseconds,
            cue.EndMilliseconds,
            cue.Speaker ?? string.Empty,
            cue.VoiceId,
            cue.OriginalText ?? string.Empty,
            cue.TranslatedText ?? string.Empty,
            cue.TranslationModelId,
            cue.TranslationModelVersion,
            cue.TranslationSourceFingerprint,
            cue.TranslationQualityStatus,
            cue.TranslationConfidence,
            JsonSerializer.Serialize(cue.TranslationWarnings ?? [], JsonOptions),
            cue.TranslationReviewedAtUtc,
            cue.OriginalLocked,
            cue.TranslationLocked,
            cue.VoiceTiming is null ? null : JsonSerializer.Serialize(cue.VoiceTiming, JsonOptions));
    }

    private sealed class StoreSnapshot : IEquatable<StoreSnapshot>
    {
        public required IReadOnlyDictionary<Guid, TrackSnapshot> Tracks { get; init; }

        public required IReadOnlyDictionary<Guid, CueSnapshot> Cues { get; init; }

        public static StoreSnapshot Create(IReadOnlyList<SubtitleDocument> tracks)
        {
            var trackSnapshots = new Dictionary<Guid, TrackSnapshot>();
            var cueSnapshots = new Dictionary<Guid, CueSnapshot>();
            for (var trackIndex = 0; trackIndex < tracks.Count; trackIndex++)
            {
                var track = tracks[trackIndex];
                trackSnapshots[track.TrackId] = new TrackSnapshot(
                    trackIndex,
                    track.LanguageCode ?? string.Empty,
                    track.Source ?? string.Empty);
                for (var cueIndex = 0; cueIndex < track.Cues.Count; cueIndex++)
                {
                    var cue = track.Cues[cueIndex];
                    cueSnapshots[cue.CueId] = CueSnapshot.Create(track.TrackId, cueIndex, cue);
                }
            }

            return new StoreSnapshot { Tracks = trackSnapshots, Cues = cueSnapshots };
        }

        public bool Equals(StoreSnapshot? other) => other is not null
            && Tracks.Count == other.Tracks.Count
            && Cues.Count == other.Cues.Count
            && Tracks.All(item => other.Tracks.TryGetValue(item.Key, out var value) && value == item.Value)
            && Cues.All(item => other.Cues.TryGetValue(item.Key, out var value) && value == item.Value);

        public override bool Equals(object? obj) => obj is StoreSnapshot other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(Tracks.Count, Cues.Count);
    }
}
