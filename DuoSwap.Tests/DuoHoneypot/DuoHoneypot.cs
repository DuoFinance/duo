using System.Numerics;
using Neo;
using Neo.SmartContract.Framework;
using Neo.SmartContract.Framework.Attributes;
using Neo.SmartContract.Framework.Native;
using Neo.SmartContract.Framework.Services;

namespace DuoHoneypot
{
    [ManifestExtra("Description", "Re-entrancy attack honeypot + Router caller for testing")]
    [ContractPermission("*", new[] { "*" })]
    public class DuoHoneypotContract : SmartContract
    {
        public static void _deploy(object data, bool update)
        {
            Assert(!update, "Not Upgradable.");
            object[] args = (object[])data;
            Storage.Put(Storage.CurrentContext, "pair", (UInt160)args[0]);
            Storage.Put(Storage.CurrentContext, "recipient", (UInt160)args[1]);
            if (args.Length > 2)
                Storage.Put(Storage.CurrentContext, "router", (UInt160)args[2]);
        }

        public static void OnNEP17Payment(UInt160 from, BigInteger amount, object data)
        {
            if (data == null) return;
            UInt160 pair = (UInt160)Storage.Get(Storage.CurrentContext, "pair");
            UInt160 recipient = (UInt160)Storage.Get(Storage.CurrentContext, "recipient");
            Contract.Call(pair, "swap", CallFlags.All, new object[] { BigInteger.Zero, amount, recipient, null });
        }

        // ── Router contract-caller support ──

        public static bool approvedTransfer(UInt160 token, UInt160 to, BigInteger amount, object data)
        {
            Assert(amount > 0, "INVALID_AMOUNT");
            Assert(to.IsValid && !to.IsZero, "INVALID_TO");

            bool ok = (bool)Contract.Call(
                token,
                "transfer",
                CallFlags.All,
                new object[] { Runtime.ExecutingScriptHash, to, amount, null });
            Assert(ok, "TRANSFER_FAILED");
            return true;
        }

        public static BigInteger[] RouterCall_AddLiquidity(
            UInt160 router, UInt160 tokenA, UInt160 tokenB,
            BigInteger amountADesired, BigInteger amountBDesired,
            BigInteger amountAMin, BigInteger amountBMin, BigInteger deadline)
        {
            return (BigInteger[])Contract.Call(router, "addLiquidity", CallFlags.All,
                new object[] { tokenA, tokenB, amountADesired, amountBDesired, amountAMin, amountBMin, deadline });
        }

        public static BigInteger[] RouterCall_RemoveLiquidity(
            UInt160 router, UInt160 tokenA, UInt160 tokenB,
            BigInteger liquidity, BigInteger amountAMin, BigInteger amountBMin, BigInteger deadline)
        {
            return (BigInteger[])Contract.Call(router, "removeLiquidity", CallFlags.All,
                new object[] { tokenA, tokenB, liquidity, amountAMin, amountBMin, deadline });
        }

        public static bool RouterCall_SwapExactTokensForTokens(
            UInt160 router, BigInteger amountIn, BigInteger amountOutMin,
            UInt160[] paths, BigInteger deadline)
        {
            return (bool)Contract.Call(router, "swapExactTokensForTokens", CallFlags.All,
                new object[] { amountIn, amountOutMin, paths, deadline });
        }

        public static bool RouterCall_SwapTokensForExactTokens(
            UInt160 router, BigInteger amountInMax, BigInteger amountOut,
            UInt160[] paths, BigInteger deadline)
        {
            return (bool)Contract.Call(router, "swapTokensForExactTokens", CallFlags.All,
                new object[] { amountInMax, amountOut, paths, deadline });
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
                ExecutionEngine.Assert(false);
        }
    }
}
