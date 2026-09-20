# Continuity

Replicas sharing an append-only log kept in object storage, with **no lock and
no coordinator** between them. A writer claims a position in the log by
compare-and-swapping one small index object — and that is the whole
synchronisation mechanism of the protocol.

Modelled after Jack Vanlightly's [TLA+ specification][spec] of Cursor's
[Git at any scale][post]. Git is not modelled: a packfile is a value, and a
repository is the sequence of values applied to it.

[spec]: https://github.com/Vanlightly/s3-wal-collection/tree/main/cursor
[post]: https://cursor.com/blog/git-at-any-scale

> The properties and scenarios here use Accordant's **model-checking** support
> — `LtlCheck`, `RltlCheck`, `Regex` over states — which is under active
> development and not yet released. The framework itself is released; this
> layer is not, and its APIs may change.

## Start here

Read four things, in this order, and you have the protocol:

1. **`Spec/State.cs`** — what the model remembers. Five small classes, no logic.
2. **`ObjectStore.CompareAndSwap`** in `Spec/StateExtensions.cs` — seven lines,
   and the whole synchronisation mechanism.
3. **`Spec/Protocol.cs`** — the eight actions, listed at the top of the file.
4. **`Spec/Properties.cs`** — what must be true, and what has to be assumed
   about scheduling before progress means anything.
5. **`Spec/Scenarios.cs`** — runs worth looking at, described by the states
   they pass through.

Everything under `Framework/` is plumbing: the same for any model, and nothing
you need to read to follow the protocol.

## The files

If you know TLA+, the mapping is direct:

```
Spec/                  the specification
  Configuration.cs       how big one run is               CONSTANTS
  State.cs               every [State] class, data only   VARIABLES
  StateExtensions.cs     what you can ask of that state   operators
  Protocol.cs            every action, one file           Init and Next
  Properties.cs          what must be true, and the         INVARIANTS, PROPERTIES
                         checks that say so

Scenarios/             runs worth looking at
  Scenarios.cs           each one, as a regular expression
  ExportForTheVisualization.cs   writes the page's data

Framework/             plumbing — the same for any model
  Step.cs                one action, as the checker wants it
  Traces/                finding a run of a shape, and trimming it

Visualization/         a step-through page over the generated traces
```

`Spec/State.cs` is deliberately only the state: a list of what the model
remembers. Anything you can *ask* about it is an extension method in
`StateExtensions.cs`, and anything that *changes* it is a step in `Protocol.cs`.

`Spec/Properties.cs` holds both kinds of property — the invariants that hold in
every reachable state, and the temporal ones that hold along every behaviour,
together with the fairness the latter are checked under. Each property is
stated once and checked immediately below itself, the way a TLA+ specification
writes `Inv == ...` and then names it in `INVARIANT`.

A **configuration** fixes the parameters — how many replicas, how many pushes,
whether they compact — the way a TLA+ model's constants do:

```csharp
Configuration.Default     // two replicas, two pushes, compaction on
Configuration.WriteOnly   // the bare multi-writer log
```

The graph for a configuration is built once and shared, so every property is
checked against the same states:

```csharp
private static readonly StateGraphNode Cluster = Protocol.Explore(Configuration.Default);

[Test]
public void TheLogIsAlwaysRebuildable()
    => Holds(LtlCheck.Always(Cluster, At(LogIsRebuildable)));
```

## The protocol

A writer uploads its packfile first — unordered, uncontended, cheap — and only
then claims a position:

```csharp
public bool CompareAndSwap(int expectedVersion, WalIndex updated)
{
    if (Index.Version != expectedVersion) return false;
    Index = updated.Copy();
    return true;
}
```

Losing that race is ordinary. Nothing is written, the packfile stays pending,
and the writer refreshes, rebases onto whatever landed, and tries again against
an index that has moved on.

**Compaction competes for the same object.** A replica rolls the log prefix it
holds into a snapshot and then tries to point the index at it — so a writer can
be displaced by a compaction just as easily as by another write. Unlike a push,
a lost compaction is *abandoned*: the snapshot stays in the store, referenced by
nothing.

## The three invariants

```csharp
TheIndexNamesOnlyStoredPackFiles(s)   // nothing the index names has gone missing
ReadsMatchTheLog(s)                   // every read returned the committed prefix
LogIsRebuildable(s)                   // the log survives compaction
```

The second is the linearizability claim made checkable. The third is what
compaction has to earn: it is not enough that the log was *once* complete, it
must stay rebuildable after a prefix has been rolled into a snapshot.

Each is checked as `[]P` over every reachable state, the way TLC checks an
INVARIANT — so a failure comes back as the trace that reaches the offending
state rather than as an assertion in a loop.

