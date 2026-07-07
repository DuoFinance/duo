using System.Numerics;
using Neo;
using Neo.SmartContract.Framework;
using Neo.SmartContract.Framework.Attributes;
using Neo.SmartContract.Framework.Services;

namespace DuoHoneypot
{
    [ManifestExtra("Description", "Re-entrancy attack honeypot for testing")]
    [ContractPermission("*", new[] { "*" })]
    public class DuoHoneypotContract : SmartContract
    {
        public static void _deploy(object data, bool update)
        {
            Assert(!update, "Not Upgradable.");
            object[] args = (object[])data;
            Storage.Put(Storage.CurrentContext, "pair", (UInt160)args[0]);
            Storage.Put(Storage.CurrentContext, "recipient", (UInt160)args[1]);
        }

        public static void OnNEP17Payment(UInt160 from, BigInteger amount, object data)
        {
            UInt160 pair = (UInt160)Storage.Get(Storage.CurrentContext, "pair");
            UInt160 recipient = (UInt160)Storage.Get(Storage.CurrentContext, "recipient");
            Contract.Call(pair, "swap", CallFlags.All, new object[] { BigInteger.Zero, amount, recipient, null });
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
                ExecutionEngine.Assert(false);
        }
    }
}
