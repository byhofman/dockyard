using Dockyard.Engine.Internal;

namespace Dockyard.Engine.Tests;

public sealed class RfcTimeTests
{
    [Fact]
    public void Parses_nanosecond_utc() =>
        Assert.Equal(new DateTimeOffset(2026, 11, 4, 10, 1, 2, TimeSpan.Zero).AddTicks(1234567),
            RfcTime.Parse("2026-11-04T10:01:02.123456789Z"));

    [Fact]
    public void Parses_offset_without_fraction() =>
        Assert.Equal(new DateTimeOffset(2026, 11, 4, 10, 1, 2, TimeSpan.FromHours(1)), RfcTime.Parse("2026-11-04T10:01:02+01:00"));

    [Fact]
    public void Returns_null_for_garbage() => Assert.Null(RfcTime.Parse("hello"));
}
