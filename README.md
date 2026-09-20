# Continuity, as an Accordant model

A model of **Continuity** — the multi-writer log Cursor describes in
[Git at any scale](https://cursor.com/blog/git-at-any-scale) — written with
[Accordant](https://github.com/microsoft/accordant), Microsoft's model-based
testing framework for .NET.

> **On the model-checking side.** Accordant itself is public and released. The
> model-checking layer this sample leans on — temporal properties in LTL, and
> scenarios as regular expressions over states in RLTL — is **still under
> active development and not yet released**. It lives on an unmerged branch,
> which this repository pins exactly (see
> [the dependency note](#a-note-on-the-dependency)). Expect those APIs to
> change.

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

Accordant is a released, public framework for model-based testing. Its
**model-checking** support is newer: `LtlCheck` for temporal properties, and
`RltlCheck` for the regular expressions over states that this sample's
scenarios are written in. That work is **in progress and unreleased**, and
lives for now on the branch `personal/imnaseer/rltl-port-refined`.

So there is no package to depend on. Accordant is a git submodule pinned to
[`40c2ffc`](https://github.com/microsoft/accordant/commit/40c2ffc) on that
branch. The pin is exact, so this repository keeps building whatever happens
upstream — and when the branch merges, the pin moves to a commit on `main`.

Treat the model-checking APIs used here as provisional. The protocol, the
specification and the properties are not going to change; the exact spelling
of `RltlCheck.Check`, `Regex.Star` and `Fairness.StrongEach` might.

## Credit

The protocol is Cursor's. The TLA+ specification this model was read against is
[Jack Vanlightly's](https://github.com/Vanlightly/s3-wal-collection/tree/main/cursor),
and it remains the more complete artifact — it models garbage collection and WAL
truncation, which this does not. Neither is normative: the blog post is the only
description Cursor published, and both are reconstructions from it.
