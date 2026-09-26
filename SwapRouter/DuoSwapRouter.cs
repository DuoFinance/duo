using System.ComponentModel;
using System.Numerics;
using Neo;
using Neo.SmartContract.Framework;
using Neo.SmartContract.Framework.Attributes;
using Neo.SmartContract.Framework.Native;
using Neo.SmartContract.Framework.Services;

namespace DuoSwapRouter
{
    [DisplayName("DuoSwapRouter")]
    [ManifestExtra("Description", "DuoSwap Router Contract")]
    [ContractSourceCode("http://github.com/DuoFinance/bond2swap.git")]
    [ContractPermission("*")]
    public partial class DuoSwapRouterContract : SmartContract
    {
        private const long FEE_NUMERATOR = 997;
        private const long FEE_DENOMINATOR = 1000;

        public static BigInteger[] AddLiquidity(
            UInt160 sender,
            UInt160 tokenA,
            UInt160 tokenB,
            BigInteger amountADesired,
            BigInteger amountBDesired,
            BigInteger amountAMin,
            BigInteger amountBMin,
            BigInteger deadLine)
        {
            ValidateLiquidityRequest(tokenA, tokenB, amountADesired, amountBDesired, amountAMin, amountBMin, deadLine);
            Assert(sender.IsValid && !sender.IsZero, "BAD_SENDER");
            Assert(Runtime.CheckWitness(sender), "NO_WITNESS");

            BigInteger[] amounts = ChooseLiquidityAmounts(tokenA, tokenB, amountADesired, amountBDesired, amountAMin, amountBMin);
            UInt160 pair = GetPairHashFromFactory(tokenA, tokenB);

            SafeTransfer(tokenA, sender, pair, amounts[0]);
            SafeTransfer(tokenB, sender, pair, amounts[1]);

            BigInteger liquidity = pair.DynamicMint(sender);
            return new BigInteger[] { amounts[0], amounts[1], liquidity };
        }

        public static BigInteger[] AddLiquidity(
            UInt160 tokenA,
            UInt160 tokenB,
            BigInteger amountADesired,
            BigInteger amountBDesired,
            BigInteger amountAMin,
            BigInteger amountBMin,
            BigInteger deadLine)
        {
            ValidateLiquidityRequest(tokenA, tokenB, amountADesired, amountBDesired, amountAMin, amountBMin, deadLine);
            UInt160 caller = Runtime.CallingScriptHash;
            Assert(ContractManagement.GetContract(caller) != null, "CALLER_NOT_CONTRACT");

            BigInteger[] amounts = ChooseLiquidityAmounts(tokenA, tokenB, amountADesired, amountBDesired, amountAMin, amountBMin);
            UInt160 pair = GetPairHashFromFactory(tokenA, tokenB);

            RequestTransfer(tokenA, caller, pair, amounts[0]);
            RequestTransfer(tokenB, caller, pair, amounts[1]);

            BigInteger liquidity = pair.DynamicMint(caller);
            return new BigInteger[] { amounts[0], amounts[1], liquidity };
        }

        public static BigInteger[] RemoveLiquidity(
            UInt160 sender,
            UInt160 tokenA,
            UInt160 tokenB,
            BigInteger liquidity,
            BigInteger amountAMin,
            BigInteger amountBMin,
            BigInteger deadLine)
        {
            ValidateRemoveRequest(tokenA, tokenB, liquidity, amountAMin, amountBMin, deadLine);
            Assert(sender.IsValid && !sender.IsZero, "BAD_SENDER");
            Assert(Runtime.CheckWitness(sender), "NO_WITNESS");

            UInt160 pair = GetPairHashFromFactory(tokenA, tokenB);
            SafeTransfer(pair, sender, pair, liquidity);
            return BurnPairLiquidity(pair, tokenA, tokenB, sender, amountAMin, amountBMin);
        }

