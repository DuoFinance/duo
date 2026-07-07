using System.Numerics;
using Xunit;

namespace DuoBondingCurveTests;

public class DuoBondingCurveFormalTests
{
    #region Math Model (replicates contract logic for formal verification)

    private struct SaleInfoModel
    {
        public BigInteger TotalSupply;
        public BigInteger TokensSold;
        public BigInteger GASRaised;
        public BigInteger TargetRaise;
        public BigInteger VirtualGas;
        public BigInteger VirtualToken;
        public BigInteger GraduationBps;
    }

    private static SaleInfoModel CreateSaleModel(
        BigInteger totalSupply,
        BigInteger targetRaise,
        BigInteger graduationBps)
    {
        BigInteger twoPminusOne = 2 * graduationBps - 10000;
        BigInteger pSquared = graduationBps * graduationBps;
        BigInteger vToken = totalSupply * pSquared / 10000 / twoPminusOne;
        BigInteger vGas = targetRaise * (10000 - graduationBps) / twoPminusOne;

        return new SaleInfoModel
        {
            TotalSupply = totalSupply,
            TokensSold = 0,
            GASRaised = 0,
            TargetRaise = targetRaise,
            VirtualGas = vGas,
            VirtualToken = vToken,
            GraduationBps = graduationBps
        };
    }

    private static BigInteger GetAmountOut(SaleInfoModel sale, BigInteger gasAmount)
    {
        BigInteger tokenReserve = sale.VirtualToken - sale.TokensSold;
        if (tokenReserve <= 0) return 0;

        BigInteger gasReserve = sale.VirtualGas + sale.GASRaised;
        BigInteger k = sale.VirtualGas * sale.VirtualToken;

        BigInteger newGasReserve = gasReserve + gasAmount;
        BigInteger newTokenReserve = k / newGasReserve;
        BigInteger tokensOut = tokenReserve - newTokenReserve;

        BigInteger maxTokens = sale.TotalSupply * sale.GraduationBps / 10000;
        BigInteger remaining = maxTokens - sale.TokensSold;
        return tokensOut > remaining ? remaining : tokensOut;
    }

    private static BigInteger GetAmountIn(SaleInfoModel sale, BigInteger tokenAmount)
    {
        BigInteger tokenReserve = sale.VirtualToken - sale.TokensSold;
        if (tokenReserve <= 0 || tokenAmount > tokenReserve) return 0;

        BigInteger gasReserve = sale.VirtualGas + sale.GASRaised;
        BigInteger k = sale.VirtualGas * sale.VirtualToken;

        BigInteger newTokenReserve = tokenReserve - tokenAmount;
        BigInteger newGasReserve = k / newTokenReserve;
        return newGasReserve - gasReserve;
    }

    private static BigInteger GetSellAmount(SaleInfoModel sale, BigInteger tokenAmount)
    {
        BigInteger tokenReserve = sale.VirtualToken - sale.TokensSold;
        BigInteger gasReserve = sale.VirtualGas + sale.GASRaised;
        BigInteger k = sale.VirtualGas * sale.VirtualToken;

        BigInteger newTokenReserve = tokenReserve + tokenAmount;
        BigInteger newGasReserve = k / newTokenReserve;
        return gasReserve - newGasReserve;
    }

    private static BigInteger GetCurrentPrice(SaleInfoModel sale)
    {
        BigInteger gasReserve = sale.VirtualGas + sale.GASRaised;
        BigInteger tokenReserve = sale.VirtualToken - sale.TokensSold;
        return gasReserve * 100_000_000 / tokenReserve;
    }

    private static SaleInfoModel ApplyBuy(SaleInfoModel sale, BigInteger gasAmount)
    {
        BigInteger tokens = GetAmountOut(sale, gasAmount);
        BigInteger maxTokens = sale.TotalSupply * sale.GraduationBps / 10000;
        BigInteger remaining = maxTokens - sale.TokensSold;
        BigInteger actualGas = gasAmount;
        if (tokens >= remaining)
        {
            tokens = remaining;
            actualGas = GetAmountIn(sale, tokens);
        }
        sale.TokensSold += tokens;
        sale.GASRaised += actualGas;
        return sale;
    }

