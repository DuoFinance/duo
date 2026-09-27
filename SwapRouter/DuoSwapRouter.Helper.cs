using System;
using System.Numerics;
using System.ComponentModel;
using Neo;
using Neo.SmartContract.Framework;
using Neo.SmartContract.Framework.Attributes;
using Neo.SmartContract.Framework.Services;

namespace DuoSwapRouter
{
    public partial class DuoSwapRouterContract : SmartContract
    {
        #region Factory Storage Key
        private const string FACTORY_KEY = "Factory";
        #endregion

        #region Deploy
        /// <summary>
        /// Deployment, the contract is not upgradable.
        /// </summary>
        public static void _deploy(object data, bool update)
        {
            Assert(!update, "Not Upgradable.");
            Assert(data != null, "Invalid Data.");
            object[] args = (object[])data;
            UInt160 factory = (UInt160)args[0];
            Assert(factory.IsValid && !factory.IsZero, "Invalid Scripthash.");

            StoragePut(FACTORY_KEY, factory);
        }
        #endregion

        #region Factory Getter
        [Safe]
        public static UInt160 GetFactory()
        {
            var factory = StorageGet(FACTORY_KEY);
            return factory?.Length == 20 ? (UInt160)factory : UInt160.Zero;
        }
        #endregion

        #region Private Helper Methods
        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                onFault(message, null);
                ExecutionEngine.Assert(false);
            }
        }

        private static void Assert(bool condition, string message, params object[] data)
        {
            if (!condition)
            {
                onFault(message, data);
                ExecutionEngine.Assert(false);
            }
        }

        [Safe]
        public static UInt160 GetPairHashFromFactory(UInt160 tokenA, UInt160 tokenB)
        {
            Assert(tokenA.IsValid && tokenB.IsValid, "Invalid A or B Address.");
            var factory = GetFactory();
            Assert(factory.IsValid && !factory.IsZero, "Factory Not Set.");
            var pairHash = (byte[])Contract.Call(factory, "getPairHash", CallFlags.ReadOnly, new object[] { tokenA, tokenB });
            Assert(pairHash != null && pairHash.Length == 20, "PairContract Not Found.", tokenA, tokenB);
            return (UInt160)pairHash;
        }

        private static void SafeTransfer(UInt160 token, UInt160 from, UInt160 to, BigInteger amount)
        {
            try
            {
                var balanceBefore = (BigInteger)Contract.Call(token, "balanceOf", CallFlags.ReadOnly, new object[] { to });
                var result = (bool)Contract.Call(token, "transfer", CallFlags.All, new object[] { from, to, amount, null });
                var balanceAfter = (BigInteger)Contract.Call(token, "balanceOf", CallFlags.ReadOnly, new object[] { to });
                Assert(result, "Transfer Fail In Router.", token);
                Assert(balanceAfter == balanceBefore + amount, "Unexpected Transfer In Router.", token);
            }
            catch (Exception)
            {
                Assert(false, "Transfer Error In Router.", token);
            }
        }

        private static void RequestTransfer(UInt160 token, UInt160 from, UInt160 to, BigInteger amount)
        {
            try
            {
                var balanceBefore = (BigInteger)Contract.Call(token, "balanceOf", CallFlags.ReadOnly, new object[] { to });
                var result = (bool)Contract.Call(from, "approvedTransfer", CallFlags.All, new object[] { token, to, amount, null });
                var balanceAfter = (BigInteger)Contract.Call(token, "balanceOf", CallFlags.ReadOnly, new object[] { to });
                Assert(result, "Transfer Not Approved in Router.", token);
                Assert(balanceAfter == balanceBefore + amount, "Unexpected Transfer In Router.", token);
            }
            catch (Exception)
            {
                Assert(false, "Transfer Error in Router", token);
            }
        }

        private static ByteString StorageGet(string key) => Storage.Get(Storage.CurrentContext, key);

        private static void StoragePut(string key, ByteString value) => Storage.Put(Storage.CurrentContext, key, value);

        private static BigInteger[] ChooseLiquidityAmounts(
            UInt160 tokenA,
            UInt160 tokenB,
            BigInteger desiredA,
            BigInteger desiredB,
            BigInteger minA,
            BigInteger minB)
        {
            BigInteger[] reserves = GetReserves(tokenA, tokenB);
            BigInteger reserveA = reserves[0];
            BigInteger reserveB = reserves[1];

            if (reserveA == 0 && reserveB == 0)
                return new BigInteger[] { desiredA, desiredB };

            BigInteger amountB = Quote(desiredA, reserveA, reserveB);
            if (amountB <= desiredB)
            {
                Assert(amountB >= minB, "LOW_AMOUNT_B");
                return new BigInteger[] { desiredA, amountB };
            }

            BigInteger amountA = Quote(desiredB, reserveB, reserveA);
            Assert(amountA <= desiredA, "HIGH_AMOUNT_A");
            Assert(amountA >= minA, "LOW_AMOUNT_A");
            return new BigInteger[] { amountA, desiredB };
        }

        private static BigInteger[] BurnPairLiquidity(
            UInt160 pair,
            UInt160 tokenA,
            UInt160 tokenB,
            UInt160 recipient,
            BigInteger amountAMin,
            BigInteger amountBMin)
        {
            BigInteger[] raw = pair.DynamicBurn(recipient);
            bool normalOrder = tokenA.ToUInteger() < tokenB.ToUInteger();
            BigInteger amountA = normalOrder ? raw[0] : raw[1];
            BigInteger amountB = normalOrder ? raw[1] : raw[0];

            Assert(amountA >= amountAMin, "LOW_AMOUNT_A");
            Assert(amountB >= amountBMin, "LOW_AMOUNT_B");
            return new BigInteger[] { amountA, amountB };
        }

        private static void ExecuteSwap(BigInteger[] amounts, UInt160[] paths, UInt160 recipient)
        {
            for (int i = 0; i < paths.Length - 1; i++)
            {
                UInt160 input = paths[i];
                UInt160 output = paths[i + 1];
                BigInteger amountOut = amounts[i + 1];

                BigInteger amount0Out = input.ToUInteger() < output.ToUInteger() ? 0 : amountOut;
                BigInteger amount1Out = input.ToUInteger() < output.ToUInteger() ? amountOut : 0;

                UInt160 to = i == paths.Length - 2
                    ? recipient
                    : GetPairHashFromFactory(output, paths[i + 2]);

                Contract.Call(
                    GetPairHashFromFactory(input, output),
                    "swap",
                    CallFlags.All,
                    new object[] { amount0Out, amount1Out, to, null });
            }
        }

        private static void ValidateLiquidityRequest(
            UInt160 tokenA,
            UInt160 tokenB,
            BigInteger desiredA,
            BigInteger desiredB,
            BigInteger minA,
            BigInteger minB,
            BigInteger deadline)
        {
            Assert(IsToken(tokenA) && IsToken(tokenB) && tokenA != tokenB, "BAD_PAIR");
            Assert(desiredA > 0 && desiredB > 0 && minA >= 0 && minB >= 0, "BAD_AMOUNT");
            ValidateDeadline(deadline);
        }

        private static void ValidateRemoveRequest(
            UInt160 tokenA,
            UInt160 tokenB,
            BigInteger liquidity,
            BigInteger amountAMin,
            BigInteger amountBMin,
            BigInteger deadline)
        {
            Assert(IsToken(tokenA) && IsToken(tokenB) && tokenA != tokenB, "BAD_PAIR");
            Assert(liquidity > 0 && amountAMin >= 0 && amountBMin >= 0, "BAD_AMOUNT");
            ValidateDeadline(deadline);
        }

        private static void ValidateSwapRequest(UInt160[] paths, BigInteger deadline)
        {
            ValidatePath(paths);
            ValidateDeadline(deadline);
        }

        private static void ValidateDeadline(BigInteger deadline)
        {
            Assert(deadline > 0, "BAD_DEADLINE");
            Assert((BigInteger)Runtime.Time <= deadline, "EXPIRED");
        }

        private static void ValidatePath(UInt160[] paths)
        {
            Assert(paths.Length >= 2, "BAD_PATH");
            for (int i = 0; i < paths.Length; i++)
            {
                Assert(IsToken(paths[i]), "BAD_TOKEN");
                if (i > 0) Assert(paths[i - 1] != paths[i], "BAD_PATH_PAIR");
            }
        }

        private static bool IsToken(UInt160 token) => token.IsValid && !token.IsZero;
        #endregion

        #region Events
        [DisplayName("Fault")]
        public static event FaultEvent onFault;
        public delegate void FaultEvent(string message, params object[] param);
        #endregion
    }

    public static class Extensions
    {
        public static BigInteger DynamicMint(this UInt160 pairContract, UInt160 toAddress)
        {
            return (BigInteger)Contract.Call(pairContract, "mint", CallFlags.All, new object[] { toAddress });
        }

        public static BigInteger[] DynamicBurn(this UInt160 pairContract, UInt160 toAddress)
        {
            return (BigInteger[])Contract.Call(pairContract, "burn", CallFlags.All, new object[] { toAddress });
        }

        [OpCode(OpCode.PUSHDATA1, "0100")]
        [OpCode(OpCode.CAT)]
        [OpCode(OpCode.CONVERT, "21")]
        public static extern BigInteger ToUInteger(this UInt160 val);
    }

    public struct ReservesData
    {
        public BigInteger Reserve0;
        public BigInteger Reserve1;
    }
}
