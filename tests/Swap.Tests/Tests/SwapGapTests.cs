using System.Numerics;
using Xunit;
using Neo;
using Neo.SmartContract;
using Neo.SmartContract.Testing;
using Neo.VM;

namespace DuoSwap.Tests;

using static TestUtils;

public class SwapGapTests
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

        var (tn, tma) = LoadContract("TestToken");
        tma.Name = "TestTokenA";
        var tokenA = DeployContract(engine, tn, tma, null);

        var (_, tmb) = LoadContract("TestToken");
        tmb.Name = "TestTokenB";
        var tokenB = DeployContract(engine, tn, tmb, null);

        Invoke(engine, tokenA, "initialise", Owner);
        Invoke(engine, tokenB, "initialise", Owner);

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

        var d = new ContractParameter(ContractParameterType.Array)
        {
            Value = new ContractParameter[]
            {
                new(ContractParameterType.Hash160) { Value = tokenA },
                new(ContractParameterType.Hash160) { Value = tokenB },
            }
        };
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

    private static ContractParameter MakePath(params UInt160[] tokens)
    {
        var arr = new ContractParameter[tokens.Length];
        for (int i = 0; i < tokens.Length; i++)
            arr[i] = new(ContractParameterType.Hash160) { Value = tokens[i] };
        return new ContractParameter(ContractParameterType.Array) { Value = arr };
    }

    /// <summary>
    /// Deploy pairs (GAS, tokenA) and (tokenA, tokenB) so both pairs have
    /// distinct symbols (TEST-GAS vs TEST-TEST) and therefore distinct hashes.
    /// Funds GAS/tokenA/tokenB liquidity into each pair.
    /// </summary>
    private static (TestEngine engine, UInt160 factory, UInt160 router, UInt160 gas, UInt160 tokenA, UInt160 tokenB, UInt160 pairGA, UInt160 pairAB) SetupMultiHop()
    {
        var (e, f, r, a, b) = DeployCore();
        var gas = e.Native.GAS.Hash;

        var pairGA = DeployPair(e, f, gas, a);
        var pairAB = DeployPair(e, f, a, b);

        // Fund Owner with GAS for the GAS-side of pair(GAS, tokenA)
        var vl = e.ProtocolSettings.StandbyValidators;
        var m = vl.Count - (vl.Count - 1) / 3;
        var va = Contract.CreateMultiSigRedeemScript(m, vl.ToArray()).ToScriptHash();
        BigInteger gasFund = 100_000_000_00;
        e.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.Global, va, Owner);
        Invoke(e, gas, "transfer", va, Owner, gasFund, null);
        e.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.Global, Owner);

        AddLiquidity(e, r, gas, a, 10_000_000_00, 10_000_000_00);
        AddLiquidity(e, r, a, b);

        return (e, f, r, gas, a, b, pairGA, pairAB);
    }

    private static BigInteger[] GetReserves(TestEngine engine, UInt160 pair)
    {
        var reserve0 = Invoke(engine, pair, "getReserve0").GetInteger();
        var reserve1 = Invoke(engine, pair, "getReserve1").GetInteger();
        return new BigInteger[] { reserve0, reserve1 };
    }

    #endregion

    // ============================================
    // GAP 1: Pair TWAP cumulative price update
    // ============================================

    [Fact]
    public void Pair_Twap_PriceCumulative_IncrementsAfterTimePasses()
    {
        var (e, f, r, a, b) = DeployCore();
        var p = DeployPair(e, f, a, b);
        AddLiquidity(e, r, a, b);

        var path = MakePath(a, b);

        // First swap: establishes BlockTimestampLast in WriteReserves
        Invoke(e, r, "swapExactTokensForTokens", Owner, 1_000_000_00, BigInteger.Zero, path, long.MaxValue);
        var c0 = Invoke(e, p, "price0CumulativeLast").GetInteger();
        var c1_0 = Invoke(e, p, "price1CumulativeLast").GetInteger();

        // Swap again within same block: elapsed==0, cumulative must NOT change
        Invoke(e, r, "swapExactTokensForTokens", Owner, 1_000_000_00, BigInteger.Zero, path, long.MaxValue);
        var c1 = Invoke(e, p, "price0CumulativeLast").GetInteger();
        Assert.Equal(c0, c1);

        // Advance time by several blocks
        e.PersistingBlock.Skip(5, TimeSpan.FromSeconds(15));
        e.PersistingBlock.Persist(
            System.Array.Empty<Neo.Network.P2P.Payloads.Transaction>(),
            System.Array.Empty<Neo.VM.VMState>());

        // Swap again: elapsed>0, cumulative must increase
        Invoke(e, r, "swapExactTokensForTokens", Owner, 1_000_000_00, BigInteger.Zero, path, long.MaxValue);
        var c2 = Invoke(e, p, "price0CumulativeLast").GetInteger();
        var c2_1 = Invoke(e, p, "price1CumulativeLast").GetInteger();

        Assert.True(c2 > c1, $"price0CumulativeLast should increase, {c1} -> {c2}");
        Assert.True(c2_1 > c1_0, $"price1CumulativeLast should increase, {c1_0} -> {c2_1}");
    }

    // ============================================
    // GAP 2: Router addLiquidity LOW_AMOUNT_A / LOW_AMOUNT_B
    // ============================================

    [Fact]
    public void Router_AddLiquidity_LowAmountB_Fails()
    {
        var (e, f, r, a, b) = DeployCore();
        DeployPair(e, f, a, b);
        AddLiquidity(e, r, a, b); // reserves 1:1

        // amountB = quote(desiredA) <= desiredB, but minB too high
        var ex = Assert.ThrowsAny<Exception>(() =>
            Invoke(e, r, "addLiquidity", Owner, a, b,
                1_000_000_00, 1_000_000_00, 0, 999_999_999_999_99, long.MaxValue));
        Assert.Contains("ASSERT", ex.Message);
    }

    [Fact]
    public void Router_AddLiquidity_LowAmountA_Fails()
    {
        var (e, f, r, a, b) = DeployCore();
        DeployPair(e, f, a, b);
        AddLiquidity(e, r, a, b); // reserves 1:1

        // desiredA:desiredB = 2:1 > reserve ratio 1:1 -> amountA branch, minA too high
        var ex = Assert.ThrowsAny<Exception>(() =>
            Invoke(e, r, "addLiquidity", Owner, a, b,
                2_000_000_00, 1_000_000_00, 999_999_999_999_99, 0, long.MaxValue));
        Assert.Contains("ASSERT", ex.Message);
    }

    // ============================================
    // GAP 3: C# multi-hop swap + getAmountsOut/In
    // ============================================

    [Fact]
    public void Router_GetAmountsOut_ThreeHopPath_ReturnsCorrectLength()
    {
        var (e, f, r, gas, a, b, pairGA, pairAB) = SetupMultiHop();

        var result = Invoke(e, r, "getAmountsOut", 1_000_000_00, MakePath(gas, a, b));
        var arr = result as Neo.VM.Types.Array;
        Assert.NotNull(arr);
        Assert.Equal(3, arr.Count);
        Assert.Equal(1_000_000_00, arr[0].GetInteger());
        Assert.True(arr[1].GetInteger() > 0);
        Assert.True(arr[2].GetInteger() > 0);
        Assert.True(arr[1].GetInteger() > arr[2].GetInteger());
    }

    [Fact]
    public void Router_GetAmountsIn_ThreeHopPath_ReturnsCorrectLength()
    {
        var (e, f, r, gas, a, b, pairGA, pairAB) = SetupMultiHop();

        var result = Invoke(e, r, "getAmountsIn", 1_000_000_00, MakePath(gas, a, b));
        var arr = result as Neo.VM.Types.Array;
        Assert.NotNull(arr);
        Assert.Equal(3, arr.Count);
        Assert.True(arr[0].GetInteger() > 0);
        Assert.True(arr[1].GetInteger() > 0);
        Assert.Equal(1_000_000_00, arr[2].GetInteger());
        Assert.True(arr[0].GetInteger() > arr[1].GetInteger());
    }

    [Fact]
    public void Router_SwapExactTokensForTokens_ThreeHop_Succeeds()
    {
        var (e, f, r, gas, a, b, pairGA, pairAB) = SetupMultiHop();

        var balBBefore = Invoke(e, b, "balanceOf", Owner).GetInteger();
        var balGasBefore = Invoke(e, gas, "balanceOf", Owner).GetInteger();

        var result = Invoke(e, r, "swapExactTokensForTokens",
            Owner, 1_000_000_00, BigInteger.Zero, MakePath(gas, a, b), long.MaxValue);
        Assert.True(result.GetBoolean());

        var balBAfter = Invoke(e, b, "balanceOf", Owner).GetInteger();
        Assert.True(balBAfter > balBBefore);
        Assert.True(Invoke(e, gas, "balanceOf", Owner).GetInteger() < balGasBefore);
    }

    // ============================================
    // GAP 4: Router NO_WITNESS / BAD_SENDER
    // ============================================

    [Fact]
    public void Router_AddLiquidity_NoWitness_Fails()
    {
        var (e, f, r, a, b) = DeployCore();
        DeployPair(e, f, a, b);

        // Sender is User but signer is Owner -> NO_WITNESS
        var ex = Assert.ThrowsAny<Exception>(() =>
            Invoke(e, r, "addLiquidity", User, a, b,
                1_000_000_00, 1_000_000_00, 0, 0, long.MaxValue));
        Assert.Contains("ASSERT", ex.Message);
    }

    [Fact]
    public void Router_AddLiquidity_ZeroSender_Fails()
    {
        var (e, f, r, a, b) = DeployCore();
        DeployPair(e, f, a, b);

        var ex = Assert.ThrowsAny<Exception>(() =>
            Invoke(e, r, "addLiquidity", UInt160.Zero, a, b,
                1_000_000_00, 1_000_000_00, 0, 0, long.MaxValue));
        Assert.Contains("ASSERT", ex.Message);
    }

    [Fact]
    public void Router_SwapExactTokensForTokens_NoWitness_Fails()
    {
        var (e, f, r, a, b) = DeployCore();
        DeployPair(e, f, a, b);
        AddLiquidity(e, r, a, b);

        var ex = Assert.ThrowsAny<Exception>(() =>
            Invoke(e, r, "swapExactTokensForTokens",
                User, 1_000_000_00, BigInteger.Zero, MakePath(a, b), long.MaxValue));
        Assert.Contains("ASSERT", ex.Message);
    }

    // ============================================
    // GAP 5: Factory keeps the deploy fee (controlled by setter via Withdraw),
    //        no longer auto-forwarded to feeTo.
    // ============================================

    [Fact]
    public void Factory_DeployFee_StaysInFactory_SetterWithdraws()
    {
        var engine = CreateEngine();
        engine.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.Global, Owner);

        var (fn, fm) = LoadContract("DuoSwapFactory");
        var factory = DeployContract(engine, fn, fm, Owner);

        var (rn, rm) = LoadContract("DuoSwapRouter");
        var router = DeployContract(engine, rn, rm, new object[] { factory });
        Invoke(engine, factory, "setRouter", router);
        Invoke(engine, factory, "setBonding", Bonding);

        var (tn, tma) = LoadContract("TestToken");
        tma.Name = "TestTokenA";
        var tokenA = DeployContract(engine, tn, tma, null);
        var (_, tmb) = LoadContract("TestToken");
        tmb.Name = "TestTokenB";
        var tokenB = DeployContract(engine, tn, tmb, null);
        Invoke(engine, tokenA, "initialise", Owner);
        Invoke(engine, tokenB, "initialise", Owner);

        // Fund Owner with GAS from validators
        var gas = engine.Native.GAS.Hash;
        var vl = engine.ProtocolSettings.StandbyValidators;
        var m = vl.Count - (vl.Count - 1) / 3;
        var va = Contract.CreateMultiSigRedeemScript(m, vl.ToArray()).ToScriptHash();
        var fee = Invoke(engine, factory, "getDeployFee").GetInteger();
        engine.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.Global, va, Owner);
        Invoke(engine, gas, "transfer", va, Owner, fee, null);
        engine.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.Global, Owner);

        // Set feeTo BEFORE deploy -> deployFee must NOT be auto-forwarded
        Invoke(engine, factory, "setFeeTo", User);

        var userGasBefore = Invoke(engine, gas, "balanceOf", User).GetInteger();
        var factoryGasBefore = Invoke(engine, gas, "balanceOf", factory).GetInteger();

        var data = new ContractParameter(ContractParameterType.Array)
        {
            Value = new ContractParameter[]
            {
                new(ContractParameterType.Hash160) { Value = tokenA },
                new(ContractParameterType.Hash160) { Value = tokenB },
            }
        };
        engine.Execute(gas.MakeScript("transfer",
            new ContractParameter(ContractParameterType.Hash160) { Value = Owner },
            new ContractParameter(ContractParameterType.Hash160) { Value = factory },
            new ContractParameter(ContractParameterType.Integer) { Value = fee }, data));

        var userGasAfter = Invoke(engine, gas, "balanceOf", User).GetInteger();
        var factoryGasAfter = Invoke(engine, gas, "balanceOf", factory).GetInteger();

        // feeTo received nothing; the deploy fee stays in the factory.
        Assert.Equal(0, userGasAfter - userGasBefore);
        Assert.Equal(fee, factoryGasAfter - factoryGasBefore);

        // Pair should still be deployed and functional
        var pair = new UInt160(Invoke(engine, factory, "getPairHash", tokenA, tokenB).GetSpan());
        AddLiquidity(engine, router, tokenA, tokenB);
        Assert.True(Invoke(engine, pair, "getReserve0").GetInteger() > 0);

        // Setter can claim and transfer the deploy fee.
        var ownerBefore = Invoke(engine, gas, "balanceOf", Owner).GetInteger();
        Invoke(engine, factory, "claimGAS", UInt160.Zero, Owner);
        Assert.Equal(fee, Invoke(engine, gas, "balanceOf", Owner).GetInteger() - ownerBefore);
        Assert.Equal(0, Invoke(engine, gas, "balanceOf", factory).GetInteger());
    }
}
