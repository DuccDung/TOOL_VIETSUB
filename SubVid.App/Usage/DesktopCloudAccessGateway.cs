using SubVid.App.Api;

namespace SubVid.App.Usage;

public interface IDesktopCloudAccessGateway
{
    Task<CloudAuthorizationApiResponse> AuthorizeAsync(
        AuthorizeCloudAccessApiRequest request,
        CancellationToken cancellationToken);

    Task<CloudReservationApiResponse> CommitAsync(
        Guid reservationId,
        CommitCloudUsageApiRequest request,
        CancellationToken cancellationToken);

    Task<CloudReservationApiResponse> ReleaseAsync(
        Guid reservationId,
        CancellationToken cancellationToken);

    Task<CloudReservationApiResponse> GetStatusAsync(
        Guid reservationId,
        CancellationToken cancellationToken);
}

public sealed class DesktopCloudAccessGateway(AuthSessionManager auth) : IDesktopCloudAccessGateway
{
    public Task<CloudAuthorizationApiResponse> AuthorizeAsync(
        AuthorizeCloudAccessApiRequest request,
        CancellationToken cancellationToken) =>
        auth.ExecuteAuthenticatedAsync(
            (api, token) => api.AuthorizeCloudAccessAsync(request, token),
            cancellationToken);

    public Task<CloudReservationApiResponse> CommitAsync(
        Guid reservationId,
        CommitCloudUsageApiRequest request,
        CancellationToken cancellationToken) =>
        auth.ExecuteAuthenticatedAsync(
            (api, token) => api.CommitCloudUsageAsync(reservationId, request, token),
            cancellationToken);

    public Task<CloudReservationApiResponse> ReleaseAsync(
        Guid reservationId,
        CancellationToken cancellationToken) =>
        auth.ExecuteAuthenticatedAsync(
            (api, token) => api.ReleaseCloudUsageAsync(reservationId, token),
            cancellationToken);

    public Task<CloudReservationApiResponse> GetStatusAsync(
        Guid reservationId,
        CancellationToken cancellationToken) =>
        auth.ExecuteAuthenticatedAsync(
            (api, token) => api.GetCloudReservationAsync(reservationId, token),
            cancellationToken);
}
