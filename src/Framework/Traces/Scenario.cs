namespace Continuity.Framework;

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Accordant;
using Microsoft.Accordant.ModelChecking;
using Microsoft.Accordant.ModelChecking.Rltl;

/// <summary>
/// A scenario described as a <em>shape</em> rather than a script.
///
/// <para>You say what the interesting run looks like — "a race, then a stale
/// loser, then both writes settled" — as a regular expression over states. The
/// model finds a run matching it. Nothing here chooses which replica acts when;
/// the interleaving is the model's, so a scenario cannot describe a behaviour
/// the protocol does not have.</para>
///
/// <para>The witness comes from asserting the shape is <em>impossible</em> and
/// taking the counterexample. That is the standard trick: a property saying
/// "this never happens" fails with a concrete run in which it does.</para>
/// </summary>
public sealed class Scenario
{
    public Scenario(
        string id,
        string title,
        Configuration configuration,
        Shape shape,
        string group = null)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        Title = title ?? throw new ArgumentNullException(nameof(title));
        Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        Shape = shape ?? throw new ArgumentNullException(nameof(shape));
        Group = group;
    }

    public string Id { get; }
    public string Title { get; }
    public Configuration Configuration { get; }

    /// <summary>
    /// What this scenario is about, for a reader choosing between them.
    ///
    /// <para>Free text, and the page takes it as such: it draws whatever
    /// groups the scenarios happen to name, in the order they first appear.
    /// Nothing anywhere holds a list of the valid ones.</para>
    /// </summary>
    public string Group { get; }

    /// <summary>The shape of the run this scenario is about.</summary>
    public Shape Shape { get; }

    /// <summary>
    /// Finds a run matching <see cref="Shape"/> and shortens it.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The protocol has no run of this shape — the scenario describes something
    /// that cannot happen.
    /// </exception>
    public Trace Find()
    {
        var root = Protocol.Explore(Configuration);

        // "No run matches this shape." The counterexample is a run that does.
        var impossible = RltlFormula.Not(
            RltlFormula.SeqPrefix(Shape.Pattern, RltlFormula.True));

        var result = RltlCheck.Check(root, impossible, 0, Fairness.None);

        if (result.Status != PropertyCheckingStatus.Violated)
        {
            throw new InvalidOperationException(
                $"scenario '{Id}': the protocol has no run of this shape " +
                $"({result.Status}).");
        }

        var actions = result.Trace
            .Where(item => item.StepFunction != null)
            .Select(item => (ContinuityStep)item.StepFunction)
            .Select(step => new Move(step.Action, step.Label))
            .ToList();

        return Trace.Build(this, TraceShortening.Shorten(this, actions));
    }
}

/// <summary>One action of a found run.</summary>
public sealed record Move(ContinuityAction Action, string Label)
{
    public override string ToString()
        => Label == null ? Action.ToString() : $"{Action}({Label})";
}

/// <summary>A found run, with the state after each move.</summary>
public sealed class Trace
{
    private Trace(
        Scenario scenario,
        IReadOnlyList<Move> moves,
        IReadOnlyList<ContinuityState> states)
    {
        Scenario = scenario;
        Moves = moves;
        States = states;
    }

    public Scenario Scenario { get; }

    /// <summary>The moves, in order.</summary>
    public IReadOnlyList<Move> Moves { get; }

    /// <summary>
    /// The states: <c>States[0]</c> is the initial state and <c>States[i+1]</c>
    /// is the state after <c>Moves[i]</c>.
    /// </summary>
    public IReadOnlyList<ContinuityState> States { get; }

    internal static Trace Build(Scenario scenario, IReadOnlyList<Move> moves)
    {
        var replay = Replay.Run(scenario.Configuration, moves);
        if (replay == null)
        {
            throw new InvalidOperationException(
                $"scenario '{scenario.Id}': the found run does not replay.");
        }

        return new Trace(scenario, moves, replay);
    }
}
