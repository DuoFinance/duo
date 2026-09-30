// SPDX-License-Identifier: MIT
// Additional Restriction: Duo 3Y Algorithm Non-Copy (virtual GAS / virtual Token) — see LICENSE Section B. Expires 2029-08-31.
using System.Numerics;
using Neo.SmartContract.Framework;
using Neo.SmartContract.Framework.Native;
using Neo.SmartContract.Framework.Services;
using Neo.SmartContract.Framework.Attributes;
using System.ComponentModel;

namespace DuoBondingCurve
{
    [DisplayName("DuoBondingCurve")]
    [ManifestExtra("Description", "Duo Fair Launchpad")]
    [ContractSourceCode("http://github.com/DuoFinance/Duo.git")]
    [ContractPermission("*")]
    public partial class DuoBondingCurve : SmartContract
    {
        #region Sale Creation

        public static void CreateSale(
            string name,
            string symbol,
            BigInteger targetRaise,
            BigInteger graduationBps,
            string discord,
            string x,
            string telegram,
            string projUrl
            )
        {
            Enter();
            BigInteger totalSupply = DEFAULT_TOTAL_SUPPLY;
            BigInteger decimals = DEFAULT_DECIMALS;
            CreateSaleInternal(
                name,
                symbol,
                totalSupply,
                decimals,
                targetRaise,
                graduationBps,
                discord,
                x,
                telegram,
                projUrl,
                Tx.Sender);
            Exit();
        }

        private static UInt160 CreateSaleInternal(
            string name,
            string symbol,
            BigInteger totalSupply,
            BigInteger decimals,
            BigInteger targetRaise,
            BigInteger graduationBps,
            string discord,
            string x,
            string telegram,
            string projUrl,
            UInt160 creator
            )
        {
            Assert(name.Length > 0 && name.Length <= 64, "INVALID_NAME");
            Assert(symbol.Length > 0 && symbol.Length <= 8, "INVALID_SYMBOL");
            Assert(targetRaise > 0, "INVALID_TARGET");
            Assert(graduationBps >= MIN_GRADUATION_BPS && graduationBps <= MAX_GRADUATION_BPS, "INVALID_GRADUATION_BPS");
            Assert(discord == null || discord.Length <= 128, "INVALID_DISCORD_TOO_LONG");
            Assert(x == null || x.Length <= 128, "INVALID_X_TOO_LONG");
            Assert(telegram == null || telegram.Length <= 128, "INVALID_TELEGRAM_TOO_LONG");
            Assert(projUrl == null || projUrl.Length <= 256, "INVALID_PROJURL_TOO_LONG");

            BigInteger twoPminusOne = 2 * graduationBps - 10000;
            Assert(twoPminusOne > 0, "INVALID_GRADUATION");

            byte[] nef = GetLaunchpadNef();
            string manifest = BuildImmutableTokenManifest(name, discord, x, telegram, projUrl);
            Contract newContract = ContractManagement.Deploy(
                nef.ToByteString(),
                manifest,
                new object[] { Runtime.ExecutingScriptHash, decimals, symbol, totalSupply });
            Assert(newContract.Hash.IsValid && !newContract.Hash.IsZero, "TOKEN_DEPLOY_FAILED");
            UInt160 tokenHash = newContract.Hash;

            BigInteger pSquared = graduationBps * graduationBps;
            BigInteger vToken = totalSupply * pSquared / (10000 * twoPminusOne);
            BigInteger vGAS = targetRaise * (10000 - graduationBps) / twoPminusOne;
            Assert(vToken > 0 && vGAS > 0, "INVALID_VIRTUAL_RESERVES");

            SaleInfo info = new SaleInfo
            {
                Creator = creator,
                TokensSold = 0,
                GASRaised = 0,
                TargetRaise = targetRaise,
                VirtualGAS = vGAS,
                VirtualToken = vToken,
                GraduationBps = graduationBps,
                Graduated = false
            };
            StoreSale(tokenHash, info);
            OnSaleCreated(creator, tokenHash, symbol, totalSupply, targetRaise, graduationBps);

            return tokenHash;
        }

        #endregion

        #region Transaction Entrance

