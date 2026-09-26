using System.Numerics;
using System.ComponentModel;
using Neo;
using Neo.SmartContract.Framework;
using Neo.SmartContract.Framework.Attributes;
using Neo.SmartContract.Framework.Services;
using Neo.SmartContract.Framework.Native;

namespace DuoSwapPair
{
    public partial class DuoSwapPairContract : SmartContract
    {
        #region Storage Keys
        private const string FACTORY_KEY = "Factory";
        private const string ROUTER_KEY = "Router";
        private const string BONDING_KEY = "Bonding";
        private const string SYMBOL_KEY = "Symbol";
        private const string KLAST_KEY = "KLast";
        #endregion

        #region Init & Deployment
        /// <summary>
        /// Deployment, the contract is not upgradable.
        /// data = { factory, router, bonding, tokenA, tokenB, pairSymbol }
        /// All pair initialization happens atomically inside _deploy (invoked by
        /// ContractManagement during Deploy), so there is no external init call
        /// and no window where the pair exists uninitialized.
        /// </summary>
        public static void _deploy(object data, bool update)
        {
            Assert(!update, "Not Upgradable.");
            Assert(data != null, "Invalid Data.");
            object[] args = (object[])data;
            Assert(args.Length == 6, "Invalid Data.");

            UInt160 factory = (UInt160)args[0];
            Assert(factory.IsAddress(), "Invalid Factory.");
            Storage.Put(FACTORY_KEY, factory);

            UInt160 router = (UInt160)args[1];
            Assert(router.IsAddress(), "Invalid Router.");
            Storage.Put(ROUTER_KEY, router);

            UInt160 bonding = (UInt160)args[2];
            Assert(bonding.IsAddress(), "Invalid Bonding.");
            Storage.Put(BONDING_KEY, bonding);

            UInt160 tokenA = (UInt160)args[3];
            UInt160 tokenB = (UInt160)args[4];
            Assert(tokenA.IsAddress() && tokenB.IsAddress(), "Invalid Token.");
            Assert(tokenA != tokenB, "Identical Tokens.");

            string pairSymbol = (string)args[5];
            Assert(pairSymbol != null && pairSymbol.Length > 0, "Invalid Symbol.");
            Storage.Put(SYMBOL_KEY, pairSymbol);

            if (tokenA.ToUInteger() < tokenB.ToUInteger())
            {
                Token0 = tokenA;
                Token1 = tokenB;
            }
            else
            {
                Token0 = tokenB;
                Token1 = tokenA;
            }

            PairDeployed(Token0, Token1);
        }
        #endregion

        #region Router Factory and Bonding Getters
        [Safe]
        public static UInt160 GetRouter()
        {
            var router = StorageGet(ROUTER_KEY);
            return router?.Length == 20 ? (UInt160)router : UInt160.Zero;
        }

        [Safe]
        public static UInt160 GetFactory()
        {
            var factory = StorageGet(FACTORY_KEY);
            return factory?.Length == 20 ? (UInt160)factory : UInt160.Zero;
        }

        [Safe]
        public static UInt160 GetBonding()
        {
            var bonding = StorageGet(BONDING_KEY);
            return bonding?.Length == 20 ? (UInt160)bonding : UInt160.Zero;
        }
        #endregion

        #region bNEO GAS Claim
        /// <summary>
        /// Claim accrued GAS reward from the bNEO reserve token and forward it to the factory.
        /// Only callable by the factory. No-op for pairs that are not bNEO pairs.
        /// </summary>
        public static bool ClaimGASFrombNEO()
        {
            UInt160 factory = GetFactory();
            Assert(Runtime.CallingScriptHash == factory, "ONLY_FACTORY");
            UInt160 bneo = (UInt160)Contract.Call(factory, "getBNeo", CallFlags.ReadOnly);

            UInt160 token0 = Token0;
            UInt160 token1 = Token1;
            UInt160 bneoToken = token0 == bneo ? token0 : (token1 == bneo ? token1 : UInt160.Zero);
            if (!bneoToken.IsAddress()) return false;

            UInt160 self = Runtime.ExecutingScriptHash;
            BigInteger beforeBalance = GAS.BalanceOf(self);

            // Sending 0 bNEO back to the bNEO contract triggers the GAS claim.
            bool ok = (bool)Contract.Call(
                bneoToken,
                "transfer",
                CallFlags.States | CallFlags.AllowCall | CallFlags.AllowNotify,
                new object[] { self, bneoToken, 0, null });
            Assert(ok, "CLAIM_FAILED", bneoToken);

            BigInteger afterBalance = GAS.BalanceOf(self);
            BigInteger claimed = afterBalance - beforeBalance;
            if (claimed > 0)
            {
                bool transferOk = GAS.Transfer(self, factory, claimed);
                Assert(transferOk, "GAS_TRANSFER_FAILED");
            }
            return true;
        }
        #endregion

