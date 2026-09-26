using System.Numerics;
using Xunit;
using Neo;
using Neo.SmartContract;
using Neo.SmartContract.Testing;
using Neo.VM;


namespace DuoSwap.Tests;

using static TestUtils;

public class MediumPriorityTests
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
        BigInteger amountA = default)
    {
        if (amountA == default) amountA = 10_000_000_00;
        Invoke(engine, router, "addLiquidity", Owner, tokenA, tokenB,
            amountA, amountA, 0, 0, long.MaxValue);
    }

    #endregion

    // ============================================
    // Router: GetReserves, GetAmountsOut, GetAmountsIn
    // ============================================

    [Fact]
    public void GetReserves_AfterLiquidity_ReturnsCorrectValues()
    {
        var (e, f, r, a, b) = DeployCore();
        var p = DeployPair(e, f, a, b);
        AddLiquidity(e, r, a, b, 10_000_000_00);

        var result = Invoke(e, r, "getReserves", a, b);
        var arr = (Neo.VM.Types.Array)result;
        Assert.Equal(2, arr.Count);
        Assert.True(arr[0].GetInteger() > 0);
        Assert.True(arr[1].GetInteger() > 0);
    }

    [Fact]
    public void GetAmountsOut_SingleHop_ReturnsCorrectValues()
    {
        var (e, f, r, a, b) = DeployCore();
        var p = DeployPair(e, f, a, b);
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
        Assert.True(arr[1].GetInteger() < 1_000_000_00);
    }

    [Fact]
    public void GetAmountsIn_SingleHop_ReturnsCorrectValues()
    {
        var (e, f, r, a, b) = DeployCore();
        var p = DeployPair(e, f, a, b);
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

    // ============================================
    // Pair LP token transfer tests
    // ============================================

    [Fact]
    public void PairTransfer_ZeroAmount_Fails()
    {
        var (e, f, r, a, b) = DeployCore();
        var p = DeployPair(e, f, a, b);
        AddLiquidity(e, r, a, b);

        var ex = Assert.ThrowsAny<Exception>(() =>
            Invoke(e, p, "transfer", Owner, User, BigInteger.Zero, null));
        Assert.Contains("ASSERT", ex.Message);
    }

    [Fact]
    public void PairTransfer_InsufficientBalance_Fails()
    {
        var (e, f, r, a, b) = DeployCore();
        var p = DeployPair(e, f, a, b);
        AddLiquidity(e, r, a, b);

        var ex = Assert.ThrowsAny<Exception>(() =>
            Invoke(e, p, "transfer", User, Owner, 1, null));
        Assert.Contains("ASSERT", ex.Message);
    }

    [Fact]
    public void PairTransfer_BetweenUsers_UpdatesBalances()
    {
        var (e, f, r, a, b) = DeployCore();
        var p = DeployPair(e, f, a, b);
        AddLiquidity(e, r, a, b);

        var lpBalance = Invoke(e, p, "balanceOf", Owner).GetInteger();
        Assert.True(lpBalance > 1000);

        var transferAmount = lpBalance / 2;
        Assert.True(Invoke(e, p, "transfer", Owner, User, transferAmount, null).GetBoolean());

        Assert.Equal(transferAmount, Invoke(e, p, "balanceOf", User).GetInteger());
        Assert.Equal(lpBalance - transferAmount, Invoke(e, p, "balanceOf", Owner).GetInteger());
    }

    // ============================================
    // Factory: GetAllPairsHash with pairs
    // ============================================

    [Fact]
    public void GetAllPairsHash_MultiplePairs_ReturnsAll()
    {
        // Use GAS as one token to get distinct pair symbols (TEST-GAS vs TEST-TEST)
        var (e, f, r, a, b) = DeployCore();
        var p1 = DeployPair(e, f, a, b);

        // Deploy pair (GAS, a) via Factory — different token combination
        var gas = e.Native.GAS.Hash;
        var vl = e.ProtocolSettings.StandbyValidators;
        var m = vl.Count - (vl.Count - 1) / 3;
        var va = Contract.CreateMultiSigRedeemScript(m, vl.ToArray()).ToScriptHash();
        var fee = Invoke(e, f, "getDeployFee").GetInteger();
        e.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.Global, va, Owner);
        Invoke(e, gas, "transfer", va, Owner, fee, null);
        e.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.Global, Owner);

        var d = new ContractParameter(ContractParameterType.Array) { Value = new ContractParameter[] {
            new(ContractParameterType.Hash160) { Value = a },
            new(ContractParameterType.Hash160) { Value = gas },
        }};
        e.Execute(gas.MakeScript("transfer",
            new ContractParameter(ContractParameterType.Hash160) { Value = Owner },
            new ContractParameter(ContractParameterType.Hash160) { Value = f },
            new ContractParameter(ContractParameterType.Integer) { Value = fee }, d));

        var allPairs = Invoke(e, f, "getAllPairsHash");
        var arr = (Neo.VM.Types.Array)allPairs;
        Assert.Equal(2, arr.Count);
    }

   
    

    
}