        /// NEP-17 payment callback — entry point for all buy/sell transactions.
        ///
        /// When sending GAS to this contract:
        ///   data[0] = UInt160 tokenHash    → ProcessBuy
        ///   data[0] = "CreateAndBuy"       → ProcessCreateAndBuy (create + buy atomically)
        ///   data[1] = BigInteger minTokensOut (optional, 0 = no slippage)
        ///
        /// When sending tokens to this contract:
        ///   data[0] = BigInteger minGasOut (optional, 0 = no slippage)
        ///   → ProcessSell
        public static void OnNEP17Payment(UInt160 from, BigInteger amount, object[] data)
        {
            if (!from.IsValid || from.IsZero) return;
            Assert(amount > 0, "INVALID_AMOUNT");

            UInt160 caller = Runtime.CallingScriptHash;
            if (caller == GAS.Hash)
            {
                Assert(data != null && data.Length > 0, "INVALID_TRANSFER_DATA");

                if (data[0] is string && (string)data[0] == "CreateAndBuy")
                {
                    ProcessCreateAndBuy(from, amount, data);
                }
                else
                {
                    UInt160 tokenHash = (UInt160)data[0];
                    BigInteger minTokensOut = 0;
                    if (data.Length >= 2 && data[1] != null)
                        minTokensOut = (BigInteger)data[1];

                    ProcessBuy(from, tokenHash, amount, minTokensOut);
                }
            }
            else
            {
                BigInteger minGasOut = 0;
                if (data != null && data.Length >= 1 && data[0] != null)
                    minGasOut = (BigInteger)data[0];

                ProcessSell(from, caller, amount, minGasOut);
            }
        }

        #endregion

        #region Buy

        /// Create a new sale and buy tokens in one atomic transaction.
        /// data[0] = "CreateAndBuy", data[1] = minTokensOut (optional),
        /// data[2..9] = name, symbol, targetRaise, graduationBps, discord, x, telegram, projUrl
        private static void ProcessCreateAndBuy(UInt160 buyer, BigInteger gasAmount, object[] data)
        {
            Assert(data.Length >= 10, "INVALID_CREATE_DATA");

            BigInteger deployFee = GetDeployFee();
            Assert(gasAmount > deployFee, "INSUFFICIENT_DEPLOY_FEE");

            bool feeTransferred = GAS.Transfer(
                Runtime.ExecutingScriptHash,
                GetFactory(),
                deployFee,
                null);
            Assert(feeTransferred, "DEPLOY_FEE_TRANSFER_FAILED");

            BigInteger minTokensOut = 0;
            if (data[1] != null)
                minTokensOut = (BigInteger)data[1];
            string name = (string)data[2];
            string symbol = (string)data[3];
            BigInteger targetRaise = (BigInteger)data[4];
            BigInteger graduationBps = (BigInteger)data[5];
            string discord = (string)data[6];
            string x = (string)data[7];
            string telegram = (string)data[8];
            string projUrl = (string)data[9];

            BigInteger totalSupply = DEFAULT_TOTAL_SUPPLY;
            BigInteger decimals = DEFAULT_DECIMALS;

            UInt160 tokenHash = CreateSaleInternal(
                name,
                symbol,
                totalSupply,
                decimals,
                targetRaise,
                graduationBps,
                discord,
                x,
                telegram,
                projUrl,
                buyer);

            ProcessBuy(buyer, tokenHash, gasAmount - deployFee, minTokensOut);
        }

        
        /// Buy tokens with GAS. If purchase reaches graduation cap,
        /// triggers Graduate. Excess GAS refunded automatically.
        private static void ProcessBuy(
            UInt160 buyer,
            UInt160 tokenHash,
            BigInteger gasAmount,
            BigInteger minTokensOut
            )
        {
            Enter();

            SaleInfo sale = GetSale(tokenHash);
            Assert(SaleExists(sale), "SALE_NOT_FOUND");
            Assert(!sale.Graduated, "SALE_GRADUATED");

            UInt160 feeTo = (UInt160)Contract.Call(GetFactory(), "getFeeTo", CallFlags.ReadOnly);
            bool feeOn = feeTo != UInt160.Zero;

            BigInteger fee = 0;
            BigInteger netGasAmount = gasAmount;
            if (feeOn)
            {
                fee = gasAmount * FEE_FACTOR / FEE_DENOMINATOR;
                netGasAmount = gasAmount - fee;
            }

            BigInteger tokenReserve = sale.VirtualToken - sale.TokensSold;
            BigInteger gasReserve = sale.VirtualGAS + sale.GASRaised;
            BigInteger tokens = GetAmountOut(gasReserve, tokenReserve, netGasAmount);

            BigInteger maxTokens = DEFAULT_TOTAL_SUPPLY * sale.GraduationBps / 10000;
            BigInteger remainingToGrad = maxTokens - sale.TokensSold;
            BigInteger actualGas = gasAmount;
            BigInteger curveGas = netGasAmount;

            if (tokens >= remainingToGrad)
            {
                tokens = remainingToGrad;
                BigInteger netActualGas = GetAmountIn(sale, tokens);
                if (netActualGas > netGasAmount) netActualGas = netGasAmount;

                if (feeOn)
                {
                    // Gross-up only to recover the pre-fee amount for refund math (excess = gasAmount - actualGas).
                    // curveGas is pinned to netActualGas — the exact value GetAmountIn priced `tokens` against —
                    // so GASRaised never drifts from what was used to compute the sale price.
                    actualGas = (netActualGas * FEE_DENOMINATOR + (FEE_DENOMINATOR - FEE_FACTOR - 1)) / (FEE_DENOMINATOR - FEE_FACTOR);
                    if (actualGas > gasAmount) actualGas = gasAmount;

                    curveGas = netActualGas;
                    fee = actualGas - curveGas;
                    Assert(fee >= 0, "FEE_UNDERFLOW"); // defensive: structurally unreachable
                }
                else
                {
                    actualGas = netActualGas;
                    curveGas = netActualGas;
                }

                BigInteger excess = gasAmount - actualGas;
                if (excess > 0)
                {
                    try
                    {
                        GAS.Transfer(Runtime.ExecutingScriptHash, buyer, excess, null);
                    }
                    catch
                    {
                        // refund failure is non-blocking
                    }
                }
            }

            Assert(tokens >= minTokensOut, "SLIPPAGE_EXCEEDED");

            sale.TokensSold += tokens;
            sale.GASRaised += curveGas;

            bool ok = (bool)Contract.Call(
                tokenHash,
                "transfer",
                CallFlags.All,
                new object[] { Runtime.ExecutingScriptHash, buyer, tokens, null });
            Assert(ok, "TRANSFER_FAILED");

            if (feeOn && fee > 0)
            {
                bool feeOk = GAS.Transfer(Runtime.ExecutingScriptHash, feeTo, fee, null);
                Assert(feeOk, "FEE_TRANSFER_FAILED");
            }

            // Graduation is defined by the configured token allocation cap.
            if (sale.TokensSold >= maxTokens)
            {
                Graduate(tokenHash, sale);
            }
            else
            {
                StoreSale(tokenHash, sale);
            }

            OnTokensPurchased(
                buyer,
                tokenHash,
                tokens,
                actualGas,
                sale.TokensSold,
                sale.GASRaised
                );
            Exit();
        }

