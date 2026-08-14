using System.Numerics;
using Xunit;
using Neo;


namespace DuoSwap.Tests;

using static TestUtils;

public class LaunchpadTests
{
    [Fact]
    public void Launchpad_Deploy_InitialisesAtomically()
    {
        var engine = CreateEngine();
        engine.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.Global, Owner);

        // 1. Deploy DuoLaunchpad
        var (nef, manifest) = LoadContract("DuoLaunchpad");
        var decimals = (byte)8;
        var symbol = "LAUNCH";
        var supply = new BigInteger(100_000_000_000_000); // 1,000,000 tokens with 8 decimals
        var hash = DeployContract(engine, nef, manifest,
            new object[] { User, 8, symbol, supply });

        // Verify state after deployment
        var actualDecimals = Invoke(engine, hash, "decimals").GetInteger();
        Assert.Equal(decimals, actualDecimals);

        var actualSymbol = Invoke(engine, hash, "symbol").GetString();
        Assert.Equal(symbol, actualSymbol);

        var userBalance = Invoke(engine, hash, "balanceOf", User).GetInteger();
        Assert.Equal(supply, userBalance);

        var totalSupply = Invoke(engine, hash, "totalSupply").GetInteger();
        Assert.Equal(supply, totalSupply);

    }

    [Fact]
    public void Launchpad_Transfer_Succeeds()
    {
        var engine = CreateEngine();
        engine.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.Global, Owner);

        var (nef, manifest) = LoadContract("DuoLaunchpad");
        var symbol = "LAUNCH";
        var supply = new BigInteger(100_000_000_000_000);
        var hash = DeployContract(engine, nef, manifest,
            new object[] { Owner, 8, symbol, supply });

        var transferAmount = new BigInteger(1_000_000_000_000);
        Invoke(engine, hash, "transfer", Owner, User, transferAmount, null);
        Assert.Equal(transferAmount, Invoke(engine, hash, "balanceOf", User).GetInteger());
        Assert.Equal(supply - transferAmount, Invoke(engine, hash, "balanceOf", Owner).GetInteger());
    }

    [Fact]
    public void Launchpad_Transfer_InsufficientBalance_Fails()
    {
        var engine = CreateEngine();
        engine.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.Global, Owner);

        var (nef, manifest) = LoadContract("DuoLaunchpad");
        var symbol = "LAUNCH";
        var supply = new BigInteger(100_000_000_000_000);
        var hash = DeployContract(engine, nef, manifest,
            new object[] { Owner, 8, symbol, supply });

        engine.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.Global, User);
        var result = Invoke(engine, hash, "transfer", User, Owner, supply, null);
        Assert.False(result.GetBoolean());
    }


}
