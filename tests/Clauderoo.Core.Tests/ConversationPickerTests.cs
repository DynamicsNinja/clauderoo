using Clauderoo.Tray;

namespace Clauderoo.Core.Tests;

public class ConversationPickerTests
{
    static Conversation Conv(string id, string profile, string? cwd, string? prompt, bool isolated = false) =>
        new(id, profile, isolated, "slug", cwd, new DateTimeOffset(2026, 11, 17, 9, 0, 0, TimeSpan.Zero), prompt, @"C:\x\" + id + ".jsonl");

    static ProfileInfo Prof(string name, bool home = false, string provider = Providers.Login) =>
        new(name, home, false, null, [], @"C:\x\" + name, provider);

    static readonly Conversation A = Conv("aaaa1111", "personal", @"C:\src\Billing", "Fix the invoice rounding bug");
    static readonly Conversation B = Conv("bbbb2222", "work", @"C:\src\Website", "Add a dark mode toggle", isolated: true);
    static readonly Conversation C = Conv("cccc3333", "work", null, null);
    static readonly IReadOnlyList<Conversation> All = [A, B, C];

    [Fact]
    public void Filter_EmptySearchAndNoProfile_KeepsAll()
    {
        Assert.Equal(All, ConversationPicker.Filter(All, "  ", null));
    }

    [Fact]
    public void Filter_SearchesPromptFolderProfileAndId_CaseInsensitive()
    {
        Assert.Equal([A], ConversationPicker.Filter(All, "INVOICE", null));
        Assert.Equal([B], ConversationPicker.Filter(All, "website", null));
        Assert.Equal([A], ConversationPicker.Filter(All, "personal", null));
        Assert.Equal([B], ConversationPicker.Filter(All, "bbbb", null));
    }

    [Fact]
    public void Filter_EveryWordMustMatch_InAnyField()
    {
        Assert.Equal([B], ConversationPicker.Filter(All, "work dark", null));
        Assert.Empty(ConversationPicker.Filter(All, "work invoice", null));
    }

    [Fact]
    public void Filter_ByProfile_IsExact()
    {
        Assert.Equal([B, C], ConversationPicker.Filter(All, "", "work"));
        Assert.Equal([B], ConversationPicker.Filter(All, "dark", "work"));
        Assert.Empty(ConversationPicker.Filter(All, "", "wor"));
    }

    [Fact]
    public void Filter_ConversationWithoutPromptOrFolder_DoesNotThrow()
    {
        Assert.Equal([C], ConversationPicker.Filter(All, "cccc", null));
    }

    [Fact]
    public void Targets_HomeOnlyIsolated_OthersBoth()
    {
        var t = ConversationPicker.Targets([Prof("personal", home: true), Prof("work")]);
        Assert.Equal(["personal (isolated)", "work", "work (isolated)"], t.Select(x => x.Label));
        Assert.Equal([("personal", true), ("work", false), ("work", true)], t.Select(x => (x.Profile, x.Isolated)));
    }

    [Fact]
    public void Targets_TokenProfile_Included()
    {
        var t = ConversationPicker.Targets([Prof("ci", provider: Providers.Token)]);
        Assert.Contains(t, x => x.Profile == "ci" && !x.Isolated);
    }

    [Fact]
    public void DefaultTarget_SkipsTheConversationsOwnLocation()
    {
        var t = ConversationPicker.Targets([Prof("personal", home: true), Prof("work")]);
        Assert.Equal("work", ConversationPicker.DefaultTarget(t, A)!.Label);
        Assert.Equal("personal (isolated)", ConversationPicker.DefaultTarget(t, B)!.Label);
    }

    [Fact]
    public void DefaultTarget_NoTargets_Null()
    {
        Assert.Null(ConversationPicker.DefaultTarget([], A));
    }

    [Fact]
    public void Refusal_MissingFolder_UsesTheSpecWording()
    {
        var msg = ConversationPicker.Refusal(A, _ => false);
        Assert.Equal(@"The folder C:\src\Billing no longer exists; Claude can only resume a conversation from its own folder.", msg);
        Assert.Null(ConversationPicker.Refusal(A, _ => true));
    }

    [Fact]
    public void Refusal_NoRecordedFolder()
    {
        Assert.Contains("doesn't record its folder", ConversationPicker.Refusal(C, _ => true));
    }

    [Fact]
    public void WhenLabel_TodayYesterdayAndOlder()
    {
        var now = new DateTime(2026, 11, 17, 15, 0, 0);
        Assert.Equal("Today 09:05", ConversationPicker.WhenLabel(new DateTime(2026, 11, 17, 9, 5, 0), now));
        Assert.Equal("Yesterday 23:59", ConversationPicker.WhenLabel(new DateTime(2026, 11, 16, 23, 59, 0), now));
        Assert.Equal("2026-11-01 08:00", ConversationPicker.WhenLabel(new DateTime(2026, 11, 1, 8, 0, 0), now));
    }

    [Fact]
    public void Labels_ProfileAndPrompt()
    {
        Assert.Equal("work (isolated)", ConversationPicker.ProfileLabel(B));
        Assert.Equal("personal", ConversationPicker.ProfileLabel(A));
        Assert.Equal("(no text)", ConversationPicker.PromptLabel(C));
        Assert.Equal("Fix the invoice rounding bug", ConversationPicker.PromptLabel(A));
        Assert.Equal("?", ConversationPicker.FolderLabel(C));
    }

    [Fact]
    public void ContinuedStatus_SaysWhenOlderCopyKept()
    {
        var t = new ContinueTarget("work", false, "work");
        Assert.Equal("Opened a new tab in work.", ConversationPicker.ContinuedStatus(t, CopyOutcome.Copied));
        Assert.Equal("Opened a new tab in work.", ConversationPicker.ContinuedStatus(t, CopyOutcome.AlreadyUpToDate));
        Assert.Equal("Opened a new tab in work. Its older copy of the conversation was kept as a .bak file.",
            ConversationPicker.ContinuedStatus(t, CopyOutcome.BackedUpAndCopied));
    }
}
