using System.Numerics;
using Xunit;
using Neo;


namespace DuoSwap.Tests;

using static TestUtils;

public class LaunchpadTests
{
    [Fact]
    public void Launchpad_Initialise_Succeeds_And_Enforces_Constraints()
    {
        var engine = CreateEngine();
        engine.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.Global, Owner);

        // 1. Deploy DuoLaunchpad
        var (nef, manifest) = LoadContract("DuoLaunchpad");
        var hash = DeployContract(engine, nef, manifest, null);

        // 2. Initialise DuoLaunchpad
        var decimals = (byte)8;
        var symbol = "LAUNCH";
        var supply = new BigInteger(100_000_000_000_000); // 1,000,000 tokens with 8 decimals
        
        // Initialise
        Invoke(engine, hash, "initialise", User, decimals, symbol, supply);

        // Verify state after initialisation
        var actualDecimals = Invoke(engine, hash, "decimals").GetInteger();
        Assert.Equal(decimals, actualDecimals);

        var actualSymbol = Invoke(engine, hash, "symbol").GetString();
        Assert.Equal(symbol, actualSymbol);

        var userBalance = Invoke(engine, hash, "balanceOf", User).GetInteger();
        Assert.Equal(supply, userBalance);

        var totalSupply = Invoke(engine, hash, "totalSupply").GetInteger();
        Assert.Equal(supply, totalSupply);

        // 3. Try to initialise again, should fail
        var ex = Assert.ThrowsAny<Exception>(() =>
            Invoke(engine, hash, "initialise", Owner, decimals, symbol, supply));
        Assert.Contains("ASSERT", ex.Message);
    }

    
}
