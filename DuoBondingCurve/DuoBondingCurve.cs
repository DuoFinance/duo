using System.Numerics;
using Neo.SmartContract.Framework;
using Neo.SmartContract.Framework.Native;
using Neo.SmartContract.Framework.Services;
using Neo.SmartContract.Framework.Attributes;

namespace DuoBondingCurve
{
    [ManifestExtra("Description", "DuoBondingCurve - Fair LaunchPad")]
    [ContractSourceCode("https://github.com/DuoFinance/duoSwap.git")]
    [ContractPermission("*", new[] { "transfer", "symbol", "totalSupply", "initialise", "getDeployFee", "getPairHash", "addLiquidity" })]
    public partial class DuoBondingCurve : SmartContract
    {
        #region Sale Creation

        public static void CreateSale(string name, string symbol, BigInteger totalSupplyWithDecimals, BigInteger targetRaise, BigInteger graduationBps)
        {
            Enter();
            Assert(name.Length > 0 && name.Length <= 64, "INVALID_NAME");
            Assert(symbol.Length > 0 && symbol.Length <= 8, "INVALID_SYMBOL");
            BigInteger totalSupply = totalSupplyWithDecimals >> 8;
            BigInteger decimals = totalSupplyWithDecimals & 255;
            Assert(decimals >= 0 && decimals <= 18, "INVALID_DECIMALS");
            Assert(totalSupply > 0, "INVALID_SUPPLY");
            Assert(targetRaise > 0, "INVALID_TARGET");
            Assert(graduationBps > 5000 && graduationBps <= 9900, "INVALID_GRADUATION_BPS");

            UInt160 factory = GetFactory();
            UInt160 router = GetRouter();
            Assert(factory.IsValid && !factory.IsZero, "FACTORY_NOT_SET");
            Assert(router.IsValid && !router.IsZero, "ROUTER_NOT_SET");

            var minimumDeploymentFee = (long)ContractManagement.GetMinimumDeploymentFee();
            Assert(targetRaise > minimumDeploymentFee * 2, "TARGET_TOO_CLOSE_TO_DEPLOY_FEE");

            BigInteger twoPminusOne = 2 * graduationBps - 10000;
            Assert(twoPminusOne > 0, "INVALID_GRADUATION");

            byte[] nef = GetLaunchpadNef();
            string manifest = BuildImmutableTokenManifest(name);
            Contract newContract = ContractManagement.Deploy(nef.ToByteString(), manifest);
            UInt160 tokenHash = newContract.Hash;

            Contract.Call(tokenHash, "initialise", CallFlags.All,
                new object[] { Runtime.ExecutingScriptHash, (byte)decimals, symbol, totalSupply });

            string tokenSymbol = (string)Contract.Call(tokenHash, "symbol", CallFlags.ReadOnly);
            BigInteger tokenTotalSupply = (BigInteger)Contract.Call(tokenHash, "totalSupply", CallFlags.ReadOnly);

            Assert(tokenSymbol == symbol, "SYMBOL_MISMATCH");
            Assert(tokenTotalSupply == totalSupply, "SUPPLY_MISMATCH");
            
            // Compute virtual reserves for price continuity at graduation:
            // vToken = T * P² / 10000 / (2P - 10000)
            // vGas   = R * (10000 - P) / (2P - 10000)

            BigInteger pSquared = graduationBps * graduationBps;
            BigInteger vToken = tokenTotalSupply * pSquared / 10000 / twoPminusOne;
            BigInteger vGas = targetRaise * (10000 - graduationBps) / twoPminusOne;

            SaleInfo info = new SaleInfo
            {
                TokenHash = tokenHash,
                Creator = Tx.Sender,
                Symbol = tokenSymbol,
                TotalSupply = tokenTotalSupply,
                TokensSold = 0,
                GASRaised = 0,
                TargetRaise = targetRaise,
                VirtualGas = vGas,
                VirtualToken = vToken,
                GraduationBps = graduationBps,
                Graduated = false
            };
            StoreSale(tokenHash, info);
            OnSaleCreated(Tx.Sender, tokenHash, tokenSymbol, tokenTotalSupply, targetRaise, graduationBps);

            Exit();
        }

        #endregion

        #region Buying

