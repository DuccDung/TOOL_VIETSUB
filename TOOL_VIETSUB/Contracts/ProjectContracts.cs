using System.ComponentModel.DataAnnotations;

namespace TOOL_VIETSUB.Contracts;

public sealed class CreateProjectRequest
{
    [Required]
    public Guid ProjectId { get; init; }

    [Required, StringLength(120, MinimumLength = 1)]
    public string Name { get; init; } = string.Empty;

    [StringLength(20)]
    public string? SourceLanguageCode { get; init; }
}

public sealed class RenameProjectRequest
{
    [Required, StringLength(120, MinimumLength = 1)]
    public string Name { get; init; } = string.Empty;
}

public sealed record ProjectResponse(
    Guid ProjectId,
    string Name,
    string Status,
    string? SourceLanguageCode,
    string TargetLanguageCode,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);
