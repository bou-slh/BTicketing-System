namespace RapidsolDestek.Domain.Entities;

/// <summary>Actions of the Efor Onayı loop (B8).</summary>
public enum EffortAction
{
    /// <summary>Staff proposes hours (first proposal or after a terminal decision).</summary>
    Propose,
    /// <summary>Staff replaces the pending proposal with a new revision.</summary>
    Revise,
    /// <summary>Staff withdraws the pending proposal before a decision.</summary>
    Withdraw,
    /// <summary>Ticket owner approves the pending proposal (one click).</summary>
    Approve,
    /// <summary>Ticket owner rejects the pending proposal (note gated by settings).</summary>
    Reject,
}

/// <summary>Stable rejection codes for <see cref="Common.EffortException"/> (UI maps to strings).</summary>
public enum EffortError
{
    None,
    /// <summary>effort.enabled is off (Approve/Reject stay allowed to drain in-flight proposals).</summary>
    Disabled,
    /// <summary>Action needs a Pending proposal and there is none.</summary>
    NotPending,
    /// <summary>Propose while a Pending proposal exists (use Revise).</summary>
    AlreadyPending,
    /// <summary>Propose after an approval — the loop is complete for this ticket.</summary>
    AlreadyApproved,
    /// <summary>effort.revision_limit reached.</summary>
    RevisionLimitReached,
    /// <summary>effort.mandatory_reject_note is on and no note was given.</summary>
    NoteRequired,
    /// <summary>Ticket is closed/archived — no effort actions.</summary>
    TicketNotOpen,
}

/// <summary>
/// Pure B8 state machine: none → pending → approved | rejected → (revise → pending).
/// No I/O — the service resolves actor rights and settings, this table answers whether
/// the transition itself is legal. Unit-tested exhaustively (S4 gate).
/// </summary>
public static class EffortStateMachine
{
    /// <summary>
    /// Validates an action against the active proposal state (null = no proposal yet;
    /// Superseded rows never count as active) and the effort settings snapshot.
    /// Returns the first violated rule or <see cref="EffortError.None"/>.
    /// </summary>
    public static EffortError Validate(
        EffortAction action,
        EffortState? activeState,
        bool effortEnabled,
        bool ticketOpen,
        int existingRevisions,
        int revisionLimit,
        bool noteProvided,
        bool mandatoryRejectNote)
    {
        if (!ticketOpen)
            return EffortError.TicketNotOpen;

        // Disabling the feature stops new proposals but lets pending ones be decided.
        if (!effortEnabled && action is EffortAction.Propose or EffortAction.Revise)
            return EffortError.Disabled;

        switch (action)
        {
            case EffortAction.Propose:
                if (activeState == EffortState.Pending)
                    return EffortError.AlreadyPending;
                if (activeState == EffortState.Approved)
                    return EffortError.AlreadyApproved;
                if (revisionLimit > 0 && existingRevisions >= revisionLimit)
                    return EffortError.RevisionLimitReached;
                return EffortError.None;

            case EffortAction.Revise:
                if (activeState != EffortState.Pending)
                    return EffortError.NotPending;
                if (revisionLimit > 0 && existingRevisions >= revisionLimit)
                    return EffortError.RevisionLimitReached;
                return EffortError.None;

            case EffortAction.Withdraw:
                return activeState == EffortState.Pending ? EffortError.None : EffortError.NotPending;

            case EffortAction.Approve:
                return activeState == EffortState.Pending ? EffortError.None : EffortError.NotPending;

            case EffortAction.Reject:
                if (activeState != EffortState.Pending)
                    return EffortError.NotPending;
                if (mandatoryRejectNote && !noteProvided)
                    return EffortError.NoteRequired;
                return EffortError.None;

            default:
                return EffortError.NotPending;
        }
    }
}
