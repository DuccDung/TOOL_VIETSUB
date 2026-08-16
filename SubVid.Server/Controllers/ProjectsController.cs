using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SubVid.Server.Auth;
using SubVid.Server.Contracts;
using SubVid.Server.Data;
using SubVid.Server.Models;

namespace SubVid.Server.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/projects")]
public sealed class ProjectsController(SubVidDbContext database) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Create(
        CreateProjectRequest request,
        CancellationToken cancellationToken)
    {
        if (!User.TryGetUserId(out var userId))
        {
            return InvalidToken();
        }

        if (request.ProjectId == Guid.Empty)
        {
            return BadRequest(ApiEnvelope<object>.Fail(
                "PROJECT_ID_INVALID", "Mã dự án không hợp lệ.", HttpContext.TraceIdentifier));
        }

        var name = request.Name.Trim();
        if (name.Length == 0 || name.Any(char.IsControl))
        {
            return BadRequest(ApiEnvelope<object>.Fail(
                "PROJECT_NAME_INVALID", "Tên dự án không hợp lệ.", HttpContext.TraceIdentifier));
        }

        var existing = await database.Projects.AsNoTracking().SingleOrDefaultAsync(
            item => item.ProjectId == request.ProjectId,
            cancellationToken);
        if (existing is not null)
        {
            return existing.OwnerUserId == userId && existing.DeletedAtUtc is null
                ? Ok(ApiEnvelope<ProjectResponse>.Ok(Map(existing), HttpContext.TraceIdentifier))
                : Conflict(ApiEnvelope<object>.Fail(
                    "PROJECT_ID_UNAVAILABLE", "Mã dự án đã được sử dụng.", HttpContext.TraceIdentifier));
        }

        var nowUtc = DateTime.UtcNow;
        var project = new Project
        {
            ProjectId = request.ProjectId,
            OwnerUserId = userId,
            ProjectName = name,
            StatusCode = "DRAFT",
            SourceLanguageCode = NormalizeLanguage(request.SourceLanguageCode),
            TargetLanguageCode = "vi",
            CurrentTranscriptVersion = 1,
            CreatedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc,
        };
        database.Projects.Add(project);
        await database.SaveChangesAsync(cancellationToken);
        return CreatedAtAction(
            nameof(Get),
            new { projectId = project.ProjectId },
            ApiEnvelope<ProjectResponse>.Ok(Map(project), HttpContext.TraceIdentifier));
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        if (!User.TryGetUserId(out var userId))
        {
            return InvalidToken();
        }

        var projects = await database.Projects.AsNoTracking()
            .Where(item => item.OwnerUserId == userId && item.DeletedAtUtc == null)
            .OrderByDescending(item => item.UpdatedAtUtc)
            .Take(100)
            .Select(item => new ProjectResponse(
                item.ProjectId,
                item.ProjectName,
                item.StatusCode,
                item.SourceLanguageCode,
                item.TargetLanguageCode,
                item.CreatedAtUtc,
                item.UpdatedAtUtc))
            .ToArrayAsync(cancellationToken);
        return Ok(ApiEnvelope<IReadOnlyList<ProjectResponse>>.Ok(projects, HttpContext.TraceIdentifier));
    }

    [HttpGet("{projectId:guid}")]
    public async Task<IActionResult> Get(Guid projectId, CancellationToken cancellationToken)
    {
        if (!User.TryGetUserId(out var userId))
        {
            return InvalidToken();
        }

        var project = await database.Projects.AsNoTracking().SingleOrDefaultAsync(
            item => item.ProjectId == projectId
                && item.OwnerUserId == userId
                && item.DeletedAtUtc == null,
            cancellationToken);
        return project is null
            ? NotFound(ApiEnvelope<object>.Fail(
                "PROJECT_NOT_FOUND", "Không tìm thấy dự án.", HttpContext.TraceIdentifier))
            : Ok(ApiEnvelope<ProjectResponse>.Ok(Map(project), HttpContext.TraceIdentifier));
    }

    [HttpPatch("{projectId:guid}/name")]
    public async Task<IActionResult> Rename(
        Guid projectId,
        RenameProjectRequest request,
        CancellationToken cancellationToken)
    {
        if (!User.TryGetUserId(out var userId))
        {
            return InvalidToken();
        }

        var name = request.Name.Trim();
        if (name.Length == 0 || name.Any(char.IsControl))
        {
            return BadRequest(ApiEnvelope<object>.Fail(
                "PROJECT_NAME_INVALID", "Tên dự án không hợp lệ.", HttpContext.TraceIdentifier));
        }

        var project = await database.Projects.SingleOrDefaultAsync(
            item => item.ProjectId == projectId
                && item.OwnerUserId == userId
                && item.DeletedAtUtc == null,
            cancellationToken);
        if (project is null)
        {
            return NotFound(ApiEnvelope<object>.Fail(
                "PROJECT_NOT_FOUND", "Không tìm thấy dự án.", HttpContext.TraceIdentifier));
        }

        project.ProjectName = name;
        project.UpdatedAtUtc = DateTime.UtcNow;
        await database.SaveChangesAsync(cancellationToken);
        return Ok(ApiEnvelope<ProjectResponse>.Ok(Map(project), HttpContext.TraceIdentifier));
    }

    private UnauthorizedObjectResult InvalidToken() => Unauthorized(ApiEnvelope<object>.Fail(
        "AUTH_TOKEN_INVALID", "Token đăng nhập không hợp lệ.", HttpContext.TraceIdentifier));

    private static string? NormalizeLanguage(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();

    private static ProjectResponse Map(Project project) => new(
        project.ProjectId,
        project.ProjectName,
        project.StatusCode,
        project.SourceLanguageCode,
        project.TargetLanguageCode,
        project.CreatedAtUtc,
        project.UpdatedAtUtc);
}
