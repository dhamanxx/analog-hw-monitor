namespace AnalogHwMonitor.Tests.Fakes;

/// <summary>
/// A clock the test moves by hand, so nothing here waits on the wall clock. Hand
/// written rather than taken from Microsoft.Extensions.TimeProvider.Testing: five
/// lines are cheaper than a package reference, and this is the whole surface the
/// tests use.
/// </summary>
public sealed class FakeTimeProvider : TimeProvider
{
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
