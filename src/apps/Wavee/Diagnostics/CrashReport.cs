using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Wavee;

static class CrashReport
{
    public static string Write(Exception ex, string? logPath)
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wavee", "Logs");
        Directory.CreateDirectory(dir);
        string stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string path = Path.Combine(dir, $"crash-report-{stamp}.txt");

        var sb = new StringBuilder(32 * 1024);
        sb.AppendLine("Wavee crash report");
        sb.AppendLine("=================");
        sb.AppendLine("timeLocal=" + DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture));
        sb.AppendLine("pid=" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        sb.AppendLine("framework=" + System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);
        sb.AppendLine("os=" + System.Runtime.InteropServices.RuntimeInformation.OSDescription);
        sb.AppendLine();
        sb.AppendLine("Exception");
        sb.AppendLine("---------");
        sb.AppendLine(ex.ToString());

        if (!string.IsNullOrWhiteSpace(logPath) && File.Exists(logPath))
        {
            sb.AppendLine();
            sb.AppendLine("wavee.log tail");
            sb.AppendLine("--------------");
            foreach (var line in TailLines(logPath, maxLines: 600))
                sb.AppendLine(line);
        }

        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    static IEnumerable<string> TailLines(string path, int maxLines)
    {
        var q = new Queue<string>(Math.Max(16, maxLines));
        foreach (var line in File.ReadLines(path))
        {
            if (q.Count == maxLines) q.Dequeue();
            q.Enqueue(line);
        }
        return q;
    }
}

