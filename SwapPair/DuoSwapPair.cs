using System.Numerics;
using Neo;
using Neo.SmartContract.Framework;
using Neo.SmartContract.Framework.Attributes;
using Neo.SmartContract.Framework.Services;

namespace DuoSwapPair
{
    [ManifestExtra("Description", "DuoSwap Pair Contract")]
    [ManifestExtra("Sourcecode", "http://github.com/DuoFinance/bond2swap.git")]
    [SupportedStandards("NEP-17")]
    [ContractPermission("*")]
    public partial class DuoSwapPairContract : SmartContract
    {
        private const long MINIMUM_LIQUIDITY = 1000;
        private const long FEE_DENOMINATOR = 1000;
        private const long TRADE_FEE = 3;
        private const long PROTOCOL_FEE_FACTOR = 5;
        private static readonly BigInteger Q112 = 100_000_000_000_000_000;

        private static UInt160 Token0
        {
            get => (UInt160)StorageGet("token0");
            set => StoragePut("token0", value);
        }

        private static UInt160 Token1
        {
            get => (UInt160)StorageGet("token1");
            set => StoragePut("token1", value);
        }

        [Safe]
        public static UInt160 GetToken0() => Token0;

        [Safe]
        public static UInt160 GetToken1() => Token1;
        [Safe]
        public static BigInteger GetReserve0() => ReservePair.Reserve0;
        [Safe]
        public static BigInteger GetReserve1() => ReservePair.Reserve1;
        [Safe]
        public static PriceCumulative GetPriceCumulative() => Cumulative;
        [Safe]
        public static BigInteger Price0CumulativeLast() => Cumulative.Price0CumulativeLast;
        [Safe]
        public static BigInteger Price1CumulativeLast() => Cumulative.Price1CumulativeLast;

        [Safe]
        public static object GetReserves() => ReservePair;

        public static bool Swap(BigInteger amount0Out, BigInteger amount1Out, UInt160 toAddress, byte[] data = null)
        {
            Enter();

            Assert(toAddress.IsAddress(), "BAD_RECIPIENT");
            Assert(amount0Out >= 0 && amount1Out >= 0, "BAD_OUTPUT");
            Assert(amount0Out > 0 || amount1Out > 0, "ZERO_OUTPUT");

            UInt160 self = Runtime.ExecutingScriptHash;
            UInt160 token0 = Token0;
            UInt160 token1 = Token1;
            ReservesData before = ReservePair;

            Assert(amount0Out < before.Reserve0 && amount1Out < before.Reserve1, "LOW_LIQUIDITY");
            Assert(toAddress != self && toAddress != token0 && toAddress != token1, "BAD_RECIPIENT");

            if (amount0Out > 0) TransferToken(token0, self, toAddress, amount0Out, data);
            if (amount1Out > 0) TransferToken(token1, self, toAddress, amount1Out, data);

            BigInteger balance0 = TokenBalance(token0, self);
            BigInteger balance1 = TokenBalance(token1, self);
            BigInteger amount0In = balance0 > before.Reserve0 - amount0Out ? balance0 - (before.Reserve0 - amount0Out) : 0;
            BigInteger amount1In = balance1 > before.Reserve1 - amount1Out ? balance1 - (before.Reserve1 - amount1Out) : 0;

            Assert(amount0In > 0 || amount1In > 0, "ZERO_INPUT");
            CheckInvariant(before, balance0, balance1, amount0In, amount1In);

            WriteReserves(balance0, balance1, before);
            Swapped(Runtime.CallingScriptHash, amount0In, amount1In, amount0Out, amount1Out, toAddress);

            Exit();
            return true;
        }

        public static BigInteger Mint(UInt160 toAddress)
        {
            Enter();

            Assert(toAddress.IsAddress(), "BAD_RECIPIENT");
            Assert(Runtime.CallingScriptHash == GetRouter() || Runtime.CallingScriptHash == GetBonding(), "ONLY_ROUTER_OR_BONDING");

            UInt160 self = Runtime.ExecutingScriptHash;
            UInt160 token0 = Token0;
            UInt160 token1 = Token1;
            ReservesData oldReserves = ReservePair;

            bool feeOn = MintProtocolFee(oldReserves);

            BigInteger balance0 = TokenBalance(token0, self);
            BigInteger balance1 = TokenBalance(token1, self);
            BigInteger amount0 = balance0 - oldReserves.Reserve0;
            BigInteger amount1 = balance1 - oldReserves.Reserve1;
            Assert(amount0 > 0 && amount1 > 0, "BAD_DEPOSIT");

            BigInteger supply = TotalSupply();
            BigInteger liquidity;
            if (supply == 0)
            {
                liquidity = (amount0 * amount1).Sqrt() - MINIMUM_LIQUIDITY;
                MintLp(UInt160.Zero, MINIMUM_LIQUIDITY);
            }
            else
            {
                BigInteger share0 = amount0 * supply / oldReserves.Reserve0;
                BigInteger share1 = amount1 * supply / oldReserves.Reserve1;
                liquidity = Min(share0, share1);
            }

            Assert(liquidity > 0, "LOW_LP_MINT");
            MintLp(toAddress, liquidity);

            WriteReserves(balance0, balance1, oldReserves);
            if (feeOn) SetKLast(balance0 * balance1);

            Minted(Runtime.CallingScriptHash, amount0, amount1, liquidity);
            Exit();
            return liquidity;
        }

