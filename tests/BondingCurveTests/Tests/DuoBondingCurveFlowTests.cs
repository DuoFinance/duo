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

        var saleBefore = getSale(engine, bondingCurve, tokenHash);
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

        var saleAfter = getSale(engine, bondingCurve, tokenHash);
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

        var saleAfter = getSale(engine, bondingCurve, tokenHash);
        Assert.Equal(BigInteger.Zero, saleAfter.TokensSold);
    }

    [Fact]
    public void Flow_BuyAndSell_WithFee()
    {
        var (engine, factory, router, bondingCurve) = Setup();
        var gas = engine.Native.GAS.Hash;

        // Set feeTo to a non-zero address Kiwi
        var feeTo = UInt160.Parse("0x0000000000000000000000000000000000000008");
        Invoke(engine, factory, "setFeeTo", feeTo);

        var tokenHash = SetupSale(engine, factory, router, bondingCurve,
            "TST", (BigInteger)100_000_000_000, (BigInteger)5001);

        BigInteger buyGas = 10_000_000_000;
        var gasBeforeBuy = Invoke(engine, gas, "balanceOf", Owner).GetInteger();
        var treasuryBeforeBuy = Invoke(engine, gas, "balanceOf", feeTo).GetInteger();

        BuyTokens(engine, bondingCurve, tokenHash, Owner, buyGas);

        var tokenBalance = Invoke(engine, tokenHash, "balanceOf", Owner).GetInteger();
        Assert.True(tokenBalance > 0);

        var gasAfterBuy = Invoke(engine, gas, "balanceOf", Owner).GetInteger();
        var treasuryAfterBuy = Invoke(engine, gas, "balanceOf", feeTo).GetInteger();

        BigInteger expectedBuyFee = buyGas * 3 / 10000;
        Assert.Equal(expectedBuyFee, treasuryAfterBuy - treasuryBeforeBuy);
        Assert.Equal(buyGas, gasBeforeBuy - gasAfterBuy);

        // Sell tokens back
        BigInteger expectedGasOut = Invoke(engine, bondingCurve, "getGASForTokens", tokenHash, tokenBalance).GetInteger();

        SellTokens(engine, bondingCurve, tokenHash, Owner, tokenBalance);

        var gasAfterSell = Invoke(engine, gas, "balanceOf", Owner).GetInteger();
        var treasuryAfterSell = Invoke(engine, gas, "balanceOf", feeTo).GetInteger();

        Assert.Equal(expectedGasOut, gasAfterSell - gasAfterBuy);
        Assert.True(treasuryAfterSell > treasuryAfterBuy, "Treasury should receive sell fee");
        Assert.Equal(BigInteger.Zero, Invoke(engine, tokenHash, "balanceOf", Owner).GetInteger());
    }

    [Fact]
    public void Flow_FeeTo_Toggle_BeforeAndAfter()
    {
        var (engine, factory, router, bondingCurve) = Setup();
        var gas = engine.Native.GAS.Hash;

        var feeTo = UInt160.Parse("0x0000000000000000000000000000000000000009");
        var tokenHash = SetupSale(engine, factory, router, bondingCurve,
            "TST", (BigInteger)100_000_000_000, (BigInteger)5001);

        UInt160 ReadFeeTo() => new UInt160(Invoke(engine, factory, "getFeeTo").GetSpan());
        BigInteger Treasury() => Invoke(engine, gas, "balanceOf", feeTo).GetInteger();
        BigInteger Raised() => getSale(engine, bondingCurve, tokenHash).GASRaised;

        Assert.True(ReadFeeTo() == UInt160.Zero, "feeTo should start unset");

        // 1) Buy before feeTo is set -> no fee at all.
        BigInteger buyGas1 = 10_000_000_000;
        BigInteger raisedBefore1 = Raised();
        BigInteger treasuryBefore1 = Treasury();
        BuyTokens(engine, bondingCurve, tokenHash, Owner, buyGas1);
        Assert.Equal(BigInteger.Zero, Treasury() - treasuryBefore1);
        Assert.Equal(buyGas1, Raised() - raisedBefore1);

        // 2) Enable feeTo -> next buy charges exactly 0.03%.
        Invoke(engine, factory, "setFeeTo", feeTo);
        Assert.Equal(feeTo, ReadFeeTo());

        BigInteger buyGas2 = 20_000_000_000;
        BigInteger expectedFee2 = buyGas2 * 3 / 10000;
        BigInteger raisedBefore2 = Raised();
        BigInteger treasuryBefore2 = Treasury();
        BuyTokens(engine, bondingCurve, tokenHash, Owner, buyGas2);
        Assert.Equal(expectedFee2, Treasury() - treasuryBefore2);
        Assert.Equal(buyGas2 - expectedFee2, Raised() - raisedBefore2);

        // 3) Disable feeTo (set to zero) -> no fee again.
        Invoke(engine, factory, "setFeeTo", UInt160.Zero);
        Assert.True(ReadFeeTo() == UInt160.Zero, "feeTo should be disabled");

        BigInteger buyGas3 = 15_000_000_000;
        BigInteger raisedBefore3 = Raised();
        BigInteger treasuryBefore3 = Treasury();
        BuyTokens(engine, bondingCurve, tokenHash, Owner, buyGas3);
        Assert.Equal(BigInteger.Zero, Treasury() - treasuryBefore3);
        Assert.Equal(buyGas3, Raised() - raisedBefore3);
    }

    [Fact]
    public void Flow_FeeTo_Sell_Toggle_BeforeAndAfter()
    {
        var (engine, factory, router, bondingCurve) = Setup();
        var gas = engine.Native.GAS.Hash;

        var feeTo = UInt160.Parse("0x000000000000000000000000000000000000000a");
        var tokenHash = SetupSale(engine, factory, router, bondingCurve,
            "TST", (BigInteger)100_000_000_000, (BigInteger)5001);

        // Buy (fee-off) first so we hold some tokens to sell back.
        BuyTokens(engine, bondingCurve, tokenHash, Owner, 20_000_000_000);
        BigInteger totalTokens = Invoke(engine, tokenHash, "balanceOf", Owner).GetInteger();
        Assert.True(totalTokens > 0);

        BigInteger Treasury() => Invoke(engine, gas, "balanceOf", feeTo).GetInteger();
        BigInteger Gross(BigInteger sellAmount)
        {
            var sale = getSale(engine, bondingCurve, tokenHash);
            BigInteger gasReserve = sale.VirtualGAS + sale.GASRaised;
            BigInteger tokenReserve = sale.VirtualToken - sale.TokensSold;
            return gasReserve * sellAmount / (tokenReserve + sellAmount);
        }

        // 1) Sell while feeTo unset -> no fee, user keeps the full curve amount.
        BigInteger sell1 = totalTokens / 3;
        BigInteger gross1 = Gross(sell1);
        Assert.Equal(gross1, Invoke(engine, bondingCurve, "getGASForTokens", tokenHash, sell1).GetInteger());
        BigInteger treasuryBefore1 = Treasury();
        BigInteger gasBefore1 = Invoke(engine, gas, "balanceOf", Owner).GetInteger();
        SellTokens(engine, bondingCurve, tokenHash, Owner, sell1);
        BigInteger gasReceived1 = Invoke(engine, gas, "balanceOf", Owner).GetInteger() - gasBefore1;
        Assert.Equal(BigInteger.Zero, Treasury() - treasuryBefore1);
        Assert.Equal(gross1, gasReceived1);

        // 2) Enable feeTo -> sell charges exactly 0.03% to the treasury.
        Invoke(engine, factory, "setFeeTo", feeTo);
        BigInteger sell2 = totalTokens / 3;
        BigInteger gross2 = Gross(sell2);
        BigInteger fee2 = gross2 * 3 / 10000;
        Assert.Equal(gross2 - fee2, Invoke(engine, bondingCurve, "getGASForTokens", tokenHash, sell2).GetInteger());
        BigInteger treasuryBefore2 = Treasury();
        BigInteger gasBefore2 = Invoke(engine, gas, "balanceOf", Owner).GetInteger();
        SellTokens(engine, bondingCurve, tokenHash, Owner, sell2);
        BigInteger gasReceived2 = Invoke(engine, gas, "balanceOf", Owner).GetInteger() - gasBefore2;
        Assert.Equal(fee2, Treasury() - treasuryBefore2);
        Assert.Equal(gross2 - fee2, gasReceived2);

        // 3) Disable feeTo (set to zero) -> no fee again.
        Invoke(engine, factory, "setFeeTo", UInt160.Zero);
        BigInteger sell3 = Invoke(engine, tokenHash, "balanceOf", Owner).GetInteger();
        BigInteger gross3 = Gross(sell3);
        BigInteger treasuryBefore3 = Treasury();
        BigInteger gasBefore3 = Invoke(engine, gas, "balanceOf", Owner).GetInteger();
        SellTokens(engine, bondingCurve, tokenHash, Owner, sell3);
        BigInteger gasReceived3 = Invoke(engine, gas, "balanceOf", Owner).GetInteger() - gasBefore3;
        Assert.Equal(BigInteger.Zero, Treasury() - treasuryBefore3);
        Assert.Equal(gross3, gasReceived3);
    }

    [Fact]
    public void Flow_Graduation_ExcessGasRefunded()
    {
        var (engine, factory, router, bondingCurve) = Setup();
        var gas = engine.Native.GAS.Hash;

        BigInteger targetRaise = 500_000_000_000;
        var tokenHash = SetupSale(engine, factory, router, bondingCurve,
            "TST", targetRaise, (BigInteger)5001);

        var saleBefore = getSale(engine, bondingCurve, tokenHash);
        BigInteger maxTokens = (BigInteger)100000000000000000 * saleBefore.GraduationBps / 10000;
        BigInteger remainingTokens = maxTokens - saleBefore.TokensSold;

        BigInteger gasBefore = Invoke(engine, gas, "balanceOf", Owner).GetInteger();
        BigInteger gasSent = targetRaise * 10;
        BuyTokens(engine, bondingCurve, tokenHash, Owner, gasSent);

        var gasAfter = Invoke(engine, gas, "balanceOf", Owner).GetInteger();
        BigInteger gasSpent = gasBefore - gasAfter;

        BigInteger gasReserve = saleBefore.VirtualGAS + saleBefore.GASRaised;
        BigInteger tokenReserve = saleBefore.VirtualToken - saleBefore.TokensSold;
        BigInteger expectedActualGas = gasReserve * remainingTokens / (tokenReserve - remainingTokens);
        if (gasReserve * remainingTokens % (tokenReserve - remainingTokens) != 0)
            expectedActualGas += 1;
        if (expectedActualGas > gasSent)
            expectedActualGas = gasSent;

        Assert.Equal(expectedActualGas, gasSpent);
        Assert.Equal(expectedActualGas, gasBefore - gasAfter);

        BigInteger refunded = gasSent - gasSpent;
        Assert.Equal(gasSent, gasSpent + refunded);
        Assert.True(refunded > 0, "Refunded should be greater than 0");

        var saleInfo = getSale(engine, bondingCurve, tokenHash);
        Assert.True(saleInfo.Graduated, "Sale should be graduated");
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

        var saleInitial = getSale(engine, bondingCurve, tokenHash);
        BigInteger idealK = saleInitial.VirtualGAS * saleInitial.VirtualToken;

        BuyTokens(engine, bondingCurve, tokenHash, Owner, 20_000_000_000);
        var saleAfterBuy = getSale(engine, bondingCurve, tokenHash);
        BigInteger actualKAfterBuy = (saleAfterBuy.VirtualGAS + saleAfterBuy.GASRaised) *
                                      (saleAfterBuy.VirtualToken - saleAfterBuy.TokensSold);

        BigInteger tolerance = idealK / 10000 + 1;
        Assert.True(BigInteger.Abs(actualKAfterBuy - idealK) <= tolerance,
            $"K should be approx constant after buy. ideal={idealK}, actual={actualKAfterBuy}");

        var halfTokens = (saleAfterBuy.TokensSold) / 2;
        if (halfTokens > 0)
        {
            SellTokens(engine, bondingCurve, tokenHash, Owner, halfTokens);
            var saleAfterSell = getSale(engine, bondingCurve, tokenHash);
            BigInteger actualKAfterSell = (saleAfterSell.VirtualGAS + saleAfterSell.GASRaised) *
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
        BigInteger rawSupply = 100000000000000000;

        var (ln, lm) = LoadContract("DuoLaunchpad");
        var tokenHash = DeployContract(engine, ln, lm,
            new object[] { bondingCurve, 8, symbol, rawSupply });

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
        map[(BigInteger)0] = (Neo.VM.Types.ByteString)Owner.GetSpan().ToArray();
        map[(BigInteger)1] = new Integer(BigInteger.Zero);
        map[(BigInteger)2] = new Integer(BigInteger.Zero);
        map[(BigInteger)3] = new Integer(targetRaise);
        map[(BigInteger)4] = new Integer(vGas);
        map[(BigInteger)5] = new Integer(vToken);
        map[(BigInteger)6] = new Integer(graduationBps);
        map[(BigInteger)7] = Neo.VM.Types.Boolean.False;

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

    private static SaleInfo getSale(TestEngine engine, UInt160 bondingCurve, UInt160 tokenHash)
    {
        var result = Invoke(engine, bondingCurve, "getSale", tokenHash);
        var map = (Neo.VM.Types.Map)result;
        return new SaleInfo
        {
            TokensSold = map[(BigInteger)1].GetInteger(),
            GASRaised = map[(BigInteger)2].GetInteger(),
            TargetRaise = map[(BigInteger)3].GetInteger(),
            VirtualGAS = map[(BigInteger)4].GetInteger(),
            VirtualToken = map[(BigInteger)5].GetInteger(),
            GraduationBps = map[(BigInteger)6].GetInteger(),
            Graduated = map[(BigInteger)7].GetBoolean()
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
        public BigInteger TokensSold;
        public BigInteger GASRaised;
        public BigInteger TargetRaise;
        public BigInteger VirtualGAS;
        public BigInteger VirtualToken;
        public BigInteger GraduationBps;
        public bool Graduated;
    }
}