        #region KLast
        [Safe]
        public static BigInteger GetKLast()
        {
            var raw = StorageGet(KLAST_KEY);
            return raw == null ? 0 : (BigInteger)raw;
        }

        private static void SetKLast(BigInteger k)
        {
            Storage.Put(KLAST_KEY, k);
        }
        #endregion

        #region NEP17 Standard Methods
        [Safe]
        public static ulong Decimals() => 8;
        [Safe]
        public static string Symbol() => (string)Storage.Get(SYMBOL_KEY);
        [Safe]
        public static BigInteger TotalSupply() => TotalSupplyStorage.Get();
        [Safe]
        public static BigInteger BalanceOf(UInt160 account)
        {
            Assert(account.IsValid, "Invalid Account");
            return AssetStorage.Get(account);
        }

        public static bool Transfer(UInt160 from, UInt160 to, BigInteger amount, object data)
        {
            Assert(from.IsValid && to.IsValid, "Invalid From Or ToAddress.");
            Assert(amount > 0, "The parameter amount MUST be greater than 0.");
            Assert(Runtime.CheckWitness(from), "No Authorization.");
            Assert(AssetStorage.Get(from) >= amount, "Insufficient Balance.");
            if (from == to) return true;

            AssetStorage.Reduce(from, amount);
            AssetStorage.Increase(to, amount);

            onTransfer(from, to, amount);

            // Validate payable
            if (ContractManagement.GetContract(to) != null)
                Contract.Call(to, "onNEP17Payment", CallFlags.All, new object[] { from, amount, data });
            return true;
        }

        public static void OnNEP17Payment(UInt160 from, BigInteger amount, object data)
        {
        }
        #endregion

        #region Storage Helpers
        public static class TotalSupplyStorage
        {
            public static readonly string mapName = "contract";
            public static readonly string key = "totalSupply";

            public static void Increase(BigInteger value) => Put(Get() + value);
            public static void Reduce(BigInteger value) => Put(Get() - value);
            public static void Put(BigInteger value) => new StorageMap(Storage.CurrentContext, mapName).Put(key, value);

            public static BigInteger Get()
            {
                var value = new StorageMap(Storage.CurrentContext, mapName).Get(key);
                return value is null ? 0 : (BigInteger)value;
            }
        }

        public static class AssetStorage
        {
            public static readonly string mapName = "asset";

            public static void Increase(UInt160 key, BigInteger value) => Put(key, Get(key) + value);

            public static void Reduce(UInt160 key, BigInteger value)
            {
                var oldValue = Get(key);
                if (oldValue == value)
                    Remove(key);
                else
                    Put(key, oldValue - value);
            }

            public static void Put(UInt160 key, BigInteger value) => new StorageMap(Storage.CurrentContext, mapName).Put(key, value);

            public static BigInteger Get(UInt160 key)
            {
                var value = new StorageMap(Storage.CurrentContext, mapName).Get(key);
                return value is null ? 0 : (BigInteger)value;
            }

            public static void Remove(UInt160 key) => new StorageMap(Storage.CurrentContext, mapName).Delete(key);
        }

        public static class LockStorage
        {
            private static readonly string Map = "lock";
            private static readonly string Key = "entered";

            public static void Put(BigInteger value) => new StorageMap(Storage.CurrentContext, Map).Put(Key, value);

            public static BigInteger Get()
            {
                var value = new StorageMap(Storage.CurrentContext, Map).Get(Key);
                return value is null ? 0 : (BigInteger)value;
            }
        }

        private static ReservesData ReservePair
        {
            get
            {
                var raw = StorageGet(nameof(ReservePair));
                if (raw is null || raw.Length == 0)
                    return new ReservesData() { Reserve0 = 0, Reserve1 = 0 };
                return (ReservesData)StdLib.Deserialize(raw);
            }
            set => StoragePut(nameof(ReservePair), StdLib.Serialize(value));
        }