    private static SaleInfoModel ApplySell(SaleInfoModel sale, BigInteger tokenAmount)
    {
        BigInteger gasReturn = GetSellAmount(sale, tokenAmount);
        sale.TokensSold -= tokenAmount;
        sale.GASRaised -= gasReturn;
        return sale;
    }

    #endregion

    #region Static Analysis & Formal Verification

    [Fact]
    public void Formal_KConstant_BuyApproximatelyPreservesK()
    {
        var sale = CreateSaleModel(
            totalSupply: 1_000_000_000,
            targetRaise: 10_000_000_000,
            graduationBps: 6500);

        BigInteger idealK = sale.VirtualGas * sale.VirtualToken;

        var after = ApplyBuy(sale, 100_000_000);

        BigInteger actualK = (after.VirtualGas + after.GASRaised) * (after.VirtualToken - after.TokensSold);

        BigInteger tolerance = idealK / 10000;
        Assert.True(BigInteger.Abs(actualK - idealK) <= tolerance,
            $"k should remain approximately constant. ideal={idealK}, actual={actualK}, diff={idealK - actualK}");
    }

    [Fact]
    public void Formal_KConstant_SellApproximatelyPreservesK()
    {
        var sale = CreateSaleModel(
            totalSupply: 1_000_000_000,
            targetRaise: 10_000_000_000,
            graduationBps: 6500);

        sale = ApplyBuy(sale, 5_000_000_000);
        BigInteger idealK = sale.VirtualGas * sale.VirtualToken;

        BigInteger sellAmount = sale.TokensSold / 2;
        var after = ApplySell(sale, sellAmount);

        BigInteger actualK = (after.VirtualGas + after.GASRaised) * (after.VirtualToken - after.TokensSold);

        BigInteger tolerance = idealK / 10000;
        Assert.True(BigInteger.Abs(actualK - idealK) <= tolerance,
            $"k should remain approximately constant. ideal={idealK}, actual={actualK}, diff={idealK - actualK}");
    }

    [Fact]
    public void Formal_PriceMonotonic_BuyIncreasesPrice()
    {
        var sale = CreateSaleModel(
            totalSupply: 1_000_000_000,
            targetRaise: 10_000_000_000,
            graduationBps: 6500);

        BigInteger priceBefore = GetCurrentPrice(sale);
        var after = ApplyBuy(sale, 100_000_000);
        BigInteger priceAfter = GetCurrentPrice(after);

        Assert.True(priceAfter >= priceBefore,
            $"Price must increase after buy. priceBefore={priceBefore}, priceAfter={priceAfter}");
    }

    [Fact]
    public void Formal_PriceMonotonic_SellDecreasesPrice()
    {
        var sale = CreateSaleModel(
            totalSupply: 1_000_000_000,
            targetRaise: 10_000_000_000,
            graduationBps: 6500);

        sale = ApplyBuy(sale, 5_000_000_000);
        BigInteger priceBefore = GetCurrentPrice(sale);

        BigInteger sellAmount = sale.TokensSold / 2;
        var after = ApplySell(sale, sellAmount);
        BigInteger priceAfter = GetCurrentPrice(after);

        Assert.True(priceAfter <= priceBefore,
            $"Price must decrease after sell. priceBefore={priceBefore}, priceAfter={priceAfter}");
    }

    [Fact]
    public void Formal_BuySellRoundTrip_LessThanOrEqual()
    {
        var sale = CreateSaleModel(
            totalSupply: 1_000_000_000,
            targetRaise: 10_000_000_000,
            graduationBps: 6500);

        BigInteger gasIn = 1_000_000_000;
        var afterBuy = ApplyBuy(sale, gasIn);
        BigInteger tokensBought = afterBuy.TokensSold;

        var afterSell = ApplySell(afterBuy, tokensBought);
        BigInteger gasOut = gasIn - (sale.GASRaised - afterSell.GASRaised + gasIn);

        BigInteger netGas = afterSell.GASRaised - sale.GASRaised;

        Assert.True(netGas >= 0,
            $"Round-trip must not remove GAS from the pool. netGas change={netGas}");
    }

