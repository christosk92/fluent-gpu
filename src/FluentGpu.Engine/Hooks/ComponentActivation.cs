using FluentGpu.Signals;

namespace FluentGpu.Hooks;

/// <summary>The reconciler's per-component record, as <see cref="RenderContext.UseIsActive"/> sees it: get-or-create THIS
/// component's KeepAlive-parked signal (<see cref="RenderContext.ComponentActivation"/>; an interface on the record the
/// reconciler already allocates, so a mount costs no delegate for it).</summary>
internal interface IComponentActivation
{
    Signal<bool> ActiveSignal();
}

/// <summary>What <see cref="RenderContext.RequestRerender"/> schedules: the component's render (a row of the reconciler's
/// runtime, PERF r3 MODEL M3). The delegate is made on the first read of RequestRerender and kept.</summary>
internal interface IRerenderTarget
{
    Action CreateSchedule();
}
