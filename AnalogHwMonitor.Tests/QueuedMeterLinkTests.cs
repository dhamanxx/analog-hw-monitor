using AnalogHwMonitor.Core;
using AnalogHwMonitor.Tests.Fakes;
using Xunit;

namespace AnalogHwMonitor.Tests;

public class QueuedMeterLinkTests
{
    /// <summary>Signals the first frame through a TaskCompletionSource, so the test
    /// never has to poll or read a List across a thread boundary.</summary>
    private sealed class SignallingMeterLink : IMeterLink
    {
        private readonly TaskCompletionSource<string> _first = new();

        public Task<string> First => _first.Task;

        public bool IsConnected => true;

        public string? LastError => null;

        public void Send(string frame) => _first.TrySetResult(frame);

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// The first write hangs until the test releases it — a jammed port. Both arrivals
    /// are signalled through a TaskCompletionSource, so the test never reads _frames
    /// while the pump could still be writing into it.
    /// </summary>
    private sealed class BlockingMeterLink : IMeterLink
    {
        private readonly ManualResetEventSlim _gate;
        private readonly TaskCompletionSource _firstArrived = new();
        private readonly TaskCompletionSource _secondArrived = new();
        private readonly List<string> _frames = new();
        private bool _blocked;

        public BlockingMeterLink(ManualResetEventSlim gate) => _gate = gate;

        public Task FirstArrived => _firstArrived.Task;

        public Task SecondArrived => _secondArrived.Task;

        /// <summary>Written exclusively by the pump. Read only after its task has finished.</summary>
        public IReadOnlyList<string> Frames => _frames;

        public bool IsConnected => true;

        public string? LastError => null;

        public void Send(string frame)
        {
            _frames.Add(frame);

            if (!_blocked)
            {
                _blocked = true;
                _firstArrived.TrySetResult();
                _gate.Wait();
                return;
            }

            _secondArrived.TrySetResult();
        }

        public void Dispose()
        {
        }
    }

    [Fact]
    public async Task Send_ReachesTheInnerLink()
    {
        var inner = new SignallingMeterLink();
        using var link = new QueuedMeterLink(inner);
        using var cts = new CancellationTokenSource();
        var pump = Task.Run(() => link.RunAsync(cts.Token));

        link.Send("V:1,2,3,4,5\n");

        Assert.Equal("V:1,2,3,4,5\n", await inner.First.WaitAsync(TimeSpan.FromSeconds(5)));

        cts.Cancel();
        await pump.WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// Capacity 1 and DropOldest: a stale frame is worthless to a needle, so while the
    /// write is stuck the intermediate frames are dropped and only the newest is sent.
    /// </summary>
    [Fact]
    public async Task Send_KeepsOnlyTheNewestFrameWhileTheLinkIsBusy()
    {
        using var gate = new ManualResetEventSlim(false);
        var inner = new BlockingMeterLink(gate);
        using var link = new QueuedMeterLink(inner);
        using var cts = new CancellationTokenSource();
        var pump = Task.Run(() => link.RunAsync(cts.Token));

        link.Send("frame-1");
        await inner.FirstArrived.WaitAsync(TimeSpan.FromSeconds(5));

        // The pump is now hanging inside Send("frame-1"). The channel holds one frame,
        // so frame-2 and frame-3 fall out and only frame-4 survives.
        link.Send("frame-2");
        link.Send("frame-3");
        link.Send("frame-4");

        gate.Set();
        await inner.SecondArrived.WaitAsync(TimeSpan.FromSeconds(5));

        cts.Cancel();
        await pump.WaitAsync(TimeSpan.FromSeconds(5));

        // Only here — the pump has finished, so nobody mutates _frames any more.
        Assert.Equal(new[] { "frame-1", "frame-4" }, inner.Frames);
    }

    [Fact]
    public void Send_DoesNotThrowAfterDispose()
    {
        var link = new QueuedMeterLink(new FakeMeterLink());
        link.Dispose();

        Assert.Null(Record.Exception(() => link.Send("V:0,0,0,0,0\n")));
    }

    [Fact]
    public void StatusDelegatesToTheInnerLink()
    {
        var inner = new FakeMeterLink { IsConnected = false, LastError = "COM7: denied" };
        using var link = new QueuedMeterLink(inner);

        Assert.False(link.IsConnected);
        Assert.Equal("COM7: denied", link.LastError);
    }

    [Fact]
    public void Dispose_DisposesTheInnerLink()
    {
        var inner = new RecordingDisposeLink();
        var link = new QueuedMeterLink(inner);

        link.Dispose();

        Assert.True(inner.Disposed);
    }

    private sealed class RecordingDisposeLink : IMeterLink
    {
        public bool Disposed { get; private set; }

        public bool IsConnected => true;

        public string? LastError => null;

        public void Send(string frame)
        {
        }

        public void Dispose() => Disposed = true;
    }
}
