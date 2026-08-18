using AnalogHwMonitor.Core;
using Xunit;

namespace AnalogHwMonitor.Tests;

public class FileLogTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "ahm-log-" + Guid.NewGuid().ToString("N"));

    public FileLogTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string LogPath => Path.Combine(_directory, "log.txt");

    private static Func<DateTimeOffset> FixedClock =>
        () => new DateTimeOffset(2026, 8, 18, 14, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Write_AppendsTimestampedLines()
    {
        var log = new FileLog(LogPath, clock: FixedClock);

        log.Write("first");
        log.Write("second");

        var lines = File.ReadAllLines(LogPath);
        Assert.Equal(2, lines.Length);
        Assert.Equal("2026-08-18 14:30:00 first", lines[0]);
        Assert.Equal("2026-08-18 14:30:00 second", lines[1]);
    }

    [Fact]
    public void OldPath_SitsNextToTheLog()
    {
        var log = new FileLog(LogPath);

        Assert.Equal(Path.Combine(_directory, "log.old.txt"), log.OldPath);
    }

    [Fact]
    public void Write_RotatesOnceTheLogPassesItsSizeLimit()
    {
        var log = new FileLog(LogPath, maxBytes: 40, clock: FixedClock);

        log.Write("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");   // pushes the file past 40 bytes
        log.Write("after rotation");

        Assert.Contains("aaaaaaaaaa", File.ReadAllText(log.OldPath));
        Assert.Equal("2026-08-18 14:30:00 after rotation", File.ReadAllText(LogPath).TrimEnd());
    }

    [Fact]
    public void Write_OverwritesAPreviousRotation()
    {
        File.WriteAllText(Path.Combine(_directory, "log.old.txt"), "stale");
        var log = new FileLog(LogPath, maxBytes: 40, clock: FixedClock);

        log.Write("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        log.Write("after rotation");

        Assert.DoesNotContain("stale", File.ReadAllText(log.OldPath));
    }

    [Fact]
    public void Write_DoesNotThrowWhenFileCannotBeWritten()
    {
        File.WriteAllText(LogPath, "initial");
        var log = new FileLog(LogPath, clock: FixedClock);

        FileStream stream = new(LogPath, FileMode.Open, FileAccess.Read, FileShare.None);
        try
        {
            // File is locked; Write should not throw despite failing to append
            log.Write("should be dropped");
        }
        finally
        {
            stream.Dispose();
        }
    }
}
