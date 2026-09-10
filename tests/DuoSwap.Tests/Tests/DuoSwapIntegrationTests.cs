using System.Numerics;
using Xunit;
using Neo;
using Neo.SmartContract;
using Neo.SmartContract.Testing;
using Neo.VM;

namespace DuoSwap.Tests;

using static TestUtils;

public class DuoSwapIntegrationTests
{
    private static (TestEngine engine, UInt160 factory, UInt160 router, UInt160 tokenA, UInt160 tokenB, UInt160 pair) Setup()
    {
        var (engine, factory, router, tokenA, tokenB) = DeployCore();
        var pair = DeployPair(engine, factory, tokenA, tokenB);
        return (engine, factory, router, tokenA, tokenB, pair);
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
        Invoke(engine, factory, "setBonding", Bonding);

        var (tn, tma) = LoadContract("DuoToken");
        tma.Name = "DuoTokenA";
        var tokenA = DeployContract(engine, tn, tma, null);

        var (_, tmb) = LoadContract("DuoToken");
        tmb.Name = "DuoTokenB";
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
        BigInteger amountA = default)
    {
        if (amountA == default) amountA = 10_000_000_00;
        Invoke(engine, router, "addLiquidity", Owner, tokenA, tokenB,
            amountA, amountA, 0, 0, long.MaxValue);
    }

    [Fact]
    public void Integration_FullCycle_AddLiquidity_Swap_RemoveLiquidity()
    {
        var (engine, factory, router, tokenA, tokenB, pair) = Setup();

        BigInteger amountADesired = 10_000_000_00;
        BigInteger amountBDesired = 10_000_000_00;

        engine.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.Global, Owner);
        var result = Invoke(engine, router, "addLiquidity",
            Owner, tokenA, tokenB, amountADesired, amountBDesired, 0, 0, long.MaxValue);
        var addLiq = result as Neo.VM.Types.Array;

        BigInteger liquidity = addLiq[2].GetInteger();
        Assert.True(liquidity > 0);

        var reservesBefore = GetReserves(engine, pair);
        Assert.Equal(amountADesired, reservesBefore[0]);
        Assert.Equal(amountBDesired, reservesBefore[1]);
        var token0 = new UInt160(Invoke(engine, pair, "getToken0").GetSpan());
        var tokenAReserveIndex = token0 == tokenA ? 0 : 1;
        var tokenBReserveIndex = tokenAReserveIndex == 0 ? 1 : 0;

