using System.Numerics;
using Xunit;
using Neo;
using Neo.SmartContract;
using Neo.SmartContract.Testing;
using Neo.VM;

namespace DuoSwap.Tests;

using static TestUtils;

public class DebugTest
{
    [Fact]
    public void DirectMintViaOverride()
    {
        var engine = CreateEngine();

        engine.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.Global, Owner);

        var (fn, fm) = LoadContract("DuoSwapFactory");
        var factory = DeployContract(engine, fn, fm, Owner);

        var (rn, rm) = LoadContract("DuoSwapRouter");
        var router = DeployContract(engine, rn, rm, new object[] { factory });
        Invoke(engine, factory, "setRouter", router);

        var (tn, tma) = LoadContract("DuoToken");
        tma.Name = "DuoTokenA";
        var tokenA = DeployContract(engine, tn, tma, null);

        var (_, tmb) = LoadContract("DuoToken");
        tmb.Name = "DuoTokenB";
        var tokenB = DeployContract(engine, tn, tmb, null);

        BigInteger supply = 1_000_000_000_000_000_000;
        Invoke(engine, tokenA, "initialise", Owner, supply);
        Invoke(engine, tokenB, "initialise", Owner, supply);

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

        var pair = new UInt160(Invoke(engine, factory, "getPairHash", tokenA, tokenB).GetSpan());

        // MANUAL mint approach:
        // 1. Transfer tokens to pair
        engine.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.Global, Owner);
        Invoke(engine, tokenA, "transfer", Owner, pair, 1000000, null);
        Invoke(engine, tokenB, "transfer", Owner, pair, 1000000, null);

        // 2. Override CallingScriptHash to be the router
        engine.OnGetCallingScriptHash = (current, expected) => router;

        // 3. Call pair.mint directly
        var liquidity = Invoke(engine, pair, "mint", Owner).GetInteger();
        Assert.True(liquidity > 0);

        // 4. Reset override
        engine.OnGetCallingScriptHash = null;

        // Verify LP balance
        var lpBalance = Invoke(engine, pair, "balanceOf", Owner).GetInteger();
        Assert.Equal(liquidity, lpBalance);

        // Verify reserves updated
        var reserve0 = Invoke(engine, pair, "getReserve0").GetInteger();
        Assert.True(reserve0 > 0);
    }
}
