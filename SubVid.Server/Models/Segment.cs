using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace SubVid.Server.Models;

[Table("segments")]
[Index("ProjectId", "TranscriptVersion", "TranslationStatus", Name = "IX_segments_review_queue")]
[Index("ProjectId", "TranscriptVersion", "StartMs", "EndMs", Name = "IX_segments_timeline")]
[Index("ProjectId", "TranscriptVersion", "SegmentIndex", Name = "UQ_segments_project_version_index", IsUnique = true)]
public partial class Segment
{
    [Key]
    [Column("segment_id")]
    public Guid SegmentId { get; set; }

    [Column("project_id")]
    public Guid ProjectId { get; set; }

    [Column("source_job_id")]
    public Guid? SourceJobId { get; set; }

    [Column("transcript_version")]
    public int TranscriptVersion { get; set; }

    [Column("segment_index")]
    public int SegmentIndex { get; set; }

    [Column("start_ms")]
    public long StartMs { get; set; }

    [Column("end_ms")]
    public long EndMs { get; set; }

    [Column("speaker_label")]
    [StringLength(100)]
    public string SpeakerLabel { get; set; } = null!;

    [Column("source_language_code")]
    [StringLength(20)]
    [Unicode(false)]
    public string? SourceLanguageCode { get; set; }

    [Column("original_text")]
    public string OriginalText { get; set; } = null!;

    [Column("translated_text")]
    public string? TranslatedText { get; set; }

    [Column("source_confidence", TypeName = "decimal(5, 4)")]
    public decimal? SourceConfidence { get; set; }

    [Column("translation_status")]
    [StringLength(20)]
    [Unicode(false)]
    public string TranslationStatus { get; set; } = null!;

    [Column("original_text_locked")]
    public bool OriginalTextLocked { get; set; }

    [Column("translation_locked")]
    public bool TranslationLocked { get; set; }

    [Column("voice_id")]
    public Guid? VoiceId { get; set; }

    [Column("tts_media_file_id")]
    public Guid? TtsMediaFileId { get; set; }

    [Column("speech_rate", TypeName = "decimal(5, 3)")]
    public decimal SpeechRate { get; set; }

    [Column("tts_duration_ms")]
    public long? TtsDurationMs { get; set; }

    [Column("approved_by_user_id")]
    public Guid? ApprovedByUserId { get; set; }

    [Column("approved_at_utc")]
    [Precision(3)]
    public DateTime? ApprovedAtUtc { get; set; }

    [Column("created_at_utc")]
    [Precision(3)]
    public DateTime CreatedAtUtc { get; set; }

    [Column("updated_at_utc")]
    [Precision(3)]
    public DateTime UpdatedAtUtc { get; set; }

    [Column("row_version")]
    public byte[] RowVersion { get; set; } = null!;

    [ForeignKey("ApprovedByUserId")]
    [InverseProperty("Segments")]
    public virtual User? ApprovedByUser { get; set; }

    [ForeignKey("ProjectId")]
    [InverseProperty("Segments")]
    public virtual Project Project { get; set; } = null!;

    [ForeignKey("SourceJobId")]
    [InverseProperty("Segments")]
    public virtual Job? SourceJob { get; set; }

    [ForeignKey("TtsMediaFileId")]
    [InverseProperty("Segments")]
    public virtual MediaFile? TtsMediaFile { get; set; }

    [ForeignKey("VoiceId")]
    [InverseProperty("Segments")]
    public virtual Voice? Voice { get; set; }
}
