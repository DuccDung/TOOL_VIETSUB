using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TOOL_VIETSUB.Contracts;
using TOOL_VIETSUB.Data;
using TOOL_VIETSUB.Models;

namespace TOOL_VIETSUB.Usage;

public sealed class UsageService(ToolVietSubDbContext database)
{
    private const string DesktopProvider = "DESKTOP_APP";
    private static readonly HashSet<string> AllowedOperations =
        ["STORAGE", "TRANSCRIPTION", "TRANSLATION", "TTS", "MEDIA_PROCESSING", "EGRESS", "OTHER"];
    private static readonly HashSet<string> AllowedUnits =
        ["MINUTE", "SECOND", "CHARACTER", "TOKEN", "BYTE", "REQUEST", "FLAT"];

    public async Task<UsageServiceResult> RecordAsync(
        Guid userId,
        UsageEventRequest request,
        CancellationToken cancellationToken)
    {
        if (request.EventId == Guid.Empty)
        {
            return UsageServiceResult.Failure("USAGE_EVENT_ID_INVALID", "Mã sự kiện sử dụng không hợp lệ.");
        }

        var operationCode = request.OperationCode.Trim().ToUpperInvariant();
        var unitCode = request.UnitCode.Trim().ToUpperInvariant();
        if (!AllowedOperations.Contains(operationCode) || !AllowedUnits.Contains(unitCode))
        {
            return UsageServiceResult.Failure(
                "USAGE_TYPE_INVALID",
                "Loại tác vụ hoặc đơn vị sử dụng không được hỗ trợ.");
        }

        var nowUtc = DateTime.UtcNow;
        var occurredAtUtc = request.OccurredAtUtc.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(request.OccurredAtUtc, DateTimeKind.Utc)
            : request.OccurredAtUtc.ToUniversalTime();
        if (request.OccurredAtUtc == default
            || occurredAtUtc < nowUtc.AddDays(-90)
            || occurredAtUtc > nowUtc.AddMinutes(5))
        {
            return UsageServiceResult.Failure(
                "USAGE_TIME_INVALID",
                "Thời điểm sử dụng nằm ngoài phạm vi đồng bộ cho phép.");
        }

        var externalRequestId = request.EventId.ToString("N");
        if (await database.UsageRecords.AsNoTracking().AnyAsync(
            item => item.ProviderCode == DesktopProvider
                && item.ExternalRequestId == externalRequestId,
            cancellationToken))
        {
            return UsageServiceResult.Success(request.EventId, duplicate: true);
        }

        if (request.ProjectId is Guid projectId
            && !await database.Projects.AsNoTracking().AnyAsync(
                item => item.ProjectId == projectId
                    && item.OwnerUserId == userId
                    && item.DeletedAtUtc == null,
                cancellationToken))
        {
            return UsageServiceResult.Failure("USAGE_PROJECT_INVALID", "Dự án không thuộc tài khoản hiện tại.");
        }

        if (request.JobId is Guid jobId
            && !await database.Jobs.AsNoTracking().AnyAsync(
                item => item.JobId == jobId && item.Project.OwnerUserId == userId,
                cancellationToken))
        {
            return UsageServiceResult.Failure("USAGE_JOB_INVALID", "Công việc không thuộc tài khoản hiện tại.");
        }

        var metadataJson = request.Metadata is null
            ? null
            : JsonSerializer.Serialize(request.Metadata);
        if (metadataJson?.Length > 4000)
        {
            return UsageServiceResult.Failure("USAGE_METADATA_TOO_LARGE", "Metadata vượt quá giới hạn cho phép.");
        }

        database.UsageRecords.Add(new UsageRecord
        {
            UsageRecordId = Guid.NewGuid(),
            UserId = userId,
            ProjectId = request.ProjectId,
            JobId = request.JobId,
            ProviderCode = DesktopProvider,
            OperationCode = operationCode,
            Quantity = request.Quantity,
            UnitCode = unitCode,
            CurrencyCode = "USD",
            ExternalRequestId = externalRequestId,
            MetadataJson = metadataJson,
            OccurredAtUtc = occurredAtUtc,
            CreatedAtUtc = nowUtc,
        });

        try
        {
            await database.SaveChangesAsync(cancellationToken);
            return UsageServiceResult.Success(request.EventId, duplicate: false);
        }
        catch (DbUpdateException)
        {
            database.ChangeTracker.Clear();
            if (await database.UsageRecords.AsNoTracking().AnyAsync(
                item => item.ProviderCode == DesktopProvider
                    && item.ExternalRequestId == externalRequestId,
                cancellationToken))
            {
                return UsageServiceResult.Success(request.EventId, duplicate: true);
            }

            throw;
        }
    }

    public async Task<UsageHistoryResponse> GetHistoryAsync(
        Guid userId,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var query = database.UsageRecords.AsNoTracking()
            .Where(item => item.UserId == userId && item.ProviderCode == DesktopProvider);
        var totalCount = await query.CountAsync(cancellationToken);
        var records = await query
            .OrderByDescending(item => item.OccurredAtUtc)
            .ThenByDescending(item => item.CreatedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(item => new
            {
                item.ExternalRequestId,
                item.OperationCode,
                item.Quantity,
                item.UnitCode,
                item.OccurredAtUtc,
                item.ProjectId,
                item.JobId,
            })
            .ToListAsync(cancellationToken);
        var items = records.Select(item => new UsageHistoryItem(
            Guid.Parse(item.ExternalRequestId!),
            item.OperationCode,
            item.Quantity,
            item.UnitCode,
            item.OccurredAtUtc,
            item.ProjectId,
            item.JobId)).ToArray();

        return new UsageHistoryResponse(page, pageSize, totalCount, items);
    }
}
