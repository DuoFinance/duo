using System;
using System.ComponentModel;
using System.Numerics;
using Neo;
using Neo.SmartContract.Framework;
using Neo.SmartContract.Framework.Attributes;
using Neo.SmartContract.Framework.Native;
using Neo.SmartContract.Framework.Services;

namespace DuoStaking
{
    [DisplayName("DuoStaking")]
    [ManifestExtra("Description", "Duo Multi-Pool Staking Contract")]
    [ContractSourceCode("https://github.com/DuoFinance/duoSwap.git")]
    [ContractPermission("*")]
    public class DuoStakingContract : SmartContract
    {
        private static readonly byte[] PoolPrefix = new byte[] { 0x01 };
        private static readonly byte[] UserPrefix = new byte[] { 0x02 };
        private static readonly byte[] TokenPoolPrefix = new byte[] { 0x03 };

        private const string INIT_KEY = "Init";
        private const string OWNER_KEY = "Owner";
        private const string REWARD_TOKEN_KEY = "RewardToken";
        private const string POOL_COUNT_KEY = "PoolCount";
        private const string RESERVED_REWARDS_KEY = "ReservedRewards";

        private static readonly BigInteger AccPrecision = 1_000_000_000_000;
        private static Transaction Tx => (Transaction)Runtime.ScriptContainer;

        public struct PoolInfo
        {
            public UInt160 LpToken;
            public BigInteger RewardPerBlock;
            public BigInteger RewardEndBlock;
            public BigInteger LastRewardBlock;
            public BigInteger AccRewardPerShare;
            public BigInteger TotalStaked;
            public bool RewardsStarted;
        }

        public struct UserStakeInfo
        {
            public BigInteger Amount;
            public BigInteger RewardDebt;
        }

        public static void _deploy(object data, bool update)
        {
            if (update) return;
            Assert(Tx.Sender.IsValid && !Tx.Sender.IsZero, "Invalid Sender.");

            Storage.Put(OWNER_KEY, Tx.Sender);
            Storage.Put(INIT_KEY, 0);
            Storage.Put(POOL_COUNT_KEY, 0);
            Storage.Put(RESERVED_REWARDS_KEY, 0);
        }

        public static void Initialise(UInt160 rewardToken)
        {
            Assert(Runtime.CheckWitness(GetOwner()), "Forbidden.");
            Assert((BigInteger)Storage.Get(INIT_KEY) == 0, "Initialised.");
            Assert(rewardToken.IsValid && !rewardToken.IsZero, "Invalid Reward Token.");

            Storage.Put(REWARD_TOKEN_KEY, rewardToken);
            Storage.Put(INIT_KEY, 1);
            OnInitialised(rewardToken);
        }

        public static void OnNEP17Payment(UInt160 from, BigInteger amount, object data)
        {
            Assert(amount >= 0, "Invalid Amount.");

            if (!IsInitialised()) return;

            UInt160 callingToken = Runtime.CallingScriptHash;
            if (callingToken == GetRewardToken())
            {
                OnRewardFunded(from, amount);
                return;
            }

            BigInteger poolId = GetPoolIdByLpToken(callingToken);
            Assert(poolId >= 0, "Unsupported Token.");

            object[] args = (object[])data;
            Assert(args != null && args.Length == 3, "Invalid Deposit Data.");
            Assert((string)args[0] == "deposit", "Invalid Deposit Action.");
            Assert((BigInteger)args[1] == poolId, "Pool Mismatch.");
            Assert((UInt160)args[2] == from, "Sender Mismatch.");
        }

        [Safe]
        public static bool IsInitialised() => (BigInteger)Storage.Get(INIT_KEY) != 0;

        [Safe]
        public static UInt160 GetOwner()
        {
            ByteString owner = Storage.Get(OWNER_KEY);
            return owner?.Length == 20 ? (UInt160)owner : UInt160.Zero;
        }

        public static bool TransferOwnership(UInt160 newOwner)
        {
            UInt160 currentOwner = GetOwner();
            Assert(Runtime.CheckWitness(currentOwner), "Forbidden.");
            Assert(newOwner.IsValid && !newOwner.IsZero, "Invalid Owner.");
            Assert(newOwner != currentOwner, "Same Owner.");

            Storage.Put(OWNER_KEY, newOwner);
            OnOwnershipTransferred(currentOwner, newOwner);
            return true;
        }

