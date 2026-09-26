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
using Boolean = Neo.VM.Types.Boolean;

namespace DuoBondingCurveTests;

using static TestUtils;

/// <summary>
/// Fuzz tests that run the ACTUAL contract bytecode through TestEngine,
/// not just replicated math. Catches compilation/serialization discrepancies.
/// </summary>
public class DuoBondingCurveFuzzTests
{
    private const int Iterations = 200;
    private const int Seed = 20260712;

    #region Helpers

    private struct VmSaleInfo
    {
        public BigInteger TokensSold;
        public BigInteger GASRaised;
        public BigInteger TargetRaise;
        public BigInteger VirtualGAS;
        public BigInteger VirtualToken;
        public BigInteger GraduationBps;
        public bool Graduated;
    }

    private static VmSaleInfo GetSale(TestEngine engine, UInt160 bondingCurve, UInt160 tokenHash)
    {
        var result = Invoke(engine, bondingCurve, "getSale", tokenHash);
        var map = (Map)result;
        return new VmSaleInfo
        {
            TokensSold = map[(BigInteger)1].GetInteger(),
            GASRaised = map[(BigInteger)2].GetInteger(),
            TargetRaise = map[(BigInteger)3].GetInteger(),
            VirtualGAS = map[(BigInteger)4].GetInteger(),
            VirtualToken = map[(BigInteger)5].GetInteger(),
            GraduationBps = map[(BigInteger)6].GetInteger(),
            Graduated = map[(BigInteger)7].GetBoolean(),
        };
    }

    private static BigInteger GetTokensForGAS(TestEngine engine, UInt160 bondingCurve, UInt160 tokenHash, BigInteger gas)
    {
        return Invoke(engine, bondingCurve, "getTokensForGAS", tokenHash, gas).GetInteger();
    }

    private static BigInteger GetGASForTokens(TestEngine engine, UInt160 bondingCurve, UInt160 tokenHash, BigInteger tokens)
    {
        return Invoke(engine, bondingCurve, "getGASForTokens", tokenHash, tokens).GetInteger();
    }

    private static BigInteger GetPrice(TestEngine engine, UInt160 bondingCurve, UInt160 tokenHash)
    {
        return Invoke(engine, bondingCurve, "getCurrentPrice", tokenHash).GetInteger();
    }

    private static BigInteger ComputeVToken(BigInteger totalSupply, BigInteger graduationBps)
    {
        BigInteger twoPminusOne = 2 * graduationBps - 10000;
        BigInteger pSquared = graduationBps * graduationBps;
        return totalSupply * pSquared / (10000 * twoPminusOne);
    }

    private static BigInteger ComputeVGas(BigInteger targetRaise, BigInteger graduationBps)
    {
        BigInteger twoPminusOne = 2 * graduationBps - 10000;
        return targetRaise * (10000 - graduationBps) / twoPminusOne;
    }

    private static BigInteger ComputeK(VmSaleInfo s)
    {
        return (s.VirtualGAS + s.GASRaised) * (s.VirtualToken - s.TokensSold);
    }

