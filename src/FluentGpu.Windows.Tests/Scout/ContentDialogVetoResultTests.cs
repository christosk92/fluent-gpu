using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Signals;
using Xunit;

namespace FluentGpu.Windows.Tests.Scout;

/// <summary>A ContentDialog whose Closing handler vetoes a button close (e.g. a delete still in flight) kept that button's
/// result: once the veto lifted, Escape or a programmatic close reported Closed(Primary), and an app acting on the
/// result deleted the playlist the user had just cancelled. WinUI computes the result per close attempt.</summary>
public sealed class ContentDialogVetoResultTests
{
    private sealed class Rig
    {
        public readonly OverlayServiceImpl Svc = new(new Signal<int>(0));
        public readonly List<ContentDialogResult> Results = new();
        public readonly OverlayHandle Handle;
        public bool Busy = true;

        public Rig()
        {
            Handle = ContentDialog.Show(Svc, d =>
            {
                d.Title = "Delete playlist?";
                d.PrimaryText = "Delete";
                d.CloseText = "Cancel";
                d.Closing = a => a.Cancel = Busy;
                d.Closed = Results.Add;
            });
        }

        // Enter routes to the default (Primary) button from anywhere in the card.
        public void PressPrimary() => ((BoxEl)Svc.Entries[0].Content()).OnKeyDown!(new KeyEventArgs(Keys.Enter));
        public void FinishClose() => Svc.Finalize(Svc.Entries[0]);
    }

    [Fact]
    public void Escape_after_a_vetoed_primary_reports_None()
    {
        var r = new Rig();
        r.PressPrimary();
        Assert.True(r.Handle.IsOpen);          // Closing vetoed the button close

        r.Busy = false;
        r.Svc.PreviewKey(Keys.Escape);
        Assert.False(r.Handle.IsOpen);
        r.FinishClose();

        Assert.Equal(new[] { ContentDialogResult.None }, r.Results);
    }

    [Fact]
    public void Programmatic_close_after_a_vetoed_primary_reports_None()
    {
        var r = new Rig();
        r.PressPrimary();
        r.Busy = false;
        r.Handle.Close();
        r.FinishClose();

        Assert.Equal(new[] { ContentDialogResult.None }, r.Results);
    }

    [Fact]
    public void An_unvetoed_primary_still_reports_Primary()
    {
        var r = new Rig { Busy = false };
        r.PressPrimary();
        r.FinishClose();

        Assert.Equal(new[] { ContentDialogResult.Primary }, r.Results);
    }
}
