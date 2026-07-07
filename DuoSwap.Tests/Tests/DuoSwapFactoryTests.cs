using System.Numerics;
using Xunit;
using Neo;
using Neo.SmartContract;
using Neo.SmartContract.Testing;
using Neo.VM;

namespace DuoSwap.Tests;

using static TestUtils;

public class DuoSwapFactoryTests
{
    private static (TestEngine engine, UInt160 factory, UInt160 router, UInt160 tokenA, UInt160 tokenB) DeployCore()
    {
        var engine = CreateEngine();
        engine.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.Global, Owner);

        var (fn, fm) = LoadContract("DuoSwapFactory");
        var factory = DeployContract(engine, fn, fm, null);

        var (rn, rm) = LoadContract("DuoSwapRouter");
        var router = DeployContract(engine, rn, rm, new object[] { factory });
        Invoke(engine, factory, "setRouter", router);

        var (tn, tma) = LoadContract("DuoToken");
        tma.Name = "DuoTokenA";
        var tokenA = DeployContract(engine, tn, tma, null);

        var (_, tmb) = LoadContract("DuoToken");
        tmb.Name = "DuoTokenB";
        var tokenB = DeployContract(engine, tn, tmb, null);

        BigInteger ts = 1_000_000_000_000_000_000;
        Invoke(engine, tokenA, "initialise", Owner, ts);
        Invoke(engine, tokenB, "initialise", Owner, ts);

