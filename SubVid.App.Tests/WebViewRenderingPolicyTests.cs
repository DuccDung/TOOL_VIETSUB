using SubVid.App.Core;

namespace SubVid.App.Tests;

public sealed class WebViewRenderingPolicyTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("software")]
    [InlineData(" SOFTWARE ")]
    [InlineData("unsupported-value")]
    public void Resolve_DefaultsToSafeSoftwareComposition(string? configuredMode)
    {
        var configuration = WebViewRenderingPolicy.Resolve(configuredMode);

        Assert.Equal(WebViewRenderingPolicy.SoftwareMode, configuration.Mode);
        Assert.Equal(
            WebViewRenderingPolicy.SoftwareCompositionArgument,
            configuration.AdditionalBrowserArguments);
        Assert.True(configuration.UsesSoftwareComposition);
    }

    [Theory]
    [InlineData("hardware")]
    [InlineData(" HARDWARE ")]
    [InlineData("HaRdWaRe")]
    public void Resolve_AllowsExplicitHardwareComposition(string configuredMode)
    {
        var configuration = WebViewRenderingPolicy.Resolve(configuredMode);

        Assert.Equal(WebViewRenderingPolicy.HardwareMode, configuration.Mode);
        Assert.Empty(configuration.AdditionalBrowserArguments);
        Assert.False(configuration.UsesSoftwareComposition);
    }
}
