namespace Continuity.Framework;

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Accordant;
using Microsoft.Accordant.ModelChecking;
using Microsoft.Accordant.ModelChecking.Rltl;

/// <summary>
/// Shortens a found run by deleting moves that are not carrying the scenario.
///
/// <para>The model checker returns <em>a</em> run of the right shape, not the
/// shortest one: its search is driven by the automaton construction, and
/// nothing in that asks for brevity. In practice the runs arrive padded with
/// state-restoring detours — a replica serving a read and returning to Ready
/// changes nothing the scenario is about, but costs two steps of a reader's
/// attention.</para>
///
/// <para>So: try removing chunks, largest first, and keep a removal when what
/// remains still replays <em>and</em> still matches the shape. Both checks are
/// necessary. Deleting a move does not simply shorten the run, because every
/// later move was enabled by the state the deleted one produced — so the whole
/// remainder is re-derived from the start, and a candidate that fails anywhere
/// is discarded.</para>
///
/// <para>This finds a local minimum, not a global one: it removes slack from
/// the run it was given and never discovers that a structurally different run
/// was shorter. That is the trade for its cost — a handful of replays of a run
/// a few dozen moves long.</para>
/// </summary>
internal static class TraceShortening
{
    /// <summary>Removes what the scenario does not need.</summary>
    internal static IReadOnlyList<Move> Shorten(
        Scenario scenario,
        IReadOnlyList<Move> actions)
    {
        var current = actions.ToList();
        // Try every removal size from largest down to one, sweeping each size
        // until a whole pass removes nothing.
        //
        // Sizes above one are not an optimisation, they are the point: the
        // padding these runs carry is *paired*. A replica's StartRead enables
        // its ServeRead, so dropping either alone leaves a move that is no
        // longer enabled and the whole remainder fails to replay. Only removing
        // the pair together works, and single-step deletion can never find it.
        var size = Math.Max(current.Count / 2, 1);
        while (size >= 1)
        {
            bool removed;
            do
            {
                removed = false;
                for (var start = 0; start + size <= current.Count; )
                {
                    var candidate = new List<Move>(current);
                    candidate.RemoveRange(start, size);

                    if (candidate.Count > 0 && Matches(scenario, candidate))
                    {
                        current = candidate;
                        removed = true;
                        // The tail has shifted into this slot; try here again.
                    }
                    else
                    {
                        start++;
                    }
                }
            }
            while (removed);

            size = size > 1 ? size / 2 : 0;
        }

        // Contiguous removal cannot reach a pair whose two halves are separated
        // by a move that has to stay — and the read churn these runs open with
        // is exactly that: r1 begins a read, something else happens, r1 serves
        // it. So finish by trying pairs that are not adjacent.
        current = RemoveSplitPairs(scenario, current);

        return current;
    }

    /// <summary>
    /// Removes two moves at a time that are not next to each other, which the
    /// contiguous sweep above cannot reach.
    /// </summary>
    private static List<Move> RemoveSplitPairs(
        Scenario scenario,
        List<Move> current)
    {
        bool removed;
        do
        {
            removed = false;
            for (var i = 0; i < current.Count && !removed; i++)
            {
                for (var j = i + 1; j < current.Count && !removed; j++)
                {
                    var candidate = new List<Move>(current);
                    candidate.RemoveAt(j);
                    candidate.RemoveAt(i);

                    if (candidate.Count > 0 && Matches(scenario, candidate))
                    {
                        current = candidate;
                        removed = true;
                    }
                }
            }
        }
        while (removed);

        return current;
    }

    private static bool Matches(
        Scenario scenario,
        IReadOnlyList<Move> actions)
    {
        var states = Replay.Run(scenario.Configuration, actions);
        if (states == null) return false;   // not a run of the protocol at all

        return scenario.Shape.Matches(states);
    }
}
