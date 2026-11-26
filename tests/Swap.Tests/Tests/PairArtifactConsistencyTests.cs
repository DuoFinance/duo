using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace DuoSwap.Tests;

/// <summary>
/// Guards the Pair NEF and ABI embedded in SwapFactory against the compiled
/// SwapPair artifacts. If Pair changes, rebuild it and synchronize the
/// embedded NEF and manifest in DuoSwapFactory.cs.
/// </summary>
public class PairArtifactConsistencyTests
{
    private sealed record AbiParameter(string Name, string Type);
    private sealed record AbiMethod(
        string Name,
        string Parameters,
        string ReturnType,
        int Offset,
        bool Safe);
    private sealed record AbiEvent(string Name, string Parameters);

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "SwapFactory", "DuoSwapFactory.cs")) &&
                File.Exists(Path.Combine(dir.FullName, "SwapPair", "DuoSwapPair.cs")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            "Repository root not found (looking for SwapFactory/DuoSwapFactory.cs) from " +
            AppContext.BaseDirectory);
    }

    private static string FactorySourcePath =>
        Path.Combine(FindRepoRoot(), "SwapFactory", "DuoSwapFactory.cs");

    private static string PairArtifactDirectory =>
        Path.Combine(FindRepoRoot(), "SwapPair", "bin", "sc");

    private static byte[] ExtractEmbeddedPairNef(string factorySource)
    {
        const string marker = "byte[] nef = new byte[]";
        var start = factorySource.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, "Embedded Pair NEF declaration not found in DuoSwapFactory.cs");

        var bodyStart = factorySource.IndexOf('{', start);
        var bodyEnd = factorySource.IndexOf("};", bodyStart, StringComparison.Ordinal);
        Assert.True(bodyStart >= 0 && bodyEnd > bodyStart, "Embedded Pair NEF byte array not found");

        var body = factorySource[bodyStart..bodyEnd];
        var matches = Regex.Matches(body, @"0x[0-9a-fA-F]{2}");
        Assert.True(matches.Count > 0, "No hex bytes found in the embedded Pair NEF");

        return matches.Select(m => Convert.ToByte(m.Value[2..], 16)).ToArray();
    }

    private static JsonDocument ParseEmbeddedPairManifest(string factorySource)
    {
        const string marker = "var manifest = ";
        var start = factorySource.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, "Embedded Pair manifest not found in DuoSwapFactory.cs");
        start += marker.Length;

        var end = factorySource.IndexOf(';', start);
        Assert.True(end > start, "End of embedded Pair manifest not found");

        var expression = factorySource[start..end];
        var fragments = Regex.Matches(expression, @"""(?:\\.|[^""\\])*""")
            .Select(match => match.Value[1..^1].Replace("\\\"", "\""))
            .ToArray();
        Assert.Equal(2, fragments.Length); // The manifest is split around the runtime pairName.

        var manifest = fragments[0] + "Pair-PLACEHOLDER" + fragments[1];
        return JsonDocument.Parse(manifest);
    }

    private static JsonDocument LoadPairArtifactManifest()
    {
        var path = Path.Combine(PairArtifactDirectory, "DuoSwapPair.manifest.json");
        Assert.True(File.Exists(path), $"Pair manifest not found at {path} — build SwapPair first");
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    private static AbiParameter[] ParseParameters(JsonElement parameters) =>
        parameters.EnumerateArray()
            .Select(parameter => new AbiParameter(
                parameter.GetProperty("name").GetString()!,
                parameter.GetProperty("type").GetString()!))
            .ToArray();

    private static string ParameterSignature(JsonElement parameters) =>
        string.Join(",", ParseParameters(parameters).Select(p => $"{p.Name}:{p.Type}"));

    private static AbiMethod[] ParseMethods(JsonElement manifest) =>
        manifest.GetProperty("abi").GetProperty("methods").EnumerateArray()
            .Select(method => new AbiMethod(
                method.GetProperty("name").GetString()!,
                ParameterSignature(method.GetProperty("parameters")),
                method.GetProperty("returntype").GetString()!,
                method.GetProperty("offset").GetInt32(),
                method.GetProperty("safe").GetBoolean()))
            .ToArray();

    private static AbiEvent[] ParseEvents(JsonElement manifest) =>
        manifest.GetProperty("abi").GetProperty("events").EnumerateArray()
            .Select(@event => new AbiEvent(
                @event.GetProperty("name").GetString()!,
                ParameterSignature(@event.GetProperty("parameters"))))
            .ToArray();

    [Fact]
    public void EmbeddedPairNef_MatchesSwapPairBuildOutput()
    {
        var factorySource = File.ReadAllText(FactorySourcePath);
        var embedded = ExtractEmbeddedPairNef(factorySource);
        var nefPath = Path.Combine(PairArtifactDirectory, "DuoSwapPair.nef");
        Assert.True(File.Exists(nefPath), $"Pair NEF not found at {nefPath} — build SwapPair first");
        var artifact = File.ReadAllBytes(nefPath);

        Assert.Equal(artifact, embedded);
    }

    [Fact]
    public void EmbeddedPairManifestAbi_MatchesSwapPairBuildOutput()
    {
        var factorySource = File.ReadAllText(FactorySourcePath);
        using var embedded = ParseEmbeddedPairManifest(factorySource);
        using var artifact = LoadPairArtifactManifest();

        Assert.Equal(ParseMethods(artifact.RootElement), ParseMethods(embedded.RootElement));
        Assert.Equal(ParseEvents(artifact.RootElement), ParseEvents(embedded.RootElement));
    }
}
