using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using SubVid.Server.Contracts;

namespace SubVid.App.Tests;

public sealed class CloudRequestValidationMetadataTests
{
    [Fact]
    public void AuthorizeRequest_InvalidEstimate_IsRejectedWithoutRecordMetadataException()
    {
        var request = new AuthorizeCloudAccessRequest(
            Guid.NewGuid(),
            null,
            Guid.NewGuid(),
            "TRANSLATION",
            "openai",
            "gpt-test",
            -1,
            100);

        var modelState = Validate(request);

        Assert.False(modelState.IsValid);
        Assert.Contains(modelState, entry =>
            entry.Key.EndsWith(nameof(AuthorizeCloudAccessRequest.EstimatedInputTokens), StringComparison.Ordinal));
    }

    [Fact]
    public void CommitRequest_InvalidUsage_IsRejectedWithoutRecordMetadataException()
    {
        var request = new CommitCloudUsageRequest(
            100,
            -1,
            0,
            1,
            0,
            "provider-request");

        var modelState = Validate(request);

        Assert.False(modelState.IsValid);
        Assert.Contains(modelState, entry =>
            entry.Key.EndsWith(nameof(CommitCloudUsageRequest.OutputTokens), StringComparison.Ordinal));
    }

    private static ModelStateDictionary Validate<T>(T model)
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddMvcCore()
            .AddDataAnnotations()
            .Services
            .BuildServiceProvider();
        var validator = services.GetRequiredService<IObjectModelValidator>();
        var modelState = new ModelStateDictionary();
        var actionContext = new ActionContext(
            new DefaultHttpContext { RequestServices = services },
            new RouteData(),
            new ActionDescriptor(),
            modelState);

        validator.Validate(actionContext, validationState: null, prefix: string.Empty, model!);
        return modelState;
    }
}
