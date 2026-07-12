using System.Numerics;
using Xunit;
using Neo;
using Neo.SmartContract.Testing;
using Neo.VM.Types;

namespace DuoSwap.Tests;

using static TestUtils;

public class RouterTests
{
    private readonly TestEngine _engine;
    private readonly UInt160 _routerHash;

    public RouterTests()
    {
        _engine = CreateEngine();

        var (factoryNef, factoryManifest) = LoadContract("DuoSwapFactory");
        var factoryHash = DeployContract(_engine, factoryNef, factoryManifest, Owner);

        var (routerNef, routerManifest) = LoadContract("DuoSwapRouter");
        _routerHash = DeployContract(_engine, routerNef, routerManifest, new object[] { factoryHash });
    }

    [Fact]
    public void Quote_SymmetricReserves_ReturnsCorrectRatio()
    {
        var result = Invoke(_engine, _routerHash, "quote", 100, 1000, 2000);
        var output = result.GetInteger();
        Assert.Equal(200, output);
    }

    [Fact]
    public void Quote_AsymmetricReserves_ReturnsCorrectRatio()
    {
        var result = Invoke(_engine, _routerHash, "quote", 50, 100, 500);
        var output = result.GetInteger();
        Assert.Equal(250, output);
    }

    [Fact]
    public void GetAmountOut_StandardFee_ReturnsCorrectAmount()
    {
        BigInteger amountIn = 1000;
        BigInteger reserveIn = 10000;
        BigInteger reserveOut = 10000;
        var result = Invoke(_engine, _routerHash, "getAmountOut", amountIn, reserveIn, reserveOut);
        var output = result.GetInteger();

        BigInteger expected = amountIn * 997 * reserveOut / (reserveIn * 1000 + amountIn * 997);
        Assert.Equal(expected, output);
    }

    [Fact]
    public void GetAmountOut_SmallInput_ReturnsNonZero()
    {
        BigInteger amountIn = 10000;
        BigInteger reserve = 1000000;
        var result = Invoke(_engine, _routerHash, "getAmountOut", amountIn, reserve, reserve);
        var output = result.GetInteger();
        Assert.True(output > 0);
        Assert.True(output < reserve);
    }

    [Fact]
    public void GetAmountOut_LargeInput_ReturnsReasonableOutput()
    {
        BigInteger amountIn = 1000000;
        BigInteger reserve = 1000000;
        var result = Invoke(_engine, _routerHash, "getAmountOut", amountIn, reserve, reserve);
        var output = result.GetInteger();
        Assert.True(output > 0);
        Assert.True(output < reserve);
    }

    [Fact]
    public void GetAmountIn_ReturnsCorrectInput()
    {
        BigInteger amountOut = 906;
        BigInteger reserve = 10000;
        var result = Invoke(_engine, _routerHash, "getAmountIn", amountOut, reserve, reserve);
        var output = result.GetInteger();

        BigInteger expected = reserve * amountOut * 1000 / ((reserve - amountOut) * 997) + 1;
        Assert.Equal(expected, output);
    }

    [Fact]
    public void GetAmountIn_SmallOutput_ReturnsOnePlusInput()
    {
        var result = Invoke(_engine, _routerHash, "getAmountIn", BigInteger.One, 10000, 10000);
        var output = result.GetInteger();
        Assert.True(output >= 1);
    }

    [Fact]
    public void Router_GetFactory_ReturnsSetFactory()
    {
        var result = Invoke(_engine, _routerHash, "getFactory");
        Assert.NotNull(result);
        Assert.IsNotType<Null>(result);
    }

    [Fact]
    public void Quote_ZeroAmount_Fails()
    {
        var ex = Assert.ThrowsAny<Exception>(() => Invoke(_engine, _routerHash, "quote",
            BigInteger.Zero, 1000, 2000));
        Assert.Contains("ASSERT", ex.Message);
    }

    [Fact]
    public void Quote_ZeroReserve_Fails()
    {
        var ex = Assert.ThrowsAny<Exception>(() => Invoke(_engine, _routerHash, "quote",
            100, BigInteger.Zero, 2000));
        Assert.Contains("ASSERT", ex.Message);
    }

    [Fact]
    public void GetAmountOut_ZeroInput_Fails()
    {
        var ex = Assert.ThrowsAny<Exception>(() => Invoke(_engine, _routerHash, "getAmountOut",
            BigInteger.Zero, 10000, 10000));
        Assert.Contains("ASSERT", ex.Message);
    }

    [Fact]
    public void GetAmountIn_ZeroOutput_Fails()
    {
        var ex = Assert.ThrowsAny<Exception>(() => Invoke(_engine, _routerHash, "getAmountIn",
            BigInteger.Zero, 10000, 10000));
        Assert.Contains("ASSERT", ex.Message);
    }

    [Fact]
    public void GetAmountIn_OutputEqualsReserve_LowLiquidity()
    {
        var ex = Assert.ThrowsAny<Exception>(() => Invoke(_engine, _routerHash, "getAmountIn",
            10000, 10000, 10000));
        Assert.Contains("ASSERT", ex.Message);
    }

    [Fact]
    public void AddLiquidity_ExpiredDeadline_Fails()
    {
        var engine = CreateEngine();
        var (factoryNef, factoryManifest) = LoadContract("DuoSwapFactory");
        var factoryHash = DeployContract(engine, factoryNef, factoryManifest, Owner);

        var (routerNef, routerManifest) = LoadContract("DuoSwapRouter");
        var routerHash = DeployContract(engine, routerNef, routerManifest, new object[] { factoryHash });
        Invoke(engine, factoryHash, "setRouter", routerHash);

        var (tokenNef, tokenManifestA) = LoadContract("DuoToken");
        tokenManifestA.Name = "DuoTokenA";
        var tokenA = DeployContract(engine, tokenNef, tokenManifestA, null);

        var (_, tokenManifestB) = LoadContract("DuoToken");
        tokenManifestB.Name = "DuoTokenB";
        var tokenB = DeployContract(engine, tokenNef, tokenManifestB, null);

        var ex = Assert.ThrowsAny<Exception>(() => Invoke(engine, routerHash, "addLiquidity",
            Owner, tokenA, tokenB,
            1000, 1000, 0, 0, -1));
        Assert.Contains("ASSERT", ex.Message);
    }

    [Fact]
    public void Router_OnNEP17Payment_NoOp()
    {
        Invoke(_engine, _routerHash, "onNEP17Payment", Owner, BigInteger.One, null);
    }
}
