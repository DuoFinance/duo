using System.Numerics;
using Neo;
using Neo.SmartContract.Framework;
using Neo.SmartContract.Framework.Attributes;
using Neo.SmartContract.Framework.Native;
using Neo.SmartContract.Framework.Services;
using System.ComponentModel;

namespace DuoSwapFactory
{
    public partial class DuoSwapFactoryContract : SmartContract
    {
        #region Constants & Storage
        private const string SETTER_KEY = "Setter";
        private const string FEETO_KEY = "FeeTo";
        private const string ROUTER_KEY = "Router";
        private const string BONDING_KEY = "Bonding";
        private const string GOVERNANCE_KEY = "GovernanceToken";
        private const string BNEO_KEY = "BNeo";
        private static readonly byte[] PAIR_PREFIX = new byte[] { 0x01 };
        #endregion

        #region Deploy
        /// <summary>
        /// Deployment, the contract is not upgradable.
        /// </summary>
        public static void _deploy(object data, bool update)
        {
            Assert(!update, "Not Upgradable.");
            UInt160 setter = (UInt160)data;
            Assert(setter.IsValid && !setter.IsZero, "Invalid Setter Address.");
            StoragePut(SETTER_KEY, setter);
        }
        #endregion

        #region Setter (Admin)
        [Safe]
        public static UInt160 GetSetter() => (UInt160)Storage.Get(Storage.CurrentContext, SETTER_KEY);

        public static bool TransferOwnershipOfSetter(UInt160 newSetter)
        {
            UInt160 current = GetSetter();
            Assert(current.IsValid && current != UInt160.Zero, "Setter Not Initialized.");
            Assert(Runtime.CheckWitness(current), "Forbidden.");
            Assert(newSetter.IsValid && newSetter != UInt160.Zero, "Invalid Address.");
            Assert(!newSetter.Equals(current), "Same Setter.");

            StoragePut(SETTER_KEY, newSetter);
            onOwnershipTransferred(current, newSetter);
            return true;
        }
        #endregion

        #region FeeTo Address
        [Safe]
        public static UInt160 GetFeeTo()
        {
            var address = StorageGet(FEETO_KEY);
            return address?.Length == 20 ? (UInt160)address : UInt160.Zero;
        }

        public static bool SetFeeTo(UInt160 feeTo)
        {
            Assert(feeTo.IsValid, "Invalid Address.");
            Assert(Runtime.CheckWitness(GetSetter()), "Forbidden.");
            StoragePut(FEETO_KEY, feeTo);
            onSetFeeTo(feeTo);
            return true;
        }
        #endregion

        #region DeployFee
        public static BigInteger GetDeployFee() => ContractManagement.GetMinimumDeploymentFee() / 10;
        #endregion

        #region Router
        [Safe]
        public static UInt160 GetRouter()
        {
            var router = StorageGet(ROUTER_KEY);
            return router?.Length == 20 ? (UInt160)router : UInt160.Zero;
        }

        public static void SetRouter(UInt160 router)
        {
            Assert(router.IsAddress(), "Invalid Router.");
            Assert(Runtime.CheckWitness(GetSetter()), "Forbidden.");
            Assert(GetRouter().IsZero, "ALREADY_SET_IMMUTABLE");
            Storage.Put(Storage.CurrentContext, ROUTER_KEY, router);
            onSetRouter(router);
        }
        #endregion

        #region Bonding
        [Safe]
        public static UInt160 GetBonding()
        {
            var bonding = StorageGet(BONDING_KEY);
            return bonding?.Length == 20 ? (UInt160)bonding : UInt160.Zero;
        }

        public static void SetBonding(UInt160 bonding)
        {
            Assert(bonding.IsAddress(), "Invalid Bonding.");
            Assert(Runtime.CheckWitness(GetSetter()), "Forbidden.");
            Assert(GetBonding().IsZero, "ALREADY_SET_IMMUTABLE");
            Storage.Put(Storage.CurrentContext, BONDING_KEY, bonding);
            onSetBonding(bonding);
        }
        #endregion

        #region Governance
        [Safe]
        public static UInt160 GetGovernanceToken()
        {
            var governance = StorageGet(GOVERNANCE_KEY);
            return governance?.Length == 20 ? (UInt160)governance : UInt160.Zero;
        }

        public static void SetGovernanceToken(UInt160 tokenHash)
        {
            Assert(tokenHash.IsAddress(), "Invalid Bonding.");
            Assert(Runtime.CheckWitness(GetSetter()), "ONLY_SETTER");
            Assert(GetGovernanceToken().IsZero, "ALREADY_SET_IMMUTABLE");
            StoragePut(GOVERNANCE_KEY, tokenHash);
            onGovernanceTokenSet(tokenHash);
        }
        #endregion