    [Fact]
    public void Formal_MarginalPrice_EqualsReserveRatio()
    {
        var sale = CreateSaleModel(
            totalSupply: 1_000_000_000,
            targetRaise: 10_000_000_000,
            graduationBps: 6500);

        sale = ApplyBuy(sale, 2_000_000_000);

        BigInteger gasReserve = sale.VirtualGas + sale.GASRaised;
        BigInteger tokenReserve = sale.VirtualToken - sale.TokensSold;
        BigInteger price = GetCurrentPrice(sale);
        BigInteger expectedPrice = gasReserve * 100_000_000 / tokenReserve;

        Assert.Equal(expectedPrice, price);
    }

    [Fact]
    public void Formal_GraduationCap_CannotExceedMaxTokens()
    {
        var sale = CreateSaleModel(
            totalSupply: 1_000_000_000,
            targetRaise: 10_000_000_000,
            graduationBps: 6500);

        BigInteger maxTokens = sale.TotalSupply * sale.GraduationBps / 10000;

        var after = ApplyBuy(sale, 100_000_000_000_000);

        Assert.True(after.TokensSold <= maxTokens,
            $"TokensSold must not exceed maxTokens. actual={after.TokensSold}, max={maxTokens}");
    }

    [Fact]
    public void Formal_GetAmountIn_GetAmountOut_Consistency()
    {
        var sale = CreateSaleModel(
            totalSupply: 1_000_000_000,
            targetRaise: 10_000_000_000,
            graduationBps: 6500);

        sale = ApplyBuy(sale, 1_000_000_000);

        BigInteger testGas = 500_000_000;
        BigInteger tokensOut = GetAmountOut(sale, testGas);

        if (tokensOut > 0)
        {
            BigInteger gasIn = GetAmountIn(sale, tokensOut);
            BigInteger tolerance = testGas / 10000 + 10;
            Assert.True(BigInteger.Abs(gasIn - testGas) <= tolerance,
                $"GetAmountIn(tokensOut) ~ testGas. gasIn={gasIn}, testGas={testGas}, diff={gasIn - testGas}");
        }
    }

    [Fact]
    public void Formal_VirtualReserves_PriceContinuityAtGraduation()
    {
        BigInteger totalSupply = 1_000_000_000;
        BigInteger targetRaise = 10_000_000_000;
        BigInteger graduationBps = 6500;

        var sale = CreateSaleModel(totalSupply, targetRaise, graduationBps);

        BigInteger maxTokens = totalSupply * graduationBps / 10000;
        BigInteger remaining = maxTokens;
        BigInteger gasNeeded = GetAmountIn(sale, remaining);

        var graduated = ApplyBuy(sale, gasNeeded * 2);

        BigInteger remainingTokens = graduated.TotalSupply - graduated.TokensSold;
        BigInteger gasForLiquidity = graduated.GASRaised;

        BigInteger ammPrice = gasForLiquidity * 100_000_000 / remainingTokens;
        BigInteger curvePrice = GetCurrentPrice(graduated);

        BigInteger tolerance = ammPrice / 1000;
        Assert.True(BigInteger.Abs(ammPrice - curvePrice) <= tolerance,
            $"Price continuity at graduation. AMM price={ammPrice}, curve price={curvePrice}, diff={ammPrice - curvePrice}");
    }

    [Fact]
    public void Formal_TwoPminusOne_PositiveForValidGraduation()
    {
        for (int bps = 5001; bps <= 8000; bps += 100)
        {
            BigInteger twoPminusOne = 2 * bps - 10000;
            Assert.True(twoPminusOne > 0,
                $"2*P - 10000 must be positive for P={bps}. Got {twoPminusOne}");
        }
    }

    [Fact]
    public void Formal_TwoPminusOne_ZeroAt5000()
    {
        BigInteger twoPminusOne = 2 * 5000 - 10000;
        Assert.Equal(BigInteger.Zero, twoPminusOne);
    }