        [Safe]
        public static UInt160 GetRewardToken()
        {
            ByteString rewardToken = Storage.Get(REWARD_TOKEN_KEY);
            return rewardToken?.Length == 20 ? (UInt160)rewardToken : UInt160.Zero;
        }

        [Safe]
        public static BigInteger GetPoolCount()
        {
            ByteString value = Storage.Get(POOL_COUNT_KEY);
            return value is null ? 0 : (BigInteger)value;
        }

        [Safe]
        public static BigInteger GetReservedRewards()
        {
            ByteString value = Storage.Get(RESERVED_REWARDS_KEY);
            return value is null ? 0 : (BigInteger)value;
        }

        private static void UpdateReservedRewards(BigInteger delta, bool add)
        {
            BigInteger current = GetReservedRewards();
            if (add) current += delta;
            else current -= delta;
            Storage.Put(RESERVED_REWARDS_KEY, current);
        }

        public static void AddPool(UInt160 lpToken)
        {
            Assert(Runtime.CheckWitness(GetOwner()), "Forbidden.");
            Assert(IsInitialised(), "Not Initialised.");
            Assert(lpToken.IsValid && !lpToken.IsZero, "Invalid LP Token.");
            Assert(lpToken != GetRewardToken(), "Reward Token Cannot Be Staked.");
            Assert(GetPoolIdByLpToken(lpToken) < 0, "Pool Exists.");

            BigInteger pid = GetPoolCount();
            PoolInfo pool = new()
            {
                LpToken = lpToken,
                RewardPerBlock = 0,
                RewardEndBlock = 0,
                LastRewardBlock = Ledger.CurrentIndex,
                AccRewardPerShare = 0,
                TotalStaked = 0,
                RewardsStarted = false
            };

            SavePoolInfo(pid, pool);
            SavePoolIdByLpToken(lpToken, pid);
            Storage.Put(POOL_COUNT_KEY, pid + 1);

            OnPoolAdded(pid, lpToken);
        }

        [Safe]
        public static UInt160 LpToken(BigInteger pid) => GetPoolInfo(pid).LpToken;

        [Safe]
        public static BigInteger RewardPerBlock(BigInteger pid) => GetPoolInfo(pid).RewardPerBlock;

        [Safe]
        public static BigInteger RewardEndBlock(BigInteger pid) => GetPoolInfo(pid).RewardEndBlock;

        [Safe]
        public static BigInteger LastRewardBlock(BigInteger pid) => GetPoolInfo(pid).LastRewardBlock;

        [Safe]
        public static BigInteger AccRewardPerShare(BigInteger pid) => GetPoolInfo(pid).AccRewardPerShare;

        [Safe]
        public static BigInteger TotalStaked(BigInteger pid) => GetPoolInfo(pid).TotalStaked;

        [Safe]
        public static bool RewardsStarted(BigInteger pid) => GetPoolInfo(pid).RewardsStarted;

        [Safe]
        public static object[] UserInfo(BigInteger pid, UInt160 account)
        {
            var user = GetUser(pid, account);
            return new object[] { user.Amount, user.RewardDebt };
        }

        [Safe]
        public static BigInteger PendingReward(BigInteger pid, UInt160 account)
        {
            PoolInfo pool = GetPoolInfo(pid);
            UserStakeInfo user = GetUser(pid, account);
            BigInteger accRewardPerShare = pool.AccRewardPerShare;
            BigInteger currentBlock = Ledger.CurrentIndex;

            if (pool.RewardsStarted && currentBlock > pool.LastRewardBlock && pool.TotalStaked > 0)
            {
                BigInteger toBlock = LastRewardBlockApplicable(pool);
                if (toBlock > pool.LastRewardBlock)
                {
                    BigInteger blocks = toBlock - pool.LastRewardBlock;
                    BigInteger reward = blocks * pool.RewardPerBlock;
                    accRewardPerShare += reward * AccPrecision / pool.TotalStaked;
                }
            }

            return user.Amount * accRewardPerShare / AccPrecision - user.RewardDebt;
        }