        public static object Burn(UInt160 toAddress)
        {
            Enter();

            Assert(toAddress.IsAddress(), "BAD_RECIPIENT");
            Assert(Runtime.CallingScriptHash == GetRouter(), "ONLY_ROUTER");

            UInt160 self = Runtime.ExecutingScriptHash;
            UInt160 token0 = Token0;
            UInt160 token1 = Token1;
            ReservesData oldReserves = ReservePair;

            bool feeOn = MintProtocolFee(oldReserves);

            BigInteger liquidity = BalanceOf(self);
            BigInteger supply = TotalSupply();
            BigInteger balance0 = TokenBalance(token0, self);
            BigInteger balance1 = TokenBalance(token1, self);
            Assert(supply > 0, "NO_SUPPLY");
            BigInteger amount0 = liquidity * balance0 / supply;
            BigInteger amount1 = liquidity * balance1 / supply;

            Assert(amount0 > 0 && amount1 > 0, "LOW_LP_BURN");

            BurnLp(self, liquidity);
            TransferToken(token0, self, toAddress, amount0);
            TransferToken(token1, self, toAddress, amount1);

            balance0 = TokenBalance(token0, self);
            balance1 = TokenBalance(token1, self);
            WriteReserves(balance0, balance1, oldReserves);
            if (feeOn) SetKLast(balance0 * balance1);

            Burned(Runtime.CallingScriptHash, liquidity, amount0, amount1, toAddress);
            Exit();
            return new BigInteger[] { amount0, amount1 };
        }

        private static void CheckInvariant(
            ReservesData reserves,
            BigInteger balance0,
            BigInteger balance1,
            BigInteger amount0In,
            BigInteger amount1In)
        {
            BigInteger adjusted0 = balance0 * FEE_DENOMINATOR - amount0In * TRADE_FEE;
            BigInteger adjusted1 = balance1 * FEE_DENOMINATOR - amount1In * TRADE_FEE;
            Assert(adjusted0 * adjusted1 >= reserves.Reserve0 * reserves.Reserve1 * FEE_DENOMINATOR * FEE_DENOMINATOR, "K");
        }

        private static bool MintProtocolFee(ReservesData reserves)
        {
            UInt160 feeTo = (UInt160)Contract.Call(GetFactory(), "getFeeTo", CallFlags.ReadOnly);
            BigInteger kLast = GetKLast();
            bool enabled = feeTo != UInt160.Zero;

            if (!enabled)
            {
                if (kLast != 0) SetKLast(0);
                return false;
            }

            if (kLast == 0) return true;

            BigInteger rootK = (reserves.Reserve0 * reserves.Reserve1).Sqrt();
            BigInteger rootKLast = kLast.Sqrt();
            if (rootK <= rootKLast) return true;

            BigInteger supply = TotalSupply();
            BigInteger numerator = supply * (rootK - rootKLast);
            BigInteger denominator = rootK * PROTOCOL_FEE_FACTOR + rootKLast;
            BigInteger protocolLiquidity = numerator / denominator;
            if (protocolLiquidity > 0) MintLp(feeTo, protocolLiquidity);
            return true;
        }

        private static void WriteReserves(BigInteger balance0, BigInteger balance1, ReservesData previous)
        {
            BigInteger timestamp = Runtime.Time / 1000 % 4294967296;
            PriceCumulative prices = Cumulative;
            BigInteger elapsed = timestamp - prices.BlockTimestampLast;

            if (elapsed > 0 && previous.Reserve0 > 0 && previous.Reserve1 > 0)
            {
                prices.Price0CumulativeLast += previous.Reserve1 * Q112 * elapsed / previous.Reserve0;
                prices.Price1CumulativeLast += previous.Reserve0 * Q112 * elapsed / previous.Reserve1;
                prices.BlockTimestampLast = timestamp;
                Cumulative = prices;
            }

            ReservePair = new ReservesData() { Reserve0 = balance0, Reserve1 = balance1 };
            Synced(balance0, balance1);
        }
    }
}