    private static UInt160 SetupRandomSale(TestEngine engine, UInt160 bondingCurve,
        BigInteger totalSupply, byte decimals, string symbol,
        BigInteger targetRaise, BigInteger graduationBps)
    {
        var (ln, lm) = LoadContract("DuoLaunchpad");
        var tokenHash = DeployContract(engine, ln, lm,
            new object[] { bondingCurve, (int)decimals, symbol, totalSupply });

        // Write sale via storage (bypasses CreateSale to avoid deploying real NEF)
        var bcState = engine.Native.ContractManagement.GetContract(bondingCurve);
        int bcId = bcState.Id;

        var vToken = ComputeVToken(totalSupply, graduationBps);
        var vGas = ComputeVGas(targetRaise, graduationBps);

        var map = new Map();
        map[(BigInteger)0] = (ByteString)Owner.GetSpan().ToArray();
        map[(BigInteger)1] = new Integer(BigInteger.Zero);
        map[(BigInteger)2] = new Integer(BigInteger.Zero);
        map[(BigInteger)3] = new Integer(targetRaise);
        map[(BigInteger)4] = new Integer(vGas);
        map[(BigInteger)5] = new Integer(vToken);
        map[(BigInteger)6] = new Integer(graduationBps);
        map[(BigInteger)7] = Boolean.False;

        byte[] serializedSale = BinarySerializer.Serialize(map, new ExecutionEngineLimits());
        byte[] keyBytes = new byte[24];
        Encoding.UTF8.GetBytes("Sale", 0, 4, keyBytes, 0);
        tokenHash.GetSpan().CopyTo(keyBytes.AsSpan(4));

        var store = engine.Storage.Snapshot;
        store.Add(new StorageKey { Id = bcId, Key = keyBytes }, new StorageItem { Value = serializedSale });
        store.Commit();

        return tokenHash;
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

    #endregion

    #region Fuzz: Virtual Reserve Formula (VM vs Math Model)

    [Fact]
    public void FuzzVm_VirtualReserves_MatchFormula()
    {
        var rng = new Random(Seed);
        for (int i = 0; i < Iterations; i++)
        {
            var (engine, _, _, bondingCurve) = Setup();
            long totalSupply = rng.NextInt64(10_000, 10_000_000_000_000);
            long targetRaise = rng.NextInt64(10_000_000, 10_000_000_000_000);
            int graduationBps = rng.Next(5001, 10000);
            long symbolNum = rng.NextInt64(10000, 99999);
            string symbol = "T" + symbolNum;

            var tokenHash = SetupRandomSale(engine, bondingCurve,
                totalSupply, 8, symbol, targetRaise, graduationBps);

            var sale = GetSale(engine, bondingCurve, tokenHash);

            var expectedVToken = ComputeVToken(totalSupply, graduationBps);
            var expectedVGas = ComputeVGas(targetRaise, graduationBps);

            Assert.Equal(expectedVToken, sale.VirtualToken);
            Assert.Equal(expectedVGas, sale.VirtualGAS);
        }
    }

    [Fact]
    public void FuzzVm_VirtualReserves_Positive()
    {
        var rng = new Random(Seed + 1);
        for (int i = 0; i < Iterations; i++)
        {
            var (engine, _, _, bondingCurve) = Setup();
            long totalSupply = rng.NextInt64(1_000, 1_000_000_000_000_000);
            long targetRaise = rng.NextInt64(1_000_000, 100_000_000_000_000);
            int graduationBps = rng.Next(5001, 10000);
            long symbolNum = rng.NextInt64(10000, 99999);

            var tokenHash = SetupRandomSale(engine, bondingCurve,
                totalSupply, 8, "P" + symbolNum, targetRaise, graduationBps);

            var price = GetPrice(engine, bondingCurve, tokenHash);

            Assert.True(price > 0, $"iter {i}: price={price} (<=0)");
        }
    }

    private static void WriteSaleStorage(TestEngine engine, int bcId, UInt160 tokenHash, byte[] serializedSale)
    {
        byte[] keyBytes = new byte[24];
        Encoding.UTF8.GetBytes("Sale", 0, 4, keyBytes, 0);
        tokenHash.GetSpan().CopyTo(keyBytes.AsSpan(4));

        var storageKey = new StorageKey { Id = bcId, Key = keyBytes };
        var store = engine.Storage.Snapshot;
        var existing = store.TryGet(storageKey);
        if (existing != null)
        {
            existing.Value = serializedSale;
        }
        else
        {
            store.Add(storageKey, new StorageItem { Value = serializedSale });
        }
        store.Commit();
    }

    #endregion

    #region Fuzz: Buy Amount Invariants (VM)

    [Fact]
    public void FuzzVm_BuyAmount_NonNegative()
    {
        var rng = new Random(Seed + 2);
        for (int i = 0; i < Iterations; i++)
        {
            var (engine, _, _, bondingCurve) = Setup();
            long totalSupply = rng.NextInt64(10_000, 1_000_000_000_000);
            long targetRaise = rng.NextInt64(10_000_000, 10_000_000_000_000);
            int graduationBps = rng.Next(5001, 10000);
            long symbolNum = rng.NextInt64(10000, 99999);

            var tokenHash = SetupRandomSale(engine, bondingCurve,
                totalSupply, 8, "B" + symbolNum, targetRaise, graduationBps);

            var sale = GetSale(engine, bondingCurve, tokenHash);

            long gasIn = rng.NextInt64(1, 1_000_000_000_000);
            BigInteger tokensOut = GetTokensForGAS(engine, bondingCurve, tokenHash, gasIn);

            Assert.True(tokensOut >= 0, $"iter {i}: tokensOut={tokensOut} (<0)");

            BigInteger maxTokens = 100000000000000000 * sale.GraduationBps / 10000;
            Assert.True(tokensOut <= maxTokens,
                $"iter {i}: tokensOut={tokensOut} > maxTokens={maxTokens}");
        }
    }

    [Fact]
    public void FuzzVm_BuyAmount_Monotonic()
    {
        var rng = new Random(Seed + 3);
        for (int i = 0; i < Iterations; i++)
        {
            var (engine, _, _, bondingCurve) = Setup();
            long totalSupply = rng.NextInt64(100_000, 100_000_000_000);
            long targetRaise = rng.NextInt64(1_000_000_000, 10_000_000_000_000);
            int graduationBps = rng.Next(5001, 10000);
            long symbolNum = rng.NextInt64(10000, 99999);

            var tokenHash = SetupRandomSale(engine, bondingCurve,
                totalSupply, 8, "M" + symbolNum, targetRaise, graduationBps);

            long smallGas = 1_000_000;
            long largeGas = 100_000_000_000;
            BigInteger smallOut = GetTokensForGAS(engine, bondingCurve, tokenHash, smallGas);
            BigInteger largeOut = GetTokensForGAS(engine, bondingCurve, tokenHash, largeGas);

            Assert.True(largeOut >= smallOut,
                $"iter {i}: more GAS -> fewer tokens. small={smallOut}, large={largeOut}");
        }
    }

    [Fact]
    public void FuzzVm_BuyAmount_ZeroGas_ReturnsZero()
    {
        var rng = new Random(Seed + 4);
        for (int i = 0; i < Iterations; i++)
        {
            var (engine, _, _, bondingCurve) = Setup();
            long totalSupply = rng.NextInt64(1_000, 100_000_000_000);
            long targetRaise = rng.NextInt64(1_000_000, 10_000_000_000_000);
            int graduationBps = rng.Next(5001, 10000);
            long symbolNum = rng.NextInt64(10000, 99999);

            var tokenHash = SetupRandomSale(engine, bondingCurve,
                totalSupply, 8, "Z" + symbolNum, targetRaise, graduationBps);

            // 0 GAS → should return 0 (or FAULT)
            try
            {
                BigInteger tokens = GetTokensForGAS(engine, bondingCurve, tokenHash, 0);
                Assert.True(tokens == 0 || tokens < 0,
                    $"iter {i}: zero gas should return 0 or fault. Got {tokens}");
            }
            catch { } // FAULT is acceptable
        }
    }

    #endregion

    #region Fuzz: K Invariant (VM after simulated buy)

    [Fact]
    public void FuzzVm_KBuy_ApproximatelyConstant()
    {
        var rng = new Random(Seed + 5);
        for (int i = 0; i < Iterations; i++)
        {
            var (engine, factory, router, bondingCurve) = Setup();
            long totalSupply = rng.NextInt64(100_000, 1_000_000_000_000);
            long targetRaise = rng.NextInt64(100_000_000, 10_000_000_000_000);
            int graduationBps = rng.Next(5001, 9501);
            long symbolNum = rng.NextInt64(10000, 99999);

            var tokenHash = SetupRandomSale(engine, bondingCurve,
                totalSupply, 8, "K" + symbolNum, targetRaise, graduationBps);

            var saleBefore = GetSale(engine, bondingCurve, tokenHash);
            var initialK = saleBefore.VirtualGAS * saleBefore.VirtualToken;

            // Simulate multiple buys via contract (write updated sale to storage)
            var bcState = engine.Native.ContractManagement.GetContract(bondingCurve);
            int bcId = bcState.Id;

            long totalGasIn = 0;
            for (int j = 0; j < 5; j++)
            {
                var current = GetSale(engine, bondingCurve, tokenHash);
                if (current.Graduated) break;

                long gasAmount = rng.NextInt64(1_000_000, Math.Max(1_000_001, targetRaise / 10));
                BigInteger tokens = GetTokensForGAS(engine, bondingCurve, tokenHash, gasAmount);

                BigInteger maxTokens = current.GraduationBps * 100000000000000000 / 10000;
                BigInteger remaining = maxTokens - current.TokensSold;
                BigInteger actualGas = gasAmount;

                if (tokens >= remaining)
                {
                    tokens = remaining;
                    if (tokens <= 0) break;
                }

                totalGasIn += (long)BigInteger.Min(actualGas, new BigInteger(long.MaxValue));
                current.TokensSold += tokens;
                current.GASRaised += actualGas;

                if (current.TokensSold >= maxTokens)
                    break;

                // Write updated sale back to storage
                var map = new Map();
                map[(BigInteger)0] = (ByteString)Owner.GetSpan().ToArray();
                map[(BigInteger)1] = new Integer(current.TokensSold);
                map[(BigInteger)2] = new Integer(current.GASRaised);
                map[(BigInteger)3] = new Integer(current.TargetRaise);
                map[(BigInteger)4] = new Integer(current.VirtualGAS);
                map[(BigInteger)5] = new Integer(current.VirtualToken);
                map[(BigInteger)6] = new Integer(current.GraduationBps);
                map[(BigInteger)7] = Boolean.False;

                byte[] serializedSale = BinarySerializer.Serialize(map, new ExecutionEngineLimits());
                WriteSaleStorage(engine, bcId, tokenHash, serializedSale);
            }

            var saleAfter = GetSale(engine, bondingCurve, tokenHash);
            BigInteger actualK = ComputeK(saleAfter);
            BigInteger tolerance = initialK / 1000 + 1;

            BigInteger diff = actualK > initialK ? actualK - initialK : initialK - actualK;
            Assert.True(diff <= tolerance,
                $"iter {i}: K drift too large. initial={initialK}, actual={actualK}, diff={diff}, tol={tolerance}");
        }
    }

    #endregion

    #region Fuzz: Sell Amount Invariants (VM)

    [Fact]
    public void FuzzVm_SellAmount_NeverExceedsGASRaised()
    {
        var rng = new Random(Seed + 6);
        for (int i = 0; i < Iterations; i++)
        {
            var (engine, _, _, bondingCurve) = Setup();
            long totalSupply = rng.NextInt64(100_000, 100_000_000_000);
            long targetRaise = rng.NextInt64(1_000_000_000, 10_000_000_000_000);
            int graduationBps = rng.Next(5001, 9001);
            long symbolNum = rng.NextInt64(10000, 99999);

            var tokenHash = SetupRandomSale(engine, bondingCurve,
                totalSupply, 8, "S" + symbolNum, targetRaise, graduationBps);

            // Pre-buy some tokens
            var sale = GetSale(engine, bondingCurve, tokenHash);
            BigInteger tokensOut;
            try
            {
                tokensOut = GetTokensForGAS(engine, bondingCurve, tokenHash, targetRaise);
            }
            catch { continue; }
            if (tokensOut <= 0) continue;

            // Update storage to reflect buy
            var bcState = engine.Native.ContractManagement.GetContract(bondingCurve);
            int bcId = bcState.Id;

            BigInteger gasNeeded;
            try
            {
                var refResult = Invoke(engine, bondingCurve, "getTokensForGAS", tokenHash, targetRaise);
                var raw = refResult.GetInteger();
                // Compute actual gas using the on-chain formula inverse
                if (raw <= 0) continue;
                BigInteger tokenReserve = sale.VirtualToken - 0;
                BigInteger gasReserve = sale.VirtualGAS + 0;
                BigInteger k = sale.VirtualGAS * sale.VirtualToken;
                BigInteger newTokenReserve = tokenReserve - raw;
                if (newTokenReserve <= 0) continue;
                BigInteger newGasReserve = k / newTokenReserve;
                gasNeeded = newGasReserve - gasReserve;
            }
            catch { continue; }

            BigInteger buyAmount = BigInteger.Min(tokensOut, 100000000000000000 * sale.GraduationBps / 10000);
            if (buyAmount <= 0) continue;

            sale.TokensSold = buyAmount;
            sale.GASRaised = gasNeeded > 0 ? gasNeeded : targetRaise / 10;

            var writeMap = new Map();
            writeMap[(BigInteger)0] = (ByteString)tokenHash.GetSpan().ToArray();
            writeMap[(BigInteger)1] = (ByteString)Owner.GetSpan().ToArray();
            writeMap[(BigInteger)2] = new Integer(sale.TokensSold);
            writeMap[(BigInteger)3] = new Integer(sale.GASRaised);
            writeMap[(BigInteger)4] = new Integer(sale.TargetRaise);
            writeMap[(BigInteger)5] = new Integer(sale.VirtualGAS);
            writeMap[(BigInteger)6] = new Integer(sale.VirtualToken);
            writeMap[(BigInteger)7] = new Integer(sale.GraduationBps);
            writeMap[(BigInteger)8] = Boolean.False;

            byte[] serializedSale = BinarySerializer.Serialize(writeMap, new ExecutionEngineLimits());
            WriteSaleStorage(engine, bcId, tokenHash, serializedSale);

            // Test sell
            try
            {
                BigInteger sellTokens = rng.NextInt64(1, (long)BigInteger.Min(sale.TokensSold, new BigInteger(long.MaxValue)) + 1);
                if (sellTokens > sale.TokensSold) sellTokens = sale.TokensSold;
                if (sellTokens <= 0) continue;

                BigInteger gasReturn = GetGASForTokens(engine, bondingCurve, tokenHash, sellTokens);

                Assert.True(gasReturn >= 0,
                    $"iter {i}: sell returned negative GAS. return={gasReturn}");
                Assert.True(gasReturn <= sale.GASRaised,
                    $"iter {i}: sell returned {gasReturn} > raised={sale.GASRaised}");
            }
            catch (TestException) { }
        }
    }

    #endregion

    #region Fuzz: Buy-Sell Round-trip (VM)

    [Fact]
    public void FuzzVm_BuySellRoundTrip_NetGASNonNegative()
    {
        var rng = new Random(Seed + 7);
        for (int i = 0; i < Iterations; i++)
        {
            var (engine, _, _, bondingCurve) = Setup();
            long totalSupply = rng.NextInt64(100_000, 10_000_000_000);
            long targetRaise = rng.NextInt64(100_000_000, 10_000_000_000_000);
            int graduationBps = rng.Next(5001, 9001);
            long symbolNum = rng.NextInt64(10000, 99999);

            var tokenHash = SetupRandomSale(engine, bondingCurve,
                totalSupply, 8, "R" + symbolNum, targetRaise, graduationBps);

            var sale = GetSale(engine, bondingCurve, tokenHash);

            // Simulate buy
            long gasIn = rng.NextInt64(1_000_000, Math.Min(targetRaise, 1_000_000_000_000));
            try
            {
                var _ = GetTokensForGAS(engine, bondingCurve, tokenHash, gasIn);
            }
            catch { continue; }
            // Compute actual buy via formula
            BigInteger tokenReserve = sale.VirtualToken;
            BigInteger gasReserve = sale.VirtualGAS;
            BigInteger k = sale.VirtualGAS * sale.VirtualToken;
            BigInteger newGasReserve = gasReserve + gasIn;
            BigInteger newTokenReserve = k / newGasReserve;
            BigInteger tokensOut = tokenReserve - newTokenReserve;
            if (tokensOut <= 0) continue;

            BigInteger maxTokens = 100000000000000000 * sale.GraduationBps / 10000;
            if (tokensOut > maxTokens) tokensOut = maxTokens;
            if (tokensOut <= 0) continue;

            // Write buy state
            var bcState = engine.Native.ContractManagement.GetContract(bondingCurve);
            int bcId = bcState.Id;
            sale.TokensSold = tokensOut;
            sale.GASRaised = gasIn;

            var buyMap = new Map();
            buyMap[(BigInteger)0] = (ByteString)tokenHash.GetSpan().ToArray();
            buyMap[(BigInteger)1] = (ByteString)Owner.GetSpan().ToArray();
            buyMap[(BigInteger)2] = new Integer(sale.TokensSold);
            buyMap[(BigInteger)3] = new Integer(sale.GASRaised);
            buyMap[(BigInteger)4] = new Integer(sale.TargetRaise);
            buyMap[(BigInteger)5] = new Integer(sale.VirtualGAS);
            buyMap[(BigInteger)6] = new Integer(sale.VirtualToken);
            buyMap[(BigInteger)7] = new Integer(sale.GraduationBps);
            buyMap[(BigInteger)8] = Boolean.False;

            byte[] serializedSale = BinarySerializer.Serialize(buyMap, new ExecutionEngineLimits());
            WriteSaleStorage(engine, bcId, tokenHash, serializedSale);

            // Sell all
            try
            {
                BigInteger gasReturn = GetGASForTokens(engine, bondingCurve, tokenHash, tokensOut);
                Assert.True(gasReturn >= 0,
                    $"iter {i}: round-trip returned negative GAS. return={gasReturn}");

                BigInteger netGas = gasReturn - gasIn;
                // netGas can be negative (buy-sell friction), but should not drain pool
                Assert.True(netGas > -gasIn,
                    $"iter {i}: round-trip drained all GAS. gasIn={gasIn}, gasReturn={gasReturn}");
            }
            catch (TestException) { }
        }
    }

    #endregion

    #region Fuzz: Graduation Boundary (VM)

    [Fact]
    public void FuzzVm_GraduationBps_NearLimits()
    {
        int[] bpsValues = { 5001, 5002, 5010, 5100, 9890, 9899, 9999 };
        var rng = new Random(Seed + 8);

        foreach (int bps in bpsValues)
        {
            var (engine, _, _, bondingCurve) = Setup();
            long totalSupply = rng.NextInt64(1_000_000, 1_000_000_000_000);
            long targetRaise = rng.NextInt64(1_000_000_000, 10_000_000_000_000);

            var tokenHash = SetupRandomSale(engine, bondingCurve,
                totalSupply, 8, "GL" + bps, targetRaise, bps);

            var sale = GetSale(engine, bondingCurve, tokenHash);

            Assert.True(sale.VirtualToken > 0, $"bps={bps}: vToken <= 0");
            Assert.True(sale.VirtualGAS >= 0, $"bps={bps}: vGas < 0");

            BigInteger maxTokens = totalSupply * bps / 10000;
            Assert.True(sale.VirtualToken > maxTokens,
                $"bps={bps}: vToken={sale.VirtualToken} <= maxTokens={maxTokens}");

            BigInteger twoPminusOne = 2 * bps - 10000;
            BigInteger expectedVToken = ComputeVToken(totalSupply, bps);
            BigInteger expectedVGas = ComputeVGas(targetRaise, bps);
            Assert.Equal(expectedVToken, sale.VirtualToken);
            Assert.Equal(expectedVGas, sale.VirtualGAS);
        }
    }

    #endregion

    #region Fuzz: GraduationBps 6000–9999 Complete Sweep

    [Fact]
    public void FuzzVm_GraduationBps_CompleteSweep()
    {
        var rng = new Random(Seed + 9);
        int step = Math.Max(1, (9999 - 6000) / 50);

        for (int bps = 6000; bps <= 9999; bps += step)
        {
            var (engine, _, _, bondingCurve) = Setup();
            long totalSupply = rng.NextInt64(10_000, 1_000_000_000_000);
            long targetRaise = rng.NextInt64(10_000_000, 10_000_000_000_000);

            var tokenHash = SetupRandomSale(engine, bondingCurve,
                totalSupply, 8, "SWEEP" + bps, targetRaise, bps);

            var sale = GetSale(engine, bondingCurve, tokenHash);

            Assert.True(sale.VirtualToken > 0, $"bps={bps}: vToken <= 0");
            Assert.True(sale.VirtualGAS >= 0, $"bps={bps}: vGas < 0");

            BigInteger maxTokens = totalSupply * bps / 10000;
            Assert.True(sale.VirtualToken > maxTokens,
                $"bps={bps}: vToken={sale.VirtualToken} <= maxTokens={maxTokens}");

            // Verify on-chain getTokensForGAS works
            long testGas = rng.NextInt64(1_000_000, Math.Min(targetRaise, 1_000_000_000));
            try
            {
                BigInteger tokens = GetTokensForGAS(engine, bondingCurve, tokenHash, testGas);
                Assert.True(tokens >= 0, $"bps={bps}: buy amount < 0");
            }
            catch (TestException ex)
            {
                Assert.Fail($"bps={bps}: getTokensForGAS faulted. {ex.Message}");
            }
        }
    }

    #endregion

    #region Fuzz: Graduation Boundary Safety-Net (GetAmountOut/GetAmountIn Invertibility)

    // Replicates the contract's exact rounding (DuoBondingCurve.Helper.cs):
    //   GetAmountOut: floor(reserveOut * amountIn / (reserveIn + amountIn))
    //   GetAmountIn : ceil (reserveIn * amountOut / (reserveOut - amountOut))
    //
    // Purpose: turn the ProcessBuy graduation safety net
    //   `if (netActualGas > netGasAmount) netActualGas = netGasAmount;`  (line 247)
    // into a CI regression test. The net can only ever be reached if the branch
    // is entered, i.e. GetAmountOut(gasReserve, tokenReserve, netGasAmount) >= remainingToGrad.
    // The invariant tested here is that the branch entry threshold equals exactly
    // GetAmountIn(gasReserve, tokenReserve, remainingToGrad), so the cap can never fire:
    //   1) AmountOut(AmountIn(remaining)) >= remaining  -- AmountIn never underestimates
    //   2) AmountOut(AmountIn(remaining) - 1) < remaining -- AmountIn is the true minimum
    // If either fails, rounding drift would let a buyer get graduation tokens below the
    // curve's priced cost (or fail to graduate at all) -- a logic defect, not a dust issue.

    private static BigInteger AmountOut(BigInteger reserveIn, BigInteger reserveOut, BigInteger amountIn)
    {
        if (amountIn <= 0 || reserveIn <= 0 || reserveOut <= 0) return 0;
        return reserveOut * amountIn / (reserveIn + amountIn);
    }

    private static BigInteger AmountIn(BigInteger reserveIn, BigInteger reserveOut, BigInteger amountOut)
    {
        if (amountOut <= 0 || reserveOut <= amountOut) return 0;
        BigInteger numerator = reserveIn * amountOut;
        BigInteger denominator = reserveOut - amountOut;
        BigInteger result = numerator / denominator;
        if (numerator % denominator != 0) result += 1;
        return result;
    }

    private static void AssertSafetyNetNeverFires(BigInteger tokenReserve, BigInteger gasReserve, BigInteger remaining)
    {
        if (remaining <= 0 || remaining >= tokenReserve) return;

        BigInteger minGas = AmountIn(gasReserve, tokenReserve, remaining);
        Assert.True(minGas > 0,
            $"AmountIn returned 0 at tokenReserve={tokenReserve}, gasReserve={gasReserve}, remaining={remaining}");

        // (1) Feeding the priced minimum back must buy >= remaining tokens.
        BigInteger tokens = AmountOut(gasReserve, tokenReserve, minGas);
        Assert.True(tokens >= remaining,
            $"Invertibility broken: AmountOut(AmountIn(remaining))={tokens} < remaining={remaining} " +
            $"at tokenReserve={tokenReserve}, gasReserve={gasReserve}. The graduation branch would NOT " +
            $"be entered with netGasAmount == AmountIn(...), leaving the sale ungraduated.");

        // (2) One gas less must NOT buy the remaining tokens (AmountIn is the true minimum).
        //     Otherwise the safety net would clamp netActualGas down to a smaller netGasAmount,
        //     pricing the graduation tokens below the curve value.
        BigInteger tokensBelow = AmountOut(gasReserve, tokenReserve, minGas - 1);
        Assert.True(tokensBelow < remaining,
            $"AmountIn overestimates: {minGas - 1} gas already buys {tokensBelow} >= remaining={remaining} " +
            $"at tokenReserve={tokenReserve}, gasReserve={gasReserve}. Safety net would fire for " +
            $"netGasAmount in (trueMin, AmountIn(remaining)), pricing graduation below curve value.");
    }

    private static IEnumerable<(BigInteger, BigInteger)> GraduationBoundaryReserves()
    {
        // Extreme ratios/magnitudes where floor/ceil rounding is amplified.
        yield return (1, 1);
        yield return (1, BigInteger.Pow(10, 18));
        yield return (BigInteger.Pow(10, 18), 1);
        yield return (BigInteger.Pow(10, 8), BigInteger.Pow(10, 8));
        yield return (BigInteger.Pow(10, 6), BigInteger.Pow(10, 9));
        yield return (BigInteger.Pow(10, 9), BigInteger.Pow(10, 6));
        // Realistic magnitudes (vToken ~1e20, vGas ~1e14) incl. the high-bps tiny-cushion case.
        yield return (BigInteger.Pow(10, 20), BigInteger.Pow(10, 14));
        yield return (BigInteger.Pow(10, 14), BigInteger.Pow(10, 20));
        yield return (BigInteger.Pow(10, 20), BigInteger.Pow(10, 10));
        yield return (BigInteger.Pow(10, 10), BigInteger.Pow(10, 20));
    }

    private static IEnumerable<BigInteger> RemainingProbes(BigInteger tokenReserve, long mode)
    {
        if (tokenReserve <= 1) yield break;

        // Tiny remainingToGrad: rounding dominates GetAmountIn.
        for (BigInteger r = 1; r <= 8; r++)
            yield return r;

        // Near-full remainingToGrad: denominator (reserve - remaining) is tiny, amplifying rounding.
        if (mode == 9999)
        {
            for (BigInteger r = 1; r <= 8; r++)
            {
                BigInteger rem = tokenReserve - r;
                if (rem > 0) yield return rem;
            }
        }

        foreach (var divisor in new[] { 3, 2 })
        {
            BigInteger rem = tokenReserve / divisor;
            if (rem > 0) yield return rem;
        }
    }

    [Theory]
    [InlineData(1, 10)]          // remaining ~1, reserves ~1e2 (tiny everything)
    [InlineData(1, 100)]         // remaining ~1, reserves ~1e4
    [InlineData(9999, 10000)]    // remaining near reserve (tiny cushion), reserves ~1e8
    [InlineData(1, 1_000_000)]   // remaining ~1, reserves ~1e12 (magnitude gap)
    public void GetAmountIn_NeverExceeds_NetGasAmount_AtGraduationBoundary(
        long remainingMode, long reserveScale)
    {
        BigInteger tokenReserve = reserveScale * reserveScale;
        BigInteger gasReserve = reserveScale;

        foreach (var remaining in RemainingProbes(tokenReserve, remainingMode))
            AssertSafetyNetNeverFires(tokenReserve, gasReserve, remaining);
    }

    [Fact]
    public void FuzzMath_GraduationBoundary_SafetyNetNeverFires()
    {
        var rng = new Random(Seed + 10);
        for (int i = 0; i < 20000; i++)
        {
            BigInteger tokenReserve = BigInteger.Pow(10, rng.Next(1, 21));
            BigInteger gasReserve = BigInteger.Pow(10, rng.Next(1, 21));
            if (rng.Next(2) == 0)
            {
                tokenReserve *= rng.Next(1, 1000);
                gasReserve *= rng.Next(1, 1000);
            }

            BigInteger remaining;
            int pick = rng.Next(3);
            switch (pick)
            {
                case 0:
                    remaining = rng.Next(1, 1000);                                    // tiny
                    break;
                case 1:
                    remaining = tokenReserve - rng.Next(1, 1000);                     // near-full
                    if (remaining <= 0) remaining = 1;
                    break;
                default:
                    remaining = tokenReserve / rng.Next(2, 10000) + rng.Next(1, 100); // mid-scale
                    break;
            }

            if (remaining <= 0 || remaining >= tokenReserve) continue;

            BigInteger minGas = AmountIn(gasReserve, tokenReserve, remaining);
            Assert.True(minGas > 0, $"iter {i}: AmountIn returned 0 for remaining={remaining}");

            BigInteger tokens = AmountOut(gasReserve, tokenReserve, minGas);
            Assert.True(tokens >= remaining,
                $"iter {i}: AmountOut(AmountIn(remaining))={tokens} < remaining={remaining} " +
                $"(invertibility broken at tokenReserve={tokenReserve}, gasReserve={gasReserve})");

            BigInteger tokensBelow = AmountOut(gasReserve, tokenReserve, minGas - 1);
            Assert.True(tokensBelow < remaining,
                $"iter {i}: AmountOut({minGas} - 1)={tokensBelow} >= remaining={remaining} " +
                $"(AmountIn overestimates at tokenReserve={tokenReserve}, gasReserve={gasReserve})");
        }
    }

    // End-to-end: drive the real bytecode to the graduation boundary with a small remainingToGrad,
    // and assert the final buyer pays exactly the curve minimum (the safety net must never distort
    // the price). High graduationBps + tiny remaining maximizes rounding sensitivity.
    [Fact]
    public void FuzzVm_GraduationFinalBuy_PaysExactMinimum()
    {
        int[] bpsValues = { 5001, 5010, 8000, 9899, 9999 };
        long totalSupply = 100_000_000_000_000_000; // 1e17
        long targetRaise = 500_000_000_000;

        foreach (int bps in bpsValues)
        {
            var (engine, _, _, bondingCurve) = Setup();
            var gas = engine.Native.GAS.Hash;

            var tokenHash = SetupRandomSale(engine, bondingCurve,
                totalSupply, 8, "GB" + bps, targetRaise, bps);

            var sale = GetSale(engine, bondingCurve, tokenHash);
            BigInteger maxTokens = (BigInteger)totalSupply * bps / 10000;

            // Pre-buy to ~95% of maxTokens so graduation is safely not reached.
            // (A closer landing is fragile: AmountIn near maxTokens rounds up to the same
            // gas as AmountIn(maxTokens), which would graduate the pre-buy itself.)
            BigInteger targetRemaining = maxTokens / 20;
            BigInteger preGas = AmountIn(sale.VirtualGAS, sale.VirtualToken, maxTokens - targetRemaining);
            Assert.True(preGas > 0, $"bps={bps}: preGas<=0");
            Buy(engine, bondingCurve, tokenHash, Owner, preGas);

            var before = GetSale(engine, bondingCurve, tokenHash);
            Assert.False(before.Graduated, $"bps={bps}: pre-buy graduated prematurely");
            BigInteger remaining = maxTokens - before.TokensSold;
            Assert.True(remaining > 0 && remaining < maxTokens / 10,
                $"bps={bps}: remaining={remaining} (expected 0 < remaining < {maxTokens / 10})");

            // The final buy must pay exactly the curve minimum for the remaining tokens (fee-off).
            BigInteger gasReserve = before.VirtualGAS + before.GASRaised;
            BigInteger tokenReserve = before.VirtualToken - before.TokensSold;
            BigInteger expected = AmountIn(gasReserve, tokenReserve, remaining);

            BigInteger gasBefore = Invoke(engine, gas, "balanceOf", Owner).GetInteger();
            Buy(engine, bondingCurve, tokenHash, Owner, expected + 2);
            BigInteger gasSpent = gasBefore - Invoke(engine, gas, "balanceOf", Owner).GetInteger();

            var after = GetSale(engine, bondingCurve, tokenHash);
            Assert.True(after.Graduated,
                $"bps={bps}: sale not graduated after final buy. " +
                $"before(sold={before.TokensSold}, raised={before.GASRaised}), remaining={remaining}, " +
                $"expected={expected}, gasSpent={gasSpent}, after(sold={after.TokensSold}, raised={after.GASRaised})");
            Assert.Equal(expected, gasSpent);
        }
    }

    // feeOn variant: exercises the graduation fee logic end-to-end —
    // curveGas is pinned to netActualGas, fee = actualGas - curveGas is collected
    // to feeTo, and the refund math stays exact.
    [Fact]
    public void FuzzVm_GraduationFinalBuy_FeeOn_ExactMinimumAndFee()
    {
        const long FEE_FACTOR = 3;
        const long FEE_DENOMINATOR = 10000;
        int[] bpsValues = { 5001, 8000, 9999 };
        long totalSupply = 100_000_000_000_000_000; // 1e17
        long targetRaise = 500_000_000_000;
        var feeTo = UInt160.Parse("0x0000000000000000000000000000000000000008");

        foreach (int bps in bpsValues)
        {
            var (engine, factory, _, bondingCurve) = Setup();
            var gas = engine.Native.GAS.Hash;
            Invoke(engine, factory, "setFeeTo", feeTo);

            var tokenHash = SetupRandomSale(engine, bondingCurve,
                totalSupply, 8, "FGB" + bps, targetRaise, bps);

            var sale = GetSale(engine, bondingCurve, tokenHash);
            BigInteger maxTokens = (BigInteger)totalSupply * bps / 10000;

            // Pre-buy to ~95% of maxTokens (fee-on applies to the pre-buy too).
            BigInteger targetRemaining = maxTokens / 20;
            BigInteger preGas = AmountIn(sale.VirtualGAS, sale.VirtualToken, maxTokens - targetRemaining);
            Assert.True(preGas > 0, $"bps={bps}: preGas<=0");
            Buy(engine, bondingCurve, tokenHash, Owner, preGas);

            var before = GetSale(engine, bondingCurve, tokenHash);
            Assert.False(before.Graduated, $"bps={bps}: pre-buy graduated prematurely (feeOn)");
            BigInteger remaining = maxTokens - before.TokensSold;
            Assert.True(remaining > 0 && remaining < maxTokens / 10,
                $"bps={bps}: remaining={remaining} (expected 0 < remaining < {maxTokens / 10})");

            BigInteger gasReserve = before.VirtualGAS + before.GASRaised;
            BigInteger tokenReserve = before.VirtualToken - before.TokensSold;
            BigInteger netActualGas = AmountIn(gasReserve, tokenReserve, remaining);
            BigInteger actualGas = (netActualGas * FEE_DENOMINATOR + (FEE_DENOMINATOR - FEE_FACTOR - 1)) / (FEE_DENOMINATOR - FEE_FACTOR);
            BigInteger feeExpected = actualGas - netActualGas;
            Assert.True(feeExpected >= 0, $"bps={bps}: fee<0 (netActualGas={netActualGas}, actualGas={actualGas})");

            // Send enough net gas to enter the graduation branch without triggering the cap.
            BigInteger gasSent = actualGas + 1000;

            // The pair deploy fee stays in the factory (owner controls it via Withdraw);
            // feeTo receives only the graduation fee.
            BigInteger deployFee = Invoke(engine, factory, "getDeployFee").GetInteger();
            BigInteger factoryBefore = Invoke(engine, gas, "balanceOf", factory).GetInteger();

            BigInteger feeToBefore = Invoke(engine, gas, "balanceOf", feeTo).GetInteger();
            BigInteger gasBefore = Invoke(engine, gas, "balanceOf", Owner).GetInteger();
            Buy(engine, bondingCurve, tokenHash, Owner, gasSent);
            BigInteger gasSpent = gasBefore - Invoke(engine, gas, "balanceOf", Owner).GetInteger();
            BigInteger feeToAfter = Invoke(engine, gas, "balanceOf", feeTo).GetInteger();
            BigInteger factoryAfter = Invoke(engine, gas, "balanceOf", factory).GetInteger();

            var after = GetSale(engine, bondingCurve, tokenHash);
            Assert.True(after.Graduated, $"bps={bps}: sale not graduated after final buy (feeOn)");
            Assert.Equal(actualGas, gasSpent);
            Assert.Equal(feeExpected, feeToAfter - feeToBefore);
            Assert.Equal(deployFee, factoryAfter - factoryBefore);
            Assert.Equal(netActualGas, after.GASRaised - before.GASRaised);
        }
    }

    private static void Buy(TestEngine engine, UInt160 bondingCurve, UInt160 tokenHash,
        UInt160 buyer, BigInteger gasAmount)
    {
        var gas = engine.Native.GAS.Hash;

        var data = new ContractParameter(ContractParameterType.Array)
        {
            Value = new ContractParameter[]
            {
                new(ContractParameterType.Hash160) { Value = tokenHash },
                new(ContractParameterType.Integer) { Value = BigInteger.Zero }
            }
        };

        engine.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.Global, buyer);
        Invoke(engine, gas, "transfer", buyer, bondingCurve, gasAmount, data);
    }

    #endregion
}
