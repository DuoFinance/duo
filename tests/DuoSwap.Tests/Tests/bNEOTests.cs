using System.Numerics;
using Xunit;
using Neo;
using Neo.SmartContract;
using Neo.SmartContract.Testing;
using Neo.VM;

namespace DuoSwap.Tests;

using static TestUtils;

public class bNEOTests
{
    private readonly TestEngine _engine;
    private readonly UInt160 _factory;
    private readonly UInt160 _router;
    private readonly UInt160 _bneo;
    private readonly UInt160 _token;
    private readonly UInt160 _bneoPair;

    public bNEOTests()
    {
        _engine = CreateEngine();
        _engine.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.Global, Owner);

        var (factoryNef, factoryManifest) = LoadContract("DuoSwapFactory");
        _factory = DeployContract(_engine, factoryNef, factoryManifest, Owner);

        var (routerNef, routerManifest) = LoadContract("DuoSwapRouter");
        _router = DeployContract(_engine, routerNef, routerManifest, new object[] { _factory });
        Invoke(_engine, _factory, "setRouter", _router);
        Invoke(_engine, _factory, "setBonding", Bonding);

        var (bneoNef, bneoManifest) = LoadContract("DuoMockBNeo");
        _bneo = DeployContract(_engine, bneoNef, bneoManifest, null);

        var (tokenNef, tokenManifest) = LoadContract("DuoToken");
        tokenManifest.Name = "DuoToken";
        _token = DeployContract(_engine, tokenNef, tokenManifest, null);
        BigInteger totalSupply = 1_000_000_000_000_000_000;
        Invoke(_engine, _token, "initialise", Owner, totalSupply);

