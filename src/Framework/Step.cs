namespace Continuity.Framework;

using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Accordant;

// Plumbing. One action of a model, as the checker wants it: an identity, a
// guard, and an atomic change. Spec/Protocol.cs is where the actions live.


using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Accordant;

/// <summary>
/// One action of a model, declared inline: a stable id, a guard, and one
/// atomic mutation of the next state.
///
/// <para>Applying an action re-emits it, so the set of actions — and with it
/// the identity of a graph node — never changes, the way the set of actions of
/// a TLA+ specification is constant. An action whose mutation left the state
/// untouched would be invisible to changing-edge fairness; none of the actions
/// in this sample does that.</para>
/// </summary>
/// <typeparam name="TState">The state the action reads and writes.</typeparam>
public abstract class Step<TState> : BaseStepFunction
    where TState : State
{
    private readonly Func<TState, bool> when;
    private readonly Action<TState> then;

    protected Step(
        string id,
        string label,
        Func<TState, bool> when,
        Action<TState> then)
    {
        StepFunctionId = id;
        Label = label;
        this.when = when ?? throw new ArgumentNullException(nameof(when));
        this.then = then ?? throw new ArgumentNullException(nameof(then));
    }

    /// <summary>The stable id, used in traces and diagnostics.</summary>
    public override string StepFunctionId { get; }

    /// <summary>
    /// Which instance of the action this is: the replica it was built for,
    /// and where one replica has several steps of the same action, which one.
    ///
    /// <para>Where TLA+ writes <c>\E r \in Replicas : ReadIndex(r)</c> and
    /// lets the checker bind <c>r</c>, the steps are built one per binding.
    /// The label records which binding each one got — so <c>"r1"</c> is the
    /// argument, and <c>"r1-start"</c> is that argument plus a note saying
    /// which of three <c>ReadIndex</c> steps this is. Those three differ only
    /// in their guard; in TLA+ they would be disjuncts of one action, or
    /// separately named actions.</para>
    /// </summary>
    public string Label { get; }

    protected override IList<StepResult> ApplyInternal(IState state)
    {
        var source = (TState)state;
        if (!when(source))
        {
            return null;
        }

        var next = (TState)source.Clone();
        then(next);
        return new[]
        {
            new StepResult
            {
                State = next,
                StepFunctions = new IStepFunction[] { this }
            }
        };
    }

    public override string ToString() => StepFunctionId;
}

/// <summary>Naming for inline-declared actions.</summary>
public static class Step
{
    /// <summary>
    /// The id of an action: <c>AppendRedo</c> is <c>append-redo</c>,
    /// <c>InstallData</c> labelled <c>k0</c> is <c>install-data-k0</c>, and
    /// <c>ReadIndex</c> labelled <c>r1-start</c> is
    /// <c>read-index-r1-start</c>.
    /// </summary>
    public static string Id(Enum action, string label = null, string prefix = null)
    {
        var id = new StringBuilder();
        if (prefix != null)
        {
            id.Append(prefix).Append('-');
        }

        var name = action.ToString();
        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]))
            {
                id.Append('-');
            }

            id.Append(char.ToLowerInvariant(name[i]));
        }

        return label == null ? id.ToString() : id.Append('-').Append(label).ToString();
    }
}

/// <summary>One action: a guard, and one atomic change.</summary>
public sealed class ContinuityStep : Step<ContinuityState>
{
    public ContinuityStep(
        ContinuityAction action,
        Func<ContinuityState, bool> when,
        Action<ContinuityState> then,
        string label = null)
        // The label has to make the instance unique, or two steps collapse
        // onto one and the checker silently loses an action.
        : base(Step.Id(action, label), label, when, then)
        => Action = action;

    /// <summary>The protocol action this step performs.</summary>
    public ContinuityAction Action { get; }
}
