namespace AnalogHwMonitor.Core;

/// <summary>
/// Appends to log.txt next to the executable and rotates to log.old.txt once the
/// file passes its size limit. A background process needs some way to say what
/// happened while nobody was watching.
/// </summary>
public sealed class FileLog : IAppLog
{
    private readonly long _maxBytes;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _gate = new();

    public FileLog(string path, long maxBytes = 1_048_576, Func<DateTimeOffset>? clock = null)
    {
        Path = path;
        _maxBytes = maxBytes;
        _clock = clock ?? (() => DateTimeOffset.Now);

        var directory = System.IO.Path.GetDirectoryName(path) ?? string.Empty;
        var name = System.IO.Path.GetFileNameWithoutExtension(path);
        var extension = System.IO.Path.GetExtension(path);
        OldPath = System.IO.Path.Combine(directory, name + ".old" + extension);
    }

    public string Path { get; }

    public string OldPath { get; }

    public void Write(string message)
    {
        lock (_gate)
        {
            var info = new FileInfo(Path);
            if (info.Exists && info.Length >= _maxBytes)
            {
                File.Move(Path, OldPath, overwrite: true);
            }

            File.AppendAllText(
                Path,
                $"{_clock():yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
    }
}
