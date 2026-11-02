using System.IO;
using System.Text;

namespace ForzaOverlay.Core.Logging;

public static class Diagnostics
{
    private const int RetainDays = 7;

    private static readonly object Gate = new();
    private static readonly string LogDirectory = Path.Combine(AppContext.BaseDirectory, "logs");

    private static string? _currentFile;

    public static string LogDirectoryPath => LogDirectory;

    public static void BeginRun()
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(LogDirectory);
                PruneOldFiles();

                _currentFile = Path.Combine(LogDirectory, $"run-{DateTime.Now:yyyyMMdd-HHmmss}.log");
                File.WriteAllText(_currentFile, string.Empty);
                AppendLine("===== 程序启动 =====");
            }
            catch (Exception)
            {
            }
        }
    }

    public static void Write(string message)
    {
        lock (Gate)
        {
            AppendLine(message);
        }
    }

    public static void WriteException(string context, Exception exception)
    {
        StringBuilder builder = new();
        builder.Append('[').Append(context).Append("] ")
            .Append(exception.GetType().Name).Append(": ").Append(exception.Message);

        if (!string.IsNullOrWhiteSpace(exception.StackTrace))
        {
            builder.AppendLine();
            builder.Append(exception.StackTrace);
        }

        for (Exception? inner = exception.InnerException; inner is not null; inner = inner.InnerException)
        {
            builder.AppendLine();
            builder.Append("  -> ").Append(inner.GetType().Name).Append(": ").Append(inner.Message);
        }

        Write(builder.ToString());
    }
    public static string ReadTail(int maxLines)
    {
        lock (Gate)
        {
            try
            {
                if (_currentFile is null || !File.Exists(_currentFile))
                {
                    return string.Empty;
                }

                using FileStream stream = new(_currentFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using StreamReader reader = new(stream);
                Queue<string> lines = new(maxLines);

                while (reader.ReadLine() is { } line)
                {
                    lines.Enqueue(line);
                    if (lines.Count > maxLines)
                    {
                        lines.Dequeue();
                    }
                }

                return string.Join(Environment.NewLine, lines);
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }
    }

    private static void AppendLine(string message)
    {
        try
        {
            if (_currentFile is null)
            {
                return;
            }

            File.AppendAllText(_currentFile, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch (Exception)
        {
        }
    }

    private static void PruneOldFiles()
    {
        DateTime threshold = DateTime.Now.AddDays(-RetainDays);

        foreach (string file in Directory.GetFiles(LogDirectory, "*.log"))
        {
            try
            {
                if (File.GetLastWriteTime(file) < threshold)
                {
                    File.Delete(file);
                }
            }
            catch (Exception)
            {
            }
        }
    }
}
