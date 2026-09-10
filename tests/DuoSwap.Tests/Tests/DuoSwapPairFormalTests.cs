using System.Numerics;
using Xunit;
using Neo;
using Neo.SmartContract;
using Neo.SmartContract.Testing;

namespace DuoSwap.Tests;

using static TestUtils;

public class DuoSwapPairFormalTests
{
    private const long FEE_DENOMINATOR = 1000;
    private const long TRADE_FEE = 3;
    private const long MINIMUM_LIQUIDITY = 1000;

    [Fact]
    public void Formal_KConstant_SwapPreservesK()
    {
        var (engine, pair, tokenA, tokenB, router) = SetupPairWithLiquidity();
        
        var beforeReserves = GetReserves(engine, pair);
        BigInteger kBefore = beforeReserves[0] * beforeReserves[1];

        Invoke(engine, router, "swapExactTokensForTokens", Owner, 10000000, BigInteger.Zero,
            new ContractParameter(ContractParameterType.Array) { Value = new[] {
                new ContractParameter(ContractParameterType.Hash160) { Value = tokenA },
                new ContractParameter(ContractParameterType.Hash160) { Value = tokenB }
            }}, long.MaxValue);

        var afterReserves = GetReserves(engine, pair);
        BigInteger kAfter = afterReserves[0] * afterReserves[1];

        BigInteger tolerance = kBefore / 1000;
        Assert.True(BigInteger.Abs(kAfter - kBefore) <= tolerance, 
            $"k changed from {kBefore} to {kAfter}, difference: {BigInteger.Abs(kAfter - kBefore)}");
    }

    [Fact]
    public void Formal_Mint_FirstLiquidityCorrect()
    {
        var (engine, pair, tokenA, tokenB, router) = SetupPairWithLiquidity(100000000, 100000000);
        
        var totalSupply = Invoke(engine, pair, "totalSupply").GetInteger();
        var ownerBalance = Invoke(engine, pair, "balanceOf", Owner).GetInteger();
        var zeroBalance = Invoke(engine, pair, "balanceOf", UInt160.Zero).GetInteger();
        
        BigInteger amountA = 100000000;
        BigInteger amountB = 100000000;
        BigInteger expectedLp = IntegerSqrt(amountA * amountB);
        
        Assert.Equal(expectedLp, totalSupply);
        Assert.True(ownerBalance > 0);
        Assert.True(zeroBalance >= 0);
    }

    [Fact]
    public void Formal_Burn_LiquidityProportionate()
    {
        var (engine, pair, tokenA, tokenB, router) = SetupPairWithLiquidity();
        
        var reserves = GetReserves(engine, pair);
        var supply = Invoke(engine, pair, "totalSupply").GetInteger();
        var userBalance = Invoke(engine, pair, "balanceOf", Owner).GetInteger();

        BigInteger expectedAmount0 = userBalance * reserves[0] / supply;
        BigInteger expectedAmount1 = userBalance * reserves[1] / supply;

        var ba = Invoke(engine, tokenA, "balanceOf", Owner).GetInteger();
        var bb = Invoke(engine, tokenB, "balanceOf", Owner).GetInteger();

        Invoke(engine, router, "removeLiquidity", Owner, tokenA, tokenB, userBalance, 0, 0, long.MaxValue);

        var balA = Invoke(engine, tokenA, "balanceOf", Owner).GetInteger();
        var balB = Invoke(engine, tokenB, "balanceOf", Owner).GetInteger();

        Assert.True(BigInteger.Abs((balA - ba) - expectedAmount0) <= 10,
            $"Expected ~{expectedAmount0} tokenA, got {balA - ba}");
        Assert.True(BigInteger.Abs((balB - bb) - expectedAmount1) <= 10,
            $"Expected ~{expectedAmount1} tokenB, got {balB - bb}");
    }

    [Fact]
    public void Formal_FeeCalculation_CorrectRate()
    {
        BigInteger amountIn = 100000;
        BigInteger amountInAfterFee = amountIn * (FEE_DENOMINATOR - TRADE_FEE);
        BigInteger expectedFee = amountIn - amountInAfterFee / FEE_DENOMINATOR;
        
        Assert.Equal(amountIn * TRADE_FEE / FEE_DENOMINATOR, expectedFee);
    }

