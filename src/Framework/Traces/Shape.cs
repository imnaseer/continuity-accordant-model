namespace Continuity.Framework;

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Accordant.ModelChecking.Rltl;

// Plumbing. A regular expression over states, built twice from the same parts.
//
// `Pattern` is an ordinary Regex, and it is all Scenario.Find needs: the
// checker searches the graph with it and hands back a matching run.
//
// `Matches` is the same language applied to one sequence of states, and it
// exists for the trimming that happens next. TraceShortening drops a span from
// the found run and asks "is this still a run of that shape?" — hundreds of
// times. That question is about one sequence, and RltlCheck only answers
// questions about graphs: using it would mean building a throwaway one-path
// graph per candidate, at milliseconds each instead of microseconds.
//
// So every combinator below returns both forms, and they cannot drift, because
// each is built from the same predicate.

/// <summary>A regular expression over states.</summary>
public sealed class Shape
{
    /// <summary>
    /// Attempts to consume a prefix of <c>states</c> starting at <c>from</c>,
    /// returning the position after what it consumed, or -1 for no match.
    /// </summary>
    private readonly Func<IReadOnlyList<ContinuityState>, int, int> match;

    private Shape(Regex pattern, Func<IReadOnlyList<ContinuityState>, int, int> match)
    {
        Pattern = pattern;
        this.match = match;
    }

    /// <summary>The expression the checker searches with.</summary>
    public Regex Pattern { get; }

    /// <summary>Any number of states, in any condition — <c>Σ*</c>.</summary>
    public static Shape Anything { get; } = new(
        Regex.Star(Regex.Sigma),
        // Not greedy: Σ* has to give states back so whatever follows it can
        // match. Returning the earliest position and letting the next part of
        // the expression scan forward is what makes the sweep exact.
        (states, from) => from);

    /// <summary>One state where <paramref name="holds"/> is true.</summary>
    public static Shape Where(Func<ContinuityState, bool> holds) => new(
        Regex.Prop(state => holds((ContinuityState)state)),
        (states, from) =>
        {
            for (var i = from; i < states.Count; i++)
            {
                if (holds(states[i])) return i + 1;
            }

            return -1;
        });

    /// <summary>Any number of states, each matching this shape — <c>this*</c>.</summary>
    public Shape Repeated() => new(
        Regex.Star(Pattern),
        (states, from) =>
        {
            // A star never fails: consume what matches, then stop.
            var at = from;
            while (at < states.Count)
            {
                var next = match(states, at);
                if (next < 0 || next == at) break;
                at = next;
            }

            return at;
        });

    /// <summary>One shape then another.</summary>
    public static Shape operator +(Shape first, Shape then) => new(
        first.Pattern.Then(then.Pattern),
        (states, from) =>
        {
            var after = first.match(states, from);
            return after < 0 ? -1 : then.match(states, after);
        });

    /// <summary>Whether these states match this shape.</summary>
    public bool Matches(IReadOnlyList<ContinuityState> states) => match(states, 0) >= 0;
}
