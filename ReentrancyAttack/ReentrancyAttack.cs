using System.Numerics;
using Neo.SmartContract.Framework;
using Neo.SmartContract.Framework.Native;
using Neo.SmartContract.Framework.Services;
using Neo.SmartContract.Framework.Attributes;

namespace ReentrancyAttack
{
    [ManifestExtra("Description", "Reentrancy Attack Test Contract")]
    [ContractPermission("*", new[] { "transfer", "balanceOf", "createSale", "onNEP17Payment" })]
    public class ReentrancyAttack : SmartContract
    {
        private static StorageMap Store => new(Storage.CurrentContext, "RA");

        public static void _deploy(object data, bool update)
        {
            if (update) return;
            object[] args = (object[])data;
            Store.Put("bonding", (UInt160)args[0]);
            Store.Put("token", (UInt160)args[1]);
            Store.Put("stage", 0);
        }

        private static UInt160 GetBonding() => (UInt160)Store.Get("bonding");
        private static UInt160 GetToken() => (UInt160)Store.Get("token");
        private static int GetStage() => (int)(BigInteger)Store.Get("stage");
        private static void SetStage(int s) => Store.Put("stage", s);
        private static int GetReentryType() => (int)(BigInteger)Store.Get("reentryType");
        private static void SetReentryType(int t) => Store.Put("reentryType", t);

        // 1 = reenter via buy (GAS transfer during sell)
        // 2 = reenter via createSale (GAS callback during sell)
        // 3 = reenter from the token recipient callback during buy
        public static void SetReentryMode(int mode)
        {
            var tx = (Transaction)Runtime.Transaction;
            ExecutionEngine.Assert(Runtime.CheckWitness(tx.Sender));
            ExecutionEngine.Assert(mode >= 1 && mode <= 3);
            SetReentryType(mode);
        }

        [Safe]
        public static int GetAttackStage() => GetStage();

        // Adversarial entry point: call bonding's callback without GAS/token
        // being the calling contract. The bonding contract must reject it.
        public static void DirectCallback()
        {
            UInt160 bonding = GetBonding();
            UInt160 self = Runtime.ExecutingScriptHash;
            Contract.Call(bonding, "onNEP17Payment", CallFlags.All,
                new object[] { self, 1, new object[] { 0 } });
        }

        public static void Attack()
        {
            UInt160 bonding = GetBonding();
            UInt160 token = GetToken();
            UInt160 self = Runtime.ExecutingScriptHash;

            BigInteger gasBal = (BigInteger)Contract.Call(GAS.Hash, "balanceOf", CallFlags.ReadOnly, new object[] { self });
            ExecutionEngine.Assert(gasBal >= 200000000);

            int reentryType = GetReentryType();

            // Mode 3 isolates buy -> token recipient callback reentrancy.
            if (reentryType == 3)
            {
                SetStage(1);
                bool buyOk = GAS.Transfer(self, bonding, 50000000,
                    new object[] { token });
                ExecutionEngine.Assert(buyOk);
                SetStage(3);
                return;
            }

            // Stage 1: Buy tokens from bonding curve (0.5 GAS)
            BigInteger buyAmount = 50000000;
            SetStage(1);
            bool ok = GAS.Transfer(self, bonding, buyAmount, new object[] { token });
            ExecutionEngine.Assert(ok);

            BigInteger tokenBal = (BigInteger)Contract.Call(token, "balanceOf", CallFlags.ReadOnly, new object[] { self });
            ExecutionEngine.Assert(tokenBal > 0);

            // Stage 2: Sell tokens back → triggers GAS.Transfer → our OnNEP17Payment
            SetStage(2);
            ok = (bool)Contract.Call(token, "transfer", CallFlags.All,
                new object[] { self, bonding, tokenBal, null });
            ExecutionEngine.Assert(ok);

            SetStage(3);
        }

        public static void OnNEP17Payment(UInt160 from, BigInteger amount, object[] data)
        {
            int stage = GetStage();
            if (stage != 2 && stage != 1) return;

            UInt160 caller = Runtime.CallingScriptHash;
            UInt160 bonding = GetBonding();
            UInt160 self = Runtime.ExecutingScriptHash;
            int reentryType = GetReentryType();
            if (reentryType == 0) reentryType = 1;

            if (reentryType == 3)
            {
                if (caller != GetToken()) return;
                Contract.Call(bonding, "onNEP17Payment", CallFlags.All,
                    new object[] { self, amount, new object[] { 0 } });
                return;
            }

            // Only react to GAS transfers (from bonding curve's sell)
            if (caller != GAS.Hash) return;

            if (reentryType == 1)
            {
                // Reenter via buy: send GAS back to bonding curve
                GAS.Transfer(self, bonding, 1, new object[] { GetToken() });
            }
            else if (reentryType == 2)
            {
                // Reenter via createSale: call createSale on bonding curve
                Contract.Call(bonding, "createSale", CallFlags.All,
                    new object[] { "Reentry", "REN", 10000000000, 8000, "", "", "", "" });
            }
        }
    }
}