        public static void StartRewards(BigInteger pid, BigInteger rewardBlocks)
        {
            Assert(Runtime.CheckWitness(GetOwner()), "Forbidden.");
            Assert(rewardBlocks > 0, "ZERO_BLOCKS");

            PoolInfo pool = GetPoolInfo(pid);
            Assert(!pool.RewardsStarted, "STARTED");

            BigInteger totalBalance = BalanceOfToken(GetRewardToken(), Runtime.ExecutingScriptHash);
            BigInteger reserved = GetReservedRewards();
            BigInteger availableRewards = totalBalance - reserved;
            Assert(availableRewards > 0, "NO_UNALLOCATED_REWARDS");

            BigInteger perBlock = availableRewards / rewardBlocks;
            Assert(perBlock > 0, "TOO_FEW_REWARDS");

            pool.RewardsStarted = true;
            pool.RewardPerBlock = perBlock;
            pool.LastRewardBlock = Ledger.CurrentIndex;
            pool.RewardEndBlock = Ledger.CurrentIndex + rewardBlocks;
            
            UpdateReservedRewards(availableRewards, true);
            SavePoolInfo(pid, pool);

            OnRewardsStarted(pid, perBlock, Ledger.CurrentIndex, pool.RewardEndBlock, availableRewards);
        }

        public static void FundAndStartRewards(BigInteger pid, BigInteger amount, BigInteger rewardBlocks)
        {
            Assert(Runtime.CheckWitness(GetOwner()), "Forbidden.");
            PoolInfo pool = GetPoolInfo(pid);
            Assert(!pool.RewardsStarted, "STARTED");
            AddRewardsInternal(pid, amount, rewardBlocks, true);
        }

        public static void AddRewards(BigInteger pid, BigInteger amount, BigInteger rewardBlocks)
        {
            Assert(Runtime.CheckWitness(GetOwner()), "Forbidden.");
            PoolInfo pool = GetPoolInfo(pid);
            Assert(pool.RewardsStarted, "NOT_STARTED");
            AddRewardsInternal(pid, amount, rewardBlocks, false);
        }

        public static void AddRewardsForNewPool(BigInteger pid, BigInteger amount, BigInteger rewardBlocks)
        {
            FundAndStartRewards(pid, amount, rewardBlocks);
        }

        public static void AddRewardsForExistingPool(BigInteger pid, BigInteger amount, BigInteger rewardBlocks)
        {
            AddRewards(pid, amount, rewardBlocks);
        }

        public static void Deposit(BigInteger pid, BigInteger amount)
        {
            Assert(IsInitialised(), "Not Initialised.");
            Assert(amount >= 0, "Invalid Amount.");

            UInt160 sender = Tx.Sender;
            Assert(Runtime.CheckWitness(sender), "Forbidden.");

            UpdatePool(pid);

            PoolInfo pool = GetPoolInfo(pid);
            UserStakeInfo user = GetUser(pid, sender);
            BigInteger accumulated =
                user.Amount * pool.AccRewardPerShare / AccPrecision;
            BigInteger pending = accumulated - user.RewardDebt;

            if (amount > 0)
            {
                BigInteger balanceBefore =
                    BalanceOfToken(pool.LpToken, Runtime.ExecutingScriptHash);

                SafeTransfer(
                    pool.LpToken,
                    sender,
                    Runtime.ExecutingScriptHash,
                    amount,
                    new object[] { "deposit", pid, sender }
                );

                BigInteger balanceAfter =
                    BalanceOfToken(pool.LpToken, Runtime.ExecutingScriptHash);
                BigInteger receivedAmount = balanceAfter - balanceBefore;
                Assert(receivedAmount == amount, "FOT_NOT_SUPPORTED");

                user.Amount += receivedAmount;
                pool.TotalStaked += receivedAmount;

                OnDeposit(sender, pid, receivedAmount);
            }

            BigInteger newAccumulated =
                user.Amount * pool.AccRewardPerShare / AccPrecision;
            user.RewardDebt = newAccumulated;

            SaveUser(pid, sender, user);
            SavePoolInfo(pid, pool);

            if (pending > 0)
            {
                BigInteger sent = SafeRewardTransfer(sender, pending);
                if (sent < pending)
                {
                    user.RewardDebt -= (pending - sent);
                    SaveUser(pid, sender, user);
                }

                OnClaim(sender, pid, sent);
            }
        }