        #region BNeo
        /// <summary>
        /// The bNEO (wrapped NEO) contract hash used by pairs to claim GAS rewards.
        /// Updatable by setter, so the wrapped bNEO version can be switched.
        /// </summary>
        [Safe]
        public static UInt160 GetBNeo()
        {
            var bneo = StorageGet(BNEO_KEY);
            return bneo?.Length == 20 ? (UInt160)bneo : UInt160.Zero;
        }

        public static bool SetBNeo(UInt160 bneo)
        {
            Assert(bneo.IsValid, "Invalid Address.");
            Assert(Runtime.CheckWitness(GetSetter()), "Forbidden.");
            StoragePut(BNEO_KEY, bneo);
            onSetBNeo(bneo);
            return true;
        }
        #endregion

        #region Claim GAS From BNeo Pair
        /// <summary>
        /// Claim GAS from a bNEO pair and transfer the factory balance to the recipient.
        /// Pass UInt160.Zero as pair to withdraw the factory balance without a pair claim.
        /// </summary>
        public static bool ClaimGAS(UInt160 pair, UInt160 to)
        {
            Assert(Runtime.CheckWitness(GetSetter()), "Forbidden.");
            Assert(to.IsAddress(), "Invalid Address.");
            bool withdrawOnly = pair == UInt160.Zero;
            Assert(withdrawOnly || pair.IsAddress(), "Invalid Pair Address.");

            if (withdrawOnly)
            {
                BigInteger withdrawBalance = GAS.BalanceOf(Runtime.ExecutingScriptHash);
                if (withdrawBalance == 0) return true;
                bool withdrawResult = GAS.Transfer(Runtime.ExecutingScriptHash, to, withdrawBalance);
                Assert(withdrawResult, "Withdraw Failed.");
                return true;
            }

            bool ok = (bool)Contract.Call(pair, "claimGASFrombNEO", CallFlags.All, new object[0]);
            // Non-bNEO pairs deliberately return false from claimGASFrombNEO.
            // Keep this operation a no-op for those pairs and do not withdraw
            // unrelated GAS already held by the factory.
            if (!ok) return true;

            BigInteger balance = GAS.BalanceOf(Runtime.ExecutingScriptHash);
            if (balance == 0) return true;

            bool result = GAS.Transfer(Runtime.ExecutingScriptHash, to, balance);
            Assert(result, "Withdraw Failed.");
            return true;
        }


        #endregion

        #region Events
        [DisplayName("StorePair")]
        public static event StorePairEvent onStorePair;
        public delegate void StorePairEvent(UInt160 tokenA, UInt160 tokenB, UInt160 pairHash);

        [DisplayName("SetRouter")]
        public static event SetRouterEvent onSetRouter;
        public delegate void SetRouterEvent(UInt160 router);

        [DisplayName("SetBonding")]
        public static event SetBondingEvent onSetBonding;
        public delegate void SetBondingEvent(UInt160 bonding);

        [DisplayName("SetGovernanceToken")]
        public static event SetGovernanceTokenEvent onGovernanceTokenSet;
        public delegate void SetGovernanceTokenEvent(UInt160 tokenHash);

        [DisplayName("SetBNeo")]
        public static event SetBNeoEvent onSetBNeo;
        public delegate void SetBNeoEvent(UInt160 bneo);

        [DisplayName("OwnershipTransferred")]
        public static event OnOwnershipTransferredEvent onOwnershipTransferred;
        public delegate void OnOwnershipTransferredEvent(UInt160 current, UInt160 newSetter);

        [DisplayName("SetFeeTo")]
        public static event SetFeeToEvent onSetFeeTo;
        public delegate void SetFeeToEvent(UInt160 feeTo);

        [DisplayName("Fault")]
        public static event FaultEvent onFault;
        public delegate void FaultEvent(string message, params object[] args);
        #endregion

        #region Private Helpers
        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                onFault(message, null);
                ExecutionEngine.Assert(false);
            }
        }

        private static void Assert(bool condition, string message, object data = null)
        {
            if (!condition)
            {
                onFault(message, data);
                ExecutionEngine.Assert(false);
            }
        }

        [OpCode(OpCode.APPEND)]
        private static extern void Append<T>(T[] array, T newItem);

        private static Iterator StorageFind(byte[] prefix)
        {
            return Storage.Find(Storage.CurrentContext, prefix, FindOptions.RemovePrefix);
        }

        private static ByteString StorageGet(ByteString key)
        {
            return Storage.Get(Storage.CurrentContext, key);
        }

        private static ByteString StorageGet(byte[] key)
        {
            return Storage.Get(Storage.CurrentContext, key);
        }

        private static void StoragePut(ByteString key, ByteString value)
        {
            Storage.Put(Storage.CurrentContext, key, value);
        }

        private static void StoragePut(byte[] key, ByteString value)
        {
            Storage.Put(Storage.CurrentContext, key, value);
        }
        #endregion

        #region Models
        public struct KeyValue
        {
            public byte[] Key;
            public UInt160 Value;
        }

        public struct TradingPair
        {
            public UInt160 TokenA;
            public UInt160 TokenB;
            public UInt160 Pair;
        }
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
}
