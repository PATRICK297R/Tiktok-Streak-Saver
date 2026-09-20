using TiktokStreakSaver.Services;
using Xunit;

namespace TiktokStreakSaver.Tests;

/// <summary>
/// The rule these pin down: the app works in local wall-clock time, the file holds instants. Getting it
/// wrong is invisible in a UTC test environment and wrong on a real device - a run at 06:00 in UTC+7 is
/// 23:00 the previous day in UTC, so a morning run was filed under yesterday and the day it belonged to
/// showed no progress at all.
/// </summary>
public class AppTimestampsTests
{
    /// <summary>
    /// The bug, stated as a rule. What comes back from the file has to be a local time, because every
    /// question the app asks about it - "was this today?", "how long ago?" - is a local question. A UTC
    /// value here is the whole defect, and this assertion holds in any time zone.
    /// </summary>
    [Fact]
    public void AStoredTimestampReadsBackAsLocalTime()
    {
        var local = new DateTime(2026, 9, 18, 6, 0, 0, DateTimeKind.Local);

        var readBack = AppTimestamps.FromStored(AppTimestamps.ToStored(local));

        Assert.Equal(DateTimeKind.Local, readBack.Kind);
    }

    [Fact]
    public void ARoundTripKeepsTheSameMomentAndTheSameWallClock()
    {
        var local = new DateTime(2026, 9, 18, 6, 0, 0, DateTimeKind.Local);

        var readBack = AppTimestamps.FromStored(AppTimestamps.ToStored(local));

        Assert.Equal(local, readBack);
        Assert.Equal(local.ToUniversalTime(), readBack.ToUniversalTime());
    }

    [Fact]
    public void ARoundTripKeepsTheLocalDay()
    {
        // The date the run is filed under, which is what the dashboard's progress is grouped by.
        var local = new DateTime(2026, 9, 18, 6, 0, 0, DateTimeKind.Local);

        Assert.Equal(local.Date, AppTimestamps.FromStored(AppTimestamps.ToStored(local)).Date);
    }

    [Fact]
    public void WhatIsWrittenIsAnInstant()
    {
        var local = new DateTime(2026, 9, 18, 6, 0, 0, DateTimeKind.Local);

        var stored = AppTimestamps.ToStored(local);

        Assert.Equal(DateTimeKind.Utc, stored.Kind);
        Assert.Equal(local.ToUniversalTime(), stored);
    }

    /// <summary>
    /// Entries written by older builds carry no zone at all. They are local wall-clock times - that is what
    /// this app has always recorded - and converting one would move it by the device's offset, changing the
    /// day it belongs to and re-filing history that was correct.
    /// </summary>
    [Fact]
    public void ALegacyTimestampWithNoZoneIsNotShifted()
    {
        var legacy = new DateTime(2026, 9, 18, 6, 0, 0, DateTimeKind.Unspecified);

        var readBack = AppTimestamps.FromStored(legacy);

        Assert.Equal(legacy, readBack);
        Assert.Equal(new DateTime(2026, 9, 18), readBack.Date);
    }

    [Fact]
    public void ALegacyTimestampWithNoZoneIsWrittenAsTheLocalTimeItMeans()
    {
        var legacy = new DateTime(2026, 9, 18, 6, 0, 0, DateTimeKind.Unspecified);

        var stored = AppTimestamps.ToStored(legacy);

        // Read as local on the way out, so re-saving an old entry cannot move it.
        Assert.Equal(legacy, AppTimestamps.FromStored(stored));
    }

    [Fact]
    public void AnInstantIsLeftAloneOnTheWayOut()
    {
        var utc = new DateTime(2026, 9, 17, 23, 0, 0, DateTimeKind.Utc);

        Assert.Equal(utc, AppTimestamps.ToStored(utc));
    }
}
