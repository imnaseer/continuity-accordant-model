namespace Continuity;

using System;
using System.Linq;
using Continuity.Framework;
using Microsoft.Accordant.ModelChecking.Rltl;
using Microsoft.Accordant;

// Scenarios: runs of the protocol worth looking at.
//
// A scenario says nothing about which replica acts when. It gives a *regular
// expression over states*, and the model finds a run that matches — so a
// scenario cannot describe a behaviour the protocol does not have, and one
// that stops being possible fails the build.
//
// Two pieces build an expression:
//
//     At(p)      one state where p holds
//     Any        any number of states, in any condition   (Σ*)
//
// so `Any + At(racing) + Any + At(settled)` reads left to right as "eventually
// a race, and eventually after that, settled". That is the common shape, but
// it is only one corner of what the notation allows — `Star` constrains what
// may appear *between* two conditions, `Plus` demands repetition, and `|`
// offers alternatives. The last scenario below uses `Star` for something the
// "eventually, then eventually" form cannot say.
//
// The model is asked whether the expression is *impossible*; the
// counterexample is the run.
//
// Changing anything here — a scenario, a condition, the protocol itself —
// changes the runs, so the page's data has to be rebuilt:
//
//     dotnet test src/Continuity.csproj
//
// which writes Visualization/scenarios.json. That file is committed, because
// GitHub Pages serves it without running anything; so a spec change that is
// not followed by a test run leaves a page showing the old runs.

/// <summary>The runs the visualization steps through.</summary>
public static class Scenarios
{
    public static Scenario[] All =>
    [
        new("sequential", "Two writes, one after the other",
            Configuration.WriteOnly,
            Any + At(Committed(1)) + Any + At(Committed(2)),
            group: "writing"),

        new("race", "Two writers race for one position",
            Configuration.WriteOnly,
            Any + At(Racing) + Any + At(LostTheRace) + Any + At(Settled(2)),
            group: "writing"),

        // A writer left two versions adrift: while it held its packfile the
        // index moved twice, and neither move was its own. The displacement
        // need not be another write — here a snapshot does it — which is why
        // one retry path handles both.
        new("far-behind", "A writer that falls two versions behind",
            Configuration.Default,
            Any + At(Pushing) + Any + At(LostTheRace)
                + Any + At(TwoVersionsBehind) + Any + At(Settled(2)),
            group: "writing"),

        // What a reader sees when it has not caught up. The answer is the
        // point of the protocol: an older prefix — position 1 of a log that
        // has reached 2 — and never a half-applied one.
        new("read-behind-a-write", "A reader served an older prefix",
            Configuration.Default,
            Any + At(Committed(2)) + Any + At(AReadHasBeenOvertaken),
            group: "reading"),

        // The other contended write, and the one whose loser does not retry:
        // the snapshot it wrote stays in the store, referenced by nothing.
        new("abandoned-snapshot", "A snapshot nobody will ever read",
            Configuration.Default,
            Any + At(Committed(1)) + Any + At(Compacted) + Any + At(OrphanedSnapshot),
            group: "compacting"),

        // The mirror of the write race, on the other contended write. Both
        // replicas hold a snapshot and go for the same index; one wins.
        // Compare-and-swap-or-abandon is one mechanism used twice, not two.
        new("snapshot-race", "Two replicas compact at the same time",
            Configuration.Default,
            Any + At(BothCompacting) + Any + At(Compacted),
            group: "compacting"),

        // A writer displaced by a compaction rather than by another write.
        // The CAS fails against a snapshot: to the loser the two are the
        // same event, which is why one retry path handles both.
        new("compaction-under-a-writer", "Compaction lands under a writer",
            Configuration.Default,
            Any + At(Pushing) + Any + At(CompactedWhilePushing) + Any + At(Settled(2)),
            group: "compacting"),

        // Here the notation earns its keep. `Star(At(NoReadYet))` constrains
        // what may appear *between* the write and the compaction — a stretch in
        // which no read has been served — so this finds a run where compaction
        // happens before anyone has read at all. A list of conditions to pass
        // through, in order, cannot say that: it can only say what must
        // happen, never what must not happen in between.
        new("compaction", "Compaction before anyone reads",
            Configuration.Default,
            Any + At(Committed(1)) + Star(At(NoReadYet)) + At(Compacted)
                + Any + At(RebuiltFromSnapshot),
            group: "compacting"),

    ];

    // ---- the conditions above are built from ---------------------------

    /// <summary>Two replicas mid-push, working from the same cached index.</summary>
    private static bool Racing(ContinuityState s)
    {
        var pushing = s.EveryReplica()
            .Where(r => r.State == ReplicaState.Pushing && r.HasStarted())
            .ToArray();

        return pushing.Length >= 2 &&
            pushing.All(r => r.CachedIndex.Version == pushing[0].CachedIndex.Version);
    }