        public static void Withdraw(BigInteger pid, BigInteger amount)
        {
            Assert(IsInitialised(), "Not Initialised.");
            Assert(amount >= 0, "Invalid Amount.");

            UInt160 sender = Tx.Sender;
            Assert(Runtime.CheckWitness(sender), "Forbidden.");

            UpdatePool(pid);

            PoolInfo pool = GetPoolInfo(pid);
            UserStakeInfo user = GetUser(pid, sender);

            Assert(user.Amount >= amount, "AMOUNT_TOO_HIGH");

            BigInteger accumulated =
                user.Amount * pool.AccRewardPerShare / AccPrecision;
            BigInteger pending = accumulated - user.RewardDebt;

            if (amount > 0)
            {
                user.Amount -= amount;
                pool.TotalStaked -= amount;
            }

            BigInteger newAccumulated =
                user.Amount * pool.AccRewardPerShare / AccPrecision;

            user.RewardDebt = newAccumulated;

            SaveUser(pid, sender, user);
            SavePoolInfo(pid, pool);

            if (pending > 0)
            {
                BigInteger sent = SafeRewardTransfer(sender, pending);

                if (sent < pending)
                {
                    user.RewardDebt -= (pending - sent);
                    SaveUser(pid, sender, user);
                }

                OnClaim(sender, pid, sent);
            }

            if (amount > 0)
            {
                SafeTransfer(
                    pool.LpToken,
                    Runtime.ExecutingScriptHash,
                    sender,
                    amount,
                    null
                );

                OnWithdraw(sender, pid, amount);
            }
        }

        public static void Claim(BigInteger pid)
        {
            Assert(IsInitialised(), "Not Initialised.");
            UInt160 sender = Tx.Sender;
            Assert(Runtime.CheckWitness(sender), "Forbidden.");

            PoolInfo pool = GetPoolInfo(pid);
            UpdatePool(pid);
            pool = GetPoolInfo(pid);
            UserStakeInfo user = GetUser(pid, sender);

            BigInteger accumulated =
                user.Amount * pool.AccRewardPerShare / AccPrecision;

            BigInteger pending = accumulated - user.RewardDebt;

            user.RewardDebt = accumulated;
            SaveUser(pid, sender, user);

            if (pending > 0)
            {
                SafeRewardTransfer(sender, pending);
                OnClaim(sender, pid, pending);
            }

        }

        public static void EmergencyWithdraw(BigInteger pid)
        {
            Assert(IsInitialised(), "Not Initialised.");
            UInt160 sender = Tx.Sender;
            Assert(Runtime.CheckWitness(sender), "Forbidden.");

            PoolInfo pool = GetPoolInfo(pid);
            UserStakeInfo user = GetUser(pid, sender);
            BigInteger amount = user.Amount;
            Assert(amount > 0, "NO_STAKE");

            user.Amount = 0;
            user.RewardDebt = 0;
            pool.TotalStaked -= amount;

            SaveUser(pid, sender, user);
            SavePoolInfo(pid, pool);

            SafeTransfer(pool.LpToken, Runtime.ExecutingScriptHash, sender, amount, null);
            OnEmergencyWithdraw(sender, pid, amount);
        }

        public static void RecoverEndedRewards(BigInteger pid, UInt160 to, BigInteger amount)
        {
            Assert(Runtime.CheckWitness(GetOwner()), "Forbidden.");
            Assert(to.IsValid && !to.IsZero, "ZERO_TO");

            PoolInfo pool = GetPoolInfo(pid);
            Assert(pool.RewardsStarted && Ledger.CurrentIndex > pool.RewardEndBlock, "NOT_ENDED");
            
            UpdateReservedRewards(amount, false);
            SafeTransfer(GetRewardToken(), Runtime.ExecutingScriptHash, to, amount, null);
        }

        public static void UpdatePool(BigInteger pid)
        {
            PoolInfo pool = GetPoolInfo(pid);
            if (!pool.RewardsStarted) return;

            BigInteger currentBlock = Ledger.CurrentIndex;
            if (currentBlock <= pool.LastRewardBlock) return;

            if (pool.TotalStaked == 0)
            {
                pool.LastRewardBlock = LastRewardBlockApplicable(pool);
                SavePoolInfo(pid, pool);
                return;
            }

            BigInteger toBlock = LastRewardBlockApplicable(pool);
            if (toBlock <= pool.LastRewardBlock) return;

            BigInteger blocks = toBlock - pool.LastRewardBlock;
            BigInteger reward = blocks * pool.RewardPerBlock;
            pool.AccRewardPerShare += reward * AccPrecision / pool.TotalStaked;
            pool.LastRewardBlock = toBlock;

            SavePoolInfo(pid, pool);
        }