        public static BigInteger[] RemoveLiquidity(
            UInt160 tokenA,
            UInt160 tokenB,
            BigInteger liquidity,
            BigInteger amountAMin,
            BigInteger amountBMin,
            BigInteger deadLine)
        {
            ValidateRemoveRequest(tokenA, tokenB, liquidity, amountAMin, amountBMin, deadLine);
            UInt160 caller = Runtime.CallingScriptHash;
            Assert(ContractManagement.GetContract(caller) != null, "CALLER_NOT_CONTRACT");

            UInt160 pair = GetPairHashFromFactory(tokenA, tokenB);
            UInt160 self = Runtime.ExecutingScriptHash;
            RequestTransfer(pair, caller, self, liquidity);
            SafeTransfer(pair, self, pair, liquidity);
            return BurnPairLiquidity(pair, tokenA, tokenB, caller, amountAMin, amountBMin);
        }

        public static BigInteger Quote(BigInteger amountA, BigInteger reserveA, BigInteger reserveB)
        {
            Assert(amountA > 0, "BAD_AMOUNT");
            Assert(reserveA > 0 && reserveB > 0, "BAD_RESERVE");
            return amountA * reserveB / reserveA;
        }

        public static BigInteger GetAmountOut(BigInteger amountIn, BigInteger reserveIn, BigInteger reserveOut)
        {
            Assert(amountIn > 0, "BAD_INPUT");
            Assert(reserveIn > 0 && reserveOut > 0, "BAD_RESERVE");

            BigInteger amountInAfterFee = amountIn * FEE_NUMERATOR;
            return amountInAfterFee * reserveOut / (reserveIn * FEE_DENOMINATOR + amountInAfterFee);
        }

        public static BigInteger GetAmountIn(BigInteger amountOut, BigInteger reserveIn, BigInteger reserveOut)
        {
            Assert(amountOut > 0, "BAD_OUTPUT");
            Assert(reserveIn > 0 && reserveOut > 0, "BAD_RESERVE");
            Assert(amountOut < reserveOut, "LOW_LIQUIDITY");

            BigInteger numerator = reserveIn * amountOut * FEE_DENOMINATOR;
            BigInteger denominator = (reserveOut - amountOut) * FEE_NUMERATOR;
            return numerator / denominator + 1;
        }

        public static BigInteger[] GetAmountsOut(BigInteger amountIn, UInt160[] paths)
        {
            Assert(amountIn > 0, "BAD_INPUT");
            ValidatePath(paths);

            BigInteger[] amounts = new BigInteger[paths.Length];
            amounts[0] = amountIn;
            for (int i = 0; i < paths.Length - 1; i++)
            {
                BigInteger[] reserves = GetReserves(paths[i], paths[i + 1]);
                amounts[i + 1] = GetAmountOut(amounts[i], reserves[0], reserves[1]);
            }
            return amounts;
        }

        public static BigInteger[] GetAmountsIn(BigInteger amountOut, UInt160[] paths)
        {
            Assert(amountOut > 0, "BAD_OUTPUT");
            ValidatePath(paths);

            BigInteger[] amounts = new BigInteger[paths.Length];
            int last = paths.Length - 1;
            amounts[last] = amountOut;
            for (int i = last; i > 0; i--)
            {
                BigInteger[] reserves = GetReserves(paths[i - 1], paths[i]);
                amounts[i - 1] = GetAmountIn(amounts[i], reserves[0], reserves[1]);
            }
            return amounts;
        }

        public static BigInteger[] GetReserves(UInt160 tokenA, UInt160 tokenB)
        {
            Assert(IsToken(tokenA) && IsToken(tokenB) && tokenA != tokenB, "BAD_PAIR");
            ReservesData reserves = (ReservesData)Contract.Call(
                GetPairHashFromFactory(tokenA, tokenB),
                "getReserves",
                CallFlags.ReadOnly,
                new object[] { });

            return tokenA.ToUInteger() < tokenB.ToUInteger()
                ? new BigInteger[] { reserves.Reserve0, reserves.Reserve1 }
                : new BigInteger[] { reserves.Reserve1, reserves.Reserve0 };
        }

