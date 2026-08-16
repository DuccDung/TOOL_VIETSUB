using SubVid.App.Api;

namespace SubVid.App.Usage;

public interface IDesktopQuotaGateway
{
    Task<QuotaReservationApiResponse> ReserveAsync(
        ReserveQuotaApiRequest request,
        CancellationToken cancellationToken);

    Task<QuotaReservationApiResponse> CommitAsync(
        Guid reservationId,
        decimal actualMinutes,
        CancellationToken cancellationToken);

    Task<QuotaReservationApiResponse> ReleaseAsync(
        Guid reservationId,
        CancellationToken cancellationToken);
}

public sealed class DesktopQuotaGateway(AuthSessionManager auth) : IDesktopQuotaGateway
{
    public Task<QuotaReservationApiResponse> ReserveAsync(
        ReserveQuotaApiRequest request,
        CancellationToken cancellationToken) =>
        auth.ExecuteAuthenticatedAsync(
            (api, token) => api.ReserveQuotaAsync(request, token),
            cancellationToken);

    public Task<QuotaReservationApiResponse> CommitAsync(
        Guid reservationId,
        decimal actualMinutes,
        CancellationToken cancellationToken) =>
        auth.ExecuteAuthenticatedAsync(
            (api, token) => api.CommitQuotaAsync(
                reservationId,
                new CommitQuotaApiRequest(actualMinutes),
                token),
            cancellationToken);

    public Task<QuotaReservationApiResponse> ReleaseAsync(
        Guid reservationId,
        CancellationToken cancellationToken) =>
        auth.ExecuteAuthenticatedAsync(
            (api, token) => api.ReleaseQuotaAsync(reservationId, token),
            cancellationToken);
}
