using System.Text.Json;
using Xunit;

namespace DuoBondingCurveTests;

/// <summary>
/// Guards the hand-embedded DuoLaunchpad artifacts inside DuoBondingCurve.Helper.cs
/// against drift from the compiled Launchpad build outputs:
///   1. GetLaunchpadNef() byte blob must equal Launchpad/bin/sc/DuoLaunchpad.nef.
///   2. ABI method offsets hard-coded in BuildImmutableTokenManifest must equal
///      the offsets in Launchpad/bin/sc/DuoLaunchpad.manifest.json.
/// If either check fails, recompile Launchpad and sync the embedded copies.
/// </summary>
public class ArtifactConsistencyTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "BondingCurve", "DuoBondingCurve.Helper.cs")) &&
                Directory.Exists(Path.Combine(dir.FullName, "Launchpad")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            "Repository root not found (looking for BondingCurve/DuoBondingCurve.Helper.cs) from " +
            AppContext.BaseDirectory);
    }

    private static string HelperSourcePath =>
        Path.Combine(FindRepoRoot(), "BondingCurve", "DuoBondingCurve.Helper.cs");

    private static byte[] ExtractEmbeddedNefBytes(string helperSource)
    {
        var start = helperSource.IndexOf("GetLaunchpadNef()", StringComparison.Ordinal);
        Assert.True(start >= 0, "GetLaunchpadNef not found in DuoBondingCurve.Helper.cs");

        var bodyStart = helperSource.IndexOf('{', start);
        var bodyEnd = helperSource.IndexOf("};", bodyStart, StringComparison.Ordinal);
        Assert.True(bodyStart >= 0 && bodyEnd > bodyStart, "GetLaunchpadNef body not found");

        var body = helperSource[bodyStart..bodyEnd];
        var matches = System.Text.RegularExpressions.Regex.Matches(body, @"0x[0-9a-fA-F]{2}");
        Assert.True(matches.Count > 0, "No hex bytes found in GetLaunchpadNef body");

        return matches.Select(m => Convert.ToByte(m.Value[2..], 16)).ToArray();
    }

    private static (string name, int offset)[] ExtractEmbeddedAbiOffsets(string helperSource)
    {
        // The ABI methods array lives entirely inside the C# string literal,
        // escaped as \" in source. Extract it and unescape to plain JSON.
        const string marker = "abi\\\":{\\\"methods\\\":[";
        var start = helperSource.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, "abi methods array not found in BuildImmutableTokenManifest");

        var arrayStart = start + marker.Length - 1; // position of '['
        var end = helperSource.IndexOf("],\\\"events\\\"", arrayStart, StringComparison.Ordinal);
        Assert.True(end > arrayStart, "end of abi methods array not found");

        var escaped = helperSource[arrayStart..end]; // from '[' up to ']'
        var jsonArray = escaped.Replace("\\\"", "\"");

        using var doc = JsonDocument.Parse(jsonArray + "]");
        var result = new List<(string, int)>();
        foreach (var method in doc.RootElement.EnumerateArray())
        {
            var name = method.GetProperty("name").GetString()!;
            var offset = method.GetProperty("offset").GetInt32();
            result.Add((name, offset));
        }
        Assert.True(result.Count > 0, "no ABI methods parsed from embedded manifest");
        return result.ToArray();
    }

    private static (string name, int offset)[] LoadArtifactAbiOffsets()
    {
        var path = Path.Combine(FindRepoRoot(), "Launchpad", "bin", "sc", "DuoLaunchpad.manifest.json");
        Assert.True(File.Exists(path), $"Launchpad manifest not found at {path} — build Launchpad first");

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var result = new List<(string, int)>();
        foreach (var method in doc.RootElement.GetProperty("abi").GetProperty("methods").EnumerateArray())
        {
            var name = method.GetProperty("name").GetString()!;
            var offset = method.GetProperty("offset").GetInt32();
            result.Add((name, offset));
        }
        return result.ToArray();
    }

    [Fact]
    public void EmbeddedNef_MatchesLaunchpadBuildOutput()
    {
        var nefPath = Path.Combine(FindRepoRoot(), "Launchpad", "bin", "sc", "DuoLaunchpad.nef");
        Assert.True(File.Exists(nefPath), $"Launchpad NEF not found at {nefPath} — build Launchpad first");

        var artifact = File.ReadAllBytes(nefPath);
        var embedded = ExtractEmbeddedNefBytes(File.ReadAllText(HelperSourcePath));

        Assert.Equal(artifact.Length, embedded.Length);
        for (int i = 0; i < artifact.Length; i++)
        {
            if (artifact[i] != embedded[i])
                Assert.Fail($"NEF byte mismatch at index {i}: embedded 0x{embedded[i]:X2} vs artifact 0x{artifact[i]:X2}. " +
                            "Recompile Launchpad and sync GetLaunchpadNef() in DuoBondingCurve.Helper.cs.");
        }
    }

    [Fact]
    public void EmbeddedManifestOffsets_MatchLaunchpadBuildOutput()
    {
        var embedded = ExtractEmbeddedAbiOffsets(File.ReadAllText(HelperSourcePath));
        var artifact = LoadArtifactAbiOffsets();

        var artifactMap = artifact.ToDictionary(m => m.name, m => m.offset);
        var embeddedMap = embedded.ToDictionary(m => m.name, m => m.offset);

        // Every artifact method must exist in the embedded manifest with the same offset.
        foreach (var (name, artifactOffset) in artifact)
        {
            Assert.True(embeddedMap.TryGetValue(name, out var embeddedOffset),
                $"Method '{name}' present in Launchpad manifest but missing from embedded manifest.");
            Assert.True(embeddedOffset == artifactOffset,
                $"ABI offset drift for '{name}': embedded {embeddedOffset} vs artifact {artifactOffset}. " +
                            "Sync BuildImmutableTokenManifest in DuoBondingCurve.Helper.cs.");
        }

        // And the embedded manifest must not invent extra methods.
        foreach (var name in embeddedMap.Keys)
            Assert.True(artifactMap.ContainsKey(name),
                $"Method '{name}' present in embedded manifest but missing from Launchpad manifest.");
    }
}
