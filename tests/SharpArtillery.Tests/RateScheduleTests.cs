using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace SharpArtillery.Tests;

public class RateScheduleTests
{
    [Fact]
    [Trait("slice", "7")]
    public void Slot_n_is_due_at_the_start_plus_n_over_rps()
    {
        var clock = new FakeTimeProvider();
        var four = new RateSchedule(4, clock, clock.GetTimestamp());
        var three = new RateSchedule(3, clock, clock.GetTimestamp());

        Assert.Equal(TimeSpan.Zero, four.DueAt(0));
        Assert.Equal(TimeSpan.FromMilliseconds(250), four.DueAt(1));
        Assert.Equal(TimeSpan.FromMilliseconds(750), four.DueAt(3));
        Assert.Equal(TimeSpan.FromSeconds(1), four.DueAt(4));
        // Computed from n, not by adding up rounded intervals, so a third of a second never drifts.
        Assert.Equal(TimeSpan.FromTicks(3_333_333), three.DueAt(1));
        Assert.Equal(TimeSpan.FromSeconds(100), three.DueAt(300));
    }

    [Fact]
    [Trait("slice", "7")]
    public async Task Clients_take_the_next_slot_of_one_shared_schedule()
    {
        var clock = new FakeTimeProvider();
        var schedule = new RateSchedule(10, clock, clock.GetTimestamp());

        // Three clients ask at the same moment: they get slots 0, 1 and 2, not one slot each "now".
        var first = schedule.WaitForSlotAsync(CancellationToken.None).AsTask();
        var second = schedule.WaitForSlotAsync(CancellationToken.None).AsTask();
        var third = schedule.WaitForSlotAsync(CancellationToken.None).AsTask();

        Assert.True(first.IsCompleted);
        Assert.False(second.IsCompleted);
        Assert.False(third.IsCompleted);

        clock.Advance(TimeSpan.FromMilliseconds(100));
        await second.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.False(third.IsCompleted);

        clock.Advance(TimeSpan.FromMilliseconds(100));
        await third.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(3, schedule.SlotsTaken);
    }

    // The old limiter slept a full interval after each request, so a slow answer came on top of it.
    [Fact]
    [Trait("slice", "7")]
    public void The_wait_counts_from_the_planned_slot_not_from_the_last_request()
    {
        var clock = new FakeTimeProvider();
        var schedule = new RateSchedule(10, clock, clock.GetTimestamp());

        Assert.True(schedule.WaitForSlotAsync(CancellationToken.None).IsCompleted);
        // The request took 250 ms: slots 1 and 2 (100 and 200 ms) are already due, so there is no wait.
        clock.Advance(TimeSpan.FromMilliseconds(250));
        Assert.True(schedule.WaitForSlotAsync(CancellationToken.None).IsCompleted);
        Assert.True(schedule.WaitForSlotAsync(CancellationToken.None).IsCompleted);
        // Slot 3 is due at 300 ms.
        Assert.False(schedule.WaitForSlotAsync(CancellationToken.None).IsCompleted);
    }

    [Fact]
    [Trait("slice", "7")]
    public void It_fell_behind_only_when_slots_were_due_that_no_client_had_taken()
    {
        var clock = new FakeTimeProvider();
        var keepingUp = new RateSchedule(10, clock, clock.GetTimestamp());
        var behind = new RateSchedule(10, clock, clock.GetTimestamp());

        for (var i = 0; i < 10; i++)
        {
            _ = keepingUp.WaitForSlotAsync(CancellationToken.None);
            if (i % 2 == 0) _ = behind.WaitForSlotAsync(CancellationToken.None);
            clock.Advance(TimeSpan.FromMilliseconds(100));
        }

        Assert.False(keepingUp.FellBehind);
        // 5 slots taken by 800 ms, when 9 were due.
        Assert.True(behind.FellBehind);
        Assert.False(new RateSchedule(10, clock, clock.GetTimestamp()).FellBehind);
    }

    [Fact]
    [Trait("slice", "7")]
    public void The_shortfall_line_says_what_was_reached_and_what_the_clients_can_do()
    {
        Assert.Equal(
            "Rate: target 100 req/s, reached 60 req/s. 10 clients at 166.7 ms per request can do at most 59 req/s; more clients (-c) can reach the target.",
            Manager.RateShortfallLine(100, 60, 10, TimeSpan.FromTicks(1_666_667)));
        Assert.Equal(
            "Rate: target 5000 req/s, reached 3000 req/s. 2 clients at 0.1 ms per request could do 20000 req/s, so the number of clients was not the limit.",
            Manager.RateShortfallLine(5000, 3000, 2, TimeSpan.FromMilliseconds(0.1)));
        Assert.Equal("Rate: target 10 req/s, reached 0 req/s.",
            Manager.RateShortfallLine(10, 0, 1, TimeSpan.Zero));
    }
}