        [Safe]
        public static BigInteger GetPoolIdByLpToken(UInt160 lpToken)
        {
            ByteString raw = Storage.Get(Storage.CurrentContext, TokenPoolKey(lpToken));
            return raw is null ? -1 : (BigInteger)raw;
        }

        private static BigInteger LastRewardBlockApplicable(PoolInfo pool)
        {
            BigInteger currentBlock = Ledger.CurrentIndex;
            return currentBlock < pool.RewardEndBlock ? currentBlock : pool.RewardEndBlock;
        }

        private static void AddRewardsInternal(BigInteger pid, BigInteger amount, BigInteger rewardBlocks, bool isNewPool)
        {
            Assert(amount > 0, "ZERO_REWARD_AMOUNT");
            Assert(rewardBlocks > 0, "ZERO_BLOCKS");

            PoolInfo pool = GetPoolInfo(pid);
            UpdatePool(pid);
            pool = GetPoolInfo(pid);

            BigInteger balanceBefore = BalanceOfToken(GetRewardToken(), Runtime.ExecutingScriptHash);
            SafeTransfer(GetRewardToken(), Tx.Sender, Runtime.ExecutingScriptHash, amount, null);
            BigInteger fundedAmount = BalanceOfToken(GetRewardToken(), Runtime.ExecutingScriptHash) - balanceBefore;
            Assert(fundedAmount == amount, "FOT_NOT_SUPPORTED");

            BigInteger remainingRewards = 0;
            if (pool.RewardsStarted && pool.RewardEndBlock > pool.LastRewardBlock)
            {
                remainingRewards = (pool.RewardEndBlock - pool.LastRewardBlock) * pool.RewardPerBlock;
            }

            BigInteger totalReward = remainingRewards + fundedAmount;
            BigInteger perBlock = totalReward / rewardBlocks;
            Assert(perBlock > 0, "TOO_FEW_REWARDS");

            BigInteger currentBlock = Ledger.CurrentIndex;
            pool.RewardsStarted = true;
            pool.RewardPerBlock = perBlock;
            pool.LastRewardBlock = currentBlock;
            pool.RewardEndBlock = currentBlock + rewardBlocks;
            
            UpdateReservedRewards(fundedAmount, true);
            SavePoolInfo(pid, pool);

            if (isNewPool)
            {
                OnRewardsStarted(pid, perBlock, currentBlock, pool.RewardEndBlock, totalReward);
            }
            else
            {
                OnRewardsAdded(pid, fundedAmount, perBlock, currentBlock, pool.RewardEndBlock, totalReward);
            }
        }

        private static BigInteger BalanceOfToken(UInt160 token, UInt160 account)
        {
            return (BigInteger)Contract.Call(token, "balanceOf", CallFlags.ReadOnly, new object[] { account });
        }

        private static void SafeTransfer(UInt160 token, UInt160 from, UInt160 to, BigInteger amount, object data)
        {
            try
            {
                bool result = (bool)Contract.Call(token, "transfer", CallFlags.All, new object[] { from, to, amount, data });
                Assert(result, "Transfer Failed.");
            }
            catch (Exception)
            {
                Assert(false, "Transfer Error.");
            }
        }

        private static BigInteger SafeRewardTransfer(UInt160 to, BigInteger amount)
        {
            UInt160 rewardToken = GetRewardToken();
            BigInteger balance = BalanceOfToken(rewardToken, Runtime.ExecutingScriptHash);

            BigInteger sendAmount = amount > balance ? balance : amount;

            if (sendAmount > 0)
            {
                UpdateReservedRewards(sendAmount, false);
                SafeTransfer(rewardToken, Runtime.ExecutingScriptHash, to, sendAmount, null);
            }

            return sendAmount; 
        }

        [Safe]
        public static PoolInfo GetPoolInfo(BigInteger pid)
        {
            Assert(pid >= 0 && pid < GetPoolCount(), "Invalid Pool.");
            ByteString raw = Storage.Get(Storage.CurrentContext, PoolKey(pid));
            Assert(raw != null, "Pool Not Found.");
            return (PoolInfo)StdLib.Deserialize(raw);
        }

