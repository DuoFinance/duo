using System.Numerics;
using Xunit;
using Neo;
using Neo.SmartContract.Testing;


namespace DuoSwap.Tests;

using static TestUtils;

public class PairTests
{
    private readonly TestEngine _engine;
    private readonly UInt160 _factoryHash;
    private readonly UInt160 _routerHash;
    private readonly UInt160 _pairHash;
    private readonly UInt160 _tokenA;
    private readonly UInt160 _tokenB;

    public PairTests()
    {
        _engine = CreateEngine();

        _tokenA = new UInt160(new byte[] { 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
        _tokenB = new UInt160(new byte[] { 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 });

        var (factoryNef, factoryManifest) = LoadContract("DuoSwapFactory");
        _factoryHash = DeployContract(_engine, factoryNef, factoryManifest, Owner);

        var (routerNef, routerManifest) = LoadContract("DuoSwapRouter");
        _routerHash = DeployContract(_engine, routerNef, routerManifest, new object[] { _factoryHash });

        Invoke(_engine, _factoryHash, "setRouter", _routerHash);

        var (pairNef, pairManifest) = LoadContract("DuoSwapPair");
        _pairHash = DeployContract(_engine, pairNef, pairManifest,
            new object[] { _factoryHash, _routerHash, Bonding, _tokenA, _tokenB, "TEST-PAIR" });
    }

    [Fact]
    public void Pair_InitialState_ReturnsCorrectValues()
    {
        var totalSupply = Invoke(_engine, _pairHash, "totalSupply");
        Assert.Equal(BigInteger.Zero, totalSupply.GetInteger());

        var decimals = Invoke(_engine, _pairHash, "decimals");
        Assert.NotNull(decimals);

        var symbol = Invoke(_engine, _pairHash, "symbol");
        Assert.NotNull(symbol);
    }

    [Fact]
    public void Pair_BalanceOf_InitiallyZero()
    {
        var balance = Invoke(_engine, _pairHash, "balanceOf", Owner);
        Assert.Equal(BigInteger.Zero, balance.GetInteger());
    }

    [Fact]
    public void Pair_GetReserves_InitiallyZero()
    {
        var result = Invoke(_engine, _pairHash, "getReserves");
        Assert.NotNull(result);
    }

    [Fact]
    public void Pair_GetToken0_ReturnsToken()
    {
        var result = Invoke(_engine, _pairHash, "getToken0");
        Assert.NotNull(result);
    }

    [Fact]
    public void Pair_GetToken1_ReturnsToken()
    {
        var result = Invoke(_engine, _pairHash, "getToken1");
        Assert.NotNull(result);
    }

    [Fact]
    public void Pair_GetFactory_ReturnsFactory()
    {
        var result = Invoke(_engine, _pairHash, "getFactory");
        Assert.NotNull(result);
    }

    [Fact]
    public void Pair_GetKLast_InitiallyZero()
    {
        var result = Invoke(_engine, _pairHash, "getKLast");
        Assert.Equal(BigInteger.Zero, result.GetInteger());
    }

    [Fact]
    public void Pair_GetReserve0_InitiallyZero()
    {
        var result = Invoke(_engine, _pairHash, "getReserve0");
        Assert.Equal(BigInteger.Zero, result.GetInteger());
    }

    [Fact]
    public void Pair_GetReserve1_InitiallyZero()
    {
        var result = Invoke(_engine, _pairHash, "getReserve1");
        Assert.Equal(BigInteger.Zero, result.GetInteger());
    }

    [Fact]
    public void Pair_GetPriceCumulative_InitiallyZero()
    {
        var result = Invoke(_engine, _pairHash, "getPriceCumulative");
        Assert.NotNull(result);
    }

    [Fact]
    public void Pair_Price0CumulativeLast_InitiallyZero()
    {
        var result = Invoke(_engine, _pairHash, "price0CumulativeLast");
        Assert.Equal(BigInteger.Zero, result.GetInteger());
    }

    [Fact]
    public void Pair_Price1CumulativeLast_InitiallyZero()
    {
        var result = Invoke(_engine, _pairHash, "price1CumulativeLast");
        Assert.Equal(BigInteger.Zero, result.GetInteger());
    }

    [Fact]
    public void Pair_GetRouter_ReturnsRouter()
    {
        var result = Invoke(_engine, _pairHash, "getRouter");
        Assert.Equal(_routerHash, new UInt160(result.GetSpan()));
    }

    [Fact]
    public void Pair_Swap_ZeroOutput_Fails()
    {
        var ex = Assert.ThrowsAny<Exception>(() =>
        {
            Invoke(_engine, _pairHash, "swap",
                BigInteger.Zero, BigInteger.Zero, Owner, new byte[] { });
        });
        Assert.Contains("ASSERT", ex.Message);
    }

    [Fact]
    public void Pair_Swap_BadRecipient_Self_Fails()
    {
        var ex = Assert.ThrowsAny<Exception>(() =>
        {
            Invoke(_engine, _pairHash, "swap",
                BigInteger.One, BigInteger.Zero, _pairHash, new byte[] { });
        });
        Assert.Contains("ASSERT", ex.Message);
    }

    [Fact]
    public void Pair_GetBonding_ReturnsBonding()
    {
        var result = Invoke(_engine, _pairHash, "getBonding");
        Assert.Equal(Bonding, new UInt160(result.GetSpan()));
    }
}