    [Fact]
    public void Formal_VirtualToken_Positive()
    {
        for (int bps = 5001; bps <= 8000; bps += 500)
        {
            var sale = CreateSaleModel(1_000_000_000, 10_000_000_000, bps);
            Assert.True(sale.VirtualToken > 0,
                $"VirtualToken must be positive for graduationBps={bps}. Got {sale.VirtualToken}");
            Assert.True(sale.VirtualGas >= 0,
                $"VirtualGas must be non-negative for graduationBps={bps}. Got {sale.VirtualGas}");
        }
    }

    [Fact]
    public void Formal_VirtualToken_GreaterThanTotalSupply()
    {
        for (int bps = 5001; bps <= 8000; bps += 500)
        {
            var sale = CreateSaleModel(1_000_000_000, 10_000_000_000, bps);
            BigInteger maxTokens = sale.TotalSupply * bps / 10000;
            Assert.True(sale.VirtualToken > maxTokens,
                $"VirtualToken must exceed maxTokens for P={bps}. vToken={sale.VirtualToken}, maxTokens={maxTokens}");
        }
    }

    [Fact]
    public void Formal_GasRaised_NeverExceedsTargetBeforeGraduation()
    {
        var sale = CreateSaleModel(
            totalSupply: 1_000_000_000,
            targetRaise: 10_000_000_000,
            graduationBps: 6500);

        var after = ApplyBuy(sale, 100_000_000_000);

        Assert.True(after.GASRaised <= after.TargetRaise * 101 / 100,
            $"GASRaised should approximately reach target. raised={after.GASRaised}, target={after.TargetRaise}");
    }

    #endregion

    #region Fuzz Tests

    [Fact]
    public void Fuzz_KConstant_RandomBuys()
    {
        var rng = new Random(42);
        for (int i = 0; i < 100; i++)
        {
            long totalSupply = rng.NextInt64(1_000_000, 10_000_000_000_000);
            long targetRaise = rng.NextInt64(1_000_000_000, 100_000_000_000);
            int graduationBps = rng.Next(5001, 8001);

            var sale = CreateSaleModel(totalSupply, targetRaise, graduationBps);
            BigInteger idealK = sale.VirtualGas * sale.VirtualToken;

            long gasAmount = rng.NextInt64(1_000_000, 1_000_000_000_000);
            var after = ApplyBuy(sale, gasAmount);

            BigInteger actualK = (after.VirtualGas + after.GASRaised) * (after.VirtualToken - after.TokensSold);

            BigInteger tolerance = idealK / 1000 + 1;
            Assert.True(BigInteger.Abs(actualK - idealK) <= tolerance,
                $"Fuzz fail: k deviates too much. iter={i}, ideal={idealK}, actual={actualK}, diff={idealK - actualK}");
        }
    }

    [Fact]
    public void Fuzz_PriceMonotonic_RandomBuySell()
    {
        var rng = new Random(123);
        for (int i = 0; i < 100; i++)
        {
            long totalSupply = rng.NextInt64(10_000_000, 1_000_000_000_000);
            long targetRaise = rng.NextInt64(1_000_000_000, 10_000_000_000_000);
            int graduationBps = rng.Next(5001, 8000);

            var sale = CreateSaleModel(totalSupply, targetRaise, graduationBps);

            long buyGas = rng.NextInt64(10_000_000, 1_000_000_000_000);
            var afterBuy = ApplyBuy(sale, buyGas);
            BigInteger priceAfterBuy = GetCurrentPrice(afterBuy);

            BigInteger sellAmount = afterBuy.TokensSold > 1 ? afterBuy.TokensSold / 2 : 0;
            if (sellAmount > 0)
            {
                var afterSell = ApplySell(afterBuy, sellAmount);
                BigInteger priceAfterSell = GetCurrentPrice(afterSell);

                Assert.True(priceAfterSell <= priceAfterBuy,
                    $"Fuzz fail: price increased after sell. iter={i}, buyPrice={priceAfterBuy}, sellPrice={priceAfterSell}");
            }
        }
    }