        BigInteger swapAmount = 1_000_000_00;
        engine.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.Global, Owner);
        Invoke(engine, tokenA, "transfer", Owner, User, swapAmount, null);

        engine.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.Global, User);
        var path = new ContractParameter(ContractParameterType.Array)
        {
            Value = new ContractParameter[]
            {
                new(ContractParameterType.Hash160) { Value = tokenA },
                new(ContractParameterType.Hash160) { Value = tokenB },
            }
        };
        Invoke(engine, router, "swapExactTokensForTokens",
            User, swapAmount, BigInteger.Zero, path, long.MaxValue);

        var reservesAfterSwap = GetReserves(engine, pair);
        Assert.True(reservesAfterSwap[tokenAReserveIndex] > reservesBefore[tokenAReserveIndex]);
        Assert.True(reservesAfterSwap[tokenBReserveIndex] < reservesBefore[tokenBReserveIndex]);

        engine.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.Global, Owner);
        Invoke(engine, router, "removeLiquidity",
            Owner, tokenA, tokenB, liquidity, 0, 0, long.MaxValue);

        var finalReserves = GetReserves(engine, pair);
        Assert.True(finalReserves[0] < reservesAfterSwap[0]);
        Assert.True(finalReserves[1] < reservesAfterSwap[1]);
    }

    [Fact]
    public void Integration_Swap_ExactTokensForTokens()
    {
        var (engine, factory, router, tokenA, tokenB, pair) = Setup();

        BigInteger amountADesired = 10_000_000_00;
        BigInteger amountBDesired = 10_000_000_00;

        engine.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.Global, Owner);
        Invoke(engine, router, "addLiquidity",
            Owner, tokenA, tokenB, amountADesired, amountBDesired, 0, 0, long.MaxValue);

        BigInteger amountIn = 1_000_000_00;
        engine.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.Global, Owner);
        Invoke(engine, tokenA, "transfer", Owner, User, amountIn, null);

        var path = new ContractParameter(ContractParameterType.Array)
        {
            Value = new ContractParameter[]
            {
                new(ContractParameterType.Hash160) { Value = tokenA },
                new(ContractParameterType.Hash160) { Value = tokenB },
            }
        };
        var amountsOut = Invoke(engine, router, "getAmountsOut", amountIn, path);
        var getArr = amountsOut as Neo.VM.Types.Array;
        BigInteger expectedOut = getArr[1].GetInteger();

        engine.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.Global, User);
        Invoke(engine, router, "swapExactTokensForTokens",
            User, amountIn, expectedOut, path, long.MaxValue);
    }

    [Fact]
    public void Integration_AddLiquidity_RemoveLiquidity_RoundTrip()
    {
        var (engine, factory, router, tokenA, tokenB, pair) = Setup();

        BigInteger amountADesired = 10_000_000_00;
        BigInteger amountBDesired = 10_000_000_00;

        engine.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.Global, Owner);
        var result = Invoke(engine, router, "addLiquidity",
            Owner, tokenA, tokenB, amountADesired, amountBDesired, 0, 0, long.MaxValue);
        var addLiq = result as Neo.VM.Types.Array;

        BigInteger liquidity = addLiq[2].GetInteger();
        Assert.True(liquidity > 0);

        var lpBalance = Invoke(engine, pair, "balanceOf", Owner).GetInteger();
        Assert.Equal(liquidity, lpBalance);

        engine.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.Global, Owner);
        var removeResult = Invoke(engine, router, "removeLiquidity",
            Owner, tokenA, tokenB, liquidity, 0, 0, long.MaxValue);
        var removeArr = removeResult as Neo.VM.Types.Array;

        Assert.True(removeArr[0].GetInteger() > 0);
        Assert.True(removeArr[1].GetInteger() > 0);

        var finalLpBalance = Invoke(engine, pair, "balanceOf", Owner).GetInteger();
        Assert.Equal(BigInteger.Zero, finalLpBalance);
    }

    [Fact]
    public void Integration_MultipleUsers_AddLiquidity()
    {
        var (engine, factory, router, tokenA, tokenB, pair) = Setup();

        BigInteger transferAmount = 15_000_000_00;
        engine.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.Global, Owner);
        Invoke(engine, tokenA, "transfer", Owner, User, transferAmount, null);
        Invoke(engine, tokenB, "transfer", Owner, User, transferAmount, null);

        BigInteger ownerAmount = 10_000_000_00;
        engine.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.Global, Owner);
        var ownerResult = Invoke(engine, router, "addLiquidity",
            Owner, tokenA, tokenB, ownerAmount, ownerAmount, 0, 0, long.MaxValue);
        var ownerLiq = ownerResult as Neo.VM.Types.Array;
        BigInteger ownerLp = ownerLiq[2].GetInteger();

        BigInteger userAmount = 5_000_000_00;
        engine.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.Global, User);
        var userResult = Invoke(engine, router, "addLiquidity",
            User, tokenA, tokenB, userAmount, userAmount, 0, 0, long.MaxValue);
        var userLiq = userResult as Neo.VM.Types.Array;
        BigInteger userLp = userLiq[2].GetInteger();

        var totalSupply = Invoke(engine, pair, "totalSupply").GetInteger();
        BigInteger minimumLiquidity = 1000;
        Assert.Equal(ownerLp + userLp + minimumLiquidity, totalSupply);

        var reserves = GetReserves(engine, pair);
        Assert.Equal(ownerAmount + userAmount, reserves[0]);
        Assert.Equal(ownerAmount + userAmount, reserves[1]);
    }

    [Fact]
    public void Integration_Swap_MultipleTrades()
    {
        var (engine, factory, router, tokenA, tokenB, pair) = Setup();

        BigInteger initialAmount = 10_000_000_00;

        engine.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.Global, Owner);
        Invoke(engine, router, "addLiquidity",
            Owner, tokenA, tokenB, initialAmount, initialAmount, 0, 0, long.MaxValue);

        BigInteger totalSwapAmount = 50000 + 60000 + 70000 + 80000 + 90000;
        engine.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.Global, Owner);
        Invoke(engine, tokenA, "transfer", Owner, User, totalSwapAmount, null);

        var initialReserves = GetReserves(engine, pair);
        BigInteger kInitial = initialReserves[0] * initialReserves[1];

        for (int i = 0; i < 5; i++)
        {
            BigInteger swapAmount = 50000 + i * 10000;
            var path = new ContractParameter(ContractParameterType.Array)
            {
                Value = new ContractParameter[]
                {
                    new(ContractParameterType.Hash160) { Value = tokenA },
                    new(ContractParameterType.Hash160) { Value = tokenB },
                }
            };
            engine.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.Global, User);
            Invoke(engine, router, "swapExactTokensForTokens",
                User, swapAmount, BigInteger.Zero, path, long.MaxValue);
        }

        var finalReserves = GetReserves(engine, pair);
        BigInteger kFinal = finalReserves[0] * finalReserves[1];

        BigInteger tolerance = kInitial / 100;
        Assert.True(BigInteger.Abs(kFinal - kInitial) <= tolerance,
            $"k changed too much: {kInitial} -> {kFinal}");
    }

    private static BigInteger[] GetReserves(TestEngine engine, UInt160 pairHash)
    {
        var reserve0 = Invoke(engine, pairHash, "getReserve0").GetInteger();
        var reserve1 = Invoke(engine, pairHash, "getReserve1").GetInteger();
        return new BigInteger[] { reserve0, reserve1 };
    }
}
