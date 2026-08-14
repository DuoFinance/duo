using System;
using System.Numerics;
using System.ComponentModel;
using Neo.SmartContract.Framework;
using Neo.SmartContract.Framework.Attributes;
using Neo.SmartContract.Framework.Services;

namespace DuoLaunchpad
{
    [ManifestExtra("Description", "Duo Bonding Curve Launchpad Series")]
    [SupportedStandards("NEP-17")]
    [ContractSourceCode("https://github.com/DuoFinance/duoSwap.git")]
    [ContractPermission("*")]

    public class DuoLaunchpad : Nep17Token
    {
        #region Storage
        private static StorageMap ContractMetadata => new StorageMap(Storage.CurrentContext, "Metadata");
        private const string OWNER_KEY = "Owner";
        private const string DECIMALS_KEY = "Decimals";
        private const string SYMBOL_KEY = "Symbol";

        #endregion

        #region Deployment

        /// <summary>
        /// Deployment and atomic token initialization, the contract is not upgradable.
        /// </summary>
        /// <param name="data">{ owner, decimals, symbol, totalSupply }</param>
        /// <param name="update"></param>
        public static void _deploy(object data, bool update)
        {
            Assert(!update, "Not Upgradable");

            Assert(data != null, "Invalid Data.");
            object[] args = (object[])data;
            Assert(args.Length == 4, "Invalid Data.");

            UInt160 sender = (UInt160)args[0];
            BigInteger decimalsValue = (BigInteger)args[1];
            string symbol = (string)args[2];
            BigInteger totalSupply = (BigInteger)args[3];

            Assert(sender.IsValid && !sender.IsZero, "Invalid Sender.");
            Assert(decimalsValue >= 0 && decimalsValue <= 255, "Invalid Decimals.");
            Assert(symbol != null && symbol.Length > 0, "Invalid Symbol.");
            Assert(totalSupply > 0, "Invalid Total Supply.");

            ContractMetadata.Put(OWNER_KEY, sender);
            ContractMetadata.Put(DECIMALS_KEY, (byte)decimalsValue);
            ContractMetadata.Put(SYMBOL_KEY, symbol);

            Mint(sender, totalSupply);
        }

        #endregion

        #region Token Info

        /// <summary>
        /// Decimals and Symbol
        /// </summary>
        /// <returns></returns>
        [Safe]
        public override byte Decimals => (byte)(BigInteger)ContractMetadata.Get(DECIMALS_KEY);

        [Safe]
        public override string Symbol => (string)ContractMetadata.Get(SYMBOL_KEY);

        #endregion

        #region Helper
        /// <summary>
        /// Assert helper, trigger event for front-end to listen when assertion failed.
        /// </summary>
        /// <param name="condition"></param>
        /// <param name="message"></param>
        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                onFault(message, null);
                ExecutionEngine.Assert(false);
            }
        }

        /// <summary>
        /// Fault event, will be triggered when assertion failed.
        /// </summary>
        [DisplayName("Fault")]
        public static event FaultEvent onFault;
        public delegate void FaultEvent(string message, params object[] args);

        #endregion
    }
}
