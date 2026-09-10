using System.Numerics;
using Xunit;
using Neo;
using Neo.SmartContract;
using Neo.SmartContract.Testing;

namespace DuoSwap.Tests;

using static TestUtils;

public class DuoSwapRouterTests
{
    private const long FEE_NUMERATOR = 997;
    private const long FEE_DENOMINATOR = 1000;

    [Fact]
    public void Router_Quote_CalculationCorrect()
    {
        var (engine, factory, router, tokenA, tokenB) = DeployCore();
        
        BigInteger amountA = 100000;
        BigInteger reserveA = 1000000;
        BigInteger reserveB = 2000000;
        
        var result = Invoke(engine, router, "quote", amountA, reserveA, reserveB);
        
        BigInteger expected = amountA * reserveB / reserveA;
        Assert.Equal(expected, result.GetInteger());
    }

    [Fact]
    public void Router_Quote_ZeroAmount_Fails()
    {
        var (engine, factory, router, tokenA, tokenB) = DeployCore();

        var ex = Assert.ThrowsAny<Exception>(() =>
        {
            Invoke(engine, router, "quote", BigInteger.Zero, 1000000, 1000000);
        });
        Assert.Contains("ASSERT", ex.Message);
    }

    [Fact]
    public void Router_GetAmountOut_CalculationCorrect()
    {
        var (engine, factory, router, tokenA, tokenB) = DeployCore();

        BigInteger amountIn = 100000;
        BigInteger reserveIn = 1000000;
        BigInteger reserveOut = 1000000;
        
        var result = Invoke(engine, router, "getAmountOut", amountIn, reserveIn, reserveOut);
        
        BigInteger amountInAfterFee = amountIn * FEE_NUMERATOR;
        BigInteger expected = amountInAfterFee * reserveOut / (reserveIn * FEE_DENOMINATOR + amountInAfterFee);
        
        Assert.Equal(expected, result.GetInteger());
    }

    [Fact]
    public void Router_GetAmountIn_CalculationCorrect()
    {
        var (engine, factory, router, tokenA, tokenB) = DeployCore();

        BigInteger amountOut = 100000;
        BigInteger reserveIn = 1000000;
        BigInteger reserveOut = 2000000;
        
        var result = Invoke(engine, router, "getAmountIn", amountOut, reserveIn, reserveOut);
        
        BigInteger numerator = reserveIn * amountOut * FEE_DENOMINATOR;
        BigInteger denominator = (reserveOut - amountOut) * FEE_NUMERATOR;
        BigInteger expected = numerator / denominator + 1;
        
        Assert.Equal(expected, result.GetInteger());
    }

    [Fact]
    public void Router_GetAmountOut_ZeroAmountIn_Fails()
    {
        var (engine, factory, router, tokenA, tokenB) = DeployCore();

        var ex = Assert.ThrowsAny<Exception>(() =>
        {
            Invoke(engine, router, "getAmountOut", BigInteger.Zero, 1000000, 1000000);
        });
        Assert.Contains("ASSERT", ex.Message);
    }

    [Fact]
    public void Router_GetAmountIn_ZeroAmountOut_Fails()
    {
        var (engine, factory, router, tokenA, tokenB) = DeployCore();

        var ex = Assert.ThrowsAny<Exception>(() =>
        {
            Invoke(engine, router, "getAmountIn", BigInteger.Zero, 1000000, 1000000);
        });
        Assert.Contains("ASSERT", ex.Message);
    }

    [Fact]
    public void Router_GetAmountIn_AmountOutExceedsReserve_Fails()
    {
        var (engine, factory, router, tokenA, tokenB) = DeployCore();

        var ex = Assert.ThrowsAny<Exception>(() =>
        {
            Invoke(engine, router, "getAmountIn", 2000000, 1000000, 1000000);
        });
        Assert.Contains("ASSERT", ex.Message);
    }

    [Fact]
    public void Router_GetAmountIn_GetAmountOut_Consistency()
    {
        var (engine, factory, router, tokenA, tokenB) = DeployCore();

        BigInteger reserveIn = 1000000;
        BigInteger reserveOut = 1000000;
        BigInteger amountIn = 100000;
        
        var amountOut = Invoke(engine, router, "getAmountOut", amountIn, reserveIn, reserveOut).GetInteger();
        var amountInBack = Invoke(engine, router, "getAmountIn", amountOut, reserveIn, reserveOut).GetInteger();
        
        Assert.True(amountInBack >= amountIn && amountInBack <= amountIn + 10,
            $"amountIn={amountIn}, amountInBack={amountInBack}");
    }

