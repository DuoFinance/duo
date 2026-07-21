using System.Numerics;
using System.Text;
using Xunit;
using Neo;
using Neo.IO;
using Neo.Persistence;
using Neo.SmartContract;
using Neo.SmartContract.Testing;
using Neo.SmartContract.Testing.Exceptions;
using Neo.VM;
using Neo.VM.Types;

namespace DuoBondingCurveTests;

using static TestUtils;

public class DuoBondingCurveFlowTests
{
    [Fact]
    public void Flow_BuyTokens_Success()
    {
        var (engine, factory, router, bondingCurve) = Setup();
        var gas = engine.Native.GAS.Hash;

        var tokenHash = SetupSale(engine, factory, router, bondingCurve,
            "TST", (BigInteger)100_000_000_000, (BigInteger)5001);

        var saleBefore = GetSaleInfo(engine, bondingCurve, tokenHash);
        Assert.Equal(BigInteger.Zero, saleBefore.TokensSold);
        Assert.Equal(BigInteger.Zero, saleBefore.GASRaised);

        BigInteger gasAmount = 10_000_000_000;
        var buyerGasBefore = Invoke(engine, gas, "balanceOf", Owner).GetInteger();
        var buyerTokenBefore = Invoke(engine, tokenHash, "balanceOf", Owner).GetInteger();
        Assert.Equal(BigInteger.Zero, buyerTokenBefore);

        BuyTokens(engine, bondingCurve, tokenHash, Owner, gasAmount);

        var buyerGasAfter = Invoke(engine, gas, "balanceOf", Owner).GetInteger();
        var buyerTokenAfter = Invoke(engine, tokenHash, "balanceOf", Owner).GetInteger();
        Assert.True(buyerGasAfter < buyerGasBefore, "GAS should decrease");
        Assert.True(buyerTokenAfter > buyerTokenBefore, "Tokens should increase");

        var saleAfter = GetSaleInfo(engine, bondingCurve, tokenHash);
        Assert.Equal(buyerTokenAfter, saleAfter.TokensSold);
        Assert.Equal(buyerGasBefore - buyerGasAfter, saleAfter.GASRaised);
        Assert.False(saleAfter.Graduated, "Sale should not graduate with small buy");
    }

    [Fact]
    public void Flow_BuyTokens_SlippageMinTokensOut()
    {
        var (engine, factory, router, bondingCurve) = Setup();
        var tokenHash = SetupSale(engine, factory, router, bondingCurve,
            "TST", (BigInteger)100_000_000_000, (BigInteger)5001);
        BigInteger gasAmount = 10_000_000_000;

        var expectedTokens = Invoke(engine, bondingCurve, "getTokensForGAS", tokenHash, gasAmount).GetInteger();

        BuyTokens(engine, bondingCurve, tokenHash, Owner, gasAmount, minTokensOut: expectedTokens);

        var buyerTokenAfter = Invoke(engine, tokenHash, "balanceOf", Owner).GetInteger();
        Assert.True(buyerTokenAfter >= expectedTokens, "Should receive at least minTokensOut");
    }

    [Fact]
    public void Flow_BuyAndSell_RoundTrip()
    {
        var (engine, factory, router, bondingCurve) = Setup();
        var gas = engine.Native.GAS.Hash;

        var tokenHash = SetupSale(engine, factory, router, bondingCurve,
            "TST", (BigInteger)100_000_000_000, (BigInteger)5001);

        BigInteger buyGas = 10_000_000_000;
        var gasBeforeBuy = Invoke(engine, gas, "balanceOf", Owner).GetInteger();
        BuyTokens(engine, bondingCurve, tokenHash, Owner, buyGas);
        var tokenBalance = Invoke(engine, tokenHash, "balanceOf", Owner).GetInteger();
        Assert.True(tokenBalance > 0);

        BigInteger expectedGasOut = Invoke(engine, bondingCurve, "getGASForTokens", tokenHash, tokenBalance).GetInteger();

        SellTokens(engine, bondingCurve, tokenHash, Owner, tokenBalance);

        var gasAfterSell = Invoke(engine, gas, "balanceOf", Owner).GetInteger();
        var tokenAfterSell = Invoke(engine, tokenHash, "balanceOf", Owner).GetInteger();
        Assert.Equal(BigInteger.Zero, tokenAfterSell);

        BigInteger gasReturned = gasAfterSell - (gasBeforeBuy - buyGas);
        BigInteger tol = buyGas / 1000 + 1;
        Assert.True(BigInteger.Abs(gasReturned - expectedGasOut) <= tol,
            $"Gas returned mismatch. expected={expectedGasOut}, actual={gasReturned}");

        var saleAfter = GetSaleInfo(engine, bondingCurve, tokenHash);
        Assert.Equal(BigInteger.Zero, saleAfter.TokensSold);
    }