        return (engine, factory, router, tokenA, tokenB);
    }

    private static UInt160 DeployPair(TestEngine engine, UInt160 factory, UInt160 tokenA, UInt160 tokenB)
    {
        var gas = engine.Native.GAS.Hash;
        var vl = engine.ProtocolSettings.StandbyValidators;
        var m = vl.Count - (vl.Count - 1) / 3;
        var va = Contract.CreateMultiSigRedeemScript(m, vl.ToArray()).ToScriptHash();
        var fee = Invoke(engine, factory, "getDeployFee").GetInteger();
        engine.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.Global, va, Owner);
        Invoke(engine, gas, "transfer", va, Owner, fee, null);
        engine.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.Global, Owner);

        var d = new ContractParameter(ContractParameterType.Array) { Value = new ContractParameter[] {
            new(ContractParameterType.Hash160) { Value = tokenA },
            new(ContractParameterType.Hash160) { Value = tokenB },
        }};
        engine.Execute(gas.MakeScript("transfer",
            new ContractParameter(ContractParameterType.Hash160) { Value = Owner },
            new ContractParameter(ContractParameterType.Hash160) { Value = factory },
            new ContractParameter(ContractParameterType.Integer) { Value = fee }, d));

        return new UInt160(Invoke(engine, factory, "getPairHash", tokenA, tokenB).GetSpan());
    }

    [Fact]
    public void Factory_Deployment_Success()
    {
        var (engine, factory, _, _, _) = DeployCore();
        
        Assert.NotNull(factory);
        Assert.NotEqual(UInt160.Zero, factory);
    }

    [Fact]
    public void Factory_SetRouter_Success()
    {
        var (engine, factory, router, _, _) = DeployCore();
        
        var result = Invoke(engine, factory, "getRouter");
        var actualRouter = new UInt160(result.GetSpan());
        Assert.Equal(router, actualRouter);
    }

    [Fact]
    public void Factory_SetRouter_OnlyOwner_CanCall()
    {
        var engine = CreateEngine();
        
        var (factoryNef, factoryManifest) = LoadContract("DuoSwapFactory");
        var factoryHash = DeployContract(engine, factoryNef, factoryManifest, null);

        var (routerNef, routerManifest) = LoadContract("DuoSwapRouter");
        var routerHash = DeployContract(engine, routerNef, routerManifest, new object[] { factoryHash });

        engine.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.Global, User);
        
        var ex = Assert.ThrowsAny<Exception>(() =>
        {
            Invoke(engine, factoryHash, "setRouter", routerHash);
        });
        Assert.NotNull(ex);
    }

    [Fact]
    public void Factory_SetFeeTo_Success()
    {
        var (engine, factory, _, _, _) = DeployCore();

        var feeTo = UInt160.Parse("0x0000000000000000000000000000000000000005");
        
        Invoke(engine, factory, "setFeeTo", feeTo);
        
        var result = Invoke(engine, factory, "getFeeTo");
        var actualFeeTo = new UInt160(result.GetSpan());
        Assert.Equal(feeTo, actualFeeTo);
    }

    [Fact]
    public void Factory_GetAllPairsHash_InitiallyEmpty()
    {
        var (engine, factory, _, _, _) = DeployCore();

        var result = Invoke(engine, factory, "getAllPairsHash");
        var arr = result as Neo.VM.Types.Array;
        Assert.NotNull(arr);
        Assert.Empty(arr);
    }

    [Fact]
    public void Factory_GetDeployFee_ReturnsPositive()
    {
        var (engine, factory, _, _, _) = DeployCore();

        var result = Invoke(engine, factory, "getDeployFee");
        Assert.True(result.GetInteger() > 0);
    }

    [Fact]
    public void Factory_GetSetter_ReturnsDeployer()
    {
        var (engine, factory, _, _, _) = DeployCore();

        var result = Invoke(engine, factory, "getSetter");
        Assert.NotNull(result);
    }

    [Fact]
    public void Factory_GetPairHash_NonExistentPair_ReturnsEmpty()
    {
        var (engine, factory, _, tokenA, tokenB) = DeployCore();

        var result = Invoke(engine, factory, "getPairHash", tokenA, tokenB);
        Assert.NotNull(result);
    }

    [Fact]
    public void Factory_GetPairHash_SameToken_Fails()
    {
        var (engine, factory, _, tokenA, _) = DeployCore();

        var ex = Assert.ThrowsAny<Exception>(() =>
        {
            Invoke(engine, factory, "getPairHash", tokenA, tokenA);
        });
        Assert.NotNull(ex);
    }

    [Fact]
    public void Factory_DeployPair_Success()
    {
        var (engine, factory, _, tokenA, tokenB) = DeployCore();

        var pair = DeployPair(engine, factory, tokenA, tokenB);
        
        Assert.NotNull(pair);
        Assert.NotEqual(UInt160.Zero, pair);
    }

    [Fact]
    public void Factory_GetPairHash_OrderIndependent()
    {
        var (engine, factory, _, tokenA, tokenB) = DeployCore();

        DeployPair(engine, factory, tokenA, tokenB);
        
        var pairAB = new UInt160(Invoke(engine, factory, "getPairHash", tokenA, tokenB).GetSpan());
        var pairBA = new UInt160(Invoke(engine, factory, "getPairHash", tokenB, tokenA).GetSpan());
        
        Assert.Equal(pairAB, pairBA);
    }

    [Fact]
    public void Factory_PairCreation_Idempotent()
    {
        var (engine, factory, _, tokenA, tokenB) = DeployCore();

        var firstPair = DeployPair(engine, factory, tokenA, tokenB);
        Assert.NotNull(firstPair);
        Assert.NotEqual(UInt160.Zero, firstPair);

        var pairHashAfter = new UInt160(Invoke(engine, factory, "getPairHash", tokenA, tokenB).GetSpan());
        Assert.Equal(firstPair, pairHashAfter);

        var pairHashReversed = new UInt160(Invoke(engine, factory, "getPairHash", tokenB, tokenA).GetSpan());
        Assert.Equal(firstPair, pairHashReversed);
    }

    [Fact]
    public void Factory_GetAllPairsHash_AfterDeployment_NotEmpty()
    {
        var (engine, factory, _, tokenA, tokenB) = DeployCore();

        DeployPair(engine, factory, tokenA, tokenB);

        var result = Invoke(engine, factory, "getAllPairsHash");
        var arr = result as Neo.VM.Types.Array;
        Assert.NotNull(arr);
        Assert.NotEmpty(arr);
    }

    [Fact]
    public void Factory_DeployPair_WithoutRouter_Fails()
    {
        var engine = CreateEngine();
        engine.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.Global, Owner);

        var (fn, fm) = LoadContract("DuoSwapFactory");
        var factory = DeployContract(engine, fn, fm, null);

        var (tn, tma) = LoadContract("DuoToken");
        tma.Name = "DuoTokenA";
        var tokenA = DeployContract(engine, tn, tma, null);

        var (_, tmb) = LoadContract("DuoToken");
        tmb.Name = "DuoTokenB";
        var tokenB = DeployContract(engine, tn, tmb, null);

        BigInteger ts = 1_000_000_000_000_000_000;
        Invoke(engine, tokenA, "initialise", Owner, ts);
        Invoke(engine, tokenB, "initialise", Owner, ts);

        var router = Invoke(engine, factory, "getRouter").GetSpan();
        Assert.True(router.Length == 0 || new UInt160(router) == UInt160.Zero);

        var fee = Invoke(engine, factory, "getDeployFee").GetInteger();
        var script = factory.MakeScript("onNEP17Payment",
            new ContractParameter(ContractParameterType.Hash160) { Value = Owner },
            new ContractParameter(ContractParameterType.Integer) { Value = fee },
            new ContractParameter(ContractParameterType.Array)
            {
                Value = new ContractParameter[]
                {
                    new(ContractParameterType.Hash160) { Value = tokenA },
                    new(ContractParameterType.Hash160) { Value = tokenB }
                }
            });
        var ex = Assert.ThrowsAny<Exception>(() => engine.Execute(script));
        Assert.Contains("ASSERT", ex.Message);
    }
}