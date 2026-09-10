using System.Numerics;
using Xunit;
using Neo;
using Neo.SmartContract;
using Neo.SmartContract.Testing;
using Neo.VM;
using Neo.VM.Types;

namespace DuoSwap.Tests;

using static TestUtils;

public class FeeToTests
{
    private readonly TestEngine _engine;
    private readonly UInt160 _factoryHash;
    private readonly UInt160 _routerHash;
    private readonly UInt160 _tokenA;
    private readonly UInt160 _tokenB;

    public FeeToTests()
    {
        var engine = CreateEngine();
        // Override to Global scope so witness propagates to nested calls
        engine.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.Global, Owner);
        _engine = engine;

        var (factoryNef, factoryManifest) = LoadContract("DuoSwapFactory");
        _factoryHash = DeployContract(_engine, factoryNef, factoryManifest, Owner);

        var (routerNef, routerManifest) = LoadContract("DuoSwapRouter");
        _routerHash = DeployContract(_engine, routerNef, routerManifest, new object[] { _factoryHash });

        Invoke(_engine, _factoryHash, "setRouter", _routerHash);
        Invoke(_engine, _factoryHash, "setBonding", Bonding);

        var (tokenNef, tokenManifestA) = LoadContract("DuoToken");
        tokenManifestA.Name = "DuoTokenA";
        _tokenA = DeployContract(_engine, tokenNef, tokenManifestA, null);

        var (_, tokenManifestB) = LoadContract("DuoToken");
        tokenManifestB.Name = "DuoTokenB";
        _tokenB = DeployContract(_engine, tokenNef, tokenManifestB, null);

        Invoke(_engine, _tokenA, "initialise", Owner);
        Invoke(_engine, _tokenB, "initialise", Owner);

        // Fund Owner with GAS from validators for pair deployment
        var gasHash = _engine.Native.GAS.Hash;
        var validatorsList = _engine.ProtocolSettings.StandbyValidators;
        var m = validatorsList.Count - (validatorsList.Count - 1) / 3;
        var validatorsScript = Contract.CreateMultiSigRedeemScript(m, validatorsList.ToArray());
        var validatorsAddr = validatorsScript.ToScriptHash();
        var deployFee = Invoke(_engine, _factoryHash, "getDeployFee").GetInteger();
        _engine.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.Global, validatorsAddr, Owner);
        Invoke(_engine, gasHash, "transfer", validatorsAddr, Owner, deployFee, null);
        _engine.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.Global, Owner);
    }

    [Fact]
    public void FeeTo_EndToEnd_MintsProtocolLpTokens()
    {
        // 1. Set feeTo to User address
        Invoke(_engine, _factoryHash, "setFeeTo", User);

        // 2. Verify feeTo is set
        var feeToResult = Invoke(_engine, _factoryHash, "getFeeTo");
        Assert.Equal(User, new UInt160(feeToResult.GetSpan()));

        // 3. Deploy Pair via Factory (send GAS to trigger OnNEP17Payment)
        var gasHash = _engine.Native.GAS.Hash;
        var deployFee = Invoke(_engine, _factoryHash, "getDeployFee").GetInteger();
        Assert.True(deployFee > 0);

        var data = new ContractParameter(ContractParameterType.Array)
        {
            Value = new ContractParameter[]
            {
                new(ContractParameterType.Hash160) { Value = _tokenA },
                new(ContractParameterType.Hash160) { Value = _tokenB },
            }
        };
        var gasScript = gasHash.MakeScript("transfer",
            new ContractParameter(ContractParameterType.Hash160) { Value = Owner },
            new ContractParameter(ContractParameterType.Hash160) { Value = _factoryHash },
            new ContractParameter(ContractParameterType.Integer) { Value = deployFee },
            data);
        _engine.Execute(gasScript);

        // 4. Get the Pair hash
        var pairResult = Invoke(_engine, _factoryHash, "getPairHash", _tokenA, _tokenB);
        Assert.IsNotType<Null>(pairResult);
        var pairHash = new UInt160(pairResult.GetSpan());

        // 5. First AddLiquidity through Router
        BigInteger amountADesired = 10_000_000_000_00;
        BigInteger amountBDesired = 10_000_000_000_00;
        BigInteger deadline = long.MaxValue;

        Invoke(_engine, _routerHash, "addLiquidity",
            Owner, _tokenA, _tokenB,
            amountADesired, amountBDesired,
            0, 0, deadline);

        // 6. Swap tokenA -> tokenB to accumulate fee (increases k)
        BigInteger swapAmount = 1_000_000_00;
        var swapScript = _routerHash.MakeScript("swapExactTokensForTokens",
            new ContractParameter(ContractParameterType.Hash160) { Value = Owner },
            new ContractParameter(ContractParameterType.Integer) { Value = swapAmount },
            new ContractParameter(ContractParameterType.Integer) { Value = BigInteger.Zero },
            new ContractParameter(ContractParameterType.Array)
            {
                Value = new ContractParameter[]
                {
                    new(ContractParameterType.Hash160) { Value = _tokenA },
                    new(ContractParameterType.Hash160) { Value = _tokenB },
                }
            },
            new ContractParameter(ContractParameterType.Integer) { Value = deadline });
        _engine.Execute(swapScript);

        // 7. Verify feeTo has no LP tokens yet
        var feeToBalanceBefore = Invoke(_engine, pairHash, "balanceOf", User).GetInteger();
        Assert.Equal(BigInteger.Zero, feeToBalanceBefore);

        // 8. Second AddLiquidity - should trigger protocol fee minting
        Invoke(_engine, _routerHash, "addLiquidity",
            Owner, _tokenA, _tokenB,
            amountADesired, amountBDesired,
            0, 0, deadline);

        // 9. Verify feeTo received LP tokens
        var feeToBalanceAfter = Invoke(_engine, pairHash, "balanceOf", User).GetInteger();
        Assert.True(feeToBalanceAfter > 0,
            $"feeTo should have received protocol LP tokens, but balance is {feeToBalanceAfter}");
    }
}
