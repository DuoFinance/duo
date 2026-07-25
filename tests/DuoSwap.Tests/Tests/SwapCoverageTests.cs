using System.Numerics;
using Xunit;
using Neo;
using Neo.SmartContract;
using Neo.SmartContract.Testing;
using Neo.VM;


namespace DuoSwap.Tests;

using static TestUtils;

public class SwapCoverageTests
{
    #region Helpers

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
        engine.Execute(gas.MakeScript("transfer",
            new ContractParameter(ContractParameterType.Hash160) { Value = Owner },
            new ContractParameter(ContractParameterType.Hash160) { Value = factory },
            new ContractParameter(ContractParameterType.Integer) { Value = fee }, d));

        return new UInt160(Invoke(engine, factory, "getPairHash", tokenA, tokenB).GetSpan());
    }

    private static void AddLiquidity(TestEngine engine, UInt160 router, UInt160 tokenA, UInt160 tokenB,
        BigInteger amountA = default, BigInteger amountB = default)
    {
        if (amountA == default) amountA = 10_000_000_00;
        if (amountB == default) amountB = amountA;
        Invoke(engine, router, "addLiquidity", Owner, tokenA, tokenB,
            amountA, amountB, 0, 0, long.MaxValue);
    }

    #endregion

    // ============================================
    // PAIR: access control & init edge cases
    // ============================================

    [Fact]
    public void Pair_Mint_NonRouterCaller_Fails()
    {
        var (e, f, r, a, b) = DeployCore();
        var p = DeployPair(e, f, a, b);
        AddLiquidity(e, r, a, b);

        var ex = Assert.ThrowsAny<Exception>(() => Invoke(e, p, "mint", Owner));
        Assert.Contains("ASSERT", ex.Message);
    }

    [Fact]
    public void Pair_Burn_NonRouterCaller_Fails()
    {
        var (e, f, r, a, b) = DeployCore();
        var p = DeployPair(e, f, a, b);
        AddLiquidity(e, r, a, b);

        var ex = Assert.ThrowsAny<Exception>(() => Invoke(e, p, "burn", Owner));
        Assert.Contains("ASSERT", ex.Message);
    }

    [Fact]
    public void Pair_InitialisePair_NonFactoryCaller_Fails()
    {
        var (e, f, _, _, _) = DeployCore();

        var (pairNef, pairManifest) = LoadContract("DuoSwapPair");
        var pair = DeployContract(e, pairNef, pairManifest, new object[] { f });

        var gas = e.Native.GAS.Hash;
        var ex = Assert.ThrowsAny<Exception>(() =>
            Invoke(e, pair, "initialisePair", gas, gas, "TEST", Owner, UInt160.Zero));
        Assert.Contains("ASSERT", ex.Message);
    }

    [Fact]
    public void Pair_Swap_NegativeAmountOut_Fails()
    {
        var (e, f, r, a, b) = DeployCore();
        var p = DeployPair(e, f, a, b);
        AddLiquidity(e, r, a, b);

        var ex = Assert.ThrowsAny<Exception>(() =>
            Invoke(e, p, "swap", BigInteger.MinusOne, BigInteger.Zero, User, new byte[] { }));
        Assert.Contains("ASSERT", ex.Message);
    }

    [Fact]
    public void Pair_Swap_BadRecipient_Self_Fails()
    {
        var (e, f, r, a, b) = DeployCore();
        var p = DeployPair(e, f, a, b);
        AddLiquidity(e, r, a, b);

        var ex = Assert.ThrowsAny<Exception>(() =>
            Invoke(e, p, "swap", BigInteger.One, BigInteger.Zero, p, new byte[] { }));
        Assert.Contains("ASSERT", ex.Message);
    }

    // ============================================
    // PAIR: LP token transfer edge cases
    // ============================================

    [Fact]
    public void Pair_Transfer_UnauthorizedCaller_Fails()
    {
        var (e, f, r, a, b) = DeployCore();
        var p = DeployPair(e, f, a, b);
        AddLiquidity(e, r, a, b);

        var lpBalance = Invoke(e, p, "balanceOf", Owner).GetInteger();
        Invoke(e, p, "transfer", Owner, User, lpBalance / 2, null);
        Assert.True(Invoke(e, p, "balanceOf", User).GetInteger() > 0);

        e.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.Global, User);
        var ex = Assert.ThrowsAny<Exception>(() =>
            Invoke(e, p, "transfer", Owner, User, 1, null));
        Assert.Contains("ASSERT", ex.Message);
    }

    // ============================================
    // FACTORY: deploy edge cases
    // ============================================

    [Fact]
    public void Factory_GetPairHash_IdenticalTokens_Fails()
    {
        var engine = CreateEngine();
        var (fn, fm) = LoadContract("DuoSwapFactory");
        var factory = DeployContract(engine, fn, fm, Owner);

        var tokenA = UInt160.Parse("0x0000000000000000000000000000000000000001");
        var ex = Assert.ThrowsAny<Exception>(() =>
            Invoke(engine, factory, "getPairHash", tokenA, tokenA));
        Assert.Contains("Cannot call this SYSCALL", ex.Message);
    }

    [Fact]
    public void Factory_WithdrawDeployFee_WithFeeSet_SendsGas()
    {
        var (e, f, _, a, b) = DeployCore();
        DeployPair(e, f, a, b);

        var balBefore = Invoke(e, e.Native.GAS.Hash, "balanceOf", User).GetInteger();
        Invoke(e, f, "setFeeTo", User);
        Invoke(e, f, "withdrawDeployFee");
        var balAfter = Invoke(e, e.Native.GAS.Hash, "balanceOf", User).GetInteger();
        Assert.True(balAfter > balBefore);
    }

    [Fact]
    public void Factory_OnNEP17Payment_InvalidSender_Fails()
    {
        var engine = CreateEngine();
        engine.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.Global, Owner);

        var (fn, fm) = LoadContract("DuoSwapFactory");
        var factory = DeployContract(engine, fn, fm, Owner);

        var (rn, rm) = LoadContract("DuoSwapRouter");
        var router = DeployContract(engine, rn, rm, new object[] { factory });
        Invoke(engine, factory, "setRouter", router);

        var script = factory.MakeScript("onNEP17Payment",
            new ContractParameter(ContractParameterType.Hash160) { Value = UInt160.Zero },
            new ContractParameter(ContractParameterType.Integer) { Value = BigInteger.One },
            new ContractParameter(ContractParameterType.Array)
            {
                Value = new ContractParameter[]
                {
                    new(ContractParameterType.Hash160) { Value = Owner },
                    new(ContractParameterType.Hash160) { Value = Owner }
                }
            });
        var ex = Assert.ThrowsAny<Exception>(() => engine.Execute(script));
        Assert.Contains("ASSERT", ex.Message);
    }

    [Fact]
    public void Factory_OnNEP17Payment_WrongFee_Fails()
    {
        var engine = CreateEngine();
        engine.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.Global, Owner);

        var (fn, fm) = LoadContract("DuoSwapFactory");
        var factory = DeployContract(engine, fn, fm, Owner);

        var (rn, rm) = LoadContract("DuoSwapRouter");
        var router = DeployContract(engine, rn, rm, new object[] { factory });
        Invoke(engine, factory, "setRouter", router);

        var script = factory.MakeScript("onNEP17Payment",
            new ContractParameter(ContractParameterType.Hash160) { Value = Owner },
            new ContractParameter(ContractParameterType.Integer) { Value = BigInteger.One },
            new ContractParameter(ContractParameterType.Array)
            {
                Value = new ContractParameter[]
                {
                    new(ContractParameterType.Hash160) { Value = Owner },
                    new(ContractParameterType.Hash160) { Value = Owner }
                }
            });
        var ex = Assert.ThrowsAny<Exception>(() => engine.Execute(script));
        Assert.Contains("ASSERT", ex.Message);
    }

    // ============================================
    // FACTORY: Router can be updated
    // ============================================

    [Fact]
    public void Factory_SetRouter_AfterFirstSet_Updates()
    {
        var (e, f, _, a, b) = DeployCore();
        DeployPair(e, f, a, b);

        var (rn, rm) = LoadContract("DuoSwapRouter");
        rm.Name = "DuoSwapRouterV2";
        var newRouter = DeployContract(e, rn, rm, new object[] { f });

        // Second set should succeed (updatable)
        Invoke(e, f, "setRouter", newRouter);
    }

    // ============================================
    // ROUTER: comprehensive coverage
    // ============================================

    [Fact]
    public void Router_AddLiquidity_NonOptimalRatio_AdjustsAmount()
    {
        var (e, f, r, a, b) = DeployCore();
        DeployPair(e, f, a, b);

        Invoke(e, r, "addLiquidity", Owner, a, b,
            10_000_000_00, 20_000_000_00, 0, 0, long.MaxValue);

        var pair = new UInt160(Invoke(e, f, "getPairHash", a, b).GetSpan());
        var reserve0 = Invoke(e, pair, "getReserve0").GetInteger();
        var reserve1 = Invoke(e, pair, "getReserve1").GetInteger();
        Assert.Equal(10_000_000_00, reserve0);
        Assert.Equal(20_000_000_00, reserve1);
    }

    [Fact]
    public void Router_RemoveLiquidity_Basic_Succeeds()
    {
        var (e, f, r, a, b) = DeployCore();
        var p = DeployPair(e, f, a, b);
        AddLiquidity(e, r, a, b);

        var lp = Invoke(e, p, "balanceOf", Owner).GetInteger();
        Assert.True(lp > 1000);

        var ba = Invoke(e, a, "balanceOf", Owner).GetInteger();
        var bb = Invoke(e, b, "balanceOf", Owner).GetInteger();

        Invoke(e, r, "removeLiquidity", Owner, a, b,
            lp / 2, 0, 0, long.MaxValue);

        Assert.True(Invoke(e, a, "balanceOf", Owner).GetInteger() > ba);
        Assert.True(Invoke(e, b, "balanceOf", Owner).GetInteger() > bb);
    }

    [Fact]
    public void Router_GetAmountsOut_SingleHop_ReturnsCorrectValues()
    {
        var (e, f, r, a, b) = DeployCore();
        DeployPair(e, f, a, b);
        AddLiquidity(e, r, a, b);

        var path = new ContractParameter(ContractParameterType.Array)
        {
            Value = new ContractParameter[]
            {
                new(ContractParameterType.Hash160) { Value = a },
                new(ContractParameterType.Hash160) { Value = b },
            }
        };
        var result = Invoke(e, r, "getAmountsOut", 1_000_000_00, path);
        var arr = (Neo.VM.Types.Array)result;
        Assert.Equal(2, arr.Count);
        Assert.Equal(1_000_000_00, arr[0].GetInteger());
        Assert.True(arr[1].GetInteger() > 0);
        Assert.True(arr[1].GetInteger() < arr[0].GetInteger());
    }

    [Fact]
    public void Router_GetAmountsIn_SingleHop_ReturnsCorrectValues()
    {
        var (e, f, r, a, b) = DeployCore();
        DeployPair(e, f, a, b);
        AddLiquidity(e, r, a, b);

        var path = new ContractParameter(ContractParameterType.Array)
        {
            Value = new ContractParameter[]
            {
                new(ContractParameterType.Hash160) { Value = a },
                new(ContractParameterType.Hash160) { Value = b },
            }
        };
        var result = Invoke(e, r, "getAmountsIn", 100_000_00, path);
        var arr = (Neo.VM.Types.Array)result;
        Assert.Equal(2, arr.Count);
        Assert.True(arr[0].GetInteger() > 100_000_00);
        Assert.Equal(100_000_00, arr[1].GetInteger());
    }

    [Fact]
    public void Router_GetAmountsOut_SingleHopPath_Fails()
    {
        var (e, _, r, a, _) = DeployCore();
        var path = new ContractParameter(ContractParameterType.Array)
        {
            Value = new ContractParameter[] { new(ContractParameterType.Hash160) { Value = a } }
        };
        var ex = Assert.ThrowsAny<Exception>(() =>
            Invoke(e, r, "getAmountsOut", 1_000_000_00, path));
        Assert.Contains("ASSERT", ex.Message);
    }

    [Fact]
    public void Router_GetAmountsOut_IdenticalTokens_Fails()
    {
        var (e, _, r, a, _) = DeployCore();
        var path = new ContractParameter(ContractParameterType.Array)
        {
            Value = new ContractParameter[]
            {
                new(ContractParameterType.Hash160) { Value = a },
                new(ContractParameterType.Hash160) { Value = a },
            }
        };
        var ex = Assert.ThrowsAny<Exception>(() =>
            Invoke(e, r, "getAmountsOut", 1_000_000_00, path));
        Assert.Contains("ASSERT", ex.Message);
    }

    [Fact]
    public void Router_SwapExactTokensForTokens_HappyPath_Succeeds()
    {
        var (e, f, r, a, b) = DeployCore();
        DeployPair(e, f, a, b);
        AddLiquidity(e, r, a, b);

        var swapAmount = 1_000_000_00;
        var balB = Invoke(e, b, "balanceOf", Owner).GetInteger();

        var path = new ContractParameter(ContractParameterType.Array)
        {
            Value = new ContractParameter[]
            {
                new(ContractParameterType.Hash160) { Value = a },
                new(ContractParameterType.Hash160) { Value = b },
            }
        };
        var result = Invoke(e, r, "swapExactTokensForTokens",
            Owner, swapAmount, BigInteger.Zero, path, long.MaxValue);
        Assert.True(result.GetBoolean());

        var balBAfter = Invoke(e, b, "balanceOf", Owner).GetInteger();
        Assert.True(balBAfter > balB);
    }

    [Fact]
    public void Router_Quote_ExtremeLargeAmount_Succeeds()
    {
        var (e, _, r, _, _) = DeployCore();
        var result = Invoke(e, r, "quote",
            BigInteger.Parse("999999999999999999999"),
            BigInteger.Parse("1000000000000000000000"),
            BigInteger.Parse("2000000000000000000000"));
        Assert.True(result.GetInteger() > 0);
    }

    [Fact]
    public void Router_GetAmountOut_ExtremeLarge_ReturnsReasonable()
    {
        var (e, _, r, _, _) = DeployCore();
        var result = Invoke(e, r, "getAmountOut",
            BigInteger.Parse("1000000000000000000"),
            BigInteger.Parse("10000000000000000000"),
            BigInteger.Parse("10000000000000000000"));
        var output = result.GetInteger();
        Assert.True(output > 0);
        Assert.True(output < BigInteger.Parse("1000000000000000000"));
    }

    [Fact]
    public void Router_GetReserves_NonExistentPair_Fails()
    {
        var (e, _, r, a, b) = DeployCore();
        var ex = Assert.ThrowsAny<Exception>(() =>
            Invoke(e, r, "getReserves", a, b));
        Assert.Contains("ASSERT", ex.Message);
    }

}
