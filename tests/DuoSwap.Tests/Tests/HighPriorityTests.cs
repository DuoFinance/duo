using System.Numerics;
using Xunit;
using Neo;
using Neo.SmartContract;
using Neo.SmartContract.Testing;
using Neo.VM;

namespace DuoSwap.Tests;

using static TestUtils;

public class HighPriorityTests
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
        Invoke(engine, factory, "setBonding", Bonding);

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

        var r = Invoke(engine, factory, "getPairHash", tokenA, tokenB);
        return new UInt160(r.GetSpan());
    }

    private static void AddLiquidity(TestEngine engine, UInt160 router, UInt160 tokenA, UInt160 tokenB,
        BigInteger amountA = default)
    {
        if (amountA == default) amountA = 10_000_000_000_00;
        Invoke(engine, router, "addLiquidity", Owner, tokenA, tokenB,
            amountA, amountA, 0, 0, long.MaxValue);
    }

    private static (UInt160 token0, UInt160 token1) GetTokens(TestEngine engine, UInt160 pair)
    {
        var t0 = new UInt160(Invoke(engine, pair, "getToken0").GetSpan());
        var t1 = new UInt160(Invoke(engine, pair, "getToken1").GetSpan());
        return (t0, t1);
    }

    #endregion

    // ============================================
    // 1. Pair.Swap assertion branches
    // ============================================

    [Fact]
    public void PairSwap_LowLiquidity_EmptyPair_Fails()
    {
        var (e, f, _, a, b) = DeployCore();
        var p = DeployPair(e, f, a, b);

        var ex = Assert.ThrowsAny<Exception>(() =>
            Invoke(e, p, "swap", BigInteger.One, BigInteger.Zero, User, new byte[] { }));
        Assert.Contains("ASSERT", ex.Message);
    }

    [Fact]
    public void PairSwap_ZeroInput_NoTokensSentIn_Fails()
    {
        var (e, f, r, a, b) = DeployCore();
        var p = DeployPair(e, f, a, b);
        AddLiquidity(e, r, a, b);

        var (t0, _) = GetTokens(e, p);
        var (out0, out1) = t0 == b ? (BigInteger.Zero, new BigInteger(100_000_00))
                                   : (new BigInteger(100_000_00), BigInteger.Zero);

        var ex = Assert.ThrowsAny<Exception>(() =>
            Invoke(e, p, "swap", out0, out1, User, new byte[] { }));
        Assert.Contains("ASSERT", ex.Message);
    }

    [Fact]
    public void PairSwap_LowLiquidity_OutputExceedsReserve_Fails()
    {
        var (e, f, r, a, b) = DeployCore();
        var p = DeployPair(e, f, a, b);
        AddLiquidity(e, r, a, b);

        // Send lots of tokenA in first so input check passes
        Invoke(e, a, "transfer", Owner, p, 999_999_999_99, null);

        var (t0, _) = GetTokens(e, p);
        var reserve = Invoke(e, p, t0 == a ? "getReserve0" : "getReserve1").GetInteger();

        BigInteger outAmt = reserve; // equal to reserve → not < reserve → LOW_LIQUIDITY
        var (out0, out1) = t0 == b ? (BigInteger.Zero, outAmt) : (outAmt, BigInteger.Zero);

        var s = p.MakeScript("swap",
            new ContractParameter(ContractParameterType.Integer) { Value = out0 },
            new ContractParameter(ContractParameterType.Integer) { Value = out1 },
            new ContractParameter(ContractParameterType.Hash160) { Value = User },
            new ContractParameter(ContractParameterType.ByteArray) { Value = new byte[] { 0x01 } });
        var ex = Assert.ThrowsAny<Exception>(() => e.Execute(s));
        Assert.Contains("ASSERT", ex.Message);
    }

    [Fact]
    public void PairSwap_BadRecipient_TokenAddress_Fails()
    {
        var (e, f, r, a, b) = DeployCore();
        var p = DeployPair(e, f, a, b);
        AddLiquidity(e, r, a, b);
        var (t0, _) = GetTokens(e, p);

        // Send tokens in so input check passes
        Invoke(e, a, "transfer", Owner, p, 100_000_00, null);

        var (out0, out1) = t0 == b ? (BigInteger.Zero, new BigInteger(1)) : (new BigInteger(1), BigInteger.Zero);
        var ex = Assert.ThrowsAny<Exception>(() =>
            Invoke(e, p, "swap", out0, out1, t0, new byte[] { }));
        Assert.Contains("ASSERT", ex.Message);
    }

    // ============================================
    // 2. Pair.Mint end-to-end
    // ============================================

    [Fact]
    public void PairMint_FirstMint_LocksMinimumLiquidity()
    {
        var (e, f, r, a, b) = DeployCore();
        var p = DeployPair(e, f, a, b);
        AddLiquidity(e, r, a, b);

        Assert.Equal(1000, Invoke(e, p, "balanceOf", UInt160.Zero).GetInteger());
        Assert.True(Invoke(e, p, "balanceOf", Owner).GetInteger() > 0);
    }

    // ============================================
    // 3. Pair.Burn end-to-end
    // ============================================

    [Fact]
    public void PairBurn_RemoveLiquidity_ReturnsTokens()
    {
        var (e, f, r, a, b) = DeployCore();
        var p = DeployPair(e, f, a, b);
        AddLiquidity(e, r, a, b);

        var lp = Invoke(e, p, "balanceOf", Owner).GetInteger();
        Assert.True(lp > 1000);

        var ba = Invoke(e, a, "balanceOf", Owner).GetInteger();
        var bb = Invoke(e, b, "balanceOf", Owner).GetInteger();

        Invoke(e, r, "removeLiquidity", Owner, a, b, lp / 2, 0, 0, long.MaxValue);

        Assert.True(Invoke(e, a, "balanceOf", Owner).GetInteger() > ba);
        Assert.True(Invoke(e, b, "balanceOf", Owner).GetInteger() > bb);
        Assert.True(Invoke(e, p, "balanceOf", Owner).GetInteger() < lp);
    }

    // ============================================
    // 4. MintProtocolFee path coverage
    // ============================================

    [Fact]
    public void MintProtocolFee_SwitchingFeeTo_RedirectsFees()
    {
        var (e, f, r, a, b) = DeployCore();
        var p = DeployPair(e, f, a, b);
        AddLiquidity(e, r, a, b);

        var deadline = long.MaxValue;

        var swapScript = r.MakeScript("swapExactTokensForTokens",
            new ContractParameter(ContractParameterType.Hash160) { Value = Owner },
            new ContractParameter(ContractParameterType.Integer) { Value = 1_000_000_00 },
            new ContractParameter(ContractParameterType.Integer) { Value = BigInteger.Zero },
            new ContractParameter(ContractParameterType.Array)
            {
                Value = new ContractParameter[]
                {
                    new(ContractParameterType.Hash160) { Value = a },
                    new(ContractParameterType.Hash160) { Value = b },
                }
            },
            new ContractParameter(ContractParameterType.Integer) { Value = deadline });

        // Enable feeTo to User
        Invoke(e, f, "setFeeTo", User);

        // Swap → AddLiquidity (kLast=0, no fee yet, but kLast set)
        e.Execute(swapScript);
        Invoke(e, r, "addLiquidity", Owner, a, b,
            10_000_000_00, 10_000_000_00, 0, 0, deadline);

        // Swap → AddLiquidity (kLast>0, fee mints to User)
        e.Execute(swapScript);
        Invoke(e, r, "addLiquidity", Owner, a, b,
            10_000_000_00, 10_000_000_00, 0, 0, deadline);

        var userBalance = Invoke(e, p, "balanceOf", User).GetInteger();
        Assert.True(userBalance > 0);

        // Switch feeTo to Treasury
        Invoke(e, f, "setFeeTo", Treasury);

        // Swap → AddLiquidity (kLast>0 but now mints to Treasury)
        e.Execute(swapScript);
        Invoke(e, r, "addLiquidity", Owner, a, b,
            10_000_000_00, 10_000_000_00, 0, 0, deadline);

        var userBalanceAfter = Invoke(e, p, "balanceOf", User).GetInteger();
        var treasuryBalance = Invoke(e, p, "balanceOf", Treasury).GetInteger();
        Assert.Equal(userBalance, userBalanceAfter);
        Assert.True(treasuryBalance > 0);
    }

    // ============================================
    // 5. Router.RemoveLiquidity slippage
    // ============================================

    [Fact]
    public void Router_RemoveLiquidity_SlippageTooHigh_Fails()
    {
        var (e, f, r, a, b) = DeployCore();
        var p = DeployPair(e, f, a, b);
        AddLiquidity(e, r, a, b);

        var lp = Invoke(e, p, "balanceOf", Owner).GetInteger();
        var ex = Assert.ThrowsAny<Exception>(() =>
            Invoke(e, r, "removeLiquidity", Owner, a, b,
                lp, 999_999_999_999_99, 999_999_999_999_99, long.MaxValue));
        Assert.Contains("ASSERT", ex.Message);
    }

    [Fact]
    public void Router_RemoveLiquidity_ExpiredDeadline_Fails()
    {
        var (e, f, r, a, b) = DeployCore();
        var p = DeployPair(e, f, a, b);
        AddLiquidity(e, r, a, b);

        var lp = Invoke(e, p, "balanceOf", Owner).GetInteger();
        var ex = Assert.ThrowsAny<Exception>(() =>
            Invoke(e, r, "removeLiquidity", Owner, a, b, lp, 0, 0, -1));
        Assert.Contains("ASSERT", ex.Message);
    }

    // ============================================
    // 6. Router.SwapTokensForExactTokens reverse swap
    // ============================================

    [Fact]
    public void Router_SwapTokensForExactTokens_Succeeds()
    {
        var (e, f, r, a, b) = DeployCore();
        DeployPair(e, f, a, b);
        AddLiquidity(e, r, a, b);

        var exactOut = 100_000_00;
        var s = r.MakeScript("swapTokensForExactTokens",
            new ContractParameter(ContractParameterType.Hash160) { Value = Owner },
            new ContractParameter(ContractParameterType.Integer) { Value = 999_999_999_99 },
            new ContractParameter(ContractParameterType.Integer) { Value = exactOut },
            new ContractParameter(ContractParameterType.Array) { Value = new ContractParameter[] {
                new(ContractParameterType.Hash160) { Value = a },
                new(ContractParameterType.Hash160) { Value = b },
            }},
            new ContractParameter(ContractParameterType.Integer) { Value = long.MaxValue });
        var result = e.Execute(s);
        Assert.NotNull(result);
    }

    // ============================================
    // 7. DuoToken.transfer boundary cases
    // ============================================

    [Fact]
    public void Token_Transfer_ZeroAmount_ReturnsTrue()
    {
        var (e, _, _, a, _) = DeployCore();
        var r = Invoke(e, a, "transfer", Owner, User, BigInteger.Zero, null);
        Assert.True(r.GetBoolean());
    }

    [Fact]
    public void Token_Transfer_InsufficientBalance_ReturnsFalse()
    {
        var (e, _, _, a, _) = DeployCore();
        var r = Invoke(e, a, "transfer", User, Owner, 1, null);
        Assert.False(r.GetBoolean());
    }

    [Fact]
    public void Token_Transfer_SelfTransfer_ReturnsTrue()
    {
        var (e, _, _, a, _) = DeployCore();
        var r = Invoke(e, a, "transfer", Owner, Owner, 100, null);
        Assert.True(r.GetBoolean());
    }
}
