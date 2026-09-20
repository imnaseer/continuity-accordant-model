namespace Continuity;

using System;
using System.Collections.Generic;
using System.Linq;

// Everything you can ask about the state, and the small operations that change
// it. Kept out of State.cs so the state itself reads as a list of what the
// model remembers.
//
// The one to read first is ObjectStore.CompareAndSwap. It is the whole
// synchronisation mechanism of the protocol, and every other operation here is
// either a local read or an uncontended write.

public static class WalIndexExtensions
{
    /// <summary>An index naming nothing.</summary>
    public static WalIndex Empty() => new WalIndex
    {
        Entries = new Dictionary<int, string>(),
        SnapshotAt = 0,
        Version = 0
    };

    /// <summary>
    /// How many writes this index accounts for: the highest position it names,
    /// or the snapshot frontier when compaction has trimmed the entries away.
    ///
    /// <para>Derived rather than stored. After compaction the entries alone
    /// cannot say how long the log is — they start above 1 — but the frontier
    /// accounts for exactly what was trimmed.</para>
    /// </summary>
    public static int Length(this WalIndex index)
        => Math.Max(index.SnapshotAt, index.Entries.Keys.DefaultIfEmpty(0).Max());

    /// <summary>The next position a writer may claim.</summary>
    public static int NextPosition(this WalIndex index) => index.Length() + 1;

    /// <summary>The packfile at <paramref name="position"/>, or <c>null</c>.</summary>
    public static string At(this WalIndex index, int position)
        => index.Entries.TryGetValue(position, out var packFile) ? packFile : null;

    /// <summary>This index with one more packfile appended, ready to CAS.</summary>
    public static WalIndex Appending(this WalIndex index, string packFile)
    {
        var next = index.Copy();
        next.Entries[index.NextPosition()] = packFile;
        next.Version = index.Version + 1;
        return next;
    }

    /// <summary>
    /// This index with everything up to <paramref name="position"/> replaced by
    /// a snapshot, ready to CAS.
    /// </summary>
    public static WalIndex Snapshotting(this WalIndex index, int position)
    {
        var next = index.Copy();
        foreach (var covered in next.Entries.Keys.Where(p => p <= position).ToArray())
        {
            next.Entries.Remove(covered);
        }

        next.SnapshotAt = position;
        next.Version = index.Version + 1;
        return next;
    }

    /// <summary>An unaliased copy, for storing into another state.</summary>
    public static WalIndex Copy(this WalIndex index) => (WalIndex)index.Clone();

    /// <summary>A short readable form, for traces and diagnostics.</summary>
    public static string Describe(this WalIndex index)
    {
        var entries = string.Join(", ",
            index.Entries.OrderBy(e => e.Key).Select(e => $"{e.Key}:{e.Value}"));
        return $"v{index.Version} len={index.Length()} snap={index.SnapshotAt} [{entries}]";
    }
}

public static class ObjectStoreExtensions
{
    /// <summary>An empty store, with an index naming nothing.</summary>
    public static ObjectStore Empty() => new ObjectStore
    {
        PackFiles = new Dictionary<string, int>(),
        Snapshots = new Dictionary<int, int[]>(),
        Index = WalIndexExtensions.Empty()
    };

    /// <summary>The value in <paramref name="packFile"/>, or <c>null</c>.</summary>
    public static int? Read(this ObjectStore store, string packFile)
        => store.PackFiles.TryGetValue(packFile, out var value) ? value : null;

    /// <summary>Uploads a packfile. Uncontended: this always succeeds.</summary>
    public static void Put(this ObjectStore store, string packFile, int value)
        => store.PackFiles[packFile] = value;

    /// <summary>Whether a snapshot has been written at <paramref name="position"/>.</summary>
    public static bool HasSnapshot(this ObjectStore store, int position)
        => store.Snapshots.ContainsKey(position);

    /// <summary>The repository held by the snapshot at <paramref name="position"/>.</summary>
    public static int[] ReadSnapshot(this ObjectStore store, int position)
        => store.Snapshots.TryGetValue(position, out var repository) ? repository : null;

    /// <summary>Writes a snapshot. Also uncontended; committing it is not.</summary>
    public static void PutSnapshot(this ObjectStore store, int position, int[] repository)
        => store.Snapshots[position] = (int[])repository.Clone();

    /// <summary>
    /// The compare-and-swap: the whole synchronisation mechanism of the
    /// protocol, and the only place two replicas can conflict.
    ///
    /// <para>A writer reads the index, works out what it should say next, and
    /// offers that back along with the version it started from. The store takes
    /// it only if nothing has changed in the meantime. There is no lock, no
    /// coordinator, and no retry loop here — losing simply returns
    /// <c>false</c>, and what a loser does next is the protocol's business,
    /// not the store's.</para>
    /// </summary>
    public static bool CompareAndSwap(
        this ObjectStore store, int expectedVersion, WalIndex updated)
    {
        if (store.Index.Version != expectedVersion) return false;
        store.Index = updated.Copy();
        return true;
    }
}

public static class ReplicaExtensions
{
    /// <summary>A replica that has read the index at least once.</summary>
    public static bool HasStarted(this Replica r) => r.CachedIndex != null;

    /// <summary>Whether its repository covers everything its cached index names.</summary>
    public static bool IsCaughtUp(this Replica r)
        => r.CachedIndex != null && r.Repository.Length == r.CachedIndex.Length();

    /// <summary>The next log position it has yet to apply.</summary>
    public static int NextToApply(this Replica r) => r.Repository.Length + 1;

    /// <summary>Whether its cached index has been overtaken by the stored one.</summary>
    public static bool IsStale(this Replica r, ObjectStore store)
        => r.CachedIndex != null && r.CachedIndex.Version != store.Index.Version;

    /// <summary>An unaliased copy, for storing into another state.</summary>
    public static Replica Copy(this Replica r) => (Replica)r.Clone();
}

public static class PushExtensions
{
    /// <summary>An unaliased copy, for storing into another state.</summary>
    public static Push Copy(this Push push) => (Push)push.Clone();
}

public static class ContinuityStateExtensions
{
    /// <summary>The replica of that name.</summary>
    public static Replica Replica(this ContinuityState s, string name) => s.Replicas[name];

    /// <summary>Every replica, for stating something about all of them.</summary>
    public static IEnumerable<Replica> EveryReplica(this ContinuityState s)
        => s.Replicas.Values;

    /// <summary>Records a committed push.</summary>
    public static void RecordCommit(this ContinuityState s, int value)
        => s.CommittedLog = s.CommittedLog.Append(value).ToArray();

    /// <summary>
    /// Records what a read returned.
    ///
    /// <para>Recording only, no checking — whether the read was correct is the
    /// invariant's job. And recording a <em>set</em>: serving the same answer
    /// again adds nothing, which is what keeps this bounded.</para>
    /// </summary>
    public static void RecordRead(this ContinuityState s, int position, int[] repository)
    {
        s.ReadsServed.TryGetValue(position, out var seen);
        seen ??= Array.Empty<int[]>();

        if (seen.Any(other => other.SequenceEqual(repository))) return;

        s.ReadsServed[position] = seen
            .Append((int[])repository.Clone())
            .OrderBy(other => string.Join(",", other), StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>Whether any read has been served at all.</summary>
    public static bool HasServedARead(this ContinuityState s) => s.ReadsServed.Count > 0;
}
