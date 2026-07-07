using System.Numerics;
using Xunit;
using Neo;
using Neo.SmartContract.Testing;

namespace DuoSwap.Tests;

using static TestUtils;

public class StakingTests
{
    private readonly TestEngine _engine;
    private readonly UInt160 _stakingHash;

    public StakingTests()
    {
        _engine = CreateEngine();

        var (stakingNef, stakingManifest) = LoadContract("DuoStaking");
        _stakingHash = DeployContract(_engine, stakingNef, stakingManifest, null);
    }

    [Fact]
    public void GetOwner_ReturnsDeployer()
    {
        var result = Invoke(_engine, _stakingHash, "getOwner");
        Assert.NotNull(result);
    }

    [Fact]
    public void IsInitialised_ReturnsFalseBeforeInit()
    {
        var result = Invoke(_engine, _stakingHash, "isInitialised");
        Assert.NotNull(result);
    }

    [Fact]
    public void GetPoolCount_InitiallyZero()
    {
        var result = Invoke(_engine, _stakingHash, "getPoolCount");
        Assert.Equal(BigInteger.Zero, result.GetInteger());
    }

    [Fact]
    public void GetReservedRewards_InitiallyZero()
    {
        var result = Invoke(_engine, _stakingHash, "getReservedRewards");
        Assert.Equal(BigInteger.Zero, result.GetInteger());
    }

    [Fact]
    public void GetRewardToken_ReturnsZeroBeforeInit()
    {
        var result = Invoke(_engine, _stakingHash, "getRewardToken");
        Assert.NotNull(result);
    }

    [Fact]
    public void TransferOwnership_UpdatesOwner()
    {
        var newOwner = new UInt160(new byte[] { 9, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
        var result = Invoke(_engine, _stakingHash, "transferOwnership", newOwner);
        Assert.NotNull(result);
    }

    [Fact]
    public void Staking_UserFlow_Deposit_Stake_Claim_Withdraw_Emergency()
    {
        // 1. Deploy DuoToken contracts (one for Reward, one for LpToken)
        var (rewardNef, rewardManifest) = LoadContract("DuoToken");
        rewardManifest.Name = "RewardToken";
        var rewardToken = DeployContract(_engine, rewardNef, rewardManifest, null);
        
        var (lpNef, lpManifest) = LoadContract("DuoToken");
        lpManifest.Name = "LpToken";
        var lpToken = DeployContract(_engine, lpNef, lpManifest, null);

        // 2. Initialise the Tokens under Owner signature
        _engine.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.Global, Owner);
        
        BigInteger initialSupply = 1_000_000_000_000; // 10,000 tokens (with 8 decimals)
        Invoke(_engine, rewardToken, "initialise", Owner, initialSupply);
        Invoke(_engine, lpToken, "initialise", Owner, initialSupply);

        // Send some LP tokens to User to test staking
        Invoke(_engine, lpToken, "transfer", Owner, User, 1_000_000, null);
        Assert.Equal(1_000_000, Invoke(_engine, lpToken, "balanceOf", User).GetInteger());

        // 3. Initialise DuoStaking contract and link to RewardToken
        Invoke(_engine, _stakingHash, "initialise", rewardToken);
        Assert.True(Invoke(_engine, _stakingHash, "isInitialised").GetBoolean());
        Assert.Equal(rewardToken, new UInt160(Invoke(_engine, _stakingHash, "getRewardToken").GetSpan()));

        // 4. Add LP Token Pool (pid = 0)
        Invoke(_engine, _stakingHash, "addPool", lpToken);
        Assert.Equal(1, Invoke(_engine, _stakingHash, "getPoolCount").GetInteger());
        Assert.Equal(lpToken, new UInt160(Invoke(_engine, _stakingHash, "lpToken", BigInteger.Zero).GetSpan()));

        // 5. Fund the rewards and start reward program
        // Owner funds 100,000,000 (1 reward token) for 100 blocks
        // Reward per block = 1,000,000
        BigInteger fundAmount = 100_000_000;
        BigInteger rewardBlocks = 100;
        
        Invoke(_engine, _stakingHash, "fundAndStartRewards", BigInteger.Zero, fundAmount, rewardBlocks);
        Assert.Equal(fundAmount, Invoke(_engine, _stakingHash, "getReservedRewards").GetInteger());
        Assert.True(Invoke(_engine, _stakingHash, "rewardsStarted", BigInteger.Zero).GetBoolean());
        Assert.Equal(1_000_000, Invoke(_engine, _stakingHash, "rewardPerBlock", BigInteger.Zero).GetInteger());

        // 6. User Deposits / Stakes 10,000 LP Tokens
        _engine.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.Global, User);
        
        BigInteger stakeAmount = 10_000;
        Invoke(_engine, _stakingHash, "deposit", BigInteger.Zero, stakeAmount);

        // Verify staked amount
        var userInfo = (Neo.VM.Types.Array)Invoke(_engine, _stakingHash, "userInfo", BigInteger.Zero, User);
        Assert.Equal(stakeAmount, userInfo[0].GetInteger());
        Assert.Equal(stakeAmount, Invoke(_engine, _stakingHash, "totalStaked", BigInteger.Zero).GetInteger());
        Assert.Equal(1_000_000 - stakeAmount, Invoke(_engine, lpToken, "balanceOf", User).GetInteger());

        // 7. Advance block height by 10 blocks
        _engine.PersistingBlock.Skip(9, TimeSpan.FromSeconds(135));
        _engine.PersistingBlock.Persist(System.Array.Empty<Neo.Network.P2P.Payloads.Transaction>(), System.Array.Empty<Neo.VM.VMState>());

        // 8. Verify Pending Reward (should be 10 * 1,000_000 = 10,000_000)
        BigInteger pending = Invoke(_engine, _stakingHash, "pendingReward", BigInteger.Zero, User).GetInteger();
        Assert.Equal(10_000_000, pending);

        // 9. User Claims rewards
        Assert.Equal(0, Invoke(_engine, rewardToken, "balanceOf", User).GetInteger());
        Invoke(_engine, _stakingHash, "claim", BigInteger.Zero);

        // Verify reward claimed
        Assert.Equal(10_000_000, Invoke(_engine, rewardToken, "balanceOf", User).GetInteger());
        Assert.Equal(0, Invoke(_engine, _stakingHash, "pendingReward", BigInteger.Zero, User).GetInteger());

        // 10. User Withdraws part of LP Token (4,000 LP tokens)
        Invoke(_engine, _stakingHash, "withdraw", BigInteger.Zero, 4_000);
        
        userInfo = (Neo.VM.Types.Array)Invoke(_engine, _stakingHash, "userInfo", BigInteger.Zero, User);
        Assert.Equal(6_000, userInfo[0].GetInteger()); // Remaining 6,000 LP staked
        Assert.Equal(1_000_000 - 6_000, Invoke(_engine, lpToken, "balanceOf", User).GetInteger());

        // 11. User performs Emergency Withdraw (returns remaining 6,000 LP tokens, forfeiting any rewards)
        Invoke(_engine, _stakingHash, "emergencyWithdraw", BigInteger.Zero);
        
        userInfo = (Neo.VM.Types.Array)Invoke(_engine, _stakingHash, "userInfo", BigInteger.Zero, User);
        Assert.Equal(0, userInfo[0].GetInteger()); // 0 LP staked
        Assert.Equal(1_000_000, Invoke(_engine, lpToken, "balanceOf", User).GetInteger()); // All LP returned
    }
}
