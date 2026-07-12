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
            Assert(feeTo.IsAddress(), "Invalid Address.");
            Assert(Runtime.CheckWitness(GetSetter()), "Forbidden.");
            StoragePut(FEETO_KEY, feeTo);
            onSetFeeTo(feeTo);
            return true;
        }
        #endregion

        #region DeployFee
        public static BigInteger GetDeployFee() => ContractManagement.GetMinimumDeploymentFee() / 100;

        public static bool WithdrawDeployFee()
        {
            Assert(Runtime.CheckWitness(GetSetter()), "Forbidden.");

            UInt160 feeTo = GetFeeTo();
            Assert(feeTo.IsAddress(), "FeeTo Not Set.");

            BigInteger balance = (BigInteger)Contract.Call(
                GAS.Hash,
                "balanceOf",
                CallFlags.ReadOnly,
                new object[] { Runtime.ExecutingScriptHash });
            Assert(balance > 0, "Insufficient Balance.");

            bool result = (bool)Contract.Call(
                GAS.Hash,
                "transfer",
                CallFlags.All,
                new object[] { Runtime.ExecutingScriptHash, feeTo, balance, null });
            Assert(result, "Withdraw Failed.");

            onWithdrawFee(feeTo, balance);
            return true;
        }
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
            Assert(Runtime.CheckWitness(GetSetter()), "Forbidden.");
            Assert(router != null && router.IsValid && !router.IsZero, "Invalid Router.");
            Assert(GetRouter() == UInt160.Zero, "Router Locked.");
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
            Assert(Runtime.CheckWitness(GetSetter()), "Forbidden.");
            Assert(bonding != null && bonding.IsValid && !bonding.IsZero, "Invalid Bonding.");
            Assert(GetBonding() == UInt160.Zero, "Bonding Locked.");
            Storage.Put(Storage.CurrentContext, BONDING_KEY, bonding);
            onSetBonding(bonding);
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

        [DisplayName("OwnershipTransferred")]
        public static event OnOwnershipTransferredEvent onOwnershipTransferred;
        public delegate void OnOwnershipTransferredEvent(UInt160 current, UInt160 newSetter);

        [DisplayName("SetFeeTo")]
        public static event SetFeeToEvent onSetFeeTo;
        public delegate void SetFeeToEvent(UInt160 feeTo);

        [DisplayName("WithdrawFee")]
        public static event WithdrawFeeEvent onWithdrawFee;
        public delegate void WithdrawFeeEvent(UInt160 feeTo, BigInteger amount);

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