        #endregion

        #region Selling

        /// Sell tokens back for GAS via bonding curve.
        /// GAS sent from contract to seller.
        private static void ProcessSell(
            UInt160 seller,
            UInt160 tokenHash,
            BigInteger tokenAmount,
            BigInteger minGasOut)
        {
            Enter();
            Assert(tokenAmount > 0, "INVALID_AMOUNT");

            SaleInfo sale = GetSale(tokenHash);
            Assert(SaleExists(sale) && !sale.Graduated, "SALE_GRADUATED");
            Assert(tokenAmount <= sale.TokensSold, "INSUFFICIENT_SOLD");

            UInt160 feeTo = (UInt160)Contract.Call(GetFactory(), "getFeeTo", CallFlags.ReadOnly);
            bool feeOn = feeTo != UInt160.Zero;

            BigInteger tokenReserve = sale.VirtualToken - sale.TokensSold;
            BigInteger gasReserve = sale.VirtualGAS + sale.GASRaised;
            BigInteger gasReturn = GetAmountOut(tokenReserve, gasReserve, tokenAmount);
            Assert(gasReturn > 0, "ZERO_GAS");
            Assert(gasReturn <= sale.GASRaised, "INSUFFICIENT_GAS");

            BigInteger fee = 0;
            BigInteger userGas = gasReturn;
            if (feeOn)
            {
                fee = gasReturn * FEE_FACTOR / FEE_DENOMINATOR;
                userGas = gasReturn - fee;
            }
            Assert(userGas >= minGasOut, "SLIPPAGE_EXCEEDED");

            sale.TokensSold -= tokenAmount;
            sale.GASRaised -= gasReturn;

            if (feeOn && fee > 0)
            {
                bool feeOk = GAS.Transfer(Runtime.ExecutingScriptHash, feeTo, fee, null);
                Assert(feeOk, "FEE_TRANSFER_FAILED");
            }

            bool ok = GAS.Transfer(Runtime.ExecutingScriptHash, seller, userGas, null);
            Assert(ok, "GAS_TRANSFER_FAILED");

            StoreSale(tokenHash, sale);
            OnTokensSold(
                seller,
                tokenHash,
                tokenAmount,
                gasReturn,
                sale.TokensSold,
                sale.GASRaised
                );

            Exit();
        }

        #endregion

