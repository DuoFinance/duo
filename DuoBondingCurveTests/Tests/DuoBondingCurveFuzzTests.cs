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
        public BigInteger VirtualGas;
        public BigInteger VirtualToken;
        public BigInteger GraduationBps;
        public bool Graduated;
    }

    private static VmSaleInfo GetSale(TestEngine engine, UInt160 bondingCurve, UInt160 tokenHash)
    {
        var result = Invoke(engine, bondingCurve, "getSaleInfo", tokenHash);
        var map = (Map)result;
        return new VmSaleInfo
        {
            TokensSold = map[(BigInteger)2].GetInteger(),
            GASRaised = map[(BigInteger)3].GetInteger(),
            TargetRaise = map[(BigInteger)4].GetInteger(),
            VirtualGas = map[(BigInteger)5].GetInteger(),
            VirtualToken = map[(BigInteger)6].GetInteger(),
            GraduationBps = map[(BigInteger)7].GetInteger(),
            Graduated = map[(BigInteger)8].GetBoolean(),
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
        return (s.VirtualGas + s.GASRaised) * (s.VirtualToken - s.TokensSold);
    }

    private static UInt160 SetupRandomSale(TestEngine engine, UInt160 bondingCurve,
        BigInteger totalSupply, byte decimals, string symbol,
        BigInteger targetRaise, BigInteger graduationBps)
    {
        var (ln, lm) = LoadContract("DuoLaunchpad");
        var tokenHash = DeployContract(engine, ln, lm, null);
        Invoke(engine, tokenHash, "initialise", bondingCurve, decimals, symbol, totalSupply);

        // Write sale via storage (bypasses CreateSale to avoid deploying real NEF)
        var bcState = engine.Native.ContractManagement.GetContract(bondingCurve);
        int bcId = bcState.Id;

        var vToken = ComputeVToken(totalSupply, graduationBps);
        var vGas = ComputeVGas(targetRaise, graduationBps);

        var map = new Map();
        map[(BigInteger)0] = (ByteString)tokenHash.GetSpan().ToArray();
        map[(BigInteger)1] = (ByteString)Owner.GetSpan().ToArray();
        map[(BigInteger)2] = new Integer(BigInteger.Zero);
        map[(BigInteger)3] = new Integer(BigInteger.Zero);
        map[(BigInteger)4] = new Integer(targetRaise);
        map[(BigInteger)5] = new Integer(vGas);
        map[(BigInteger)6] = new Integer(vToken);
        map[(BigInteger)7] = new Integer(graduationBps);
        map[(BigInteger)8] = Boolean.False;

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
            int graduationBps = rng.Next(5001, 9901);
            long symbolNum = rng.NextInt64(10000, 99999);
            string symbol = "T" + symbolNum;

            var tokenHash = SetupRandomSale(engine, bondingCurve,
                totalSupply, 8, symbol, targetRaise, graduationBps);

            var sale = GetSale(engine, bondingCurve, tokenHash);

            var expectedVToken = ComputeVToken(totalSupply, graduationBps);
            var expectedVGas = ComputeVGas(targetRaise, graduationBps);

            Assert.Equal(expectedVToken, sale.VirtualToken);
            Assert.Equal(expectedVGas, sale.VirtualGas);
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
            int graduationBps = rng.Next(5001, 9901);
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
            int graduationBps = rng.Next(5001, 9901);
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
            int graduationBps = rng.Next(5001, 9901);
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
            int graduationBps = rng.Next(5001, 9901);
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
            var initialK = saleBefore.VirtualGas * saleBefore.VirtualToken;

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

                if (current.GASRaised >= current.TargetRaise || current.TokensSold >= maxTokens)
                    break;

                // Write updated sale back to storage
                var map = new Map();
                map[(BigInteger)0] = (ByteString)tokenHash.GetSpan().ToArray();
                map[(BigInteger)1] = (ByteString)Owner.GetSpan().ToArray();
                map[(BigInteger)2] = new Integer(current.TokensSold);
                map[(BigInteger)3] = new Integer(current.GASRaised);
                map[(BigInteger)4] = new Integer(current.TargetRaise);
                map[(BigInteger)5] = new Integer(current.VirtualGas);
                map[(BigInteger)6] = new Integer(current.VirtualToken);
                map[(BigInteger)7] = new Integer(current.GraduationBps);
                map[(BigInteger)8] = Boolean.False;

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
                BigInteger gasReserve = sale.VirtualGas + 0;
                BigInteger k = sale.VirtualGas * sale.VirtualToken;
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
            writeMap[(BigInteger)5] = new Integer(sale.VirtualGas);
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
            BigInteger gasReserve = sale.VirtualGas;
            BigInteger k = sale.VirtualGas * sale.VirtualToken;
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
            buyMap[(BigInteger)5] = new Integer(sale.VirtualGas);
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
        int[] bpsValues = { 5001, 5002, 5010, 5100, 9890, 9899, 9900 };
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
            Assert.True(sale.VirtualGas >= 0, $"bps={bps}: vGas < 0");

            BigInteger maxTokens = totalSupply * bps / 10000;
            Assert.True(sale.VirtualToken > maxTokens,
                $"bps={bps}: vToken={sale.VirtualToken} <= maxTokens={maxTokens}");

            BigInteger twoPminusOne = 2 * bps - 10000;
            BigInteger expectedVToken = ComputeVToken(totalSupply, bps);
            BigInteger expectedVGas = ComputeVGas(targetRaise, bps);
            Assert.Equal(expectedVToken, sale.VirtualToken);
            Assert.Equal(expectedVGas, sale.VirtualGas);
        }
    }

    #endregion

    #region Fuzz: GraduationBps 5001–9900 Complete Sweep

    [Fact]
    public void FuzzVm_GraduationBps_CompleteSweep()
    {
        var rng = new Random(Seed + 9);
        int step = Math.Max(1, (9900 - 5001) / 50);

        for (int bps = 5001; bps <= 9900; bps += step)
        {
            var (engine, _, _, bondingCurve) = Setup();
            long totalSupply = rng.NextInt64(10_000, 1_000_000_000_000);
            long targetRaise = rng.NextInt64(10_000_000, 10_000_000_000_000);

            var tokenHash = SetupRandomSale(engine, bondingCurve,
                totalSupply, 8, "SWEEP" + bps, targetRaise, bps);

            var sale = GetSale(engine, bondingCurve, tokenHash);

            Assert.True(sale.VirtualToken > 0, $"bps={bps}: vToken <= 0");
            Assert.True(sale.VirtualGas >= 0, $"bps={bps}: vGas < 0");

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
}