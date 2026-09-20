namespace Continuity;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Accordant;
using Continuity.Framework;

// The protocol: every action, in one file.
//
// In TLA+ terms this is Init and Next. Each action is a guard and an atomic
// change — `when` and `then` below — and the checker explores every
// interleaving of every action that is enabled. Nothing here says who goes
// first; that is the point.
//
// The whole protocol in eight actions:
//
//     ReadIndex        copy the shared index into this replica's cache
//     UploadPackFile   put an object in the store          (uncontended)
//     CommitToIndex    claim a log position                (CONTENDED)
//     ApplyPackFile    apply the next entry to the repo
//     LoadSnapshot     jump the repo to the frontier
//     ServeRead        return the repo to a reader
//     WriteSnapshot    put a snapshot in the store         (uncontended)
//     CommitSnapshot   point the index at it               (CONTENDED)
//
// The two marked CONTENDED are compare-and-swaps on the same object, and they
// are the only places two replicas can conflict. Everything else is local or
// uncontended.

/// <summary>The actions of the protocol.</summary>
public enum ContinuityAction
{
    ReadIndex,
    UploadPackFile,
    CommitToIndex,
    ApplyPackFile,
    LoadSnapshot,
    ServeRead,
    WriteSnapshot,
    CommitSnapshot
}

/// <summary>
/// Continuity: replicas sharing an append-only log kept in object storage,
/// with no lock and no coordinator between them.
///
/// <para>A writer uploads its packfile first — unordered, uncontended, cheap —
/// and only then claims a position in the log by compare-and-swapping one small
/// index object. Losing that race is ordinary: nothing is written, the packfile
/// stays pending, and the writer refreshes, rebases onto whatever landed, and
/// tries again. That is why the log is correct under concurrent writers.</para>
///
/// <para>Compaction competes for the same object. A replica rolls the log
/// prefix it holds into a snapshot and then tries to point the index at it — so
/// a writer can be displaced by a compaction as easily as by another write.
/// Unlike a push, a lost compaction is abandoned rather than retried.</para>
///
/// <para>Modelled after Jack Vanlightly's TLA+ specification in
/// <c>s3-wal-collection/cursor</c>, itself a reconstruction of Cursor's
/// "Git at any scale". Git is not modelled: a packfile is a value and a
/// repository is the sequence of values applied to it.</para>
/// </summary>
public static class Protocol
{
    /// <summary>An empty store, and replicas that have not read the index yet.</summary>
    public static ContinuityState InitialState(Configuration configuration) => new ContinuityState
    {
        Store = ObjectStoreExtensions.Empty(),
        Replicas = configuration.Replicas.ToDictionary(name => name, _ => new Replica
        {
            State = ReplicaState.New,
            CachedIndex = null,
            Repository = Array.Empty<int>(),
            Pending = null
        }),
        CommittedLog = Array.Empty<int>(),
        ReadsServed = new Dictionary<int, int[][]>()
    };

