using FluentGpu.WindowsApi.Activation;
using Xunit;

namespace FluentGpu.Windows.Tests;

/// <summary>
/// How a sign-in start reaches the app: the unpackaged <c>Run</c> value carries arguments
/// (<see cref="ProtocolRegistrar.RegisterStartup"/>), a switch never disturbs the command-line classification, and a
/// packaged <c>StartupTask</c> launch is recognised from the platform activation kind (<see cref="ActivationArgs.Refine"/>).
/// Pure — no registry write, no WinRT call.
/// </summary>
public sealed class StartupActivationTests
{
    [Fact]
    public void The_run_value_is_the_quoted_exe_alone_without_arguments()
    {
        Assert.Equal("\"C:\\Program Files\\Wavee\\Wavee.exe\"",
            ProtocolRegistrar.StartupCommandLine(@"C:\Program Files\Wavee\Wavee.exe", null));
        Assert.Equal("\"C:\\w\\Wavee.exe\"", ProtocolRegistrar.StartupCommandLine(@"C:\w\Wavee.exe", "   "));
    }

    [Fact]
    public void Arguments_follow_the_quoted_exe_after_one_space()
    {
        Assert.Equal("\"C:\\Program Files\\Wavee\\Wavee.exe\" --tray",
            ProtocolRegistrar.StartupCommandLine(@"C:\Program Files\Wavee\Wavee.exe", " --tray "));
        Assert.Equal("\"C:\\w\\Wavee.exe\" --tray \"--profile=a b\"",
            ProtocolRegistrar.StartupCommandLine(@"C:\w\Wavee.exe", "--tray \"--profile=a b\""));
    }

    [Fact]
    public void A_startup_switch_is_still_a_plain_launch_on_the_command_line()
    {
        Assert.Equal(ActivationKind.Launch, ActivationArgs.Classify(["--tray"], "wavee").Kind);
        // …and never hides a deep link that rides the same command line.
        var link = ActivationArgs.Classify(["--tray", "wavee://open"], "wavee");
        Assert.Equal(ActivationKind.Protocol, link.Kind);
        Assert.Equal("wavee://open", link.Argument);
    }

    [Fact]
    public void A_packaged_startup_task_launch_becomes_StartupTask_with_its_task_id()
    {
        var launch = new ActivationArgs(ActivationKind.Launch, string.Empty);
        var refined = ActivationArgs.Refine(launch, platformKind: 1020, startupTaskId: "WaveeStartup");
        Assert.Equal(ActivationKind.StartupTask, refined.Kind);
        Assert.Equal("WaveeStartup", refined.Argument);
        Assert.Equal(string.Empty, ActivationArgs.Refine(launch, 1020, null).Argument);
        Assert.Equal(1020, (int)ActivationKind.StartupTask);   // the platform's own value
    }

    [Theory]
    [InlineData(0)]      // Launch (a Start-menu click)
    [InlineData(4)]      // Protocol
    [InlineData(1010)]   // ToastNotification
    [InlineData(1021)]   // CommandLineLaunch
    public void Any_other_platform_kind_leaves_a_launch_alone(int platformKind)
    {
        var launch = new ActivationArgs(ActivationKind.Launch, string.Empty);
        Assert.Equal(launch, ActivationArgs.Refine(launch, platformKind, "WaveeStartup"));
    }

    [Fact]
    public void The_command_line_wins_over_the_platform_when_it_says_more_than_launch()
    {
        var link = new ActivationArgs(ActivationKind.Protocol, "wavee://play");
        Assert.Equal(link, ActivationArgs.Refine(link, platformKind: 1020, startupTaskId: "WaveeStartup"));
        var toast = new ActivationArgs(ActivationKind.ToastActivated, "action=open");
        Assert.Equal(toast, ActivationArgs.Refine(toast, 1020, "WaveeStartup"));
    }
}
