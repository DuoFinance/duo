using System;
using System.Numerics;
using Neo;
using System.ComponentModel;
using Neo.SmartContract.Framework;
using Neo.SmartContract.Framework.Attributes;
using Neo.SmartContract.Framework.Services;

namespace DuoLaunchpad
{
    [ManifestExtra("Description", "DuoLaunchpad Series")]
    [SupportedStandards("NEP-17")]
    [ContractSourceCode("https://github.com/DuoFinance/duoSwap.git")]
    [ContractPermission("*", new[] { "*" })]

    public class DuoLaunchpad : Nep17Token
    {
        #region Storage
        private static StorageMap ContractMetadata => new StorageMap(Storage.CurrentContext, "Metadata");
        private const string INIT_KEY = "Init";
        private const string OWNER_KEY = "Owner";
        private const string DECIMALS_KEY = "Decimals";
        private const string SYMBOL_KEY = "Symbol";

        private static Transaction Tx => (Transaction)Runtime.ScriptContainer;

        #endregion

        #region Deployment

        /// <summary>
        /// Deployment, the contract is not upgradable.
        /// </summary>
        /// <param name="data"></param>
        /// <param name="update"></param>
        /// <exception cref="Exception"></exception>
        public static void _deploy(object data, bool update)
        {
            Assert(!update, "Not Upgradable");
            Assert(Tx.Sender.IsValid && !Tx.Sender.IsZero, "Invalid Sender.");

            ContractMetadata.Put(OWNER_KEY, Tx.Sender);
            ContractMetadata.Put(INIT_KEY, 0);
        }

        /// <summary>
        /// Initialize the token info and mint total supply to the sender or the owner,
        /// only can be called once.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="decimals"></param>
        /// <param name="symbol"></param>
        /// <param name="totalSupply"></param>
        public static void Initialise(UInt160 sender, Byte decimals, string symbol, BigInteger totalSupply)
        {
            BigInteger initialised = (BigInteger)ContractMetadata.Get(INIT_KEY);
            Assert(initialised == 0, "Initialised.");
            Assert(sender.IsValid && !sender.IsZero, "Invalid Sender.");

            ContractMetadata.Put(OWNER_KEY, sender);
            ContractMetadata.Put(DECIMALS_KEY, decimals);
            ContractMetadata.Put(SYMBOL_KEY, symbol);

            Mint(sender, totalSupply);
            ContractMetadata.Put(INIT_KEY, 1);
        }

        #endregion

        #region Token Info

        /// <summary>
        /// Decimals and Symbol
        /// </summary>
        /// <returns></returns>
        [Safe]
        public override byte Decimals() => (byte)(BigInteger)ContractMetadata.Get(DECIMALS_KEY);

        [Safe]
        public override string Symbol() => (string)ContractMetadata.Get(SYMBOL_KEY);

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
