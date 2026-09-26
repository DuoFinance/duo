using System.Numerics;
using Neo;
using Neo.SmartContract.Framework;
using Neo.SmartContract.Framework.Attributes;
using Neo.SmartContract.Framework.Native;
using Neo.SmartContract.Framework.Services;

namespace DuoMockBNeo
{
    [ManifestExtra("Description", "Mock bNEO (wrapped NEO) with GAS claim for testing")]
    [SupportedStandards("NEP-17")]
    [ContractPermission("*", new[] { "*" })]
    public class DuoMockBNeoContract : SmartContract
    {
        public static void _deploy(object data, bool update)
        {
            Assert(!update, "Not Upgradable.");
        }

        [Safe]
        public static string Symbol() => "bNEO";

        [Safe]
        public static byte Decimals() => 8;

        [Safe]
        public static BigInteger TotalSupply()
        {
            return GetStorageValue(Storage.Get(Storage.CurrentContext, "totalSupply"));
        }

        [Safe]
        public static BigInteger BalanceOf(UInt160 account)
        {
            return GetStorageValue(Storage.Get(Storage.CurrentContext, account));
        }

        public static bool Transfer(UInt160 from, UInt160 to, BigInteger amount, object data)
        {
            Assert(from.IsValid && to.IsValid, "INVALID_ADDRESS");
            BigInteger fromBalance = GetStorageValue(Storage.Get(Storage.CurrentContext, from));
            Assert(fromBalance >= amount, "INSUFFICIENT_BALANCE");

            if (amount > 0)
            {
                Storage.Put(Storage.CurrentContext, from, fromBalance - amount);
                BigInteger toBalance = GetStorageValue(Storage.Get(Storage.CurrentContext, to));
                Storage.Put(Storage.CurrentContext, to, toBalance + amount);
            }

            // Simulate bNEO GAS claim: transfer GAS reward to the caller.
            if (to == Runtime.ExecutingScriptHash)
            {
                BigInteger gasBalance = (BigInteger)Contract.Call(
                    GAS.Hash,
                    "balanceOf",
                    CallFlags.ReadOnly,
                    new object[] { Runtime.ExecutingScriptHash });

                if (gasBalance > 0)
                {
                    bool ok = (bool)Contract.Call(
                        GAS.Hash,
                        "transfer",
                        CallFlags.All,
                        new object[] { Runtime.ExecutingScriptHash, from, gasBalance, null });
                    Assert(ok, "GAS_FORWARD_FAILED");
                }
            }
            return true;
        }

        public static void OnNEP17Payment(UInt160 from, BigInteger amount, object data)
        {
        }

        public static void Mint(UInt160 account, BigInteger amount)
        {
            Assert(account.IsValid && !account.IsZero, "INVALID_ACCOUNT");
            Assert(amount > 0, "INVALID_AMOUNT");
            BigInteger balance = GetStorageValue(Storage.Get(Storage.CurrentContext, account));
            Storage.Put(Storage.CurrentContext, account, balance + amount);
            BigInteger total = GetStorageValue(Storage.Get(Storage.CurrentContext, "totalSupply"));
            Storage.Put(Storage.CurrentContext, "totalSupply", total + amount);
        }

        private static BigInteger GetStorageValue(ByteString value)
        {
            return value?.Length > 0 ? (BigInteger)value : BigInteger.Zero;
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
                ExecutionEngine.Assert(false);
        }
    }
}
