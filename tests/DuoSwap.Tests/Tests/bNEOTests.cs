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
        Invoke(_engine, _token, "initialise", Owner, 1_000_000_000_000_000_000);

        _bneoPair = DeployPair(_bneo, _token);
    }

    private UInt160 DeployPair(UInt160 tokenA, UInt160 tokenB)
    {
        var gasHash = _engine.Native.GAS.Hash;
        var vl = _engine.ProtocolSettings.StandbyValidators;
        var m = vl.Count - (vl.Count - 1) / 3;
        var va = Contract.CreateMultiSigRedeemScript(m, vl.ToArray()).ToScriptHash();
        var fee = Invoke(_engine, _factory, "getDeployFee").GetInteger();
        _engine.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.Global, va, Owner);
        Invoke(_engine, gasHash, "transfer", va, Owner, fee, null);
        _engine.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.Global, Owner);

        var data = new ContractParameter(ContractParameterType.Array) { Value = new ContractParameter[] {
            new(ContractParameterType.Hash160) { Value = tokenA },
            new(ContractParameterType.Hash160) { Value = tokenB },
        }};
        _engine.Execute(gasHash.MakeScript("transfer",
            new ContractParameter(ContractParameterType.Hash160) { Value = Owner },
            new ContractParameter(ContractParameterType.Hash160) { Value = _factory },
            new ContractParameter(ContractParameterType.Integer) { Value = fee }, data));

        return new UInt160(Invoke(_engine, _factory, "getPairHash", tokenA, tokenB).GetSpan());
    }

    private BigInteger GasBalance(UInt160 account)
    {
        return Invoke(_engine, _engine.Native.GAS.Hash, "balanceOf", account).GetInteger();
    }

    private void FundOwnerFromValidators(BigInteger amount)
    {
        var gasHash = _engine.Native.GAS.Hash;
        var vl = _engine.ProtocolSettings.StandbyValidators;
        var m = vl.Count - (vl.Count - 1) / 3;
        var va = Contract.CreateMultiSigRedeemScript(m, vl.ToArray()).ToScriptHash();
        _engine.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.Global, va, Owner);
        Invoke(_engine, gasHash, "transfer", va, Owner, amount, null);
        _engine.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.Global, Owner);
    }

    private void FundBNeo(BigInteger amount)
    {
        FundOwnerFromValidators(amount);
        Invoke(_engine, _engine.Native.GAS.Hash, "transfer", Owner, _bneo, amount, null);
    }

    #region Get/Set BNeo

    [Fact]
    public void GetBNeo_ReturnsZeroBeforeSet()
    {
        var bneo = new UInt160(Invoke(_engine, _factory, "getBNeo").GetSpan());
        Assert.Equal(UInt160.Zero, bneo);
    }

    [Fact]
    public void SetBNeo_SetsAddress()
    {
        var result = Invoke(_engine, _factory, "setBNeo", _bneo);
        Assert.True(result.GetBoolean());

        var bneo = new UInt160(Invoke(_engine, _factory, "getBNeo").GetSpan());
        Assert.Equal(_bneo, bneo);
    }

    [Fact]
    public void SetBNeo_CanUpdate()
    {
        Invoke(_engine, _factory, "setBNeo", _bneo);

        var (newNef, newManifest) = LoadContract("DuoMockBNeo");
        newManifest.Name = "DuoMockBNeoV2";
        var newBneo = DeployContract(_engine, newNef, newManifest, null);

        var result = Invoke(_engine, _factory, "setBNeo", newBneo);
        Assert.True(result.GetBoolean());

        var bneo = new UInt160(Invoke(_engine, _factory, "getBNeo").GetSpan());
        Assert.Equal(newBneo, bneo);
    }

    [Fact]
    public void SetBNeo_NonSetterCaller_Fails()
    {
        _engine.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.CalledByEntry, User);
        var ex = Assert.ThrowsAny<Exception>(() => Invoke(_engine, _factory, "setBNeo", _bneo));
        Assert.Contains("ASSERT", ex.Message);
    }

    #endregion

    #region ClaimGAS

    [Fact]
    public void ClaimGAS_NonSetterCaller_Fails()
    {
        _engine.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.CalledByEntry, User);
        var ex = Assert.ThrowsAny<Exception>(() => Invoke(_engine, _factory, "claimGAS", _bneoPair));
        Assert.Contains("ASSERT", ex.Message);
    }

    [Fact]
    public void ClaimGAS_NonBNeoPair_Noop()
    {
        var (tokenNef, tokenManifest) = LoadContract("DuoToken");
        tokenManifest.Name = "DuoTokenOther";
        var otherToken = DeployContract(_engine, tokenNef, tokenManifest, null);
        Invoke(_engine, otherToken, "initialise", Owner, 1_000_000_000_000_000_000);
        var normalPair = DeployPair(otherToken, _token);

        Invoke(_engine, _factory, "setBNeo", _bneo);
        BigInteger baseline = GasBalance(_factory);
        var result = Invoke(_engine, _factory, "claimGAS", normalPair);
        Assert.True(result.GetBoolean());
        Assert.Equal(baseline, GasBalance(_factory));
    }

    [Fact]
    public void ClaimGAS_ClaimsAndForwardsToFactory()
    {
        Invoke(_engine, _factory, "setBNeo", _bneo);
        BigInteger reward = 123_456_789;
        FundBNeo(reward);

        BigInteger baseline = GasBalance(_factory);
        var result = Invoke(_engine, _factory, "claimGAS", _bneoPair);
        Assert.True(result.GetBoolean());

        Assert.Equal(baseline + reward, GasBalance(_factory));
    }

    [Fact]
    public void ClaimGAS_AccruesAcrossMultipleClaims()
    {
        Invoke(_engine, _factory, "setBNeo", _bneo);
        BigInteger baseline = GasBalance(_factory);
        BigInteger first = 50_000_000;
        FundBNeo(first);
        Invoke(_engine, _factory, "claimGAS", _bneoPair);

        BigInteger second = 30_000_000;
        FundBNeo(second);
        var result = Invoke(_engine, _factory, "claimGAS", _bneoPair);
        Assert.True(result.GetBoolean());

        Assert.Equal(baseline + first + second, GasBalance(_factory));
    }

    #endregion

    #region Withdraw

    [Fact]
    public void Withdraw_FullBalance_WhenAmountZero()
    {
        Invoke(_engine, _factory, "setBNeo", _bneo);
        BigInteger reward = 100_000_000;
        FundBNeo(reward);
        Invoke(_engine, _factory, "claimGAS", _bneoPair);

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
        BigInteger reward = 200_000_000;
        FundBNeo(reward);
        Invoke(_engine, _factory, "claimGAS", _bneoPair);

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
        BigInteger reward = 10_000_000;
        FundBNeo(reward);
        Invoke(_engine, _factory, "claimGAS", _bneoPair);

        BigInteger factoryBalance = GasBalance(_factory);
        var ex = Assert.ThrowsAny<Exception>(() => Invoke(_engine, _factory, "withdraw", Owner, factoryBalance + 1));
        Assert.Contains("ASSERT", ex.Message);
    }

    [Fact]
    public void Withdraw_NonSetterCaller_Fails()
    {
        _engine.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.CalledByEntry, User);
        var ex = Assert.ThrowsAny<Exception>(() => Invoke(_engine, _factory, "withdraw", User, 1));
        Assert.Contains("ASSERT", ex.Message);
    }

    #endregion

    #region Pair claim gate

    [Fact]
    public void PairClaimGASFrombNEO_OnlyFactoryAllowed()
    {
        _engine.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.Global, Owner);
        var ex = Assert.ThrowsAny<Exception>(() =>
            Invoke(_engine, _bneoPair, "claimGASFrombNEO"));
        Assert.Contains("ASSERT", ex.Message);
    }

    #endregion
}