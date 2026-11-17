using Clauderoo.Core;
using Clauderoo.Tray;

namespace Clauderoo.Core.Tests;

public class CheckListScrollTests
{
    private static List<CheckItem> Rows(int done)
    {
        var keys = new[] { "gcloud", "signin", "gac", "iam", "leftovers", "claude", "model:sonnet", "model:opus", "model:haiku" };
        return keys.Select((k, i) => new CheckItem(i < done ? CheckStatus.Ok : CheckStatus.Running,
            $"Check {k} with a label long enough to wrap onto a second line in a narrow list", "Some hint text") { Key = k }).ToList();
    }

    private static CheckListView Make()
    {
        var v = new CheckListView { Width = 300, MaxContentHeight = 120 };
        v.CreateControl();
        return v;
    }

    [Fact]
    public void Live_FollowsWaitingRows_WhenUserHasNotScrolled()
    {
        using var v = Make();
        v.SetItems(Rows(0), live: true);
        v.SetItems(Rows(6), live: true);
        Assert.True(v.ScrollOffset > 0); // moved down to the rows still running
    }

    [Fact]
    public void Live_UserScrolledUp_IsNotPulledBack()
    {
        using var v = Make();
        v.SetItems(Rows(6), live: true);
        v.UserScrollBy(-100_000); // the user scrolls to the top to read a finished row
        Assert.Equal(0, v.ScrollOffset);
        v.SetItems(Rows(7), live: true); // another check finishes
        v.SetItems(Rows(8), live: true);
        Assert.Equal(0, v.ScrollOffset);
    }

    [Fact]
    public void NewLiveRun_FollowsAgain()
    {
        using var v = Make();
        v.SetItems(Rows(6), live: true);
        v.UserScrollBy(-100_000);
        v.SetItems(Rows(9), live: false); // check finished
        v.SetItems(Rows(0), live: true);  // next check starts
        v.SetItems(Rows(6), live: true);
        Assert.True(v.ScrollOffset > 0);
    }
}
