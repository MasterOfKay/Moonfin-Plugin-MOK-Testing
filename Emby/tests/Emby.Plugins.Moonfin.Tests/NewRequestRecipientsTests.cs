using Emby.Plugins.Moonfin.Services;
using Xunit;

namespace Emby.Plugins.Moonfin.Tests;

/// <summary>
/// A new request notifies every request manager signed in to Seerr through Moonfin, including
/// ones who never saved notification settings, since the app shows the switch on from the start.
/// </summary>
public sealed class NewRequestRecipientsTests
{
    private const int ManageRequestsBit = 16;
    private const int AdminBit = 2;

    private static readonly Guid Manager = Guid.NewGuid();
    private static readonly Guid Admin = Guid.NewGuid();
    private static readonly Guid Viewer = Guid.NewGuid();

    private static SeerrSession Session(Guid userId, int permissions, int seerrUserId = 5) =>
        new() { JellyfinUserId = userId, SeerrUserId = seerrUserId, Permissions = permissions };

    private static List<Guid> Recipients(
        IEnumerable<SeerrSession> sessions,
        Func<Guid, bool>? wantsNewRequests = null,
        Guid? requester = null) =>
        SeerrWebhookService.NewRequestRecipients(
            sessions,
            wantsNewRequests ?? (_ => new NotificationPrefs().NotifyOnNewRequests),
            requester).ToList();

    [Fact]
    public void NewRequestsAreOnWhenNothingIsSaved()
    {
        Assert.True(new NotificationPrefs().NotifyOnNewRequests);
    }

    [Fact]
    public void RequestManagersWithoutSavedSettingsAreNotified()
    {
        var recipients = Recipients(new[]
        {
            Session(Manager, ManageRequestsBit),
            Session(Admin, AdminBit),
        });

        Assert.Equal(new[] { Manager, Admin }, recipients);
    }

    [Fact]
    public void SomeoneWhoCantManageRequestsIsLeftOut()
    {
        var recipients = Recipients(new[] { Session(Viewer, 0) });

        Assert.Empty(recipients);
    }

    [Fact]
    public void TheSeerrOwnerCountsWithoutPermissionBits()
    {
        var recipients = Recipients(new[] { Session(Admin, 0, seerrUserId: 1) });

        Assert.Equal(new[] { Admin }, recipients);
    }

    [Fact]
    public void WhoeverMadeTheRequestIsLeftOut()
    {
        var recipients = Recipients(
            new[] { Session(Manager, ManageRequestsBit), Session(Admin, AdminBit) },
            requester: Admin);

        Assert.Equal(new[] { Manager }, recipients);
    }

    [Fact]
    public void SomeoneWhoTurnedItOffIsLeftOut()
    {
        var recipients = Recipients(
            new[] { Session(Manager, ManageRequestsBit), Session(Admin, AdminBit) },
            wantsNewRequests: id => id != Admin);

        Assert.Equal(new[] { Manager }, recipients);
    }

    [Fact]
    public void TwoSessionsForOneUserNotifyThemOnce()
    {
        var recipients = Recipients(new[]
        {
            Session(Manager, ManageRequestsBit),
            Session(Manager, ManageRequestsBit),
        });

        Assert.Equal(new[] { Manager }, recipients);
    }
}
