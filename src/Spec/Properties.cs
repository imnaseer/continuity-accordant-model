namespace Continuity;

using System;
using System.Linq;
using Microsoft.Accordant;
using Microsoft.Accordant.ModelChecking;
using Microsoft.Accordant.ModelChecking.Ltl;
using NUnit.Framework;

// What must be true of the system, and the checks that say so.
//
// Each property is stated once and checked immediately below itself, the way
// a TLA+ specification writes `Inv == ...` and then names it in INVARIANT.
//
// Two kinds, and the difference matters. An *invariant* holds in every state
// the protocol can reach — it says nothing about whether anything happens. A
// *temporal* property is about whole behaviours, so it only means something
// under an assumption that steps keep being taken; that assumption is the
// fairness at the bottom of this file.
//
// Every check is a temporal formula over the explored graph — there is no
// hand-written traversal. `Always` is [], `Eventually` is <>, `LeadsTo` is ~>,
// and a failure comes back as the trace that reaches the offending state.
//
// The three invariants, in one line each:
//
//     TheIndexNamesOnlyStoredPackFiles   nothing the index points at is gone
//     ReadsMatchTheLog                   every read saw a real prefix
//     LogIsRebuildable                   the log survives compaction
//
// The second is the linearizability claim. The third is what compaction has to
// earn.

/// <summary>The properties of the protocol, and their proofs.</summary>
[TestFixture]
public class Properties
{
    /// <summary>Two replicas, two pushes, compaction on.</summary>
    private static readonly StateGraphNode Cluster = Protocol.Explore(Configuration.Default);

    /// <summary>The same, without compaction, to check the append path alone.</summary>
    private static readonly StateGraphNode BareLog = Protocol.Explore(Configuration.WriteOnly);

    // ---- invariants: true in every reachable state --------------------
    //
    // Each is []P over every reachable state, the way TLC checks an INVARIANT.

    /// <summary>
    /// Every packfile the index names is still in the store.
    ///
    /// <para>Nothing removes packfiles here, so this is close to free — but it
    /// is the property that stops being free the moment anything does. A
    /// collector removing a packfile the index still names would strand any
    /// replica replaying through that position.</para>
    /// </summary>
    public static bool TheIndexNamesOnlyStoredPackFiles(ContinuityState s)
        => s.Store.Index.Entries.Values.All(p => s.Store.Read(p) != null);

    [Test]
    public void TheIndexAlwaysNamesOnlyStoredPackFiles()
        => Holds(LtlCheck.Always(Cluster, At(TheIndexNamesOnlyStoredPackFiles)));

    /// <summary>
    /// Every read returned exactly the committed prefix at the position it was
    /// served at — no gaps, no reordering, nothing from the future.
    ///
    /// <para>This is the linearizability claim, made checkable.</para>
    /// </summary>
    public static bool ReadsMatchTheLog(ContinuityState s)
    {
        foreach (var (position, repositories) in s.ReadsServed)
        {
            // A read cannot have been served beyond what was ever committed.
            if (position > s.CommittedLog.Length) return false;

            foreach (var repository in repositories)
            {
                // What came back is exactly the committed prefix at that
                // position — neither short (a stale read) nor wrong.
                if (repository.Length != position) return false;
                if (!repository.SequenceEqual(s.CommittedLog.Take(position))) return false;
            }
        }

        return true;
    }

    [Test]
    public void ReadsAlwaysMatchTheLog()
        => Holds(LtlCheck.Always(Cluster, At(ReadsMatchTheLog)));

    /// <summary>
    /// The whole committed log can be rebuilt from what the store holds
    /// <em>now</em>: the snapshot the index points at, plus the packfiles it
    /// still names.
    ///
    /// <para>This is what compaction has to earn. It is not enough that the log
    /// was once complete; it must stay rebuildable after a prefix has been
    /// rolled into a snapshot.</para>
    /// </summary>
    public static bool LogIsRebuildable(ContinuityState s)
    {
        var index = s.Store.Index;
        var log = s.CommittedLog;

        // The index accounts for exactly what was committed.
        if (index.Length() != log.Length) return false;
        if (index.SnapshotAt > log.Length) return false;

        // The snapshot is exactly the committed prefix through the frontier.
        if (index.SnapshotAt > 0)
        {
            var snapshot = s.Store.ReadSnapshot(index.SnapshotAt);
            if (snapshot == null) return false;
            if (snapshot.Length != index.SnapshotAt) return false;
            if (!snapshot.SequenceEqual(log.Take(index.SnapshotAt))) return false;
        }

        // Everything after the frontier is still individually available.
        for (var position = index.SnapshotAt + 1; position <= log.Length; position++)
        {
            var packFile = index.At(position);
            if (packFile == null) return false;

            var value = s.Store.Read(packFile);
            if (value == null || value.Value != log[position - 1]) return false;
        }

        return true;
    }

    [Test]
    public void TheLogIsAlwaysRebuildable()
        => Holds(LtlCheck.Always(Cluster, At(LogIsRebuildable)));

    [Test]
    public void TheBareLogHoldsEveryInvariant()
    {
        // The same three, against the configuration with compaction off: the
        // append path has to stand on its own.
        Holds(LtlCheck.Always(BareLog, At(TheIndexNamesOnlyStoredPackFiles)));
        Holds(LtlCheck.Always(BareLog, At(ReadsMatchTheLog)));
        Holds(LtlCheck.Always(BareLog, At(LogIsRebuildable)));
    }