        public static void OnNEP17Payment(UInt160 from, BigInteger amount, object[] data)
        {
            // Mint operations (PostTransfer from null) are a no-op for us
            if (!from.IsValid || from.IsZero) return;
            Assert(amount > 0, "INVALID_AMOUNT");

            UInt160 caller = Runtime.CallingScriptHash;

            if (caller == GAS.Hash)
            {
                if (data == null || data.Length == 0) return;

                UInt160 tokenHash = (UInt160)data[0];
                BigInteger minTokensOut = 0;
                if (data.Length >= 2 && data[1] != null)
                    minTokensOut = (BigInteger)data[1];

                ProcessBuy(from, tokenHash, amount, minTokensOut);
            }
            else
            {
                BigInteger minGasOut = 0;
                if (data != null && data.Length >= 1 && data[0] != null)
                    minGasOut = (BigInteger)data[0];

                ProcessSell(from, caller, amount, minGasOut);
            }
        }

        private static void ProcessBuy(UInt160 buyer, UInt160 tokenHash, BigInteger gasAmount, BigInteger minTokensOut)
        {
            Enter();

            SaleInfo sale = GetSale(tokenHash);
            Assert(SaleExists(sale) && !sale.Graduated, "SALE_GRADUATED");

            // Compute tokens from the CP-AMM curve using virtual reserves
            BigInteger tokens = GetAmountOut(sale, gasAmount);
            Assert(tokens > 0, "ZERO_TOKENS");

            // Cap at graduation point: cannot exceed P * TotalSupply
            BigInteger maxTokens = sale.TotalSupply * sale.GraduationBps / 10000;
            BigInteger remainingToGrad = maxTokens - sale.TokensSold;
            BigInteger actualGas = gasAmount;
            if (tokens >= remainingToGrad)
            {
                tokens = remainingToGrad;
                actualGas = GetAmountIn(sale, tokens);
                Assert(actualGas <= gasAmount, "CALC_ERROR");

                BigInteger excess = gasAmount - actualGas;
                if (excess > 0)
                {
                    bool refundOk = GAS.Transfer(Runtime.ExecutingScriptHash, buyer, excess, null);
                    Assert(refundOk, "REFUND_FAILED");
                }
            }

            // capped token amount they will actually receive.
            Assert(tokens >= minTokensOut, "SLIPPAGE_EXCEEDED");

            bool ok = (bool)Contract.Call(
                tokenHash,
                "transfer",
                CallFlags.All,
                new object[] { Runtime.ExecutingScriptHash, buyer, tokens, null });
            Assert(ok, "TRANSFER_FAILED");

            sale.TokensSold += tokens;
            sale.GASRaised += actualGas;

            if (sale.GASRaised >= sale.TargetRaise)
            {
                FinalizeSale(tokenHash, sale);
            }
            else
            {
                StoreSale(tokenHash, sale);
            }

            OnTokensPurchased(buyer, tokenHash, tokens, actualGas, sale.TokensSold, sale.GASRaised);

            Exit();
        }

        #endregion

        #region Selling

        private static void ProcessSell(UInt160 seller, UInt160 tokenHash, BigInteger tokenAmount, BigInteger minGasOut)
        {
            Enter();

            SaleInfo sale = GetSale(tokenHash);

            Assert(SaleExists(sale) && !sale.Graduated, "SALE_GRADUATED");
            Assert(sale.TokensSold >= tokenAmount, "INSUFFICIENT_SOLD");

            BigInteger gasReturn = GetSellAmount(sale, tokenAmount);
            Assert(gasReturn > 0, "ZERO_GAS");
            Assert(gasReturn <= sale.GASRaised, "INSUFFICIENT_GAS");

            Assert(gasReturn >= minGasOut, "SLIPPAGE_EXCEEDED");

            sale.TokensSold -= tokenAmount;
            sale.GASRaised -= gasReturn;

            bool ok = GAS.Transfer(Runtime.ExecutingScriptHash, seller, gasReturn, null);
            Assert(ok, "GAS_TRANSFER_FAILED");

            StoreSale(tokenHash, sale);
            OnTokensSold(seller, tokenHash, tokenAmount, gasReturn, sale.TokensSold, sale.GASRaised);

            Exit();
        }

        #endregion

        #region Finalize
        private const int LIQUIDITY_SLIPPAGE_BPS = 200; // 2%

