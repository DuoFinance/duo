using System.Numerics;
using System.Reflection;
using Neo;
using Neo.Json;
using Neo.SmartContract;
using Neo.SmartContract.Manifest;
using Neo.SmartContract.Testing;
using Neo.VM.Types;

namespace DuoSwap.Tests;

#nullable enable

public static class TestUtils
{
    public static readonly UInt160 Owner = UInt160.Parse("0x0000000000000000000000000000000000000002");
    public static readonly UInt160 User = UInt160.Parse("0x0000000000000000000000000000000000000003");
    public static readonly UInt160 Treasury = UInt160.Parse("0x0000000000000000000000000000000000000004");
    public static readonly UInt160 Bonding = UInt160.Parse("0x0000000000000000000000000000000000000005");

    private static readonly object EngineLock = new();

    public static TestEngine CreateEngine()
    {
        lock (EngineLock)
        {
            var engine = new TestEngine();
            engine.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.CalledByEntry, Owner);
            return engine;
        }
    }

    public static (NefFile nef, ContractManifest manifest) LoadContract(string contractName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var nefStream = assembly.GetManifestResourceStream($"DuoSwap.Tests.Contracts.{contractName}.nef");
        using var manifestStream = assembly.GetManifestResourceStream($"DuoSwap.Tests.Contracts.{contractName}.manifest.json");

        if (nefStream == null || manifestStream == null)
            throw new FileNotFoundException($"Contract {contractName} not found in embedded resources");

        using var ms = new MemoryStream();
        nefStream.CopyTo(ms);
        var nef = Neo.IO.Helper.AsSerializable<NefFile>(ms.ToArray());

        using var mr = new StreamReader(manifestStream);
        var json = mr.ReadToEnd();
        var manifest = ContractManifest.FromJson((JObject)JObject.Parse(json)!);

        return (nef, manifest);
    }

    public static UInt160 DeployContract(TestEngine engine, NefFile nef, ContractManifest manifest, object? data = null)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        nef.Serialize(writer);
        var nefBytes = ms.ToArray();

        var manifestJson = manifest.ToJson().ToString();
        var manifestBytes = System.Text.Encoding.UTF8.GetBytes(manifestJson);

        var contractState = engine.Native.ContractManagement.Deploy(nefBytes, manifestBytes, data);
        return contractState.Hash;
    }

    public static StackItem Invoke(TestEngine engine, UInt160 contractHash, string method, params object[] args)
    {
        var script = Neo.VM.Helper.MakeScript(contractHash, method, args);
        var result = engine.Execute(script);
        return result;
    }
}