Alongside them, `SafetyTests` asserts the interesting states are actually
reached: a writer really does lose a compare-and-swap, compaction really does
move the frontier, a replica really does rebuild from a snapshot. An invariant
that holds because nothing interesting happens is not worth much.

## Auxiliary state, and keeping it bounded

Two properties are about *history*, which no protocol state records:

```csharp
int[]                   CommittedLog   // what was committed, in order
Dictionary<int, int[][]> ReadsServed   // position -> the repositories returned there
```

Neither is part of the protocol. They exist so the properties can be stated, the
way a TLA+ spec carries auxiliary variables — and the specification this is
modelled on carries the same two, as `auxWrittenValues` and `auxReadRepos`.

`ReadsServed` reads directly: at position 1, these repositories came back.

```
{ 1: [ [1] ],  2: [ [1,2] ] }
```

The position is recorded rather than inferred from the repository's length,
because **a stale read is exactly the case where they disagree**: a replica
serving without catching up returns `[1]` while claiming position 2, and `[1]`
on its own is a perfectly good prefix. Drop the position and that bug becomes
invisible — which one of the broken variants demonstrates.

The values are a **set** per position, not a sequence, and that is what keeps
the model finite. A replica can serve the same read forever: recording each
occurrence would make `[r]`, `[r,r]`, `[r,r,r]` all distinct states and the
search would never terminate. Recording each *distinct* answer cannot grow
without bound.

That is a general constraint on auxiliary state, not a detail of this model: it
has to record enough history to state the property, and little enough that the
state space still closes.

## Scenarios as regular expressions

A scenario says nothing about which replica acts when. It gives a **regular
expression over states**:

```csharp
new("race", "Two writers race for one position",
    Configuration.WriteOnly,
    Any + At(Racing) + Any + At(LostTheRace) + Any + At(Settled(2)))
```

`At(p)` is one state where `p` holds, `Any` is `Σ*`. The expression is asserted
**impossible**, and the counterexample is a run that does it — so a scenario
cannot describe a behaviour the protocol does not have, and one that stops being
possible fails the build.

"Eventually a, then eventually b" is the common shape, but it is only one corner
of the notation. `Star` constrains what may appear *between* two conditions:

```csharp
Any + At(Committed(1)) + Star(At(NoReadYet)) + At(Compacted)
```

That finds a run where compaction happens **before anyone has read at all** — a
stretch in which no read has been served, between the write and the compaction.
A list of conditions to pass through in order cannot say that: it can only say
what must happen, never what must *not* happen in between. `Plus` demands
repetition and `|` offers alternatives in the same way.

The runs come back longer than they need to be, so `TraceShortening` trims them:
try dropping a span, keep the result if what remains still replays *and* still
matches the shape. Both checks matter — dropping a move does not simply shorten
a run, because every later move was enabled by the state the dropped one
produced.

That second check is why `Shape` carries the expression twice: once as a
`Regex` for the checker to search the graph with, and once as a matcher over a
single sequence. The checker only answers questions about graphs, and trimming
asks its question hundreds of times about one run at a time.

Five scenarios, and the traces are short enough to read:

```
race: 8 moves
    ReadIndex(r1-start)   ReadIndex(r2-start)
    UploadPackFile(r1-p1) UploadPackFile(r2-p2)   <- both mid-write
    CommitToIndex(r1)                             <- r1 wins, r2 is now stale
    ReadIndex(r2-rebase)  ApplyPackFile(r2)       <- refetch and rebase
    CommitToIndex(r2)                             <- and it still lands
```

## What is deliberately not here

**Nothing removes packfiles.** Compaction writes a snapshot and points the index
at it; the packfiles it covers stay in the store, unreferenced.

Collecting them is a genuinely harder problem. A pending packfile and a
compacted-away one look identical in the store — both present, neither
referenced — and only the writer's own memory distinguishes them. A real
collector needs a grace period, an intent record, or a generational rule, and
the TLA+ specification this is modelled on declares the distinction *magic*
rather than inventing one. The blog post does not describe deletion at all.

`TheIndexNamesOnlyStoredPackFiles` is close to free without a collector. It is
here because it is the property that stops being free the moment anything
removes objects.

## Progress

The invariants hold with no fairness assumption at all. Progress needs one: with
none, a writer can lose the compare-and-swap forever, and nothing in the
protocol prevents it.

Weak fairness suffices here, which is worth stating because the TLA+
specification needs *strong* fairness for the same property. The difference is a
modelling choice, not a protocol one: there, a replica can serve reads while a
push of its own is outstanding, so starting a push is repeatedly disabled. Here
a replica finishes what it started, so once a push is under way nothing takes
the next step away from it.

```bash
dotnet test src/Continuity.csproj
```

The suite also writes `Visualization/scenarios.json`, which the step-through
page reads.
