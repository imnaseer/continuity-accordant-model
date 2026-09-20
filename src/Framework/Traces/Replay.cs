namespace Continuity.Framework;

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Accordant;

/// <summary>
/// Replays a sequence of actions through the real step functions.
///
/// <para>This is what keeps a shortened run honest: dropping a move from a run
/// does not simply shorten it, because every later move was enabled by the
/// state the dropped one produced. A candidate is only accepted if <em>every</em>
/// remaining move is still enabled, in order, from the start.</para>
/// </summary>
internal static class Replay
{
    /// <summary>
    /// Replays <paramref name="actions"/>, returning the state after each one
    /// (with the initial state first), or <c>null</c> if any action was not
    /// enabled where it was asked for.
    /// </summary>
    internal static IReadOnlyList<ContinuityState> Run(
        Configuration configuration,
        IReadOnlyList<Move> moves)
    {
        var steps = Protocol.Steps(configuration);
        var state = Protocol.InitialState(configuration);
        var states = new List<ContinuityState> { (ContinuityState)state.Clone() };

        foreach (var move in moves)
        {
            var next = Apply(steps, state, move);
            if (next == null) return null;
            state = next;
            states.Add((ContinuityState)state.Clone());
        }

        return states;
    }

    /// <summary>Applies one action, or returns <c>null</c> if it is not enabled.</summary>
    private static ContinuityState Apply(
        IList<IStepFunction> steps,
        ContinuityState state,
        Move move)
    {
        foreach (var step in steps)
        {
            var protocolStep = (ContinuityStep)step;
            if (protocolStep.Action != move.Action ||
                protocolStep.Label != move.Label) continue;

            var results = step.Apply(state, Array.Empty<(IStepFunction, StateGraphNode)>());
            if (results == null || results.Count == 0) return null;
            return (ContinuityState)results[0].State;
        }

        return null;
    }
}