        private static void FinalizeSale(UInt160 tokenHash, SaleInfo sale)
        {
            UInt160 factory = GetFactory();
            UInt160 router = GetRouter();

            var minimumDeploymentFee = (long)ContractManagement.GetMinimumDeploymentFee();
            var pair_DeployFee = (BigInteger)Contract.Call(factory, "getDeployFee", CallFlags.ReadOnly);

            BigInteger raisedGAS = sale.GASRaised;
            BigInteger remainingTokens = sale.TotalSupply - sale.TokensSold;
            Assert(remainingTokens > 0, "NO_TOKENS_FOR_LP");
            Assert(raisedGAS > minimumDeploymentFee + pair_DeployFee, "INSUFFICIENT_FOR_DEPLOY");

            BigInteger gasForLiquidity = raisedGAS - minimumDeploymentFee - pair_DeployFee;

            bool deployed = GAS.Transfer(Runtime.ExecutingScriptHash, factory, pair_DeployFee,
                new object[] { tokenHash, GAS.Hash });
            Assert(deployed, "PAIR_DEPLOY_FAILED");

            UInt160 pair = (UInt160)Contract.Call(
                factory, "getPairHash", CallFlags.ReadOnly,
                new object[] { tokenHash, GAS.Hash });
            Assert(pair.IsValid && !pair.IsZero, "PAIR_NOT_FOUND");

            BigInteger deadline = Runtime.Time + 3600000;

            BigInteger minTokens = remainingTokens * (10000 - LIQUIDITY_SLIPPAGE_BPS) / 10000;
            BigInteger minGas = gasForLiquidity * (10000 - LIQUIDITY_SLIPPAGE_BPS) / 10000;

            SetPendingApproval(tokenHash, pair, remainingTokens);
            SetPendingApproval(GAS.Hash, pair, gasForLiquidity);

            BigInteger[] amounts = (BigInteger[])Contract.Call(
                router, "addLiquidity", CallFlags.All,
                new object[]
                {
                    tokenHash,
                    GAS.Hash,
                    remainingTokens,
                    gasForLiquidity,
                    minTokens,
                    minGas,
                    deadline
                });


            ClearPendingApproval(tokenHash, pair);
            ClearPendingApproval(GAS.Hash, pair);

            BigInteger liquidity = amounts[2];

            // Burn LP tokens to address(0) — abandon ownership for fair launch
            if (liquidity > 0)
            {
                bool burnOk = (bool)Contract.Call(
                    pair, "transfer", CallFlags.All,
                    new object[] { Runtime.ExecutingScriptHash, UInt160.Zero, liquidity, null });
                Assert(burnOk, "LP_BURN_FAILED");
            }

            sale.Graduated = true;
            StoreSale(tokenHash, sale);

            OnSaleCompleted(tokenHash, sale.Symbol, pair, liquidity, remainingTokens, gasForLiquidity);
        }

        #endregion

        #region ApprovedTransfer (for Router)

        public static bool ApprovedTransfer(UInt160 token, UInt160 to, BigInteger amount, object data)
        {
            UInt160 router = GetRouter();
            Assert(Runtime.CallingScriptHash == router, "ONLY_ROUTER");
            Assert(amount > 0, "INVALID_AMOUNT");
            Assert(to.IsValid && !to.IsZero, "INVALID_TO");
            Assert(IsEntered(), "NOT_AUTHORIZED");

            BigInteger remaining = GetPendingApproval(token, to);
            Assert(remaining > 0, "NOT_APPROVED");
            Assert(amount <= remaining, "EXCEEDS_APPROVED");
            SetPendingApproval(token, to, remaining - amount);

            bool ok = (bool)Contract.Call(
                token,
                "transfer",
                CallFlags.All,
                new object[] { Runtime.ExecutingScriptHash, to, amount, null });
            Assert(ok, "TRANSFER_FAILED");
            return true;
        }

        #endregion

        #region Reentrancy Lock

        private static StorageMap LockMap => new StorageMap(Storage.CurrentContext, "Lock");

        private static void Enter()
        {
            BigInteger entered = (BigInteger)LockMap.Get("entered");
            Assert(entered == 0, "REENTRANCY");
            LockMap.Put("entered", 1);
        }

        private static void Exit()
        {
            LockMap.Put("entered", 0);
        }

        private static bool IsEntered()
        {
            var raw = LockMap.Get("entered");
            return raw != null && (BigInteger)raw == 1;
        }

        #endregion

        #region Pending Approval (scoped ApprovedTransfer allowances)

        private static StorageMap PendingMap => new StorageMap(Storage.CurrentContext, "Pending");

        private static ByteString PendingKey(UInt160 token, UInt160 to)
        {
            return ((ByteString)token).Concat((ByteString)to);
        }

        private static void SetPendingApproval(UInt160 token, UInt160 to, BigInteger amount)
        {
            PendingMap.Put(PendingKey(token, to), amount);
        }

        private static BigInteger GetPendingApproval(UInt160 token, UInt160 to)
        {
            var raw = PendingMap.Get(PendingKey(token, to));
            return raw is null ? 0 : (BigInteger)raw;
        }

        private static void ClearPendingApproval(UInt160 token, UInt160 to)
        {
            PendingMap.Delete(PendingKey(token, to));
        }

        #endregion
    }
}
