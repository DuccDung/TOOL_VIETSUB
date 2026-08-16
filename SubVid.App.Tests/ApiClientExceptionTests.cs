using SubVid.App.Api;

namespace SubVid.App.Tests;

public sealed class ApiClientExceptionTests
{
    [Fact]
    public void QuotaForbidden_DoesNotInvalidateAuthenticatedSession()
    {
        var exception = new ApiClientException(
            "QUOTA_FEATURE_NOT_INCLUDED",
            "Gói hiện tại không hỗ trợ tính năng này.",
            403);

        Assert.False(exception.IsAuthenticationFailure);
    }

    [Fact]
    public void GenericForbidden_DoesNotInvalidateAuthenticatedSession()
    {
        var exception = new ApiClientException(
            "AUTH_FORBIDDEN",
            "Tài khoản không có quyền thực hiện thao tác này.",
            403);

        Assert.False(exception.IsAuthenticationFailure);
    }

    [Theory]
    [InlineData("AUTH_REQUIRED", 401)]
    [InlineData("AUTH_REFRESH_EXPIRED", 403)]
    [InlineData("AUTH_ACCOUNT_UNAVAILABLE", 403)]
    [InlineData("AUTH_DEVICE_MISMATCH", 403)]
    public void InvalidOrExpiredAuthentication_StillInvalidatesSession(string code, int statusCode)
    {
        var exception = new ApiClientException(code, "Authentication failed.", statusCode);

        Assert.True(exception.IsAuthenticationFailure);
    }
}
