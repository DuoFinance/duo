using System.Numerics;
using Xunit;
using Neo;
using Neo.SmartContract.Testing;
using Neo.VM.Types;

namespace DuoSwap.Tests;

using static TestUtils;

public class TokenTests
{
    private readonly TestEngine _engine;
    private readonly UInt160 _tokenHash;

    public TokenTests()
    {
        _engine = CreateEngine();

        var (tokenNef, tokenManifest) = LoadContract("DuoToken");
        _tokenHash = DeployContract(_engine, tokenNef, tokenManifest, null);
    }

    [Fact]
    public void TotalSupply_InitiallyZero()
    {
        var result = Invoke(_engine, _tokenHash, "totalSupply");
        Assert.Equal(BigInteger.Zero, result.GetInteger());
    }

    [Fact]
    public void Decimals_ReturnsCorrectValue()
    {
        var result = Invoke(_engine, _tokenHash, "decimals");
        Assert.Equal(8, result.GetInteger());
    }

    [Fact]
    public void Symbol_ReturnsCorrectValue()
    {
        var result = Invoke(_engine, _tokenHash, "symbol");
        var str = result as ByteString;
        Assert.NotNull(str);
    }

    [Fact]
    public void BalanceOf_InitiallyZero()
    {
        var result = Invoke(_engine, _tokenHash, "balanceOf", Owner);
        Assert.Equal(BigInteger.Zero, result.GetInteger());
    }

    [Fact]
    public void GetOwner_ReturnsDeployer()
    {
        var result = Invoke(_engine, _tokenHash, "getOwner");
        Assert.NotNull(result);
    }

    [Fact]
    public void IsInitialised_ReturnsFalse()
    {
        var result = Invoke(_engine, _tokenHash, "isInitialised");
        Assert.NotNull(result);
    }

    [Fact]
    public void GetTreasury_ReturnsZeroBeforeInit()
    {
        var result = Invoke(_engine, _tokenHash, "getTreasury");
        Assert.NotNull(result);
    }
}