    [Fact]
    public void Router_GetReserves_ReturnsCorrectValues()
    {
        var (engine, pair, tokenA, tokenB, router) = SetupPairWithLiquidity();
        
        var reserves = Invoke(engine, router, "getReserves", tokenA, tokenB);
        var arr = reserves as Neo.VM.Types.Array;
        
        Assert.NotNull(arr);
        Assert.Equal(2, arr.Count);
        Assert.True(arr[0].GetInteger() > 0);
        Assert.True(arr[1].GetInteger() > 0);
    }

    [Fact]
    public void Router_GetReserves_TokenOrderIndependent()
    {
        var (engine, pair, tokenA, tokenB, router) = SetupPairWithLiquidity();
        
        var reservesAB = Invoke(engine, router, "getReserves", tokenA, tokenB);
        var reservesBA = Invoke(engine, router, "getReserves", tokenB, tokenA);
        
        var arrAB = reservesAB as Neo.VM.Types.Array;
        var arrBA = reservesBA as Neo.VM.Types.Array;
        
        Assert.NotNull(arrAB);
        Assert.NotNull(arrBA);
        Assert.Equal(arrAB[0].GetInteger(), arrBA[1].GetInteger());
        Assert.Equal(arrAB[1].GetInteger(), arrBA[0].GetInteger());
    }

    [Fact]
    public void Router_GetAmountsOut_SinglePair()
    {
        var (engine, pair, tokenA, tokenB, router) = SetupPairWithLiquidity();
        
        var path = new ContractParameter(ContractParameterType.Array) { Value = new[] {
            new ContractParameter(ContractParameterType.Hash160) { Value = tokenA },
            new ContractParameter(ContractParameterType.Hash160) { Value = tokenB }
        }};
        var amounts = Invoke(engine, router, "getAmountsOut", 10000000, path);
        var arr = amounts as Neo.VM.Types.Array;
        
        Assert.NotNull(arr);
        Assert.Equal(2, arr.Count);
        Assert.Equal(10000000, arr[0].GetInteger());
        Assert.True(arr[1].GetInteger() > 0);
    }

    [Fact]
    public void Router_GetAmountsIn_SinglePair()
    {
        var (engine, pair, tokenA, tokenB, router) = SetupPairWithLiquidity();
        
        var path = new ContractParameter(ContractParameterType.Array) { Value = new[] {
            new ContractParameter(ContractParameterType.Hash160) { Value = tokenA },
            new ContractParameter(ContractParameterType.Hash160) { Value = tokenB }
        }};
        var amounts = Invoke(engine, router, "getAmountsIn", 10000000, path);
        var arr = amounts as Neo.VM.Types.Array;
        
        Assert.NotNull(arr);
        Assert.Equal(2, arr.Count);
        Assert.True(arr[0].GetInteger() > 0);
        Assert.Equal(10000000, arr[1].GetInteger());
    }

    [Fact]
    public void Router_GetAmountsOut_InsufficientPath_Fails()
    {
        var (engine, factory, router, tokenA, tokenB) = DeployCore();
        
        var path = new ContractParameter(ContractParameterType.Array) { Value = new[] {
            new ContractParameter(ContractParameterType.Hash160) { Value = tokenA }
        }};
        var ex = Assert.ThrowsAny<Exception>(() =>
        {
            Invoke(engine, router, "getAmountsOut", 10000000, path);
        });
        Assert.Contains("ASSERT", ex.Message);
    }

    [Fact]
    public void Router_GetAmountsIn_InsufficientPath_Fails()
    {
        var (engine, factory, router, tokenA, tokenB) = DeployCore();
        
        var path = new ContractParameter(ContractParameterType.Array) { Value = new[] {
            new ContractParameter(ContractParameterType.Hash160) { Value = tokenA }
        }};
        var ex = Assert.ThrowsAny<Exception>(() =>
        {
            Invoke(engine, router, "getAmountsIn", 10000000, path);
        });
        Assert.Contains("ASSERT", ex.Message);
    }

    [Fact]
    public void Router_GetFactory_ReturnsFactory()
    {
        var (engine, factory, router, tokenA, tokenB) = DeployCore();

        var result = Invoke(engine, router, "getFactory");
        var actualFactory = new UInt160(result.GetSpan());
        Assert.Equal(factory, actualFactory);
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
}
