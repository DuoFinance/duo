using System.Numerics;
using Xunit;
using Neo;
using Neo.SmartContract;
using Neo.SmartContract.Testing;
using Neo.VM;

namespace DuoSwap.Tests;
using static TestUtils;

public class DebugBNeoTest
{
    [Fact]
    public void Debug()
    {
        var engine = CreateEngine();
        engine.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.Global, Owner);

        var (factoryNef, factoryManifest) = LoadContract("DuoSwapFactory");
        var factory = DeployContract(engine, factoryNef, factoryManifest, Owner);
        var (routerNef, routerManifest) = LoadContract("DuoSwapRouter");
        var router = DeployContract(engine, routerNef, routerManifest, new object[] { factory });
        Invoke(engine, factory, "setRouter", router);
        Invoke(engine, factory, "setBonding", Bonding);

        var (bneoNef, bneoManifest) = LoadContract("DuoMockBNeo");
        var bneo = DeployContract(engine, bneoNef, bneoManifest, null);

        var (tokenNef, tokenManifest) = LoadContract("DuoToken");
        tokenManifest.Name = "DuoToken";
        var token = DeployContract(engine, tokenNef, tokenManifest, null);
        Invoke(engine, token, "initialise", Owner, 1_000_000_000_000_000_000);

        // deploy pair
        var gasHash = engine.Native.GAS.Hash;
        var validatorsList = engine.ProtocolSettings.StandbyValidators;
        var m = validatorsList.Count - (validatorsList.Count - 1) / 3;
        var validatorsScript = Contract.CreateMultiSigRedeemScript(m, validatorsList.ToArray());
        var validatorsAddr = validatorsScript.ToScriptHash();
        var deployFee = Invoke(engine, factory, "getDeployFee").GetInteger();
        engine.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.Global, validatorsAddr, Owner);
        Invoke(engine, gasHash, "transfer", validatorsAddr, Owner, deployFee, null);
        engine.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.Global, Owner);
        var data = new ContractParameter(ContractParameterType.Array)
        {
            Value = new ContractParameter[]
            {
                new(ContractParameterType.Hash160) { Value = bneo },
                new(ContractParameterType.Hash160) { Value = token },
            }
        };
        var gasScript = gasHash.MakeScript("transfer",            new ContractParameter(ContractParameterType.Hash160) { Value = Owner },
            new ContractParameter(ContractParameterType.Hash160) { Value = factory },
            new ContractParameter(ContractParameterType.Integer) { Value = deployFee },
            data);
        engine.Execute(gasScript);

        var pairRes = Invoke(engine, factory, "getPairHash", bneo, token);
        var pair = new UInt160(pairRes.GetSpan());
        Console.WriteLine("factory gas before setBNeo: " + Invoke(engine, gasHash, "balanceOf", factory).GetInteger());
        Invoke(engine, factory, "setBNeo", bneo);
        Console.WriteLine("bneo token0: " + new UInt160(Invoke(engine, pair, "getToken0").GetSpan()));
        Console.WriteLine("bneo token1: " + new UInt160(Invoke(engine, pair, "getToken1").GetSpan()));
        Console.WriteLine("bneo itself: " + bneo);

        BigInteger reward = 123_456_789;
        engine.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.Global, validatorsAddr, Owner);
        Invoke(engine, gasHash, "transfer", validatorsAddr, Owner, reward + deployFee, null);
        engine.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.Global, Owner);
        Invoke(engine, gasHash, "transfer", Owner, bneo, reward, null);
        Console.WriteLine("bneo gas balance: " + Invoke(engine, gasHash, "balanceOf", bneo).GetInteger());
        Console.WriteLine("pair gas balance before claim: " + Invoke(engine, gasHash, "balanceOf", pair).GetInteger());

        engine.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.Global, Owner);
        var allPairs = Invoke(engine, factory, "getAllPairsHash");
        Console.WriteLine("getAllPairsHash count: " + ((allPairs as Neo.VM.Types.Array)?.Count ?? -1));
        var result = Invoke(engine, factory, "claimGasAll");
        Console.WriteLine("claimGasAll result: " + result);
        Console.WriteLine("bneo gas balance after: " + Invoke(engine, gasHash, "balanceOf", bneo).GetInteger());
        Console.WriteLine("pair gas balance after: " + Invoke(engine, gasHash, "balanceOf", pair).GetInteger());
        Console.WriteLine("factory gas balance after: " + Invoke(engine, gasHash, "balanceOf", factory).GetInteger());
        Console.WriteLine("OWNER gas: " + Invoke(engine, gasHash, "balanceOf", Owner).GetInteger());
        Assert.Equal(deployFee + reward, Invoke(engine, gasHash, "balanceOf", factory).GetInteger());
    }
}