        _bneoPair = DeployPair(_bneo, _token);
    }

    private UInt160 DeployPair(UInt160 tokenA, UInt160 tokenB)
    {
        var gasHash = _engine.Native.GAS.Hash;
        var validatorsList = _engine.ProtocolSettings.StandbyValidators;
        var m = validatorsList.Count - (validatorsList.Count - 1) / 3;
        var validatorsScript = Contract.CreateMultiSigRedeemScript(m, validatorsList.ToArray());
        var validatorsAddr = validatorsScript.ToScriptHash();
        var deployFee = Invoke(_engine, _factory, "getDeployFee").GetInteger();
        _engine.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.Global, validatorsAddr, Owner);
        Invoke(_engine, gasHash, "transfer", validatorsAddr, Owner, deployFee, null);
        _engine.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.Global, Owner);

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
            new ContractParameter(ContractParameterType.Hash160) { Value = _factory },
            new ContractParameter(ContractParameterType.Integer) { Value = deployFee },
            data);
        _engine.Execute(gasScript);

        var pairResult = Invoke(_engine, _factory, "getPairHash", tokenA, tokenB);
        return new UInt160(pairResult.GetSpan());
    }

    private BigInteger GasBalance(UInt160 account)
    {
        return Invoke(_engine, _engine.Native.GAS.Hash, "balanceOf", account).GetInteger();
    }

    private void FundOwnerFromValidators(BigInteger amount)
    {
        var gasHash = _engine.Native.GAS.Hash;
        var validatorsList = _engine.ProtocolSettings.StandbyValidators;
        var m = validatorsList.Count - (validatorsList.Count - 1) / 3;
        var validatorsScript = Contract.CreateMultiSigRedeemScript(m, validatorsList.ToArray());
        var validatorsAddr = validatorsScript.ToScriptHash();
        _engine.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.Global, validatorsAddr, Owner);
        Invoke(_engine, gasHash, "transfer", validatorsAddr, Owner, amount, null);
        _engine.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.Global, Owner);
    }

    [Fact]
    public void GetBNeo_ReturnsZeroBeforeSet()
    {
        var result = Invoke(_engine, _factory, "getBNeo");
        var bneo = new UInt160(result.GetSpan());
        Assert.Equal(UInt160.Zero, bneo);
    }

    [Fact]
    public void SetBNeo_SetsAddress()
    {
        Invoke(_engine, _factory, "setBNeo", _bneo);
        var result = Invoke(_engine, _factory, "getBNeo");
        var bneo = new UInt160(result.GetSpan());
        Assert.Equal(_bneo, bneo);
    }

    [Fact]
    public void SetBNeo_CanUpdate()
    {
        Invoke(_engine, _factory, "setBNeo", _bneo);

        var (newNef, newManifest) = LoadContract("DuoMockBNeo");
        newManifest.Name = "DuoMockBNeoV2";
        var newBneo = DeployContract(_engine, newNef, newManifest, null);

        Invoke(_engine, _factory, "setBNeo", newBneo);
        var result = Invoke(_engine, _factory, "getBNeo");
        var bneo = new UInt160(result.GetSpan());
        Assert.Equal(newBneo, bneo);
    }

    [Fact]
    public void SetBNeo_NonSetterCaller_Fails()
    {
        var engine = CreateEngine();
        var (factoryNef, factoryManifest) = LoadContract("DuoSwapFactory");
        var factory = DeployContract(engine, factoryNef, factoryManifest, Owner);
        var (bneoNef, bneoManifest) = LoadContract("DuoMockBNeo");
        var bneo = DeployContract(engine, bneoNef, bneoManifest, null);

        engine.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.CalledByEntry, User);
        var ex = Assert.ThrowsAny<Exception>(() => Invoke(engine, factory, "setBNeo", bneo));
        Assert.Contains("ASSERT", ex.Message);
    }

    [Fact]
    public void Withdraw_NonSetterCaller_Fails()
    {
        _engine.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.CalledByEntry, User);
        var ex = Assert.ThrowsAny<Exception>(() => Invoke(_engine, _factory, "withdraw", User, 1));
        Assert.Contains("ASSERT", ex.Message);
    }

    [Fact]
    public void ClaimGasAll_NonSetterCaller_Fails()
    {
        _engine.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.CalledByEntry, User);
        var ex = Assert.ThrowsAny<Exception>(() => Invoke(_engine, _factory, "claimGasAll"));
        Assert.Contains("ASSERT", ex.Message);
    }

    [Fact]
    public void ClaimGasAll_NonBNeoPairs_Noop()
    {
        _engine.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.Global, Owner);
        BigInteger baseline = GasBalance(_factory);
        var result = Invoke(_engine, _factory, "claimGasAll");
        Assert.NotNull(result);
        Assert.Equal(baseline, GasBalance(_factory));
    }

    [Fact]
    public void ClaimGasAll_ClaimsAndForwardsToFactory()
    {
        Invoke(_engine, _factory, "setBNeo", _bneo);
        var gasHash = _engine.Native.GAS.Hash;
        BigInteger reward = 123_456_789;
        FundOwnerFromValidators(reward);
        Invoke(_engine, gasHash, "transfer", Owner, _bneo, reward, null);

        _engine.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.Global, Owner);
        BigInteger baseline = GasBalance(_factory);
        var result = Invoke(_engine, _factory, "claimGasAll");
        Assert.NotNull(result);

        Assert.Equal(baseline + reward, GasBalance(_factory));
    }

    [Fact]
    public void Withdraw_FullBalance_WhenAmountZero()
    {
        Invoke(_engine, _factory, "setBNeo", _bneo);
        var gasHash = _engine.Native.GAS.Hash;
        BigInteger reward = 100_000_000;
        FundOwnerFromValidators(reward);
        Invoke(_engine, gasHash, "transfer", Owner, _bneo, reward, null);
        Invoke(_engine, _factory, "claimGasAll");

        _engine.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.Global, Owner);
        BigInteger factoryBefore = GasBalance(_factory);
        BigInteger ownerBefore = GasBalance(Owner);
        Invoke(_engine, _factory, "withdraw", Owner, 0);
        Assert.Equal(0, GasBalance(_factory));
        Assert.Equal(ownerBefore + factoryBefore, GasBalance(Owner));
    }

    [Fact]
    public void Withdraw_PartialAmount()
    {
        Invoke(_engine, _factory, "setBNeo", _bneo);
        var gasHash = _engine.Native.GAS.Hash;
        BigInteger reward = 200_000_000;
        FundOwnerFromValidators(reward);
        Invoke(_engine, gasHash, "transfer", Owner, _bneo, reward, null);
        Invoke(_engine, _factory, "claimGasAll");

        _engine.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.Global, Owner);
        BigInteger factoryBefore = GasBalance(_factory);
        BigInteger ownerBefore = GasBalance(Owner);
        BigInteger partial = 50_000_000;
        Invoke(_engine, _factory, "withdraw", Owner, partial);
        Assert.Equal(factoryBefore - partial, GasBalance(_factory));
        Assert.Equal(ownerBefore + partial, GasBalance(Owner));
    }

    [Fact]
    public void Withdraw_ExceedsBalance_Fails()
    {
        Invoke(_engine, _factory, "setBNeo", _bneo);
        var gasHash = _engine.Native.GAS.Hash;
        BigInteger reward = 10_000_000;
        FundOwnerFromValidators(reward);
        Invoke(_engine, gasHash, "transfer", Owner, _bneo, reward, null);
        Invoke(_engine, _factory, "claimGasAll");

        _engine.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.Global, Owner);
        var ex = Assert.ThrowsAny<Exception>(() => Invoke(_engine, _factory, "withdraw", Owner, reward + 1));
        Assert.Contains("ASSERT", ex.Message);
    }

    [Fact]
    public void PairClaimGASFrombNEO_OnlyFactoryAllowed()
    {
        _engine.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.Global, Owner);
        var ex = Assert.ThrowsAny<Exception>(() =>
            Invoke(_engine, _bneoPair, "claimGASFrombNEO"));
        Assert.Contains("ASSERT", ex.Message);
    }
}