        public static void OnNEP17Payment(UInt160 sender, BigInteger amountIn, object data)
        {
        }

        public static bool SwapExactTokensForTokens(
            UInt160 sender,
            BigInteger exactAmountIn,
            BigInteger amountOutMin,
            UInt160[] paths,
            BigInteger deadLine)
        {
            ValidateSwapRequest(paths, deadLine);
            Assert(sender.IsValid && !sender.IsZero, "BAD_SENDER");
            Assert(exactAmountIn > 0 && amountOutMin >= 0, "BAD_AMOUNT");
            Assert(Runtime.CheckWitness(sender), "NO_WITNESS");

            BigInteger[] amounts = GetAmountsOut(exactAmountIn, paths);
            Assert(amounts[amounts.Length - 1] >= amountOutMin, "LOW_OUTPUT");

            SafeTransfer(paths[0], sender, GetPairHashFromFactory(paths[0], paths[1]), amounts[0]);
            ExecuteSwap(amounts, paths, sender);
            return true;
        }

        public static bool SwapExactTokensForTokens(
            BigInteger exactAmountIn,
            BigInteger amountOutMin,
            UInt160[] paths,
            BigInteger deadLine)
        {
            ValidateSwapRequest(paths, deadLine);
            Assert(exactAmountIn > 0 && amountOutMin >= 0, "BAD_AMOUNT");
            UInt160 caller = Runtime.CallingScriptHash;
            Assert(ContractManagement.GetContract(caller) != null, "CALLER_NOT_CONTRACT");

            BigInteger[] amounts = GetAmountsOut(exactAmountIn, paths);
            Assert(amounts[amounts.Length - 1] >= amountOutMin, "LOW_OUTPUT");

            RequestTransfer(paths[0], caller, GetPairHashFromFactory(paths[0], paths[1]), amounts[0]);
            ExecuteSwap(amounts, paths, caller);
            return true;
        }

        public static bool SwapTokensForExactTokens(
            UInt160 sender,
            BigInteger amountInMax,
            BigInteger exactAmountOut,
            UInt160[] paths,
            BigInteger deadLine)
        {
            ValidateSwapRequest(paths, deadLine);
            Assert(sender.IsValid && !sender.IsZero, "BAD_SENDER");
            Assert(amountInMax >= 0 && exactAmountOut > 0, "BAD_AMOUNT");
            Assert(Runtime.CheckWitness(sender), "NO_WITNESS");

            BigInteger[] amounts = GetAmountsIn(exactAmountOut, paths);
            Assert(amounts[0] <= amountInMax, "HIGH_INPUT");

            SafeTransfer(paths[0], sender, GetPairHashFromFactory(paths[0], paths[1]), amounts[0]);
            ExecuteSwap(amounts, paths, sender);
            return true;
        }

        public static bool SwapTokensForExactTokens(
            BigInteger amountInMax,
            BigInteger exactAmountOut,
            UInt160[] paths,
            BigInteger deadLine)
        {
            ValidateSwapRequest(paths, deadLine);
            Assert(amountInMax >= 0 && exactAmountOut > 0, "BAD_AMOUNT");
            UInt160 caller = Runtime.CallingScriptHash;
            Assert(ContractManagement.GetContract(caller) != null, "CALLER_NOT_CONTRACT");

            BigInteger[] amounts = GetAmountsIn(exactAmountOut, paths);
            Assert(amounts[0] <= amountInMax, "HIGH_INPUT");

            RequestTransfer(paths[0], caller, GetPairHashFromFactory(paths[0], paths[1]), amounts[0]);
            ExecuteSwap(amounts, paths, caller);
            return true;
        }
    }
}
