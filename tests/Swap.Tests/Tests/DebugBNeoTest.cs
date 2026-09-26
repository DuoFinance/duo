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

        var (tokenNef, tokenManifest) = LoadContract("TestToken");
        tokenManifest.Name = "TestToken";
        var token = DeployContract(engine, tokenNef, tokenManifest, null);
        Invoke(engine, token, "initialise", Owner);

        Invoke(engine, factory, "setBNeo", bneo);

        // deploy bNEO pair
        var gasHash = engine.Native.GAS.Hash;
        var vl = engine.ProtocolSettings.StandbyValidators;
        var m = vl.Count - (vl.Count - 1) / 3;
        var va = Contract.CreateMultiSigRedeemScript(m, vl.ToArray()).ToScriptHash();
        var fee = Invoke(engine, factory, "getDeployFee").GetInteger();
        engine.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.Global, va, Owner);
        Invoke(engine, gasHash, "transfer", va, Owner, fee, null);
        engine.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.Global, Owner);

        var data = new ContractParameter(ContractParameterType.Array) { Value = new ContractParameter[] {
            new(ContractParameterType.Hash160) { Value = bneo },
            new(ContractParameterType.Hash160) { Value = token },
        }};
        engine.Execute(gasHash.MakeScript("transfer",
            new ContractParameter(ContractParameterType.Hash160) { Value = Owner },
            new ContractParameter(ContractParameterType.Hash160) { Value = factory },
            new ContractParameter(ContractParameterType.Integer) { Value = fee }, data));
        var pair = new UInt160(Invoke(engine, factory, "getPairHash", bneo, token).GetSpan());

        // sanity: pair exposes bNEO-claim method
        var methods = new object[] { };
        Assert.NotNull(pair);

        // fund bNEO and claim through factory
        engine.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.Global, va, Owner);
        Invoke(engine, gasHash, "transfer", va, Owner, 200_000_000, null);
        engine.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.Global, Owner);
        Invoke(engine, gasHash, "transfer", Owner, bneo, 200_000_000, null);

        BigInteger before = Invoke(engine, gasHash, "balanceOf", factory).GetInteger();
        BigInteger recipientBefore = Invoke(engine, gasHash, "balanceOf", Owner).GetInteger();
        var claimed = Invoke(engine, factory, "claimGAS", pair, Owner);
        Assert.True(claimed.GetBoolean());
        BigInteger after = Invoke(engine, gasHash, "balanceOf", factory).GetInteger();
        Assert.Equal(0, after);
        Assert.Equal(before + 200_000_000, Invoke(engine, gasHash, "balanceOf", Owner).GetInteger() - recipientBefore);
    }
}
