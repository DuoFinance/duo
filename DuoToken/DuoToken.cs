using System.ComponentModel;
using System.Numerics;
using Neo;
using Neo.SmartContract.Framework;
using Neo.SmartContract.Framework.Attributes;
using Neo.SmartContract.Framework.Services;

namespace DuoToken
{
    [ManifestExtra("Description", "Duo Token Contract")]
    [SupportedStandards("NEP-17")]
    [ContractSourceCode("https://github.com/DuoFinance/duoSwap.git")]
    [ContractPermission("*", new[] { "*" })]
    public class DuoTokenContract : Nep17Token
    {
        private static StorageMap Metadata => new StorageMap(Storage.CurrentContext, "Metadata");
        private static Transaction Tx => (Transaction)Runtime.ScriptContainer;

        private const string INIT_KEY = "Init";
        private const string OWNER_KEY = "Owner";
        private const string TREASURY_KEY = "Treasury";


        public static void _deploy(object data, bool update)
        {
            Assert(!update, "Not Upgradable.");
            Assert(Tx.Sender.IsValid && !Tx.Sender.IsZero, "Invalid Sender.");

            Metadata.Put(OWNER_KEY, Tx.Sender);
            Metadata.Put(INIT_KEY, 0);
        }

        public static void Initialise(
            UInt160 treasury,
            BigInteger totalSupply)
        {
            Assert(Runtime.CheckWitness(GetOwner()), "Forbidden.");
            Assert((BigInteger)Metadata.Get(INIT_KEY) == 0, "Initialised.");
            Assert(treasury.IsValid && !treasury.IsZero, "Invalid Treasury.");
            
            Metadata.Put(INIT_KEY, 1);
            Metadata.Put(TREASURY_KEY, treasury);

            Mint(treasury, totalSupply);
            OnInitialised(treasury, totalSupply);
        }

        [Safe]
        public static UInt160 GetOwner()
        {
            ByteString owner = Metadata.Get(OWNER_KEY);
            return owner?.Length == 20 ? (UInt160)owner : UInt160.Zero;
        }

        public static bool TransferOwnership(UInt160 newOwner)
        {
            UInt160 currentOwner = GetOwner();
            Assert(Runtime.CheckWitness(currentOwner), "Forbidden.");
            Assert(newOwner.IsValid && !newOwner.IsZero, "Invalid Owner.");
            Assert(newOwner != currentOwner, "Same Owner.");

            Metadata.Put(OWNER_KEY, newOwner);
            OnOwnershipTransferred(currentOwner, newOwner);
            return true;
        }

        [Safe]
        public static UInt160 GetTreasury()
        {
            ByteString treasury = Metadata.Get(TREASURY_KEY);
            return treasury?.Length == 20 ? (UInt160)treasury : UInt160.Zero;
        }

        [Safe]
        public static bool IsInitialised() => (BigInteger)Metadata.Get(INIT_KEY) != 0;

        [Safe]
        public override byte Decimals() => 8;

        [Safe]
        public override string Symbol() => "DUO";


        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                OnFault(message, null);
                ExecutionEngine.Assert(false);
            }
        }

        [DisplayName("Initialised")]
        public static event InitialisedEvent OnInitialised;
        public delegate void InitialisedEvent(
            UInt160 treasury,
            BigInteger totalSupply);

        [DisplayName("OwnershipTransferred")]
        public static event OwnershipTransferredEvent OnOwnershipTransferred;
        public delegate void OwnershipTransferredEvent(UInt160 previousOwner, UInt160 newOwner);

        [DisplayName("Fault")]
        public static event FaultEvent OnFault;
        public delegate void FaultEvent(string message, params object[] args);
    }
}
