using SubVid.App.Api;
using SubVid.App.Core;
using SubVid.App.Usage;

namespace SubVid.App.Translation;

public interface ILocalJobAwareTranslationProvider
{
    void BindJob(LocalJob job);
}

public sealed class ServerManagedTranslationProvider(
    IDesktopCloudAccessGateway cloudAccess,
    HttpClient httpClient,
    ProjectWorkspaceService workspace,
    ProjectManifest project,
    string providerId,
    string modelId)
    : ITranslationProvider, ILocalJobAwareTranslationProvider
{
    private const int ProviderMaximumAttempts = 3;
    private readonly string _providerId = TranslationProviders.Normalize(providerId);
    private readonly SemaphoreSlim _settlementGate = new(1, 1);
    private LocalJob? _job;

    public string ProviderId => _providerId;

    public string ModelId { get; } = modelId;

    public bool SupportsContextualReview => true;

    public void BindJob(LocalJob job) => _job = job;

    public async Task<TranslationSceneResult> TranslateAsync(
        TranslationSceneRequest request,
        CancellationToken cancellationToken)
    {
        var job = _job ?? throw new InvalidOperationException(
            "Cloud translation provider has not been bound to a local job.");
        var estimate = EstimateUsage(request);
        var settlement = new CloudUsageSettlement
        {
            RequestId = Guid.NewGuid(),
            ProviderCode = ProviderId,
            ModelId = ModelId,
            EstimatedInputUnits = estimate.ReservedInputTokens,
            EstimatedOutputUnits = estimate.ReservedOutputTokens,
            Status = "AUTHORIZING",
        };
        await MutateSettlementAsync(
            () => job.CloudSettlements.Add(settlement),
            cancellationToken);

        CloudAuthorizationApiResponse authorization;
        try
        {
            authorization = await cloudAccess.AuthorizeAsync(
                new AuthorizeCloudAccessApiRequest(
                    settlement.RequestId,
                    project.ProjectId,
                    job.JobId,
                    "TRANSLATION",
                    ProviderId,
                    ModelId,
                    estimate.ReservedInputTokens,
                    estimate.ReservedOutputTokens),
                cancellationToken);
        }
        catch (ApiClientException exception)
        {
            await MutateSettlementAsync(() =>
            {
                settlement.Status = "AUTHORIZATION_FAILED";
                settlement.ErrorMessage = exception.Message;
                settlement.UpdatedAtUtc = DateTime.UtcNow;
            }, CancellationToken.None);
            throw new TranslationProviderException(
                exception.Code,
                exception.Message,
                IsRetryableServerError(exception),
                exception);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            await MutateSettlementAsync(() =>
            {
                settlement.Status = "AUTHORIZATION_FAILED";
                settlement.ErrorMessage = exception.Message;
                settlement.UpdatedAtUtc = DateTime.UtcNow;
            }, CancellationToken.None);
            throw new TranslationProviderException(
                "CLOUD_CONTROL_UNAVAILABLE",
                "Không thể xin quyền sử dụng Cloud từ Server.",
                retryable: true,
                exception);
        }

        await MutateSettlementAsync(() =>
        {
            settlement.ReservationId = authorization.ReservationId;
            settlement.UnitCode = authorization.UnitCode;
            settlement.ExpiresAtUtc = authorization.ExpiresAtUtc;
            settlement.Status = "HELD";
            settlement.ErrorMessage = null;
            settlement.UpdatedAtUtc = DateTime.UtcNow;
        }, cancellationToken);

        var provider = CreateProvider(authorization.ApiKey);
        try
        {
            var result = await provider.TranslateAsync(request, cancellationToken);
            var usageWasEstimated = TotalTokens(result.Usage) == 0;
            var usage = NormalizeSuccessfulUsage(result.Usage, estimate);
            await CommitOrPersistAsync(settlement, usage, cancellationToken, usageWasEstimated);
            return result with { Usage = usage };
        }
        catch (TranslationProviderException exception)
        {
            if (TotalTokens(exception.Usage) > 0)
            {
                await CommitOrPersistAsync(settlement, exception.Usage!, CancellationToken.None);
            }
            else if (IsUnknownProviderOutcome(exception))
            {
                await MutateSettlementAsync(() =>
                {
                    settlement.Status = "UNKNOWN";
                    settlement.ErrorMessage = exception.Message;
                    settlement.UpdatedAtUtc = DateTime.UtcNow;
                }, CancellationToken.None);
            }
            else
            {
                await ReleaseOrPersistAsync(settlement, CancellationToken.None);
            }

            throw;
        }
        catch
        {
            await ReleaseOrPersistAsync(settlement, CancellationToken.None);
            throw;
        }
    }

    private ITranslationProvider CreateProvider(string apiKey) => ProviderId switch
    {
        TranslationProviders.OpenAi => new OpenAiTranslationProvider(httpClient, apiKey, ModelId),
        TranslationProviders.Gemini => new GeminiTranslationProvider(httpClient, apiKey, ModelId),
        TranslationProviders.DeepSeek => new DeepSeekTranslationProvider(httpClient, apiKey, ModelId),
        TranslationProviders.Groq => new GroqTranslationProvider(httpClient, apiKey, ModelId),
        _ => throw new InvalidOperationException("Nhà cung cấp dịch Cloud chưa được hỗ trợ."),
    };

    private async Task CommitOrPersistAsync(
        CloudUsageSettlement settlement,
        TranslationUsage usage,
        CancellationToken cancellationToken,
        bool usageWasEstimated = false)
    {
        if (settlement.ReservationId is not Guid reservationId)
        {
            return;
        }

        await MutateSettlementAsync(() =>
        {
            settlement.UsageWasEstimated = usageWasEstimated;
            settlement.ActualInputUnits = Math.Max(0, usage.InputTokens);
            settlement.ActualOutputUnits = Math.Max(0, usage.OutputTokens);
            settlement.CachedInputUnits = Math.Clamp(
                usage.CachedInputTokens,
                0,
                settlement.ActualInputUnits);
            settlement.ApiRequests = Math.Max(0, usage.ApiRequests);
            settlement.RetryRequests = Math.Max(0, usage.RetryRequests);
            settlement.Status = "PENDING_COMMIT";
            settlement.ErrorMessage = null;
            settlement.UpdatedAtUtc = DateTime.UtcNow;
        }, CancellationToken.None);
        try
        {
            var response = await cloudAccess.CommitAsync(
                reservationId,
                ToCommitRequest(settlement),
                cancellationToken);
            await MutateSettlementAsync(() =>
            {
                settlement.Status = response.Status;
                settlement.ErrorMessage = null;
                settlement.UpdatedAtUtc = DateTime.UtcNow;
            }, CancellationToken.None);
        }
        catch (Exception exception) when (exception is ApiClientException or HttpRequestException or TaskCanceledException)
        {
            await MutateSettlementAsync(() =>
            {
                settlement.Status = "PENDING_COMMIT";
                settlement.ErrorMessage = exception.Message;
                settlement.UpdatedAtUtc = DateTime.UtcNow;
            }, CancellationToken.None);
        }
    }

    private async Task ReleaseOrPersistAsync(
        CloudUsageSettlement settlement,
        CancellationToken cancellationToken)
    {
        if (settlement.ReservationId is not Guid reservationId)
        {
            return;
        }

        try
        {
            var response = await cloudAccess.ReleaseAsync(reservationId, cancellationToken);
            await MutateSettlementAsync(() =>
            {
                settlement.Status = response.Status;
                settlement.ErrorMessage = null;
                settlement.UpdatedAtUtc = DateTime.UtcNow;
            }, CancellationToken.None);
        }
        catch (Exception exception) when (exception is ApiClientException or HttpRequestException or TaskCanceledException)
        {
            await MutateSettlementAsync(() =>
            {
                settlement.Status = "PENDING_RELEASE";
                settlement.ErrorMessage = exception.Message;
                settlement.UpdatedAtUtc = DateTime.UtcNow;
            }, CancellationToken.None);
        }
    }

    private async Task MutateSettlementAsync(Action mutation, CancellationToken cancellationToken)
    {
        await _settlementGate.WaitAsync(cancellationToken);
        try
        {
            mutation();
            await workspace.SaveAsync(project, cancellationToken);
        }
        finally
        {
            _settlementGate.Release();
        }
    }

    private static TranslationUsage NormalizeSuccessfulUsage(
        TranslationUsage? usage,
        CloudUsageEstimate estimate)
    {
        if (TotalTokens(usage) > 0)
        {
            return usage!;
        }

        return new TranslationUsage(
            estimate.FallbackInputTokens,
            estimate.FallbackOutputTokens,
            ApiRequests: 1);
    }

    private static CloudUsageEstimate EstimateUsage(TranslationSceneRequest request)
    {
        var promptCharacters = TranslationPromptBuilder.SystemPrompt.Length
            + TranslationPromptBuilder.JsonOutputInstruction.Length
            + TranslationPromptBuilder.BuildUserPrompt(request).Length;
        var estimatedInput = Math.Max(128L, (long)Math.Ceiling(promptCharacters / 2d));
        var expectedOutputCharacters = request.Cues
            .Where(item => item.IsTarget)
            .Sum(item => Math.Max(32, item.SuggestedMaximumCharacters));
        var estimatedOutput = Math.Max(256L, (long)Math.Ceiling(expectedOutputCharacters * 1.5d) + 180);
        return new CloudUsageEstimate(
            estimatedInput,
            estimatedOutput,
            checked(estimatedInput * ProviderMaximumAttempts),
            checked(estimatedOutput * ProviderMaximumAttempts));
    }

    private static CommitCloudUsageApiRequest ToCommitRequest(CloudUsageSettlement settlement) => new(
        settlement.ActualInputUnits,
        settlement.ActualOutputUnits,
        settlement.CachedInputUnits,
        settlement.ApiRequests,
        settlement.RetryRequests,
        ProviderRequestId: null);

    private static long TotalTokens(TranslationUsage? usage) => usage is null
        ? 0
        : Math.Max(0, usage.InputTokens) + Math.Max(0, usage.OutputTokens);

    private static bool IsUnknownProviderOutcome(TranslationProviderException exception) =>
        exception.Code is "TRANSLATION_PROVIDER_TIMEOUT" or "TRANSLATION_NETWORK_ERROR";

    private static bool IsRetryableServerError(ApiClientException exception) =>
        exception.StatusCode >= 500 || exception.Code is "SERVER_UNAVAILABLE" or "CLOUD_CONTROL_UNAVAILABLE";

    private sealed record CloudUsageEstimate(
        long FallbackInputTokens,
        long FallbackOutputTokens,
        long ReservedInputTokens,
        long ReservedOutputTokens);
}

