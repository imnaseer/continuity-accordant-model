namespace Continuity;

using System.IO;
using Continuity.Framework;
using NUnit.Framework;

/// <summary>
/// Writes the data the step-through page reads.
///
/// <para>Not a test of the protocol — the properties next door do that. This
/// is here because finding the runs <em>is</em> model checking, so it belongs
/// with the work that already pays that cost, and because a scenario the
/// protocol can no longer produce makes <see cref="Scenario.Find"/> throw:
/// the page cannot quietly go stale.</para>
/// </summary>
[TestFixture]
public class ExportForTheVisualization
{
    [Test]
    public void WriteScenarioData()
    {
        var json = ScenarioExport.ToJson(Scenarios.All);

        // Walk up from the test binary until the Visualization folder appears,
        // rather than counting directories: the count depends on the build
        // layout, and being wrong writes the file somewhere nothing reads.
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory != null &&
               !Directory.Exists(Path.Combine(directory.FullName, "Visualization")))
        {
            directory = directory.Parent;
        }

        Assert.That(directory, Is.Not.Null, "no Visualization folder above the test binary");

        var target = Path.Combine(directory.FullName, "Visualization", "scenarios.json");

        File.WriteAllText(target, json);
        TestContext.Out.WriteLine($"wrote {json.Length} bytes to {target}");
    }
}
