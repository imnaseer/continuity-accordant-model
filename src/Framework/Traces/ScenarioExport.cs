namespace Continuity.Framework;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Exports found traces as JSON for the visualization.
///
/// <para>The exported states are the ones the model produced, so the
/// visualization cannot drift from the protocol: regenerating is a build step,
/// not a redrawing exercise.</para>
///
/// <para>What is exported is the model and nothing else — the state's own
/// fields, and for each step the action that produced it and the label that
/// says which instance took it. There is deliberately no prose here, and no
/// derived "what this step really did": every sentence a reader sees is
/// written in the page, from these two states. That line matters because it is
/// what lets the same export drive a view that knows nothing about
/// Continuity.</para>
///
/// <para>The states are serialized as they are, with no shape of their own
/// devising: the JSON's nesting, naming and keying are the C#'s. A hand-built
/// projection here would be one more thing that can disagree with the model,
/// and it did — it flattened the store away, and reported a packfile as
/// deleted on the step that added one.</para>
/// </summary>
public static class ScenarioExport
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,

        // Enums by name: "Ready" rather than 1, which is what the model calls it.
        Converters = { new JsonStringEnumConverter() },

    };

    /// <summary>Serializes every scenario's trace as one JSON document.</summary>
    public static string ToJson(IEnumerable<Scenario> scenarios)
        => JsonSerializer.Serialize(
            new { scenarios = scenarios.Select(s => Export(s.Find())).ToArray() },
            Options);

    private static object Export(Trace trace)
    {
        var scenario = trace.Scenario;

        return new
        {
            id = scenario.Id,
            title = scenario.Title,
            group = scenario.Group,
            replicas = scenario.Configuration.Replicas,
            frames = trace.States.Select((state, i) => new
            {
                index = i,
                // The move that produced this state; the first state has none.
                // Both fields are the model's own: the action name as declared,
                // and the label naming which instance of it this was.
                action = i == 0 ? null : trace.Moves[i - 1].Action.ToString(),
                label = i == 0 ? null : trace.Moves[i - 1].Label,
                state
            }).ToArray()
        };
    }
}