        private static PriceCumulative Cumulative
        {
            get
            {
                var raw = StorageGet(nameof(Cumulative));
                if (raw is null || raw.Length == 0)
                    return new PriceCumulative() { Price0CumulativeLast = 0, Price1CumulativeLast = 0, BlockTimestampLast = 0 };
                return (PriceCumulative)StdLib.Deserialize(raw);
            }
            set => StoragePut(nameof(Cumulative), StdLib.Serialize(value));
        }
        #endregion

        #region Internal Helper Methods
        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                onFault(message, null);
                ExecutionEngine.Assert(false);
            }
        }

        private static void Assert(bool condition, string message, params object[] data)
        {
            if (!condition)
            {
                onFault(message, data);
                ExecutionEngine.Assert(false);
            }
        }

        private static ByteString StorageGet(ByteString key) => Storage.Get(Storage.CurrentContext, key);

        private static void StoragePut(ByteString key, ByteString value) => Storage.Put(Storage.CurrentContext, key, value);
        private static void StoragePut(ByteString key, BigInteger value) => Storage.Put(Storage.CurrentContext, key, value);

        private static void Enter()
        {
            Assert(LockStorage.Get() == 0, "LOCKED");
            LockStorage.Put(1);
        }

        private static void Exit() => LockStorage.Put(0);

        private static BigInteger Min(BigInteger a, BigInteger b) => a < b ? a : b;

        private static void MintLp(UInt160 account, BigInteger amount)
        {
            AssetStorage.Increase(account, amount);
            TotalSupplyStorage.Increase(amount);
            onTransfer(null, account, amount);
        }

        private static void BurnLp(UInt160 account, BigInteger amount)
        {
            AssetStorage.Reduce(account, amount);
            TotalSupplyStorage.Reduce(amount);
            onTransfer(account, null, amount);
        }

        private static BigInteger TokenBalance(UInt160 token, UInt160 owner)
        {
            return (BigInteger)Contract.Call(token, "balanceOf", CallFlags.ReadOnly, new object[] { owner });
        }

        private static void TransferToken(UInt160 token, UInt160 from, UInt160 to, BigInteger amount, byte[] data = null)
        {
            //bool ok = (bool)Contract.Call(token, "transfer", CallFlags.All, new object[] { from, to, amount, data });
            bool ok = (bool)Contract.Call(
               token, "transfer",
               CallFlags.States | CallFlags.AllowCall | CallFlags.AllowNotify,
               new object[] { from, to, amount, data });
            Assert(ok, "TOKEN_TRANSFER_FAILED", token);
        }
        #endregion

        #region Events
        public static event SyncedEvent Synced;
        public delegate void SyncedEvent(BigInteger balance0, BigInteger balance1);

        public static event MintedEvent Minted;
        public delegate void MintedEvent(UInt160 caller, BigInteger amount0, BigInteger amount1, BigInteger liquidity);

        public static event BurnedEvent Burned;
        public delegate void BurnedEvent(UInt160 caller, BigInteger liquidity, BigInteger amount0, BigInteger amount1, UInt160 to);

        public static event SwappedEvent Swapped;
        public delegate void SwappedEvent(UInt160 caller, BigInteger amount0In, BigInteger amount1In, BigInteger amount0Out, BigInteger amount1Out, UInt160 to);

        public static event PairDeployedEvent PairDeployed;
        public delegate void PairDeployedEvent(UInt160 token0, UInt160 token1);

        [DisplayName("Transfer")]
        public static event OnTransferEvent onTransfer;
        public delegate void OnTransferEvent(UInt160 from, UInt160 to, BigInteger amount);

        [DisplayName("Fault")]
        public static event FaultEvent onFault;
        public delegate void FaultEvent(string message, params object[] args);
        #endregion
    }

    public static class Extensions
    {
        [OpCode(OpCode.PUSHDATA1, "0100")]
        [OpCode(OpCode.CAT)]
        [OpCode(OpCode.CONVERT, "21")]
        public static extern BigInteger ToUInteger(this UInt160 val);

        public static bool IsAddress(this UInt160 address)
        {
            return address.IsValid && !address.IsZero;
        }
    }

    public struct PriceCumulative
    {
        public BigInteger Price0CumulativeLast;
        public BigInteger Price1CumulativeLast;
        public BigInteger BlockTimestampLast;
    }

    public struct ReservesData
    {
        public BigInteger Reserve0;
        public BigInteger Reserve1;
    }
}
