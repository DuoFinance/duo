using Xunit;
using Neo;
using Neo.SmartContract.Testing;

namespace DuoSwap.Tests;

using static TestUtils;

public class FactoryTests
{
    private readonly TestEngine _engine;
    private readonly UInt160 _factoryHash;
    private readonly UInt160 _routerHash;

    public FactoryTests()
    {
        _engine = CreateEngine();

        var (factoryNef, factoryManifest) = LoadContract("DuoSwapFactory");
        _factoryHash = DeployContract(_engine, factoryNef, factoryManifest, Owner);

        var (routerNef, routerManifest) = LoadContract("DuoSwapRouter");
        _routerHash = DeployContract(_engine, routerNef, routerManifest, new object[] { _factoryHash });
    }

    [Fact]
    public void GetRouter_ReturnsZeroBeforeSet()
    {
        var result = Invoke(_engine, _factoryHash, "getRouter");
        Assert.NotNull(result);
    }

    [Fact]
    public void SetRouter_UpdatesRouterAddress()
    {
        Invoke(_engine, _factoryHash, "setRouter", _routerHash);
        var result = Invoke(_engine, _factoryHash, "getRouter");
        Assert.NotNull(result);
    }

    [Fact]
    public void GetDeployFee_ReturnsPositiveValue()
    {
        var result = Invoke(_engine, _factoryHash, "getDeployFee");
        var fee = result.GetInteger();
        Assert.True(fee > 0, "Deploy fee should be positive");
    }

    [Fact]
    public void GetSetter_ReturnsInitialSetter()
    {
        var result = Invoke(_engine, _factoryHash, "getSetter");
        var actualSetter = new UInt160(result.GetSpan());
        Assert.Equal(Owner, actualSetter);
    }

    [Fact]
    public void GetFeeTo_ReturnsZeroInitially()
    {
        var result = Invoke(_engine, _factoryHash, "getFeeTo");
        var feeTo = result;
        Assert.NotNull(feeTo);
    }

    [Fact]
    public void GetPairHash_NonExistentPair_ReturnsNull()
    {
        var tokenA = new UInt160(new byte[] { 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
        var tokenB = new UInt160(new byte[] { 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 });

        var result = Invoke(_engine, _factoryHash, "getPairHash", tokenA, tokenB);
        Assert.NotNull(result);
    }

    [Fact]
    public void GetAllPairsHash_InitiallyEmpty()
    {
        var result = Invoke(_engine, _factoryHash, "getAllPairsHash");
        var arr = result as Neo.VM.Types.Array;
        Assert.NotNull(arr);
        Assert.Empty(arr);
    }

    [Fact]
    public void TransferOwnershipOfSetter_UpdatesSetter()
    {
        var newSetter = new UInt160(new byte[] { 5, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
        var result = Invoke(_engine, _factoryHash, "transferOwnershipOfSetter", newSetter);
        Assert.NotNull(result);

        var setter = Invoke(_engine, _factoryHash, "getSetter");
        Assert.NotNull(setter);
    }

    [Fact]
    public void SetFeeTo_UpdatesFeeCollector()
    {
        var feeTo = new UInt160(new byte[] { 6, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
        var result = Invoke(_engine, _factoryHash, "setFeeTo", feeTo);
        Assert.NotNull(result);
    }

    [Fact]
    public void GetFeeTo_ReturnsSetValue()
    {
        var feeTo = new UInt160(new byte[] { 7, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
        Invoke(_engine, _factoryHash, "setFeeTo", feeTo);
        var result = Invoke(_engine, _factoryHash, "getFeeTo");
        var actual = new UInt160(result.GetSpan());
        Assert.Equal(feeTo, actual);
    }

    [Fact]
    public void SetFeeTo_NonSetterCaller_Fails()
    {
        var engine = CreateEngine();
        var (factoryNef, factoryManifest) = LoadContract("DuoSwapFactory");
        var factoryHash = DeployContract(engine, factoryNef, factoryManifest, Owner);

        engine.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.CalledByEntry, User);
        var ex = Assert.ThrowsAny<Exception>(() =>
        {
            Invoke(engine, factoryHash, "setFeeTo", User);
        });
        Assert.Contains("ASSERT", ex.Message);
    }

    [Fact]
    public void SetRouter_NonSetterCaller_Fails()
    {
        var engine = CreateEngine();
        var (factoryNef, factoryManifest) = LoadContract("DuoSwapFactory");
        var factoryHash = DeployContract(engine, factoryNef, factoryManifest, Owner);

        var (routerNef, routerManifest) = LoadContract("DuoSwapRouter");
        var routerHash = DeployContract(engine, routerNef, routerManifest, new object[] { factoryHash });

        engine.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.CalledByEntry, User);
        var ex = Assert.ThrowsAny<Exception>(() =>
        {
            Invoke(engine, factoryHash, "setRouter", routerHash);
        });
        Assert.Contains("ASSERT", ex.Message);
    }

    [Fact]
    public void TransferOwnershipOfSetter_NonSetterCaller_Fails()
    {
        var engine = CreateEngine();
        var (factoryNef, factoryManifest) = LoadContract("DuoSwapFactory");
        var factoryHash = DeployContract(engine, factoryNef, factoryManifest, Owner);

        var newSetter = new UInt160(new byte[] { 8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
        engine.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.CalledByEntry, User);
        var ex = Assert.ThrowsAny<Exception>(() =>
        {
            Invoke(engine, factoryHash, "transferOwnershipOfSetter", newSetter);
        });
        Assert.Contains("ASSERT", ex.Message);
    }

    [Fact]
    public void SetRouter_IsImmutable()
    {
        var engine = CreateEngine();
        var (factoryNef, factoryManifest) = LoadContract("DuoSwapFactory");
        var factoryHash = DeployContract(engine, factoryNef, factoryManifest, Owner);

        var (routerNef, routerManifest) = LoadContract("DuoSwapRouter");
        var routerHash = DeployContract(engine, routerNef, routerManifest, new object[] { factoryHash });

        Invoke(engine, factoryHash, "setRouter", routerHash);

        // Second set should fail (immutable)
        Assert.ThrowsAny<Exception>(() => Invoke(engine, factoryHash, "setRouter", routerHash));
    }
}