    // ---- temporal: true along every behaviour -------------------------

    /// <summary>Every push a configuration allows has landed in the log.</summary>
    public static Func<ContinuityState, bool> EverythingLanded(Configuration configuration)
        => s => s.CommittedLog.Length == configuration.Pushes.Count;

    /// <summary>Some replica is part-way through a push.</summary>
    public static bool APushIsUnderway(ContinuityState s)
        => s.EveryReplica().Any(r => r.State == ReplicaState.Pushing);

    /// <summary>No replica is holding a packfile it has not landed.</summary>
    public static bool NoPushIsOutstanding(ContinuityState s)
        => s.EveryReplica().All(r => r.State != ReplicaState.Pushing);

    [Test]
    public void AWriterCanLoseForever()
    {
        // Compare-and-swap is not fair. With no assumption about scheduling, a
        // writer can refetch, rebase, and be beaten to the index again without
        // end — a real behaviour of the protocol, not an artifact of the model.
        var result = LtlCheck.Eventually(
            BareLog, At(EverythingLanded(Configuration.WriteOnly)), NoAssumption);

        Assert.That(result.Status, Is.EqualTo(PropertyCheckingStatus.Violated),
            "nothing forces a writer to ever win the compare-and-swap");
    }

    [Test]
    public void EveryPushEventuallyLands()
        => Holds(LtlCheck.Eventually(
            BareLog, At(EverythingLanded(Configuration.WriteOnly)), EachStepEventually));

    [Test]
    public void APushThatStartsEventuallyLands()
        => Holds(LtlCheck.LeadsTo(
            BareLog,
            trigger: At(APushIsUnderway),
            response: At(NoPushIsOutstanding),
            EachStepEventually));

    // ---- the interesting states are actually reached -------------------
    //
    // A property that holds because nothing interesting happens is not worth
    // much, so each of these names something the model must reach.

    /// <summary>A writer holds a packfile while the index has moved on.</summary>
    public static bool AWriterHasLost(ContinuityState s)
        => s.EveryReplica().Any(r => r.State == ReplicaState.Pushing && r.IsStale(s.Store));

    /// <summary>A snapshot is committed into the index.</summary>
    public static bool TheFrontierHasMoved(ContinuityState s)
        => s.Store.Index.SnapshotAt > 0;

    /// <summary>A replica is caught up past a committed snapshot frontier.</summary>
    public static bool ARepositoryCameFromASnapshot(ContinuityState s)
        => s.Store.Index.SnapshotAt > 0 &&
            s.EveryReplica().Any(r => r.IsCaughtUp() &&
                r.Repository.Length >= s.Store.Index.SnapshotAt);

    [Test]
    public void AWriterLosesTheCompareAndSwap() => Reaches(AWriterHasLost);

    [Test]
    public void CompactionMovesTheFrontier() => Reaches(TheFrontierHasMoved);

    [Test]
    public void AReplicaRebuildsFromASnapshot() => Reaches(ARepositoryCameFromASnapshot);

    [Test]
    public void ReadsAreServed()
        => Reaches(s => s.HasServedARead());

    [Test]
    public void EveryPushCanLand()
        => Reaches(EverythingLanded(Configuration.Default));

    // ---- fairness: what scheduling is assumed -------------------------

    /// <summary>
    /// Nothing: every interleaving is allowed, including ones in which a
    /// replica simply stops taking steps.
    ///
    /// <para>The invariants hold under this, which is the point of stating them
    /// separately — nothing has to happen for them to be true.</para>
    /// </summary>
    public static Fairness NoAssumption { get; } = Fairness.None;

    /// <summary>
    /// A step that stays continuously available is eventually taken.
    ///
    /// <para>Enough here, because a replica finishes what it started: serving a
    /// read requires being Ready, so once a push is under way nothing takes the
    /// next step away from it. The TLA+ specification needs strong fairness for
    /// the same property, because there a replica can serve reads while a push
    /// of its own is outstanding.</para>
    /// </summary>
    public static Fairness EachStepEventually { get; } = Fairness.WeakAll;

    // ---- plumbing ------------------------------------------------------

    /// <summary>Lifts a predicate on the state to one the checker can call.</summary>
    private static Func<IState, bool> At(Func<ContinuityState, bool> p)
        => s => p((ContinuityState)s);

    private static void Holds(PropertyCheckingResult result)
        => Assert.That(result.Status, Is.EqualTo(PropertyCheckingStatus.Holds),
            result.GetTraceString());

    /// <summary>
    /// Asserts some reachable state satisfies <paramref name="what"/>.
    ///
    /// <para>Written as <c>[]!P</c> and expected to fail. <c>&lt;&gt;P</c> would
    /// be the wrong operator: it asks whether <em>every</em> behaviour reaches
    /// P, and these states are reachable without being inevitable — a replica
    /// can simply keep serving reads and never compact. Asserting P never
    /// happens, and taking the counterexample, is the reachability
    /// question.</para>
    /// </summary>
    private static void Reaches(Func<ContinuityState, bool> what)
    {
        var result = LtlCheck.Always(Cluster, At(s => !what(s)));

        Assert.That(result.Status, Is.EqualTo(PropertyCheckingStatus.Violated),
            "no reachable state satisfies this, so a property about it would be vacuous");
    }
}
