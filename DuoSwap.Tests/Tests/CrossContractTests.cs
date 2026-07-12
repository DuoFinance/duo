using System.Numerics;
using Xunit;
using Neo;
using Neo.SmartContract;
using Neo.SmartContract.Testing;
using Neo.VM;

namespace DuoSwap.Tests;

using static TestUtils;

public class CrossContractTests
{
    [Fact]
    public void PairSwap_ReEntrancyViaHoneypot_BlockedByLock()
    {
        var (engine, _, _, pairHash, tokenA, tokenB) = Setup();

        var swapAmount = 100_000_00;
        Invoke(engine, tokenA, "transfer", Owner, pairHash, swapAmount, null);

        var token0Result = Invoke(engine, pairHash, "getToken0");
        var token0Hash = new UInt160(token0Result.GetSpan());

        BigInteger amount0Out = token0Hash == tokenB ? swapAmount : 0;
        BigInteger amount1Out = token0Hash == tokenA ? swapAmount : 0;

        var (honeypotNef, honeypotManifest) = LoadContract("DuoHoneypot");
        var honeypotHash = DeployContract(engine, honeypotNef, honeypotManifest,
            new object[] { pairHash, User });

        var swapScript = pairHash.MakeScript("swap",
            new ContractParameter(ContractParameterType.Integer) { Value = amount0Out },
            new ContractParameter(ContractParameterType.Integer) { Value = amount1Out },
            new ContractParameter(ContractParameterType.Hash160) { Value = honeypotHash },
            new ContractParameter(ContractParameterType.ByteArray) { Value = new byte[] { 0x01 } });
        var ex = Assert.ThrowsAny<Exception>(() => engine.Execute(swapScript));
        Assert.Contains("ASSERT", ex.Message);
    }

    [Fact]
    public void RouterSwap_ExactTokensForTokens_Succeeds()
    {
        var (engine, _, routerHash, _, tokenA, tokenB) = Setup();

        var swapAmount = 100_000_00;
        var deadline = long.MaxValue;
        var swapScript = routerHash.MakeScript("swapExactTokensForTokens",
            new ContractParameter(ContractParameterType.Hash160) { Value = Owner },
            new ContractParameter(ContractParameterType.Integer) { Value = swapAmount },
            new ContractParameter(ContractParameterType.Integer) { Value = BigInteger.Zero },
            new ContractParameter(ContractParameterType.Array)
            {
                Value = new ContractParameter[]
                {
                    new(ContractParameterType.Hash160) { Value = tokenA },
                    new(ContractParameterType.Hash160) { Value = tokenB },
                }
            },
            new ContractParameter(ContractParameterType.Integer) { Value = deadline });
        var result = engine.Execute(swapScript);
        Assert.NotNull(result);
    }

    private static (TestEngine engine, UInt160 factoryHash, UInt160 routerHash, UInt160 pairHash, UInt160 tokenA, UInt160 tokenB) Setup()
    {
        var engine = CreateEngine();
        engine.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.Global, Owner);

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

        BigInteger totalSupply = 1_000_000_000_000_000_000;
        Invoke(engine, tokenA, "initialise", Owner, totalSupply);
        Invoke(engine, tokenB, "initialise", Owner, totalSupply);

        var gasHash = engine.Native.GAS.Hash;
        var validatorsList = engine.ProtocolSettings.StandbyValidators;
        var m = validatorsList.Count - (validatorsList.Count - 1) / 3;
        var validatorsScript = Contract.CreateMultiSigRedeemScript(m, validatorsList.ToArray());
        var validatorsAddr = validatorsScript.ToScriptHash();
        var deployFee = Invoke(engine, factoryHash, "getDeployFee").GetInteger();
        engine.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.Global, validatorsAddr, Owner);
        Invoke(engine, gasHash, "transfer", validatorsAddr, Owner, deployFee, null);
        engine.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.Global, Owner);

        var data = new ContractParameter(ContractParameterType.Array)
        {
            Value = new ContractParameter[]
            {
                new(ContractParameterType.Hash160) { Value = tokenA },
                new(ContractParameterType.Hash160) { Value = tokenB },
            }
        };
        var gasScript = gasHash.MakeScript("transfer",
            new ContractParameter(ContractParameterType.Hash160) { Value = Owner },
            new ContractParameter(ContractParameterType.Hash160) { Value = factoryHash },
            new ContractParameter(ContractParameterType.Integer) { Value = deployFee },
            data);
        engine.Execute(gasScript);

        var pairResult = Invoke(engine, factoryHash, "getPairHash", tokenA, tokenB);
        var pairHash = new UInt160(pairResult.GetSpan());

        BigInteger amountADesired = 10_000_000_000_00;
        BigInteger amountBDesired = 10_000_000_000_00;
        BigInteger deadline = long.MaxValue;
        Invoke(engine, routerHash, "addLiquidity",
            Owner, tokenA, tokenB,
            amountADesired, amountBDesired,
            0, 0, deadline);

        return (engine, factoryHash, routerHash, pairHash, tokenA, tokenB);
    }
}