    [Fact]
    public void Fuzz_AmountInAmountOut_Consistency()
    {
        var rng = new Random(456);
        for (int i = 0; i < 100; i++)
        {
            long totalSupply = rng.NextInt64(10_000_000, 1_000_000_000_000);
            long targetRaise = rng.NextInt64(1_000_000_000, 10_000_000_000_000);
            int graduationBps = rng.Next(5001, 8000);

            var sale = CreateSaleModel(totalSupply, targetRaise, graduationBps);

            long preBuyGas = rng.NextInt64(100_000_000, 1_000_000_000_000);
            sale = ApplyBuy(sale, preBuyGas);

            long testGas = rng.NextInt64(1_000_000, 100_000_000_000);
            BigInteger tokensOut = GetAmountOut(sale, testGas);

            if (tokensOut > 0)
            {
                BigInteger gasIn = GetAmountIn(sale, tokensOut);
                BigInteger tolerance = testGas / 1000 + 10;
                Assert.True(BigInteger.Abs(gasIn - testGas) <= tolerance,
                    $"Fuzz fail: GetAmountIn deviates too much. iter={i}, gasIn={gasIn}, testGas={testGas}, diff={gasIn - testGas}");
            }
        }
    }

    [Fact]
    public void Fuzz_TokensSoldNeverExceedsMax()
    {
        var rng = new Random(789);
        for (int i = 0; i < 100; i++)
        {
            long totalSupply = rng.NextInt64(1_000_000, 10_000_000_000_000);
            long targetRaise = rng.NextInt64(100_000_000, 100_000_000_000_000);
            int graduationBps = rng.Next(5000, 8001);

            var sale = CreateSaleModel(totalSupply, targetRaise, graduationBps);
            BigInteger maxTokens = sale.TotalSupply * graduationBps / 10000;

            long hugeGas = rng.NextInt64(1_000_000_000_000, 1_000_000_000_000_000_000);
            var after = ApplyBuy(sale, hugeGas);

            Assert.True(after.TokensSold <= maxTokens,
                $"Fuzz fail: tokens exceed max. iter={i}, sold={after.TokensSold}, max={maxTokens}");
        }
    }

    [Fact]
    public void Fuzz_MultipleBuys_MonotonicPrice()
    {
        var rng = new Random(101112);
        for (int i = 0; i < 50; i++)
        {
            long totalSupply = rng.NextInt64(10_000_000, 1_000_000_000_000);
            long targetRaise = rng.NextInt64(1_000_000_000, 10_000_000_000_000);
            int graduationBps = rng.Next(5001, 8000);

            var sale = CreateSaleModel(totalSupply, targetRaise, graduationBps);
            BigInteger prevPrice = GetCurrentPrice(sale);

            for (int j = 0; j < 10; j++)
            {
                long gasAmount = rng.NextInt64(10_000_000, 100_000_000_000);
                sale = ApplyBuy(sale, gasAmount);
                BigInteger newPrice = GetCurrentPrice(sale);

                Assert.True(newPrice >= prevPrice,
                    $"Fuzz fail: price decreased at iter {i}/{j}. prev={prevPrice}, new={newPrice}");
                prevPrice = newPrice;
            }
        }
    }