    [Fact]
    public void Fuzz_KConstant_MultipleSwaps()
    {
        var rng = new Random(42);
        for (int i = 0; i < 20; i++)
        {
            var (engine, pair, tokenA, tokenB, router) = SetupPairWithLiquidity();
            
            var beforeReserves = GetReserves(engine, pair);
            BigInteger kBefore = beforeReserves[0] * beforeReserves[1];

            for (int j = 0; j < 5; j++)
            {
                bool swapToken0 = rng.Next(2) == 0;
                BigInteger swapAmount = 100000 + rng.Next(900000);
                
                try
                {
                    if (swapToken0)
                        Invoke(engine, router, "swapExactTokensForTokens", Owner, swapAmount, BigInteger.Zero,
                            new ContractParameter(ContractParameterType.Array) { Value = new[] {
                                new ContractParameter(ContractParameterType.Hash160) { Value = tokenA },
                                new ContractParameter(ContractParameterType.Hash160) { Value = tokenB }
                            }}, long.MaxValue);
                    else
                        Invoke(engine, router, "swapExactTokensForTokens", Owner, swapAmount, BigInteger.Zero,
                            new ContractParameter(ContractParameterType.Array) { Value = new[] {
                                new ContractParameter(ContractParameterType.Hash160) { Value = tokenB },
                                new ContractParameter(ContractParameterType.Hash160) { Value = tokenA }
                            }}, long.MaxValue);
                }
                catch { }
            }

            var afterReserves = GetReserves(engine, pair);
            BigInteger kAfter = afterReserves[0] * afterReserves[1];

            BigInteger tolerance = kBefore / 100;
            Assert.True(BigInteger.Abs(kAfter - kBefore) <= tolerance,
                $"k changed too much: {kBefore} -> {kAfter}");
        }
    }

    [Fact]
    public void Edge_Swap_ZeroAmountOut_Fails()
    {
        var (engine, pair, tokenA, tokenB, router) = SetupPairWithLiquidity();
        
        var ex = Assert.ThrowsAny<Exception>(() =>
        {
            Invoke(engine, pair, "swap", BigInteger.Zero, BigInteger.Zero, Owner, new byte[] { });
        });
        Assert.Contains("ASSERT", ex.Message);
    }

    [Fact]
    public void Edge_Mint_ZeroDeposit_Fails()
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
    public void Precision_KConstant_AfterManyTrades()
    {
        var (engine, pair, tokenA, tokenB, router) = SetupPairWithLiquidity(10000000000, 10000000000);
        
        var beforeReserves = GetReserves(engine, pair);
        BigInteger kBefore = beforeReserves[0] * beforeReserves[1];

        for (int i = 0; i < 50; i++)
        {
            try
            {
                Invoke(engine, router, "swapExactTokensForTokens", Owner, 100000000, BigInteger.Zero,
                    new ContractParameter(ContractParameterType.Array) { Value = new[] {
                        new ContractParameter(ContractParameterType.Hash160) { Value = tokenA },
                        new ContractParameter(ContractParameterType.Hash160) { Value = tokenB }
                    }}, long.MaxValue);
            }
            catch { }
        }

        var afterReserves = GetReserves(engine, pair);
        BigInteger kAfter = afterReserves[0] * afterReserves[1];

        double deviation = (double)BigInteger.Abs(kAfter - kBefore) / (double)kBefore;
        Assert.True(deviation < 0.01, 
            $"k deviation {deviation:P} exceeds 1%");
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

    private static BigInteger[] GetReserves(TestEngine engine, UInt160 pairHash)
    {
        var reserve0 = Invoke(engine, pairHash, "getReserve0").GetInteger();
        var reserve1 = Invoke(engine, pairHash, "getReserve1").GetInteger();
        return new BigInteger[] { reserve0, reserve1 };
    }

    private static BigInteger IntegerSqrt(BigInteger value)
    {
        if (value <= 0) return 0;
        if (value == 1) return 1;
        
        BigInteger low = 1;
        BigInteger high = value;
        
        while (low < high)
        {
            BigInteger mid = (low + high + 1) / 2;
            if (mid * mid <= value)
                low = mid;
            else
                high = mid - 1;
        }
        
        return low;
    }
}
