namespace SubVid.App.Core;

internal sealed record WebViewRenderingConfiguration(
    string Mode,
    string AdditionalBrowserArguments)
{
    public bool UsesSoftwareComposition =>
        string.Equals(Mode, WebViewRenderingPolicy.SoftwareMode, StringComparison.Ordinal);
}

internal static class WebViewRenderingPolicy
{
    internal const string EnvironmentVariableName = "SUBVID_WEBVIEW_COMPOSITION_MODE";
    internal const string HardwareMode = "hardware";
    internal const string SoftwareMode = "software";
    internal const string SoftwareCompositionArgument = "--disable-gpu-compositing";

    public static WebViewRenderingConfiguration Resolve(string? configuredMode)
    {
        // WebView2 can paint a correct internal surface while DirectComposition presents
        // only the host background. Software page composition bypasses that presentation
        // path while keeping hardware video decoding available.
        if (string.Equals(
                configuredMode?.Trim(),
                HardwareMode,
                StringComparison.OrdinalIgnoreCase))
        {
            return new WebViewRenderingConfiguration(HardwareMode, string.Empty);
        }

        return new WebViewRenderingConfiguration(
            SoftwareMode,
            SoftwareCompositionArgument);
    }
}