public sealed class CloudUsageSettlementReconciler(
    IDesktopCloudAccessGateway cloudAccess,
    ProjectWorkspaceService workspace)
{
    public async Task ReconcileAsync(ProjectManifest project, CancellationToken cancellationToken)
    {
        var changed = false;
        foreach (var settlement in project.Jobs
            .SelectMany(job => job.CloudSettlements ?? [])
            .Where(item => item.ReservationId is not null
                && item.Status is "PENDING_COMMIT" or "PENDING_RELEASE" or "UNKNOWN"))
        {
            try
            {
                var reservationId = settlement.ReservationId!.Value;
                CloudReservationApiResponse response;
                if (settlement.Status == "PENDING_COMMIT")
                {
                    response = await cloudAccess.CommitAsync(
                        reservationId,
                        ToCommitRequest(settlement),
                        cancellationToken);
                }
                else if (settlement.Status == "PENDING_RELEASE")
                {
                    response = await cloudAccess.ReleaseAsync(reservationId, cancellationToken);
                }
                else
                {
                    response = await cloudAccess.GetStatusAsync(reservationId, cancellationToken);
                    if (response.Status == "HELD")
                    {
                        continue;
                    }
                }

                settlement.Status = response.Status;
                settlement.ErrorMessage = null;
                settlement.UpdatedAtUtc = DateTime.UtcNow;
                changed = true;
            }
            catch (Exception exception) when (exception is ApiClientException or HttpRequestException or TaskCanceledException)
            {
                settlement.ErrorMessage = exception.Message;
                settlement.UpdatedAtUtc = DateTime.UtcNow;
                changed = true;
            }
        }

        if (changed)
        {
            await workspace.SaveAsync(project, CancellationToken.None);
        }
    }

    private static CommitCloudUsageApiRequest ToCommitRequest(CloudUsageSettlement settlement) => new(
        settlement.ActualInputUnits,
        settlement.ActualOutputUnits,
        settlement.CachedInputUnits,
        settlement.ApiRequests,
        settlement.RetryRequests,
        ProviderRequestId: null);
}
