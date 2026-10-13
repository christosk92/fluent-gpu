using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using FluentGpu.Foundation;

namespace FluentGpu;

/// <summary>Cold run boundary: preserve the first failure before unwinding the same reverse-order disposable owners.
/// Resource-specific disposal still owns safety (including refusing release after a healthy GPU drain failure).</summary>
internal sealed class RunLifetime
{
    private readonly List<IDisposable> _owners = new(4);

    internal T Own<T>(T owner) where T : IDisposable { _owners.Add(owner); return owner; }

    internal static void Run(Action<RunLifetime> body, Action? beforeCleanup = null)
    {
        var lifetime = new RunLifetime();
        ExceptionDispatchInfo? first = null;
        List<Exception>? secondary = null;
        try { body(lifetime); }
        catch (Exception ex) { first = ExceptionDispatchInfo.Capture(ex); }
        // Publish the ended run before any owner starts disposal: late callbacks must not see a closing host/window.
        try { beforeCleanup?.Invoke(); }
        catch (Exception ex)
        {
            if (first is null) first = ExceptionDispatchInfo.Capture(ex);
            else (secondary ??= new()).Add(ex);
        }
        for (int i = lifetime._owners.Count - 1; i >= 0; i--)
        {
            try { lifetime._owners[i].Dispose(); }
            catch (Exception ex)
            {
                if (first is null) first = ExceptionDispatchInfo.Capture(ex);
                else (secondary ??= new()).Add(ex);
                // A diagnostic sink failing during unwinding must not replace the primary failure either.
                try { Diag.Line("[run.cleanup] dispose failed: " + ex); }
                catch (Exception) { }
            }
        }
        if (first is not null && secondary is not null)
        {
            // Custom exceptions can expose read-only or throwing Data dictionaries. Annotation is best-effort,
            // just like the log sink above: it must never replace the exception we are preserving.
            try { first.SourceException.Data["FluentGpu.CleanupFailures"] = secondary.ToArray(); }
            catch (Exception) { }
        }
        first?.Throw();
    }
}
