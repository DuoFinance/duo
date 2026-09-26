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

    // ── Router contract-caller overload tests (via caller contract) ──

    private (TestEngine engine, UInt160 factory, UInt160 router, UInt160 caller, UInt160 pair, UInt160 tokenA, UInt160 tokenB) DeployCaller()
    {
        var (e, f, r, _, ta, tb) = Setup();
        var pairRes = Invoke(e, f, "getPairHash", ta, tb);
        var pair = new UInt160(pairRes.GetSpan());

        var (cn, cm) = LoadContract("DuoHoneypot");
        cm.Name = "DuoHoneypot";
        var caller = DeployContract(e, cn, cm, new object[] { pair, Owner, r });

        // Include the caller contract hash as a signer so that token.transfer(checkWitness=caller) passes
        // during the approvedTransfer path (Neo 3.7.4 test engine does not auto-grant witness for contracts
        // in the invocation stack).
        e.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.Global, Owner, caller);

        return (e, f, r, caller, pair, ta, tb);
    }

    [Fact]
    public void Router_ContractCaller_AddLiquidity()
    {
        var (engine, factory, router, caller, pair, tokenA, tokenB) = DeployCaller();

        // Fund caller with both tokens
        BigInteger fundAmount = 20_000_000_000_00;
        Invoke(engine, tokenA, "transfer", Owner, caller, fundAmount, null);
        Invoke(engine, tokenB, "transfer", Owner, caller, fundAmount, null);

        BigInteger tokenABefore = Invoke(engine, tokenA, "balanceOf", caller).GetInteger();
        BigInteger tokenBBefore = Invoke(engine, tokenB, "balanceOf", caller).GetInteger();
        Assert.True(tokenABefore >= fundAmount);
        Assert.True(tokenBBefore >= fundAmount);

        BigInteger amountADesired = 10_000_000_000_00;
        BigInteger amountBDesired = 10_000_000_000_00;
        BigInteger deadline = long.MaxValue;

        var result = Invoke(engine, caller, "routerCall_AddLiquidity",
            router, tokenA, tokenB, amountADesired, amountBDesired, 0, 0, deadline);
        Assert.NotNull(result);

        var arr = result as Neo.VM.Types.Array;
        Assert.NotNull(arr);
        Assert.Equal(3, arr.Count);

        // Caller should have received LP tokens from the pair
        BigInteger lpBalance = Invoke(engine, pair, "balanceOf", caller).GetInteger();
        Assert.True(lpBalance > 0, "Caller should hold LP tokens");

        // Caller's token balances should have decreased by approximately the deposited amounts
        BigInteger tokenAAfter = Invoke(engine, tokenA, "balanceOf", caller).GetInteger();
        BigInteger tokenBAfter = Invoke(engine, tokenB, "balanceOf", caller).GetInteger();
        Assert.True(tokenAAfter < tokenABefore, "TokenA should be spent");
        Assert.True(tokenBAfter < tokenBBefore, "TokenB should be spent");
        Console.WriteLine("   contract-caller AddLiquidity: LP=" + lpBalance + " ✓");
    }

    [Fact]
    public void Router_ContractCaller_RemoveLiquidity()
    {
        var (engine, factory, router, caller, pair, tokenA, tokenB) = DeployCaller();

        BigInteger fundAmount = 20_000_000_000_00;
        Invoke(engine, tokenA, "transfer", Owner, caller, fundAmount, null);
        Invoke(engine, tokenB, "transfer", Owner, caller, fundAmount, null);

        BigInteger amountADesired = 10_000_000_000_00;
        BigInteger amountBDesired = 10_000_000_000_00;
        BigInteger deadline = long.MaxValue;

        // First add liquidity to get LP tokens
        var addResult = Invoke(engine, caller, "routerCall_AddLiquidity",
            router, tokenA, tokenB, amountADesired, amountBDesired, 0, 0, deadline);

        BigInteger lpBalance = Invoke(engine, pair, "balanceOf", caller).GetInteger();
        Assert.True(lpBalance > 0, "Caller should hold LP before remove");

        BigInteger tokenABefore = Invoke(engine, tokenA, "balanceOf", caller).GetInteger();
        BigInteger tokenBBefore = Invoke(engine, tokenB, "balanceOf", caller).GetInteger();

        // Now remove liquidity via contract-caller overload
        var removeResult = Invoke(engine, caller, "routerCall_RemoveLiquidity",
            router, tokenA, tokenB, lpBalance, 0, 0, deadline);
        Assert.NotNull(removeResult);

        BigInteger lpAfter = Invoke(engine, pair, "balanceOf", caller).GetInteger();
        Assert.True(lpAfter < lpBalance, "LP should decrease after removal");

        BigInteger tokenAAfter = Invoke(engine, tokenA, "balanceOf", caller).GetInteger();
        BigInteger tokenBAfter = Invoke(engine, tokenB, "balanceOf", caller).GetInteger();
        Assert.True(tokenAAfter > tokenABefore, "TokenA should be returned");
        Assert.True(tokenBAfter > tokenBBefore, "TokenB should be returned");
        Console.WriteLine("   contract-caller RemoveLiquidity: ΔA=" + (tokenAAfter - tokenABefore) + " ΔB=" + (tokenBAfter - tokenBBefore) + " ✓");
    }

    [Fact]
    public void Router_ContractCaller_SwapExactTokensForTokens()
    {
        var (engine, factory, router, caller, pair, tokenA, tokenB) = DeployCaller();

        BigInteger fundAmount = 20_000_000_000_00;
        Invoke(engine, tokenA, "transfer", Owner, caller, fundAmount, null);

        // Also fund the pair (via explicit addLiquidity) so it has reserves for the swap
        Invoke(engine, tokenA, "transfer", Owner, pair, 10_000_000_000_00, null);
        Invoke(engine, tokenB, "transfer", Owner, pair, 10_000_000_000_00, null);

        BigInteger deadline = long.MaxValue;
        BigInteger swapAmount = 1_000_000_00;

        BigInteger tokenBBefore = Invoke(engine, tokenB, "balanceOf", caller).GetInteger();

        var path = new ContractParameter(ContractParameterType.Array)
        {
            Value = new ContractParameter[]
            {
                new(ContractParameterType.Hash160) { Value = tokenA },
                new(ContractParameterType.Hash160) { Value = tokenB },
            }
        };

        var result = Invoke(engine, caller, "routerCall_SwapExactTokensForTokens",
            router, swapAmount, BigInteger.Zero, path, deadline);

        Assert.NotNull(result);
        Assert.True((result as Neo.VM.Types.Boolean)?.GetBoolean() ?? false, "Swap should succeed");

        BigInteger tokenAAfter = Invoke(engine, tokenA, "balanceOf", caller).GetInteger();
        BigInteger tokenBAfter = Invoke(engine, tokenB, "balanceOf", caller).GetInteger();
        Assert.True(tokenAAfter < fundAmount, "TokenA should be spent");
        Assert.True(tokenBAfter > tokenBBefore, "TokenB should be received");
        Console.WriteLine("   contract-caller SwapExactTokensForTokens: ΔB=" + (tokenBAfter - tokenBBefore) + " ✓");
    }

    [Fact]
    public void Router_ContractCaller_SwapTokensForExactTokens()
    {
        var (engine, factory, router, caller, pair, tokenA, tokenB) = DeployCaller();

        BigInteger fundAmount = 20_000_000_000_00;
        Invoke(engine, tokenA, "transfer", Owner, caller, fundAmount, null);
        Invoke(engine, tokenA, "transfer", Owner, pair, 10_000_000_000_00, null);
        Invoke(engine, tokenB, "transfer", Owner, pair, 10_000_000_000_00, null);

        BigInteger deadline = long.MaxValue;
        BigInteger exactAmountOut = 500_000_00;
        BigInteger amountInMax = 2_000_000_00;

        BigInteger tokenBBefore = Invoke(engine, tokenB, "balanceOf", caller).GetInteger();

        var path = new ContractParameter(ContractParameterType.Array)
        {
            Value = new ContractParameter[]
            {
                new(ContractParameterType.Hash160) { Value = tokenA },
                new(ContractParameterType.Hash160) { Value = tokenB },
            }
        };

        var result = Invoke(engine, caller, "routerCall_SwapTokensForExactTokens",
            router, amountInMax, exactAmountOut, path, deadline);

        Assert.NotNull(result);
        Assert.True((result as Neo.VM.Types.Boolean)?.GetBoolean() ?? false, "Swap should succeed");

        BigInteger tokenAAfter = Invoke(engine, tokenA, "balanceOf", caller).GetInteger();
        BigInteger tokenBAfter = Invoke(engine, tokenB, "balanceOf", caller).GetInteger();
        Assert.True(tokenAAfter < fundAmount, "TokenA should be spent");
        Assert.True(tokenBAfter >= tokenBBefore + exactAmountOut, "TokenB should be received");
        Console.WriteLine("   contract-caller SwapTokensForExactTokens: ΔB=" + (tokenBAfter - tokenBBefore) + " ✓");
    }

    [Fact]
    public void FactoryClaimGAS_CrossContract_ForwardsRewardToRecipient()
    {
        var (engine, factory, router, pair, tokenA, tokenB) = DeployBNeoSetup();

        var gasHash = engine.Native.GAS.Hash;
        var bneo = new UInt160(Invoke(engine, factory, "getBNeo").GetSpan());
        BigInteger reward = 123_000_000;
        var vl = engine.ProtocolSettings.StandbyValidators;
        var m = vl.Count - (vl.Count - 1) / 3;
        var va = Contract.CreateMultiSigRedeemScript(m, vl.ToArray()).ToScriptHash();
        engine.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.Global, va, Owner);
        Invoke(engine, gasHash, "transfer", va, Owner, reward, null);
        engine.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.Global, Owner);
        Invoke(engine, gasHash, "transfer", Owner, bneo, reward, null);

        BigInteger factoryBefore = Invoke(engine, gasHash, "balanceOf", factory).GetInteger();
        BigInteger pairBefore = Invoke(engine, gasHash, "balanceOf", pair).GetInteger();

        BigInteger recipientBefore = Invoke(engine, gasHash, "balanceOf", Owner).GetInteger();
        var result = Invoke(engine, factory, "claimGAS", pair, Owner);
        Assert.True(result.GetBoolean());

        // Factory -> pair -> bNEO mock -> GAS native chain delivered the reward.
        Assert.Equal(0, Invoke(engine, gasHash, "balanceOf", factory).GetInteger());
        Assert.Equal(factoryBefore + reward, Invoke(engine, gasHash, "balanceOf", Owner).GetInteger() - recipientBefore);
        Assert.Equal(0, Invoke(engine, gasHash, "balanceOf", pair).GetInteger() - pairBefore);
    }

    [Fact]
    public void FactoryClaimGAS_NonBNeoPair_Noop()
    {
        var (engine, factory, _, pair, tokenA, tokenB) = Setup();

        var gasHash = engine.Native.GAS.Hash;
        BigInteger factoryBefore = Invoke(engine, gasHash, "balanceOf", factory).GetInteger();

        var result = Invoke(engine, factory, "claimGAS", pair, Owner);
        Assert.True(result.GetBoolean());
        Assert.Equal(factoryBefore, Invoke(engine, gasHash, "balanceOf", factory).GetInteger());
    }

    private static (TestEngine engine, UInt160 factory, UInt160 router, UInt160 pair, UInt160 tokenA, UInt160 tokenB) DeployBNeoSetup()
    {
        var (e, f, r, p, ta, tb) = Setup();

        var (bneoNef, bneoManifest) = LoadContract("DuoMockBNeo");
        var bneo = DeployContract(e, bneoNef, bneoManifest, null);
        Invoke(e, f, "setBNeo", bneo);

        var gasHash = e.Native.GAS.Hash;
        var vl = e.ProtocolSettings.StandbyValidators;
        var m = vl.Count - (vl.Count - 1) / 3;
        var va = Contract.CreateMultiSigRedeemScript(m, vl.ToArray()).ToScriptHash();
        var fee = Invoke(e, f, "getDeployFee").GetInteger();
        e.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.Global, va, Owner);
        Invoke(e, gasHash, "transfer", va, Owner, fee, null);
        e.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.Global, Owner);

        var data = new ContractParameter(ContractParameterType.Array)
        {
            Value = new ContractParameter[]
            {
                new(ContractParameterType.Hash160) { Value = bneo },
                new(ContractParameterType.Hash160) { Value = ta },
            }
        };
        e.Execute(gasHash.MakeScript("transfer",
            new ContractParameter(ContractParameterType.Hash160) { Value = Owner },
            new ContractParameter(ContractParameterType.Hash160) { Value = f },
            new ContractParameter(ContractParameterType.Integer) { Value = fee }, data));

        var pairRes = Invoke(e, f, "getPairHash", bneo, ta);
        var bneoPair = new UInt160(pairRes.GetSpan());

        return (e, f, r, bneoPair, ta, tb);
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
        Invoke(engine, factoryHash, "setBonding", Bonding);

        var (tokenNef, tokenManifestA) = LoadContract("TestToken");
        tokenManifestA.Name = "TestTokenA";
        var tokenA = DeployContract(engine, tokenNef, tokenManifestA, null);

        var (_, tokenManifestB) = LoadContract("TestToken");
        tokenManifestB.Name = "TestTokenB";
        var tokenB = DeployContract(engine, tokenNef, tokenManifestB, null);

        Invoke(engine, tokenA, "initialise", Owner);
        Invoke(engine, tokenB, "initialise", Owner);

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
