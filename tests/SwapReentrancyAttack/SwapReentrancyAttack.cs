using System.Numerics;
using Neo.SmartContract.Framework;
using Neo.SmartContract.Framework.Native;
using Neo.SmartContract.Framework.Services;
using Neo.SmartContract.Framework.Attributes;

namespace SwapReentrancyAttack
{
    [ManifestExtra("Description", "Swap Reentrancy Attack Test Contract")]
    [ContractPermission("*", new[] { "swap", "getToken0", "getToken1", "balanceOf", "transfer" })]
    public class SwapReentrancyAttack : SmartContract
    {
        private static StorageMap Store => new(Storage.CurrentContext, "SR");

        public static void _deploy(object data, bool update)
        {
            if (update) return;
            object[] args = (object[])data;
            Store.Put("pair", (UInt160)args[0]);
            Store.Put("meme", (UInt160)args[1]);
            Store.Put("stage", 0);
        }

        private static UInt160 GetPair() => (UInt160)Store.Get("pair");
        private static UInt160 GetMeme() => (UInt160)Store.Get("meme");
        private static int GetStage() => (int)(BigInteger)Store.Get("stage");
        private static void SetStage(int s) => Store.Put("stage", s);

        [Safe]
        public static int GetAttackStage() => GetStage();

        public static void Attack()
        {
            UInt160 pair = GetPair();
            UInt160 meme = GetMeme();
            UInt160 self = Runtime.ExecutingScriptHash;

            // Determine which token is token0 on the pair
            UInt160 token0 = (UInt160)Contract.Call(pair, "getToken0", CallFlags.ReadOnly);

            BigInteger gasPayment = 10000000000;
            BigInteger memeOut = 1000000000;

            // Send GAS to the pair as payment
            SetStage(1);
            if (gasPayment > 0)
            {
                bool ok = GAS.Transfer(self, pair, gasPayment, null);
                ExecutionEngine.Assert(ok);
            }

            // Call swap: ask for meme tokens out, with self as recipient
            // This triggers meme.transfer(pair, self, memeOut) → calls self.OnNEP17Payment
            SetStage(2);
            if (token0 == meme)
            {
                Contract.Call(pair, "swap", CallFlags.All,
                    new object[] { memeOut, 0, self, new byte[] { } });
            }
            else
            {
                Contract.Call(pair, "swap", CallFlags.All,
                    new object[] { 0, memeOut, self, new byte[] { } });
            }

            SetStage(3);
        }

        public static void OnNEP17Payment(UInt160 from, BigInteger amount, object[] data)
        {
            int stage = GetStage();
            if (stage != 2) return;

            UInt160 caller = Runtime.CallingScriptHash;
            UInt160 meme = GetMeme();
            UInt160 pair = GetPair();
            UInt160 self = Runtime.ExecutingScriptHash;

            // Only react when receiving the meme token from the pair
            if (caller != meme) return;

            // Attempt reentrancy: call pair.swap again → should FAULT with "LOCKED"
            UInt160 token0 = (UInt160)Contract.Call(pair, "getToken0", CallFlags.ReadOnly);
            if (token0 == meme)
            {
                Contract.Call(pair, "swap", CallFlags.All,
                    new object[] { 1, 0, self, new byte[] { } });
            }
            else
            {
                Contract.Call(pair, "swap", CallFlags.All,
                    new object[] { 0, 1, self, new byte[] { } });
            }
        }
    }
}
