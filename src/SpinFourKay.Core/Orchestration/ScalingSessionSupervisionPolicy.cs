namespace SpinFourKay.Core.Orchestration;

/// <summary>
/// Describes the controller's view of an owned fullscreen scaling session.
/// </summary>
public enum ScalingSessionMode
{
    Active,
    Suspended,
    Resuming,
}

/// <summary>
/// Classifies the latest scaling-window inspection without coupling the policy
/// to Windows or Magpie APIs.
/// </summary>
public enum ScalingOutputValidation
{
    Inactive,
    Pending,
    ExactSafe,
    Unsafe,
}

public enum ScalingSupervisionAction
{
    Continue,
    StopOwnedSession,
}

public enum ScalingSupervisionStopReason
{
    None,
    InfrastructureFailure,
    NativeUiScaleFailure,
    SourceFailure,
    UnsafeOutput,
    ResumeGraceExpired,
}

/// <summary>
/// Pure lifecycle state. A resume deadline exists only while resuming.
/// </summary>
public sealed record ScalingSessionSupervisionState(
    ScalingSessionMode Mode,
    DateTimeOffset? ResumeDeadlineUtc = null)
{
    public static ScalingSessionSupervisionState Active { get; } =
        new(ScalingSessionMode.Active);

    public static ScalingSessionSupervisionState Suspended { get; } =
        new(ScalingSessionMode.Suspended);

    public static ScalingSessionSupervisionState ResumingUntil(
        DateTimeOffset deadlineUtc) =>
        new(ScalingSessionMode.Resuming, deadlineUtc);
}

/// <summary>
/// Side-effect-free inputs captured by the application for one health check.
/// ExactSafe means the source HWND, owned engine PID, source geometry, target
/// monitor, coordinate map, and every other required attachment invariant match.
/// </summary>
public sealed record ScalingSessionObservation(
    DateTimeOffset ObservedAtUtc,
    bool InfrastructureHealthy,
    bool NativeUiScaleSafe,
    bool SourceHealthy,
    bool SourceIsForeground,
    ScalingOutputValidation OutputValidation);

public sealed record ScalingSessionSupervisionDecision(
    ScalingSessionSupervisionState State,
    ScalingSupervisionAction Action,
    ScalingSupervisionStopReason StopReason);

/// <summary>
/// Decides whether an owned scaling session remains active, pauses for Alt+Tab,
/// waits for the exact profile's engine-owned restoration, or must stop.
/// </summary>
public static class ScalingSessionSupervisionPolicy
{
    public static ScalingSessionSupervisionDecision Evaluate(
        ScalingSessionSupervisionState current,
        ScalingSessionObservation observation,
        TimeSpan resumeGrace)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(observation);
        if (resumeGrace <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(resumeGrace),
                "Resume grace must be positive.");
        }

        ValidateState(current);
        ValidateOutput(observation.OutputValidation);

        if (!observation.InfrastructureHealthy)
        {
            return Stop(
                current,
                ScalingSupervisionStopReason.InfrastructureFailure);
        }

        if (!observation.NativeUiScaleSafe)
        {
            return Stop(
                current,
                ScalingSupervisionStopReason.NativeUiScaleFailure);
        }

        if (!observation.SourceHealthy)
        {
            return Stop(
                current,
                ScalingSupervisionStopReason.SourceFailure);
        }

        if (observation.OutputValidation == ScalingOutputValidation.Unsafe)
        {
            return Stop(
                current,
                ScalingSupervisionStopReason.UnsafeOutput);
        }

        if (current.Mode == ScalingSessionMode.Active
            && observation.SourceIsForeground
            && observation.OutputValidation == ScalingOutputValidation.Pending)
        {
            // Resume grace is for an output rebuilding after an observed focus
            // suspension. A foreground output that was already verified and then
            // loses an input/fullscreen invariant must fail closed immediately.
            return Stop(
                current,
                ScalingSupervisionStopReason.UnsafeOutput);
        }

        // Losing foreground focus is an intentional, unbounded pause. Do not
        // start the resume clock until the exact game source returns. Record
        // suspension even when normal-mode output remains exact in the background.
        if (!observation.SourceIsForeground)
        {
            return Continue(ScalingSessionSupervisionState.Suspended);
        }

        if (observation.OutputValidation == ScalingOutputValidation.ExactSafe)
        {
            return Continue(ScalingSessionSupervisionState.Active);
        }

        if (current.Mode != ScalingSessionMode.Resuming)
        {
            DateTimeOffset deadline = observation.ObservedAtUtc.Add(resumeGrace);
            ScalingSessionSupervisionState resuming =
                ScalingSessionSupervisionState.ResumingUntil(deadline);
            // Magpie's exact executable/class auto-profile and its internal
            // source-reposition recovery own restoration. The controller never
            // sends the stock foreground-window toggle because that command
            // cannot be bound atomically to the verified EverQuest HWND.
            return Continue(resuming);
        }

        if (observation.ObservedAtUtc >= current.ResumeDeadlineUtc!.Value)
        {
            return Stop(
                current,
                ScalingSupervisionStopReason.ResumeGraceExpired);
        }

        return Continue(current);
    }

    private static void ValidateState(ScalingSessionSupervisionState state)
    {
        if (!Enum.IsDefined(state.Mode))
        {
            throw new ArgumentOutOfRangeException(
                nameof(state),
                state.Mode,
                "The scaling-session mode is not supported.");
        }

        bool hasDeadline = state.ResumeDeadlineUtc is not null;
        if (state.Mode == ScalingSessionMode.Resuming && !hasDeadline)
        {
            throw new ArgumentException(
                "A resuming session requires a deadline.",
                nameof(state));
        }

        if (state.Mode != ScalingSessionMode.Resuming && hasDeadline)
        {
            throw new ArgumentException(
                "Only a resuming session may carry a deadline.",
                nameof(state));
        }
    }

    private static void ValidateOutput(ScalingOutputValidation validation)
    {
        if (!Enum.IsDefined(validation))
        {
            throw new ArgumentOutOfRangeException(
                nameof(validation),
                validation,
                "The scaling-output validation state is not supported.");
        }
    }

    private static ScalingSessionSupervisionDecision Continue(
        ScalingSessionSupervisionState state) =>
        new(
            state,
            ScalingSupervisionAction.Continue,
            ScalingSupervisionStopReason.None);

    private static ScalingSessionSupervisionDecision Stop(
        ScalingSessionSupervisionState state,
        ScalingSupervisionStopReason reason) =>
        new(
            state,
            ScalingSupervisionAction.StopOwnedSession,
            reason);
}
