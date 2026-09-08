using MediaBrowser.Model.Tasks;
using Moonfin.Server.Helpers;
using Xunit;

namespace Moonfin.Server.Tests;

/// <summary>
/// Jellyfin 10.11 turned TaskTriggerInfo.Type from a string into an enum, so assigning it
/// directly stopped binding and every scheduled task silently lost its startup trigger.
/// These run against the 10.10 packages, which is the string half of that split.
/// </summary>
public sealed class TaskTriggersTests
{
    [Fact]
    public void StartupTriggerCarriesTheStartupTypeAndNoTimeOfDay()
    {
        var trigger = TaskTriggers.Startup();

        Assert.Equal(TaskTriggerInfo.TriggerStartup, trigger.Type);
        Assert.Null(trigger.TimeOfDayTicks);
    }

    [Fact]
    public void DailyTriggerCarriesTheDailyTypeAndTheRequestedTime()
    {
        var trigger = TaskTriggers.Daily(TimeSpan.FromHours(5));

        Assert.Equal(TaskTriggerInfo.TriggerDaily, trigger.Type);
        Assert.Equal(TimeSpan.FromHours(5).Ticks, trigger.TimeOfDayTicks);
    }

    [Fact]
    public void TriggerNamesMatchWhatTheServerExpects()
    {
        Assert.Equal("StartupTrigger", TaskTriggerInfo.TriggerStartup);
        Assert.Equal("DailyTrigger", TaskTriggerInfo.TriggerDaily);
    }
}
