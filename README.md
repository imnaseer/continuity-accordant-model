# Continuity, as an Accordant model

A model of **Continuity** — the multi-writer log Cursor describes in
[Git at any scale](https://cursor.com/blog/git-at-any-scale) — written with
[Accordant](https://github.com/microsoft/accordant), Microsoft's model-based
testing and model-checking framework for .NET.

Replicas share an append-only log kept in object storage. There is no lock and
no coordinator: a writer claims a position by compare-and-swapping a single
index object, and a writer that loses the swap refetches, rebases and tries
again.

**[Step through the protocol →](https://imnaseer.github.io/continuity-accordant-model/Visualization/)**

The page is generated from the model. Nobody chose the interleavings in it:
each scenario states only the *shape* of the run it is about, as a regular
expression over states, and the checker finds a run that matches.

## What is here

```
src/Spec/            the specification
  Configuration.cs     how big one run is               CONSTANTS
  State.cs             every [State] class, data only   VARIABLES
  StateExtensions.cs   what you can ask of that state   operators
  Protocol.cs          every action, one file           Init and Next
  Properties.cs        what must be true, and the       INVARIANTS, PROPERTIES
                       checks that say so

src/Scenarios/       runs worth looking at
  Scenarios.cs         each one, as a regular expression
  ExportForTheVisualization.cs   writes the page's data

src/Framework/       plumbing — the same for any model
Visualization/       a step-through page over the generated traces
```

`src/README.md` is the guided tour: what the protocol does, how the model maps
onto TLA+ vocabulary, and what each property earns.

## Building it

```bash
git clone --recursive https://github.com/imnaseer/continuity-accordant-model.git
cd continuity-accordant-model
dotnet test src/Continuity.csproj
```

`--recursive` matters: Accordant is a submodule, and a plain clone leaves it
empty. If you already cloned without it, `git submodule update --init` fixes
things.

`dotnet test` checks every property **and** regenerates
`Visualization/scenarios.json`, so the page can never show a run the protocol
does not have.

## A note on the dependency

Accordant is pinned as a submodule to
[`40c2ffc`](https://github.com/microsoft/accordant/commit/40c2ffc) on the
branch `personal/imnaseer/rltl-port-refined`. That branch is not yet merged to
`main`: this model uses the RLTL support — regular expressions over states —
which lives there for now. The pin is exact, so this repository keeps building
whatever happens upstream.

## Credit

The protocol is Cursor's. The TLA+ specification this model was read against is
[Jack Vanlightly's](https://github.com/Vanlightly/s3-wal-collection/tree/main/cursor),
and it remains the more complete artifact — it models garbage collection and WAL
truncation, which this does not. Neither is normative: the blog post is the only
description Cursor published, and both are reconstructions from it.