        private static void SavePoolInfo(BigInteger pid, PoolInfo pool)
        {
            Storage.Put(Storage.CurrentContext, PoolKey(pid), StdLib.Serialize(pool));
        }

        private static UserStakeInfo GetUser(BigInteger pid, UInt160 account)
        {
            ByteString raw = Storage.Get(Storage.CurrentContext, UserKey(pid, account));
            if (raw is null)
            {
                return new UserStakeInfo { Amount = 0, RewardDebt = 0 };
            }

            return (UserStakeInfo)StdLib.Deserialize(raw);
        }

        private static void SaveUser(BigInteger pid, UInt160 account, UserStakeInfo user)
        {
            Storage.Put(Storage.CurrentContext, UserKey(pid, account), StdLib.Serialize(user));
        }

        private static void SavePoolIdByLpToken(UInt160 lpToken, BigInteger pid)
        {
            Storage.Put(Storage.CurrentContext, TokenPoolKey(lpToken), pid);
        }

        private static ByteString PoolKey(BigInteger pid)
        {
            return Concat((ByteString)PoolPrefix, (ByteString)pid.ToByteArray());
        }

        private static ByteString UserKey(BigInteger pid, UInt160 account)
        {
            return Concat(Concat((ByteString)UserPrefix, (ByteString)pid.ToByteArray()), (ByteString)account);
        }

        private static ByteString TokenPoolKey(UInt160 token)
        {
            return Concat((ByteString)TokenPoolPrefix, (ByteString)token);
        }

        [OpCode(OpCode.CAT)]
        private static extern ByteString Concat(ByteString left, ByteString right);

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                OnFault(message, null);
                ExecutionEngine.Assert(false);
            }
        }

         public static void Update(ByteString nef, string manifest)
        {
            Assert(Runtime.CheckWitness(GetOwner()), "Forbidden.");
            ContractManagement.Update(nef, manifest);
        }

        [DisplayName("Initialised")]
        public static event InitialisedEvent OnInitialised;
        public delegate void InitialisedEvent(UInt160 rewardToken);

        [DisplayName("OwnershipTransferred")]
        public static event OwnershipTransferredEvent OnOwnershipTransferred;
        public delegate void OwnershipTransferredEvent(UInt160 previousOwner, UInt160 newOwner);

        [DisplayName("PoolAdded")]
        public static event PoolAddedEvent OnPoolAdded;
        public delegate void PoolAddedEvent(BigInteger pid, UInt160 lpToken);

        [DisplayName("Deposit")]
        public static event DepositEvent OnDeposit;
        public delegate void DepositEvent(UInt160 user, BigInteger pid, BigInteger amount);

        [DisplayName("Withdraw")]
        public static event WithdrawEvent OnWithdraw;
        public delegate void WithdrawEvent(UInt160 user, BigInteger pid, BigInteger amount);

        [DisplayName("Claim")]
        public static event ClaimEvent OnClaim;
        public delegate void ClaimEvent(UInt160 user, BigInteger pid, BigInteger amount);

        [DisplayName("EmergencyWithdraw")]
        public static event EmergencyWithdrawEvent OnEmergencyWithdraw;
        public delegate void EmergencyWithdrawEvent(UInt160 user, BigInteger pid, BigInteger amount);

        [DisplayName("RewardsStarted")]
        public static event RewardsStartedEvent OnRewardsStarted;
        public delegate void RewardsStartedEvent(
            BigInteger pid,
            BigInteger rewardPerBlock,
            BigInteger startBlock,
            BigInteger endBlock,
            BigInteger totalReward);

        [DisplayName("RewardsAdded")]
        public static event RewardsAddedEvent OnRewardsAdded;
        public delegate void RewardsAddedEvent(
            BigInteger pid,
            BigInteger addedReward,
            BigInteger rewardPerBlock,
            BigInteger startBlock,
            BigInteger endBlock,
            BigInteger totalReward);

        [DisplayName("RewardFunded")]
        public static event RewardFundedEvent OnRewardFunded;
        public delegate void RewardFundedEvent(UInt160 from, BigInteger amount);

        [DisplayName("Fault")]
        public static event FaultEvent OnFault;
        public delegate void FaultEvent(string message, params object[] args);
    }
}
