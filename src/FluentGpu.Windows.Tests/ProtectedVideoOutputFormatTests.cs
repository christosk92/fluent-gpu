using System;
using System.IO;
using FluentGpu.Media;
using FluentGpu.WindowsApi.Media.PlayReady;
using Xunit;

namespace FluentGpu.Windows.Tests;

/// <summary>
/// F249 (protected engine): the media engine's output format is chosen on the managed side and handed to the native runtime BEFORE the
/// create reads it (<c>FgPrRuntimeSetVideoOutputFormat</c>), over <see cref="FakeRuntimeNative"/>. The default - the switch off - is
/// BGRA, exactly the format the engine has always been forced to; the NV12 choice itself is the pure
/// <see cref="VideoOverlayCaps.ChooseOutputFormat"/>, covered in the engine tests.
/// </summary>
public sealed class ProtectedVideoOutputFormatTests
{
    [Fact]
    public void WithTheNv12SwitchOff_TheNativeCreateIsAskedForBgra_SetBeforeTheCreate()
    {
        var native = new FakeRuntimeNative();
        string store = Path.Combine(Path.GetTempPath(), "fluentgpu-playready-tests", Guid.NewGuid().ToString("N"));
        using var runtime = new ProtectedVideoRuntime(native, store, 60_000, new FakeSessionNative(), () => 0L, 0);
        try
        {
            Assert.True(runtime.Acquire());
            Assert.Equal(0, native.LastVideoOutputFormat);                   // BGRA: nothing asks for NV12 until --fg video-nv12 is on
            Assert.Equal(new[] { 0 }, native.VideoOutputFormatsAtCreate);    // and the value was in force when the runtime was created
        }
        finally
        {
            try { if (Directory.Exists(store)) Directory.Delete(store, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
