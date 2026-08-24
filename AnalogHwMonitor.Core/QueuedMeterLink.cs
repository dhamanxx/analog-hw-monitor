using System.Threading.Channels;

namespace AnalogHwMonitor.Core;

/// <summary>
/// Decouples sending a frame from the caller. <see cref="Send"/> now means "enqueue" — a
/// mild lie in a name inherited from <see cref="IMeterLink"/>, and this comment is what
/// pays for it. An honester name would cost a changed MonitorService constructor, its
/// rewritten tests and a new fake, all for identical behaviour.
///
/// The reason is that <see cref="SerialMeterLink.Send"/> is a blocking write and it used
/// to run on the UI thread. On a wedged adapter that is not microseconds but seconds.
///
/// The channel holds one frame with <see cref="BoundedChannelFullMode.DropOldest"/>: a
/// stale frame is worthless to a needle, so while the write is slow the intermediate
/// frames are dropped and only the newest is sent. This does not paper over a throughput
/// problem — a frame is ~22 bytes, ~1.5 ms at 115200 baud, so about 3 % of the link at
/// 21 Hz. The capacity of one is there for the pathological case, not the normal one.
///
/// Async I/O is deliberately not introduced. To the rest of the application a blocking
/// Write on a dedicated task is exactly as non-blocking as WriteAsync, and it is a class
/// less code.
/// </summary>
public sealed class QueuedMeterLink : IMeterLink
{
    private readonly IMeterLink _inner;

    private readonly Channel<string> _frames = Channel.CreateBounded<string>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,   // sender task
            SingleWriter = true,   // UI tick
        });

    public QueuedMeterLink(IMeterLink inner) => _inner = inner;

    public bool IsConnected => _inner.IsConnected;

    public string? LastError => _inner.LastError;

    /// <summary>
    /// Enqueues a frame and returns. Never blocks and never throws: <c>DropOldest</c> makes
    /// <c>TryWrite</c> always succeed, and after <see cref="Dispose"/> a <c>false</c> return
    /// only means "we are shutting down", not a failure.
    /// </summary>
    public void Send(string frame) => _frames.Writer.TryWrite(frame);

    /// <summary>
    /// Must be started through <c>Task.Run</c>. Called straight from the UI thread it would
    /// marshal its continuations back onto it through the WinForms SynchronizationContext
    /// and the write would stay on the UI thread — the whole change would be a no-op.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var frame in _frames.Reader
                               .ReadAllAsync(cancellationToken)
                               .ConfigureAwait(false))
            {
                _inner.Send(frame);
            }
        }
        catch (OperationCanceledException)
        {
            // A normal shutdown.
        }
    }

    public void Dispose()
    {
        _frames.Writer.TryComplete();
        _inner.Dispose();
    }
}