    /// <summary>A writer holding a packfile against an index that has moved on.</summary>
    private static bool LostTheRace(ContinuityState s)
        => s.EveryReplica().Any(r => r.State == ReplicaState.Pushing && r.IsStale(s.Store));

    /// <summary>A writer two versions behind the index it must CAS against.</summary>
    private static bool TwoVersionsBehind(ContinuityState s)
        => s.EveryReplica().Any(r => r.State == ReplicaState.Pushing && r.HasStarted() &&
            s.Store.Index.Version - r.CachedIndex.Version >= 2);

    /// <summary>A writer is part-way through a push.</summary>
    private static bool Pushing(ContinuityState s)
        => s.EveryReplica().Any(r => r.State == ReplicaState.Pushing);

    /// <summary>At least <paramref name="n"/> pushes have landed.</summary>
    private static Func<ContinuityState, bool> Committed(int n)
        => s => s.CommittedLog.Length >= n;

    /// <summary>Everything landed, and nothing is in flight.</summary>
    private static Func<ContinuityState, bool> Settled(int pushes)
        => s => s.CommittedLog.Length >= pushes &&
            s.EveryReplica().All(r => r.State != ReplicaState.Pushing);

    /// <summary>A snapshot has been committed into the index.</summary>
    private static bool Compacted(ContinuityState s) => s.Store.Index.SnapshotAt > 0;

    /// <summary>A snapshot object nothing references — a lost commit's litter.</summary>
    private static bool OrphanedSnapshot(ContinuityState s)
        => s.Store.Snapshots.Keys.Any(position => position != s.Store.Index.SnapshotAt);

    /// <summary>A replica rebuilt its repository from a snapshot.</summary>
    private static bool RebuiltFromSnapshot(ContinuityState s)
        => s.Store.Index.SnapshotAt > 0 &&
            s.EveryReplica().Any(r => r.IsCaughtUp() &&
                r.Repository.Length >= s.Store.Index.SnapshotAt);

    /// <summary>
    /// A read already served is now shorter than the log, and some replica is
    /// still behind.
    ///
    /// <para><em>Overtaken, not stale.</em> The protocol never serves a stale
    /// read: <see cref="Properties.ReadsMatchTheLog"/> holds, so what came
    /// back was exactly the committed prefix at the position claimed. This
    /// finds a read that was correct when served and that later commits have
    /// since moved past.</para>
    ///
    /// <para>Both halves are needed in the same state. "A short repository was
    /// returned" alone is satisfied the instant the log grows past any earlier
    /// read; pairing it with a replica that is <em>still</em> short pins the
    /// run to a reader that really is lagging.</para>
    /// </summary>
    private static bool AReadHasBeenOvertaken(ContinuityState s)
        => s.ReadsServed.Any(at =>
               at.Value.Any(repository => repository.Length < s.CommittedLog.Length))
           && s.EveryReplica().Any(r => r.HasStarted() &&
               r.Repository.Length < s.CommittedLog.Length);

    /// <summary>
    /// A read handed back a repository shorter than the committed log — a
    /// reader given an older prefix than the one already agreed.
    ///
    /// <para>The test is on the value returned, not on the position it was
    /// recorded at: a position can fall behind later, when someone else
    /// commits, whereas a short repository was short when it was served.</para>
    /// </summary>
    private static bool ReadServedBehindTheLog(ContinuityState s)
        => s.ReadsServed.Any(at =>
            at.Value.Any(repository => repository.Length < s.CommittedLog.Length));

    /// <summary>Two replicas holding a snapshot, both going for the index.</summary>
    private static bool BothCompacting(ContinuityState s)
        => s.EveryReplica().Count(r => r.State == ReplicaState.Compacting) >= 2;

    /// <summary>A snapshot is in the index while a writer is still pushing.</summary>
    private static bool CompactedWhilePushing(ContinuityState s)
        => s.Store.Index.SnapshotAt > 0 &&
            s.EveryReplica().Any(r => r.State == ReplicaState.Pushing);



    /// <summary>No read has been served yet.</summary>
    private static bool NoReadYet(ContinuityState s) => !s.HasServedARead();

    // ---- building expressions ------------------------------------------
    //
    // Thin names over the regex builders, so a scenario reads as the expression
    // it denotes. `Any + At(p)` is Regex.Star(Regex.Sigma).Then(Regex.Prop(p)).

    /// <summary>Any number of states, in any condition — <c>Σ*</c>.</summary>
    private static Shape Any => Shape.Anything;

    /// <summary>One state where <paramref name="holds"/> is true.</summary>
    private static Shape At(Func<ContinuityState, bool> holds) => Shape.Where(holds);

    /// <summary>Any number of states, each matching <paramref name="inner"/>.</summary>
    private static Shape Star(Shape inner) => inner.Repeated();
}