    /// <summary>Every action, one set per replica.</summary>
    public static IList<IStepFunction> Steps(Configuration configuration)
    {
        var steps = new List<IStepFunction>();

        foreach (var name in configuration.Replicas)
        {
            var replica = name;

            // ---- starting up ----------------------------------------

            steps.Add(new ContinuityStep(
                ContinuityAction.ReadIndex,
                label: $"{replica}-start",
                when: s => s.Replica(replica).State == ReplicaState.New,
                then: s =>
                {
                    var r = s.Replica(replica);
                    r.CachedIndex = s.Store.Index.Copy();
                    r.State = r.IsCaughtUp() ? ReplicaState.Ready : ReplicaState.Replaying;
                }));

            // ---- catching up ----------------------------------------
            // One entry at a time, so another replica can interleave between
            // any two of them.

            steps.Add(new ContinuityStep(
                ContinuityAction.LoadSnapshot,
                label: replica,
                when: s =>
                {
                    var r = s.Replica(replica);
                    return r.State == ReplicaState.Replaying &&
                        r.NextToApply() <= r.CachedIndex.SnapshotAt;
                },
                then: s =>
                {
                    var r = s.Replica(replica);
                    r.Repository = (int[])s.Store.ReadSnapshot(r.CachedIndex.SnapshotAt).Clone();
                    if (r.IsCaughtUp()) r.State = Settled(r);
                }));

            steps.Add(new ContinuityStep(
                ContinuityAction.ApplyPackFile,
                label: replica,
                when: s =>
                {
                    var r = s.Replica(replica);
                    return r.State == ReplicaState.Replaying &&
                        r.NextToApply() > r.CachedIndex.SnapshotAt &&
                        !r.IsCaughtUp();
                },
                then: s =>
                {
                    var r = s.Replica(replica);
                    var packFile = r.CachedIndex.At(r.NextToApply());
                    r.Repository = r.Repository.Append(s.Store.Read(packFile).Value).ToArray();
                    if (r.IsCaughtUp()) r.State = Settled(r);
                }));

            // ---- pushing: upload ------------------------------------
            // Uncontended. The object is in the store well before anyone agrees
            // where it sits in the log.

            foreach (var candidate in configuration.Pushes)
            {
                var push = candidate;
                steps.Add(new ContinuityStep(
                    ContinuityAction.UploadPackFile,
                    label: $"{replica}-{push.PackFile}",
                    when: s => s.Replica(replica).State == ReplicaState.Ready &&
                        s.Store.Read(push.PackFile) == null,
                    then: s =>
                    {
                        var r = s.Replica(replica);
                        s.Store.Put(push.PackFile, push.Value);
                        r.Pending = push.Copy();
                        r.State = ReplicaState.Pushing;
                    }));
            }

            // ---- pushing: refresh before claiming a position ---------

            steps.Add(new ContinuityStep(
                ContinuityAction.ReadIndex,
                label: $"{replica}-rebase",
                when: s =>
                {
                    var r = s.Replica(replica);
                    return r.State == ReplicaState.Pushing && r.IsStale(s.Store);
                },
                then: s =>
                {
                    var r = s.Replica(replica);
                    r.CachedIndex = s.Store.Index.Copy();
                    if (!r.IsCaughtUp()) r.State = ReplicaState.Replaying;
                }));

            // ---- pushing: the compare-and-swap ----------------------
            // The one contended operation. On failure nothing is written, the
            // packfile stays pending, and the replica goes round again.

            steps.Add(new ContinuityStep(
                ContinuityAction.CommitToIndex,
                label: replica,
                when: s =>
                {
                    var r = s.Replica(replica);
                    return r.State == ReplicaState.Pushing &&
                        !r.IsStale(s.Store) && r.IsCaughtUp();
                },
                then: s =>
                {
                    var r = s.Replica(replica);
                    var proposed = r.CachedIndex.Appending(r.Pending.PackFile);

                    if (!s.Store.CompareAndSwap(r.CachedIndex.Version, proposed)) return;

                    r.CachedIndex = s.Store.Index.Copy();
                    r.Repository = r.Repository.Append(r.Pending.Value).ToArray();
                    s.RecordCommit(r.Pending.Value);
                    r.Pending = null;
                    r.State = ReplicaState.Ready;
                }));

            // ---- reading --------------------------------------------

            steps.Add(new ContinuityStep(
                ContinuityAction.ServeRead,
                label: replica,
                when: s =>
                {
                    var r = s.Replica(replica);
                    return r.State == ReplicaState.Ready && r.IsCaughtUp();
                },
                then: s =>
                {
                    var r = s.Replica(replica);
                    s.RecordRead(r.CachedIndex.Length(), r.Repository);
                }));

            // ---- noticing the index moved ---------------------------

            steps.Add(new ContinuityStep(
                ContinuityAction.ReadIndex,
                label: $"{replica}-refresh",
                when: s =>
                {
                    var r = s.Replica(replica);
                    return r.State == ReplicaState.Ready && r.IsStale(s.Store);
                },
                then: s =>
                {
                    var r = s.Replica(replica);
                    r.CachedIndex = s.Store.Index.Copy();
                    if (!r.IsCaughtUp()) r.State = ReplicaState.Replaying;
                }));

            if (!configuration.Compacts) continue;

            // ---- compacting: write the snapshot ---------------------

            steps.Add(new ContinuityStep(
                ContinuityAction.WriteSnapshot,
                label: replica,
                when: s => CanCompact(s, replica),
                then: s =>
                {
                    var r = s.Replica(replica);
                    s.Store.PutSnapshot(r.CachedIndex.Length(), r.Repository);
                    r.State = ReplicaState.Compacting;
                }));

            // ---- compacting: point the index at it ------------------
            // A lost race here is abandoned, not retried: the snapshot stays in
            // the store referenced by nothing.

            steps.Add(new ContinuityStep(
                ContinuityAction.CommitSnapshot,
                label: replica,
                when: s => s.Replica(replica).State == ReplicaState.Compacting,
                then: s =>
                {
                    var r = s.Replica(replica);
                    var proposed = r.CachedIndex.Snapshotting(r.CachedIndex.Length());

                    if (s.Store.CompareAndSwap(r.CachedIndex.Version, proposed))
                    {
                        r.CachedIndex = s.Store.Index.Copy();
                    }

                    r.State = ReplicaState.Ready;
                }));
        }

        return steps;
    }

    /// <summary>Where a replica lands once it has caught up.</summary>
    private static ReplicaState Settled(Replica r)
        => r.Pending != null ? ReplicaState.Pushing : ReplicaState.Ready;

    /// <summary>Whether this replica has something new to snapshot.</summary>
    private static bool CanCompact(ContinuityState s, string replica)
    {
        var r = s.Replica(replica);
        if (r.State != ReplicaState.Ready || !r.IsCaughtUp()) return false;
        if (r.Repository.Length == 0) return false;

        var position = r.CachedIndex.Length();
        return position >= r.CachedIndex.SnapshotAt && !s.Store.HasSnapshot(position);
    }

    /// <summary>Explores every reachable state.</summary>
    /// <summary>
    /// Explores every reachable state.
    ///
    /// <para>The graph for a configuration never changes, and building it is
    /// the expensive part, so it is built once and shared. Every property is
    /// then checked against the same graph.</para>
    /// </summary>
    public static StateGraphNode Explore(Configuration configuration = null)
    {
        configuration ??= Configuration.Default;
        return Graphs.GetOrAdd(configuration, c =>
            StateGraph.ExploreStateGraph(Steps(c), InitialState(c), lazy: true));
    }

    private static readonly ConcurrentDictionary<Configuration, StateGraphNode> Graphs = new();
}