    [Fact]
    public void Fuzz_SellNeverExceedsSold()
    {
        var rng = new Random(131415);
        for (int i = 0; i < 100; i++)
        {
            long totalSupply = rng.NextInt64(10_000_000, 1_000_000_000_000);
            long targetRaise = rng.NextInt64(1_000_000_000, 10_000_000_000_000);
            int graduationBps = rng.Next(5001, 8000);

            var sale = CreateSaleModel(totalSupply, targetRaise, graduationBps);

            long buyGas = rng.NextInt64(100_000_000, 5_000_000_000_000);
            sale = ApplyBuy(sale, buyGas);

            BigInteger tokensSold = sale.TokensSold;
            BigInteger gasRaised = sale.GASRaised;

            if (tokensSold > 0)
            {
                BigInteger sellAmount = rng.NextInt64(1, (long)(tokensSold + 1));
                if (sellAmount > tokensSold) sellAmount = tokensSold;

                var afterSell = ApplySell(sale, sellAmount);

                Assert.True(afterSell.TokensSold >= 0,
                    $"Fuzz fail: negative tokens sold. iter={i}, sold={afterSell.TokensSold}");
                Assert.True(afterSell.GASRaised >= 0,
                    $"Fuzz fail: negative GAS raised. iter={i}, raised={afterSell.GASRaised}");
                Assert.True(afterSell.GASRaised <= gasRaised,
                    $"Fuzz fail: GAS increased after sell. iter={i}, before={gasRaised}, after={afterSell.GASRaised}");
            }
        }
    }

    [Fact]
    public void Fuzz_VirtualReserves_AlwaysValid()
    {
        var rng = new Random(161718);
        for (int i = 0; i < 200; i++)
        {
            long totalSupply = rng.NextInt64(1_000_000, 10_000_000_000_000);
            long targetRaise = rng.NextInt64(10_000_000, 100_000_000_000_000);
            int graduationBps = rng.Next(5001, 8001);

            var sale = CreateSaleModel(totalSupply, targetRaise, graduationBps);

            Assert.True(sale.VirtualToken > 0,
                $"Fuzz fail: VirtualToken <= 0. iter={i}, vToken={sale.VirtualToken}");
            Assert.True(sale.VirtualGas >= 0,
                $"Fuzz fail: VirtualGas < 0. iter={i}, vGas={sale.VirtualGas}");

            BigInteger maxTokens = sale.TotalSupply * graduationBps / 10000;
            BigInteger initialTokenReserve = sale.VirtualToken;
            Assert.True(initialTokenReserve > maxTokens,
                $"Fuzz fail: initial token reserve <= maxTokens. iter={i}, reserve={initialTokenReserve}, max={maxTokens}");
        }
    }

    [Fact]
    public void Fuzz_GetSellAmount_NeverExceedsGASRaised()
    {
        var rng = new Random(192021);
        for (int i = 0; i < 100; i++)
        {
            long totalSupply = rng.NextInt64(10_000_000, 1_000_000_000_000);
            long targetRaise = rng.NextInt64(1_000_000_000, 10_000_000_000_000);
            int graduationBps = rng.Next(5001, 8000);

            var sale = CreateSaleModel(totalSupply, targetRaise, graduationBps);

            long buyGas = rng.NextInt64(100_000_000, 5_000_000_000_000);
            sale = ApplyBuy(sale, buyGas);

            if (sale.TokensSold > 0)
            {
                BigInteger sellAmount = rng.NextInt64(1, (long)(sale.TokensSold + 1));
                if (sellAmount > sale.TokensSold) sellAmount = sale.TokensSold;

                BigInteger gasReturn = GetSellAmount(sale, sellAmount);

                Assert.True(gasReturn <= sale.GASRaised,
                    $"Fuzz fail: sell returns more than raised. iter={i}, return={gasReturn}, raised={sale.GASRaised}");
                Assert.True(gasReturn >= 0,
                    $"Fuzz fail: negative gas return. iter={i}, return={gasReturn}");
            }
        }
    }

    #endregion

    #region Boundary & Edge Case Tests

    [Fact]
    public void Edge_BuyZeroAmount_ReturnsZero()
    {
        var sale = CreateSaleModel(1_000_000_000, 10_000_000_000, 6500);
        BigInteger tokens = GetAmountOut(sale, 0);
        Assert.Equal(BigInteger.Zero, tokens);
    }

    [Fact]
    public void Edge_SellZeroAmount_ReturnsNearZero()
    {
        var sale = CreateSaleModel(1_000_000_000, 10_000_000_000, 6500);
        sale = ApplyBuy(sale, 1_000_000_000);
        BigInteger gasReturn = GetSellAmount(sale, 0);
        Assert.True(BigInteger.Abs(gasReturn) < 10,
            $"Sell zero amount should return near zero. Got {gasReturn}");
    }

