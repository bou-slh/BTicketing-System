using RapidsolDestek.Domain.Entities;
using RapidsolDestek.Domain.Queues;
using RapidsolDestek.Domain.Services;

namespace RapidsolDestek.Tests;

/// <summary>Pure S4 unit tests — no database.</summary>
public class EffortStateMachineTests
{
    private static EffortError Validate(EffortAction action, EffortState? active,
        bool enabled = true, bool open = true, int revisions = 0, int limit = 3,
        bool note = false, bool mandatoryNote = true) =>
        EffortStateMachine.Validate(action, active, enabled, open, revisions, limit, note, mandatoryNote);

    [Theory]
    [InlineData(null)]
    [InlineData(EffortState.Withdrawn)]
    [InlineData(EffortState.Rejected)]
    [InlineData(EffortState.Superseded)]
    public void Propose_AllowedFromNonActiveStates(EffortState? active) =>
        Assert.Equal(EffortError.None, Validate(EffortAction.Propose, active));

    [Fact]
    public void Propose_BlockedWhilePending() =>
        Assert.Equal(EffortError.AlreadyPending, Validate(EffortAction.Propose, EffortState.Pending));

    [Fact]
    public void Propose_BlockedAfterApproval() =>
        Assert.Equal(EffortError.AlreadyApproved, Validate(EffortAction.Propose, EffortState.Approved));

    [Fact]
    public void Propose_BlockedWhenDisabled() =>
        Assert.Equal(EffortError.Disabled, Validate(EffortAction.Propose, null, enabled: false));

    [Fact]
    public void Propose_BlockedOnClosedTicket() =>
        Assert.Equal(EffortError.TicketNotOpen, Validate(EffortAction.Propose, null, open: false));

    [Fact]
    public void Propose_BlockedAtRevisionLimit() =>
        Assert.Equal(EffortError.RevisionLimitReached, Validate(EffortAction.Propose, EffortState.Rejected, revisions: 3, limit: 3));

    [Fact]
    public void Propose_UnlimitedWhenLimitZero() =>
        Assert.Equal(EffortError.None, Validate(EffortAction.Propose, EffortState.Rejected, revisions: 99, limit: 0));

    [Fact]
    public void Revise_OnlyFromPending()
    {
        Assert.Equal(EffortError.None, Validate(EffortAction.Revise, EffortState.Pending));
        Assert.Equal(EffortError.NotPending, Validate(EffortAction.Revise, EffortState.Rejected));
        Assert.Equal(EffortError.NotPending, Validate(EffortAction.Revise, null));
    }

    [Fact]
    public void DecisionsRemainAllowedWhileDisabled()
    {
        // Disabling stops new proposals but drains pending ones.
        Assert.Equal(EffortError.None, Validate(EffortAction.Approve, EffortState.Pending, enabled: false));
        Assert.Equal(EffortError.None, Validate(EffortAction.Reject, EffortState.Pending, enabled: false, note: true));
    }

    [Fact]
    public void Reject_RequiresNoteWhenMandatory()
    {
        Assert.Equal(EffortError.NoteRequired, Validate(EffortAction.Reject, EffortState.Pending, note: false, mandatoryNote: true));
        Assert.Equal(EffortError.None, Validate(EffortAction.Reject, EffortState.Pending, note: true, mandatoryNote: true));
        Assert.Equal(EffortError.None, Validate(EffortAction.Reject, EffortState.Pending, note: false, mandatoryNote: false));
    }

    [Theory]
    [InlineData(EffortAction.Withdraw)]
    [InlineData(EffortAction.Approve)]
    public void PendingOnlyActions_RejectOtherStates(EffortAction action)
    {
        Assert.Equal(EffortError.None, Validate(action, EffortState.Pending));
        Assert.Equal(EffortError.NotPending, Validate(action, EffortState.Approved));
        Assert.Equal(EffortError.NotPending, Validate(action, null));
    }
}

public class TicketNumberFormatterTests
{
    [Theory]
    [InlineData("R######", 716555, '0', "R716555")]
    [InlineData("R######", 42, '0', "R000042")]
    [InlineData("T-####", 2042, '0', "T-2042")]
    [InlineData("T-####", 7, '0', "T-0007")]
    [InlineData("BRD-######", 716567, '0', "BRD-716567")]
    [InlineData("######", 12345678, '0', "12345678")] // overflow keeps all digits
    [InlineData("", 5, '0', "5")]
    [InlineData("NO-HASH", 5, '0', "NO-HASH")]
    public void Formats(string format, long value, char pad, string expected) =>
        Assert.Equal(expected, TicketNumberFormatter.Format(format, value, pad));
}

public class TemplateVariableExpanderTests
{
    [Fact]
    public void ExpandsKnownAndBlanksUnknown()
    {
        var vars = new Dictionary<string, string?> { ["ticket.number"] = "R716555", ["effort.hours"] = "6" };
        Assert.Equal("Talep R716555 için 6 saat — ",
            TemplateVariableExpander.Expand("Talep %{ticket.number} için %{effort.hours} saat — %{unknown.var}", vars));
    }

    [Fact]
    public void NullValueExpandsEmpty()
    {
        var vars = new Dictionary<string, string?> { ["effort.note"] = null };
        Assert.Equal("Not: ", TemplateVariableExpander.Expand("Not: %{effort.note}", vars));
    }

    [Fact]
    public void ListsDistinctVariables()
    {
        var found = TemplateVariableExpander.ListVariables("%{a.b} x %{c} y %{a.b}");
        Assert.Equal(["a.b", "c"], found);
    }
}

public class QueueCriteriaParserTests
{
    [Fact]
    public void ParsesSeededShape()
    {
        var c = QueueCriteria.Parse("""{"state":"open","isanswered":false,"effort":"pending","assignee":"me"}""");
        Assert.Equal("open", c.State);
        Assert.False(c.IsAnswered);
        Assert.Equal("pending", c.Effort);
        Assert.Equal("me", c.Assignee);
        Assert.Empty(c.UnknownKeys);
    }

    [Fact]
    public void CollectsUnknownKeysInsteadOfFailing()
    {
        var c = QueueCriteria.Parse("""{"state":"open","sla_remaining_lt":"2h","org_tag":"VIP"}""");
        Assert.Equal("open", c.State);
        Assert.Equal(2, c.UnknownKeys.Count);
        Assert.Contains("sla_remaining_lt", c.UnknownKeys);
    }

    [Fact]
    public void NumericAssigneeResolvesStaffId()
    {
        var c = QueueCriteria.Parse("""{"assignee":7}""");
        Assert.Equal(7, c.AssigneeStaffId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyInputYieldsEmptyCriteria(string? json) =>
        Assert.Same(QueueCriteria.Empty, QueueCriteria.Parse(json));
}