    [Fact]
    public void Flow_Graduation_ExcessGasRefunded()
    {
        var (engine, factory, router, bondingCurve) = Setup();
        var gas = engine.Native.GAS.Hash;

        BigInteger targetRaise = 500_000_000_000;
        var tokenHash = SetupSale(engine, factory, router, bondingCurve,
            "TST", targetRaise, (BigInteger)5001);

        BigInteger gasBefore = Invoke(engine, gas, "balanceOf", Owner).GetInteger();
        BuyTokens(engine, bondingCurve, tokenHash, Owner, targetRaise * 10);

        var gasAfter = Invoke(engine, gas, "balanceOf", Owner).GetInteger();
        var gasSpent = gasBefore - gasAfter;

        var saleInfo = GetSaleInfo(engine, bondingCurve, tokenHash);
        Assert.True(saleInfo.Graduated, "Should graduate");
        Assert.True(gasSpent <= targetRaise * 2,
            $"Should not spend excessive GAS. Spent: {gasSpent}, targetRaise: {targetRaise}");
    }

    [Fact]
    public void Flow_MultipleBuys_PriceMonotonic()
    {
        var (engine, factory, router, bondingCurve) = Setup();
        var tokenHash = SetupSale(engine, factory, router, bondingCurve,
            "TST", (BigInteger)100_000_000_000, (BigInteger)5001);

        BigInteger GetPrice()
        {
            return Invoke(engine, bondingCurve, "getCurrentPrice", tokenHash).GetInteger();
        }

        BigInteger previousPrice = GetPrice();

        for (int i = 0; i < 5; i++)
        {
            BuyTokens(engine, bondingCurve, tokenHash, Owner, 5_000_000_000);
            var currentPrice = GetPrice();
            Assert.True(currentPrice >= previousPrice,
                $"Price should not decrease on buy {i + 1}. prev={previousPrice}, cur={currentPrice}");
            previousPrice = currentPrice;
        }
    }

    [Fact]
    public void Flow_BuySell_KConstantPreserved()
    {
        var (engine, factory, router, bondingCurve) = Setup();
        var tokenHash = SetupSale(engine, factory, router, bondingCurve,
            "TST", (BigInteger)100_000_000_000, (BigInteger)5001);

        var saleInitial = GetSaleInfo(engine, bondingCurve, tokenHash);
        BigInteger idealK = saleInitial.VirtualGas * saleInitial.VirtualToken;

        BuyTokens(engine, bondingCurve, tokenHash, Owner, 20_000_000_000);
        var saleAfterBuy = GetSaleInfo(engine, bondingCurve, tokenHash);
        BigInteger actualKAfterBuy = (saleAfterBuy.VirtualGas + saleAfterBuy.GASRaised) *
                                      (saleAfterBuy.VirtualToken - saleAfterBuy.TokensSold);

        BigInteger tolerance = idealK / 10000 + 1;
        Assert.True(BigInteger.Abs(actualKAfterBuy - idealK) <= tolerance,
            $"K should be approx constant after buy. ideal={idealK}, actual={actualKAfterBuy}");

        var halfTokens = (saleAfterBuy.TokensSold) / 2;
        if (halfTokens > 0)
        {
            SellTokens(engine, bondingCurve, tokenHash, Owner, halfTokens);
            var saleAfterSell = GetSaleInfo(engine, bondingCurve, tokenHash);
            BigInteger actualKAfterSell = (saleAfterSell.VirtualGas + saleAfterSell.GASRaised) *
                                            (saleAfterSell.VirtualToken - saleAfterSell.TokensSold);
            Assert.True(BigInteger.Abs(actualKAfterSell - idealK) <= tolerance,
                $"K should be approx constant after sell. ideal={idealK}, actual={actualKAfterSell}");
        }
    }

    private static UInt160 SetupSale(TestEngine engine, UInt160 factory, UInt160 router,
        UInt160 bondingCurve,
        string symbol,
        BigInteger targetRaise, BigInteger graduationBps)
    {
        BigInteger rawSupply = 100000000000000000; byte decimals = 8;

        var (ln, lm) = LoadContract("DuoLaunchpad");
        var tokenHash = DeployContract(engine, ln, lm, null);

        Invoke(engine, tokenHash, "initialise", bondingCurve, (byte)decimals, symbol, rawSupply);

        return WriteSaleToStorage(engine, bondingCurve, tokenHash, rawSupply, symbol, targetRaise, graduationBps);
    }