    [Fact]
    public void Edge_GraduationBps5001_Valid()
    {
        var sale = CreateSaleModel(1_000_000_000, 10_000_000_000, 5001);
        BigInteger twoPminusOne = 2 * 5001 - 10000;
        Assert.True(twoPminusOne > 0);
        Assert.True(sale.VirtualToken > 0);
    }

    [Fact]
    public void Edge_GraduationBps8000_Valid()
    {
        var sale = CreateSaleModel(1_000_000_000, 10_000_000_000, 8000);
        BigInteger twoPminusOne = 2 * 8000 - 10000;
        Assert.True(twoPminusOne > 0);
    }

    [Fact]
    public void Edge_SmallSupply_Valid()
    {
        var sale = CreateSaleModel(1_000, 10_000_000_000, 6500);
        Assert.True(sale.VirtualToken > 0);
    }

    [Fact]
    public void Edge_SmallTarget_Valid()
    {
        var sale = CreateSaleModel(1_000_000_000, 10_000, 6500);
        Assert.True(sale.VirtualGas >= 0);
    }

    [Fact]
    public void Edge_TokenReserveZero_GetAmountOutZero()
    {
        var sale = CreateSaleModel(1_000_000_000, 10_000_000_000, 6500);
        sale.TokensSold = sale.VirtualToken;
        BigInteger tokens = GetAmountOut(sale, 1_000_000_000);
        Assert.Equal(BigInteger.Zero, tokens);
    }

    [Fact]
    public void Edge_GetAmountIn_MoreThanReserve_ReturnsZero()
    {
        var sale = CreateSaleModel(1_000_000_000, 10_000_000_000, 6500);
        BigInteger tooMuch = sale.VirtualToken * 2;
        BigInteger gas = GetAmountIn(sale, tooMuch);
        Assert.Equal(BigInteger.Zero, gas);
    }

    #endregion

    #region Numerical Precision Tests

    [Fact]
    public void Precision_KConstant_RemainsWithinTolerance()
    {
        var sale = CreateSaleModel(1_000_000_000, 10_000_000_000, 6500);

        BigInteger initialK = sale.VirtualGas * sale.VirtualToken;

        for (int i = 0; i < 10; i++)
        {
            sale = ApplyBuy(sale, 100_000_000);
        }

        BigInteger finalK = (sale.VirtualGas + sale.GASRaised) * (sale.VirtualToken - sale.TokensSold);

        BigInteger tolerance = initialK / 10000;
        Assert.True(BigInteger.Abs(finalK - initialK) <= tolerance,
            $"k should remain within tolerance. initial={initialK}, final={finalK}, diff={initialK - finalK}");
    }

    [Fact]
    public void Precision_Slippage_BuyLessThanMarginalPrice()
    {
        var sale = CreateSaleModel(1_000_000_000, 10_000_000_000, 6500);

        BigInteger priceBefore = GetCurrentPrice(sale);
        BigInteger gasAmount = 100_000_000;
        BigInteger tokensOut = GetAmountOut(sale, gasAmount);
        BigInteger avgPrice = gasAmount * 100_000_000 / tokensOut;

        Assert.True(avgPrice >= priceBefore,
            $"Average buy price must >= initial marginal price. initial={priceBefore}, avg={avgPrice}");
    }

    [Fact]
    public void Precision_Slippage_SellLessThanMarginalPrice()
    {
        var sale = CreateSaleModel(1_000_000_000, 10_000_000_000, 6500);
        sale = ApplyBuy(sale, 5_000_000_000);

        BigInteger priceBefore = GetCurrentPrice(sale);
        BigInteger sellAmount = sale.TokensSold / 2;
        BigInteger gasOut = GetSellAmount(sale, sellAmount);
        BigInteger avgPrice = gasOut * 100_000_000 / sellAmount;

        Assert.True(avgPrice <= priceBefore,
            $"Average sell price must <= marginal price. marginal={priceBefore}, avg={avgPrice}");
    }

    #endregion
}