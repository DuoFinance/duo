using System.Numerics;
using Xunit;
using Neo;
using Neo.SmartContract;
using Neo.SmartContract.Testing;

namespace DuoSwap.Tests;

using static TestUtils;

public class DuoSwapPairSecurityTests
{
    [Fact]
    public void Security_Swap_OnlyRouterOrPair_CanCall()
    {
        var (engine, pair, tokenA, tokenB, router) = SetupPairWithLiquidity();
        
        engine.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.Global, User);
        
        var ex = Assert.ThrowsAny<Exception>(() =>
        {
            Invoke(engine, pair, "swap", BigInteger.Zero, 1000, User, new byte[] { });
        });
        Assert.Contains("ASSERT", ex.Message);
    }

    [Fact]
    public void Security_Mint_OnlyRouter_CanCall()
    {
        var (engine, factory, router, tokenA, tokenB) = DeployCore();
        var pair = DeployPair(engine, factory, tokenA, tokenB);

        engine.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.Global, Owner);
        
        var ex = Assert.ThrowsAny<Exception>(() =>
        {
            Invoke(engine, pair, "mint", Owner);
        });
        Assert.Contains("ASSERT", ex.Message);
    }

    [Fact]
    public void Security_Burn_OnlyRouter_CanCall()
    {
        var (engine, pair, tokenA, tokenB, router) = SetupPairWithLiquidity();
        
        var userLp = Invoke(engine, pair, "balanceOf", Owner).GetInteger();
        
        engine.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.Global, Owner);
        Invoke(engine, pair, "transfer", Owner, pair, userLp, null);

        var ex = Assert.ThrowsAny<Exception>(() =>
        {
            Invoke(engine, pair, "burn", Owner);
        });
        Assert.Contains("ASSERT", ex.Message);
    }

    [Fact]
    public void Security_InitialisePair_OnlyFactory_CanCall()
    {
        var (engine, factory, router, tokenA, tokenB) = DeployCore();

        var (pairNef, pairManifest) = LoadContract("DuoSwapPair");
        var pair = DeployContract(engine, pairNef, pairManifest, new object[] { factory });

        engine.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.Global, Owner);
        
        var ex = Assert.ThrowsAny<Exception>(() =>
        {
            Invoke(engine, pair, "initialisePair", tokenA, tokenB, "TEST-PAIR", router);
        });
        Assert.Contains("ASSERT", ex.Message);
    }

    [Fact]
    public void Security_Swap_BadRecipient_Rejected()
    {
        var (engine, pair, tokenA, tokenB, router) = SetupPairWithLiquidity();
        
        var ex = Assert.ThrowsAny<Exception>(() =>
        {
            Invoke(engine, pair, "swap", BigInteger.Zero, 1000, pair, new byte[] { });
        });
        Assert.Contains("ASSERT", ex.Message);
    }

    [Fact]
    public void Security_Swap_InsufficientLiquidity_Rejected()
    {
        var (engine, factory, router, tokenA, tokenB) = DeployCore();
        var pair = DeployPair(engine, factory, tokenA, tokenB);
        Invoke(engine, router, "addLiquidity", Owner, tokenA, tokenB, 10000, 10000, 0, 0, long.MaxValue);
        
        var ex = Assert.ThrowsAny<Exception>(() =>
        {
            Invoke(engine, pair, "swap", BigInteger.Zero, 100000, Owner, new byte[] { });
        });
        Assert.Contains("ASSERT", ex.Message);
    }

    [Fact]
    public void Security_Transfer_FromNonOwner_Rejected()
    {
        var (engine, pair, tokenA, tokenB, router) = SetupPairWithLiquidity();
        
        var ownerBalance = Invoke(engine, pair, "balanceOf", Owner).GetInteger();

        engine.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.Global, User);
        
        var ex = Assert.ThrowsAny<Exception>(() =>
        {
            Invoke(engine, pair, "transfer", Owner, User, ownerBalance, null);
        });
        Assert.Contains("ASSERT", ex.Message);
    }

    [Fact]
    public void Security_Transfer_InsufficientBalance_Rejected()
    {
        var (engine, pair, tokenA, tokenB, router) = SetupPairWithLiquidity();
        
        engine.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.Global, Owner);
        
        var ex = Assert.ThrowsAny<Exception>(() =>
        {
            Invoke(engine, pair, "transfer", Owner, User, BigInteger.Parse("10000000000000000000"), null);
        });
        Assert.Contains("ASSERT", ex.Message);
    }

    [Fact]
    public void Security_Transfer_ZeroAmount_Rejected()
    {
        var (engine, pair, tokenA, tokenB, router) = SetupPairWithLiquidity();
        
        engine.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.Global, Owner);
        
        var ex = Assert.ThrowsAny<Exception>(() =>
        {
            Invoke(engine, pair, "transfer", Owner, User, BigInteger.Zero, null);
        });
        Assert.Contains("ASSERT", ex.Message);
    }

    [Fact]
    public void Security_ProtocolFee_OnlyWhenFeeToSet()
    {
        var (engine, pair, tokenA, tokenB, router) = SetupPairWithLiquidity();
        
        var kLast = Invoke(engine, pair, "getKLast").GetInteger();
        Assert.Equal(BigInteger.Zero, kLast);
    }

    [Fact]
    public void Boundary_Swap_MaxAmountOut()
    {
        var (engine, pair, tokenA, tokenB, router) = SetupPairWithLiquidity();
        
        var reserves = GetReserves(engine, pair);
        
        var ex = Assert.ThrowsAny<Exception>(() =>
        {
            Invoke(engine, pair, "swap", reserves[0], BigInteger.Zero, Owner, new byte[] { });
        });
        Assert.Contains("ASSERT", ex.Message);
    }

    [Fact]
    public void Boundary_Mint_SingleTokenDeposit_Fails()
    {
        var (engine, factory, router, tokenA, tokenB) = DeployCore();
        var pair = DeployPair(engine, factory, tokenA, tokenB);

        var ex = Assert.ThrowsAny<Exception>(() =>
        {
            Invoke(engine, router, "addLiquidity", Owner, tokenA, tokenB, 100000, 0, 0, 0, long.MaxValue);
        });
        Assert.Contains("ASSERT", ex.Message);
    }

    private static (TestEngine engine, UInt160 pair, UInt160 tokenA, UInt160 tokenB, UInt160 router) SetupPairWithLiquidity(
        BigInteger amountA = default, BigInteger amountB = default)
    {
        var (engine, factory, router, tokenA, tokenB) = DeployCore();
        var pair = DeployPair(engine, factory, tokenA, tokenB);

        if (amountA == default) amountA = 10_000_000_00;
        if (amountB == default) amountB = amountA;
        Invoke(engine, router, "addLiquidity", Owner, tokenA, tokenB, amountA, amountB, 0, 0, long.MaxValue);

        return (engine, pair, tokenA, tokenB, router);
    }

    private static (TestEngine engine, UInt160 factory, UInt160 router, UInt160 tokenA, UInt160 tokenB) DeployCore()
    {
        var engine = CreateEngine();
        engine.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.Global, Owner);

        var (fn, fm) = LoadContract("DuoSwapFactory");
        var factory = DeployContract(engine, fn, fm, Owner);

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
        engine.Execute(Neo.VM.Helper.MakeScript(gas, "transfer",
            new ContractParameter(ContractParameterType.Hash160) { Value = Owner },
            new ContractParameter(ContractParameterType.Hash160) { Value = factory },
            new ContractParameter(ContractParameterType.Integer) { Value = fee }, d));

        return new UInt160(Invoke(engine, factory, "getPairHash", tokenA, tokenB).GetSpan());
    }

    private static BigInteger[] GetReserves(TestEngine engine, UInt160 pairHash)
    {
        var reserve0 = Invoke(engine, pairHash, "getReserve0").GetInteger();
        var reserve1 = Invoke(engine, pairHash, "getReserve1").GetInteger();
        return new BigInteger[] { reserve0, reserve1 };
    }
}
