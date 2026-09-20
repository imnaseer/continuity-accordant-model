namespace Continuity;

using System;
using System.Collections.Generic;
using System.Linq;

// How big one run of the model is. In TLA+ terms, the CONSTANTS — the
// parameters a configuration fixes before the specification is checked.

public sealed class Configuration
{
    /// <summary>Two replicas, two pushes, compaction on.</summary>
    public static Configuration Default { get; } = new Configuration(replicas: 2, pushes: 2);

    /// <summary>The bare multi-writer log: appends only, no compaction.</summary>
    public static Configuration WriteOnly { get; } =
        new Configuration(replicas: 2, pushes: 2, compacts: false);

    public Configuration(int replicas, int pushes, bool compacts = true)
    {
        if (replicas < 1) throw new ArgumentOutOfRangeException(nameof(replicas));
        if (pushes < 1) throw new ArgumentOutOfRangeException(nameof(pushes));

        Replicas = Enumerable.Range(1, replicas).Select(i => $"r{i}").ToArray();
        Pushes = Enumerable.Range(1, pushes)
            .Select(i => new Push { PackFile = $"p{i}", Value = i })
            .ToArray();
        Compacts = compacts;
    }

    /// <summary>The replicas, by name.</summary>
    public IReadOnlyList<string> Replicas { get; }

    /// <summary>Every push that may be made, each carrying a distinct value.</summary>
    public IReadOnlyList<Push> Pushes { get; }

    /// <summary>Whether replicas roll the log prefix into snapshots.</summary>
    public bool Compacts { get; }
}
