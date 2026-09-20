namespace Continuity;

using System;
using System.Collections.Generic;
using Microsoft.Accordant;

// The state of the system, and nothing else. Every field here is something
// the model remembers; everything you can *ask* about that state lives in
// StateExtensions.cs, and everything that *changes* it lives in Protocol.cs.
//
// In TLA+ terms this is the VARIABLES block.

[State]
public partial class WalIndex
{
    /// <summary>Which packfile sits at which position.</summary>
    public Dictionary<int, string> Entries { get; set; }

    /// <summary>
    /// Everything up to and including this position lives in a snapshot rather
    /// than in individual packfiles. Zero until the first compaction.
    /// </summary>
    public int SnapshotAt { get; set; }

    /// <summary>What a compare-and-swap is conditioned on — S3's etag.</summary>
    public int Version { get; set; }
}

/// <summary>
/// The object store: packfiles, snapshots, and the one index everyone shares.
///
/// <para>Packfiles and snapshots are written freely and never overwritten —
/// uploading one is uncontended. The index is the only object anyone competes
/// for.</para>
/// </summary>
[State]
public partial class ObjectStore
{
    /// <summary>Every packfile in the store, and the value it carries.</summary>
    public Dictionary<string, int> PackFiles { get; set; }

    /// <summary>Every snapshot in the store, and the repository it holds.</summary>
    public Dictionary<int, int[]> Snapshots { get; set; }

    /// <summary>The one contended object.</summary>
    public WalIndex Index { get; set; }
}

/// <summary>Where a replica is in the protocol.</summary>
public enum ReplicaState
{
    /// <summary>Has not read the index yet.</summary>
    New,

    /// <summary>Idle and caught up, free to take on work.</summary>
    Ready,

    /// <summary>Catching up: applying entries its cached index names.</summary>
    Replaying,

    /// <summary>Holding an uploaded packfile, trying to land it.</summary>
    Pushing,

    /// <summary>A snapshot is written; the index has yet to point at it.</summary>
    Compacting
}

/// <summary>
/// One replica: a cached copy of the index, the repository built from it, and
/// whatever push it is currently trying to land.
/// </summary>
[State]
public partial class Replica
{
    /// <summary>Where this replica is in the protocol.</summary>
    public ReplicaState State { get; set; }

    /// <summary>What it last read of the index, or <c>null</c> before it starts.</summary>
    public WalIndex CachedIndex { get; set; }

    /// <summary>The values applied so far, in log order.</summary>
    public int[] Repository { get; set; }

    /// <summary>The push it has uploaded but not yet landed, or <c>null</c>.</summary>
    public Push Pending { get; set; }
}

/// <summary>
/// One push: the packfile it uploads, and the value that packfile carries.
///
/// <para>Values are distinct, so a replay applying the wrong packfile, applying
/// one twice, or applying two out of order produces a repository the invariants
/// can name and reject.</para>
/// </summary>
[State]
public partial class Push
{
    /// <summary>The packfile this push uploads.</summary>
    public string PackFile { get; set; }

    /// <summary>The value that packfile carries.</summary>
    public int Value { get; set; }
}

/// <summary>
/// The whole system: one shared store, some replicas, and the record of what
/// actually happened that the invariants are stated against.
/// </summary>
[State]
public partial class ContinuityState
{
    /// <summary>The shared object store.</summary>
    public ObjectStore Store { get; set; }

    /// <summary>The replicas, by name.</summary>
    public Dictionary<string, Replica> Replicas { get; set; }

    // ---- auxiliary: what happened, for the invariants -----------------
    //
    // These are not part of the protocol. They record enough history for the
    // properties to be stated, the way a TLA+ spec carries aux variables.

    /// <summary>
    /// The values committed to the log, in order. Every read is checked against
    /// this, and the index must always be able to reconstruct it.
    /// </summary>
    public int[] CommittedLog { get; set; }

    /// <summary>
    /// What reads have returned: for each log position a read was served at,
    /// the distinct repositories that came back.
    ///
    /// <para>The position is recorded rather than inferred from the length of
    /// the repository, because <em>a stale read is exactly the case where they
    /// disagree</em>: a replica serving without catching up returns one value
    /// while claiming position two, and one value on its own is a perfectly
    /// good prefix.</para>
    ///
    /// <para>A <em>set</em> per position, not a sequence. That is what keeps
    /// the model finite: a replica can serve the same read forever, so
    /// recording each occurrence would grow without bound, while recording
    /// each distinct answer cannot.</para>
    /// </summary>
    public Dictionary<int, int[][]> ReadsServed { get; set; }
}