    private static UInt160 WriteSaleToStorage(TestEngine engine, UInt160 bondingCurve,
        UInt160 tokenHash, BigInteger totalSupply, string symbol,
        BigInteger targetRaise, BigInteger graduationBps)
    {
        var bcState = engine.Native.ContractManagement.GetContract(bondingCurve);
        int bcId = bcState.Id;

        BigInteger twoPminusOne = 2 * graduationBps - 10000;
        BigInteger pSquared = graduationBps * graduationBps;
        BigInteger vToken = totalSupply * pSquared / (10000 * twoPminusOne);
        BigInteger vGas = targetRaise * (10000 - graduationBps) / twoPminusOne;

        var map = new Map();
        map[(BigInteger)0] = (Neo.VM.Types.ByteString)tokenHash.GetSpan().ToArray();
        map[(BigInteger)1] = (Neo.VM.Types.ByteString)Owner.GetSpan().ToArray();
        map[(BigInteger)2] = new Integer(BigInteger.Zero);
        map[(BigInteger)3] = new Integer(BigInteger.Zero);
        map[(BigInteger)4] = new Integer(targetRaise);
        map[(BigInteger)5] = new Integer(vGas);
        map[(BigInteger)6] = new Integer(vToken);
        map[(BigInteger)7] = new Integer(graduationBps);
        map[(BigInteger)8] = Neo.VM.Types.Boolean.False;

        byte[] serializedSale = BinarySerializer.Serialize(map, new ExecutionEngineLimits());

        byte[] keyBytes = new byte[24];
        Encoding.UTF8.GetBytes("Sale", 0, 4, keyBytes, 0);
        tokenHash.GetSpan().CopyTo(keyBytes.AsSpan(4));

        var store = engine.Storage.Snapshot;
        store.Add(new StorageKey { Id = bcId, Key = keyBytes }, new StorageItem { Value = serializedSale });
        store.Commit();

        return tokenHash;
    }

    private static void BuyTokens(TestEngine engine, UInt160 bondingCurve, UInt160 tokenHash,
        UInt160 buyer, BigInteger gasAmount, BigInteger minTokensOut = default)
    {
        var gas = engine.Native.GAS.Hash;

        var data = new ContractParameter(ContractParameterType.Array)
        {
            Value = new ContractParameter[]
            {
                new(ContractParameterType.Hash160) { Value = tokenHash },
                new(ContractParameterType.Integer) { Value = minTokensOut }
            }
        };

        engine.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.Global, buyer);
        Invoke(engine, gas, "transfer", buyer, bondingCurve, gasAmount, data);
    }

    private static void SellTokens(TestEngine engine, UInt160 bondingCurve, UInt160 tokenHash,
        UInt160 seller, BigInteger tokenAmount, BigInteger minGasOut = default)
    {
        var data = new ContractParameter(ContractParameterType.Array)
        {
            Value = new ContractParameter[]
            {
                new(ContractParameterType.Integer) { Value = minGasOut }
            }
        };

        engine.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.Global, seller);
        Invoke(engine, tokenHash, "transfer", seller, bondingCurve, tokenAmount, data);
    }

    private static SaleInfo GetSaleInfo(TestEngine engine, UInt160 bondingCurve, UInt160 tokenHash)
    {
        var result = Invoke(engine, bondingCurve, "getSaleInfo", tokenHash);
        var map = (Neo.VM.Types.Map)result;
        return new SaleInfo
        {
            TokenHash = tokenHash,
            TokensSold = map[(BigInteger)2].GetInteger(),
            GASRaised = map[(BigInteger)3].GetInteger(),
            TargetRaise = map[(BigInteger)4].GetInteger(),
            VirtualGas = map[(BigInteger)5].GetInteger(),
            VirtualToken = map[(BigInteger)6].GetInteger(),
            GraduationBps = map[(BigInteger)7].GetInteger(),
            Graduated = map[(BigInteger)8].GetBoolean()
        };
    }

    private static (TestEngine engine, UInt160 factory, UInt160 router, UInt160 bondingCurve) Setup()
    {
        var engine = CreateEngine();
        engine.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.Global, Owner);

        var (fn, fm) = LoadContract("DuoSwapFactory");
        var factory = DeployContract(engine, fn, fm, Owner);

        var (rn, rm) = LoadContract("DuoSwapRouter");
        var router = DeployContract(engine, rn, rm, new object[] { factory });
        Invoke(engine, factory, "setRouter", router);

        var (bn, bm) = LoadContract("DuoBondingCurve");
        var bondingCurve = DeployContract(engine, bn, bm, new object[] { factory, router });
        Invoke(engine, factory, "setBonding", bondingCurve);

        return (engine, factory, router, bondingCurve);
    }

    private struct SaleInfo
    {
        public UInt160 TokenHash;
        public BigInteger TokensSold;
        public BigInteger GASRaised;
        public BigInteger TargetRaise;
        public BigInteger VirtualGas;
        public BigInteger VirtualToken;
        public BigInteger GraduationBps;
        public bool Graduated;
    }
}