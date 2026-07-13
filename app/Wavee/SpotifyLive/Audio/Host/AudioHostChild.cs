using System.IO.Pipes;
using System.Runtime;

namespace Wavee.SpotifyLive.Audio.Host;

public static class AudioHostChild
{
    public static int Run(string[] args)
    {
        string? pipeName = null;
        string? token = null;

        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--pipe") pipeName = args[i + 1];
            else if (args[i] == "--token") token = args[i + 1];
        }

        string logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wavee", "logs");
        string logPath = Path.Combine(logDir, "audio-child-" + Environment.ProcessId + ".log");
        WaveeLog.Instance.Configure(crashLogPath: logPath, echo: null,
            minLevel: WaveeLogLevel.Info, fileMinLevel: WaveeLogLevel.Info);

        try { GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency; } catch { }

        if (string.IsNullOrWhiteSpace(pipeName))
        {
            WaveeLog.Instance.Error("audio", "audio.host.missing_pipe", "Audio host child missing --pipe");
            return 2;
        }

        try
        {
            WaveeLog.Instance.Info("audio", "audio.host.start", "Audio host child starting",
                WaveeLogField.Of("pid", Environment.ProcessId),
                WaveeLogField.Of("pipe", pipeName),
                WaveeLogField.Of("log", logPath));

            using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            server.WaitForConnection();
            using var transport = IpcPipeTransport.FromServerStream(server);
            var host = new AudioHostServer(transport, token, new WaveeLogger(WaveeLog.Instance, "audio"));
            host.RunAsync().GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception ex) when (ex is EndOfStreamException or IOException)
        {
            // The parent closes the pipe for runtime rebinding, app shutdown, and supervised recycle. That ends this
            // child normally; it is not an audio-engine crash and should not pollute incident logs as Critical.
            WaveeLog.Instance.Info("audio", "audio.host.pipe_closed", "Audio host parent pipe closed",
                WaveeLogField.Of("error", ex.GetType().Name), WaveeLogField.Of("detail", ex.Message));
            return 0;
        }
        catch (Exception ex)
        {
            WaveeLog.Instance.Critical("audio", "Audio host child crashed", ex);
            return 1;
        }
        finally
        {
            WaveeLog.Instance.FlushForTests();
        }
    }
}
