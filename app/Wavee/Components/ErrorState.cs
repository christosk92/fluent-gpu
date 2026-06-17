using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>Friendly ERROR state — never the raw exception. A neutral message + a Retry that re-runs the loader.
/// The technical detail goes to the log (visible in the Diagnostics page), not to the user.</summary>
public static class ErrorState
{
    public static Element Build(Exception? error = null, Action? onRetry = null, string message = "Something went wrong.")
    {
        WaveeLog.Instance.Log(WaveeLogLevel.Warning, "ui",
            error is null ? "Surface error shown" : "Surface error shown: " + error.Message, error);

        var kids = new List<Element>
        {
            Icon(Icons.Cancel, 32f, Tok.SystemFillCritical),
            new BoxEl { Height = WaveeSpace.M },
            WaveeType.RailHeader(message),
            WaveeType.TrackMeta("Check your connection and try again."),
        };
        if (onRetry is not null)
        {
            kids.Add(new BoxEl { Height = WaveeSpace.L });
            kids.Add(Button.Accent("Retry", onRetry));
        }
        return EmptyState.Centered(kids);
    }
}