        #region Graduation
        /// Graduation flow, three paths:
        ///
        ///   1. Pair exists with non-zero reserves (anti-sniping):
        ///       Transfer token+GAS to pair → pair.mint → burn LP to zero address → return
        ///
        ///   2. Pair exists with zero reserves (first LP):
        ///       No deploy fee, router.addLiquidity → burn LP to zero address
        ///
        ///   3. Pair does not exist (needs deployment):
        ///       Deduct deploy fee, deploy pair, router.addLiquidity → burn LP to zero address
        private static void Graduate(UInt160 tokenHash, SaleInfo sale)
        {
            Assert(IsEntered(), "NOT_ENTERED");
            UInt160 factory = GetFactory();
            UInt160 router = GetRouter();
            UInt160 self = Runtime.ExecutingScriptHash;

            BigInteger raisedGAS = sale.GASRaised;
            BigInteger remainingTokens = DEFAULT_TOTAL_SUPPLY - sale.TokensSold;
            Assert(remainingTokens > 0, "NO_TOKENS_FOR_LP");

            UInt160 pair = (UInt160)Contract.Call(
                factory, "getPairHash", CallFlags.ReadOnly,
                new object[] { tokenHash, GAS.Hash });

            BigInteger gasForLiquidity;

            if (pair.IsValid && !pair.IsZero)
            {
                BigInteger reserve0 = (BigInteger)Contract.Call(pair, "getReserve0", CallFlags.ReadOnly);
                BigInteger reserve1 = (BigInteger)Contract.Call(pair, "getReserve1", CallFlags.ReadOnly);

                if (reserve0 > 0 || reserve1 > 0)
                {
                    bool tokensOk = (bool)Contract.Call(
                        tokenHash, "transfer", CallFlags.All,
                        new object[] { self, pair, remainingTokens, null });
                    Assert(tokensOk, "TOKEN_TRANSFER_FAILED");

                    bool gasOk = (bool)Contract.Call(
                        GAS.Hash, "transfer", CallFlags.All,
                        new object[] { self, pair, raisedGAS, null });
                    Assert(gasOk, "GAS_TRANSFER_FAILED");

                    BigInteger mintedLiquidity = (BigInteger)Contract.Call(
                        pair, "mint", CallFlags.All,
                        new object[] { self });
                    Assert(mintedLiquidity > 0, "ZERO_LIQUIDITY_MINTED");

                    bool burnOk = (bool)Contract.Call(
                        pair, "transfer", CallFlags.All,
                        new object[] { self, UInt160.Zero, mintedLiquidity, null });
                    Assert(burnOk, "LP_BURN_FAILED");

                    sale.Graduated = true;
                    StoreSale(tokenHash, sale);
                    OnSaleCompleted(
                        tokenHash,
                        pair,
                        mintedLiquidity,
                        remainingTokens,
                        raisedGAS
                        );
                    return;
                }

                gasForLiquidity = raisedGAS;
            }
            else
            {
                var pair_DeployFee = (BigInteger)Contract.Call(factory, "getDeployFee", CallFlags.ReadOnly);

                Assert(raisedGAS > pair_DeployFee, "INSUFFICIENT_FOR_DEPLOY");
                gasForLiquidity = raisedGAS - pair_DeployFee;

                bool deployed = GAS.Transfer(
                    Runtime.ExecutingScriptHash, factory, pair_DeployFee,
                    new object[] { tokenHash, GAS.Hash });
                Assert(deployed, "PAIR_DEPLOY_FAILED");

                pair = (UInt160)Contract.Call(
                    factory, "getPairHash", CallFlags.ReadOnly,
                    new object[] { tokenHash, GAS.Hash });
                Assert(pair.IsValid && !pair.IsZero, "PAIR_NOT_FOUND");
            }

            BigInteger deadline = Runtime.Time + 60000;

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
                    0,
                    0,
                    deadline
                });

            Assert(amounts != null && amounts.Length >= 3, "INVALID_ADDLIQUIDITY_RESULT");

            ClearPendingApproval(tokenHash, pair);
            ClearPendingApproval(GAS.Hash, pair);

            BigInteger liquidity = amounts[2];

            if (liquidity > 0)
            {
                bool burnOk = (bool)Contract.Call(
                    pair, "transfer", CallFlags.All,
                    new object[] { Runtime.ExecutingScriptHash, UInt160.Zero, liquidity, null });
                Assert(burnOk, "LP_BURN_FAILED");
            }

            sale.Graduated = true;
            StoreSale(tokenHash, sale);

            OnSaleCompleted(tokenHash, pair, liquidity, remainingTokens, gasForLiquidity);
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
