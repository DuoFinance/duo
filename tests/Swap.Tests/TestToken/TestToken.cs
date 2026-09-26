using System.ComponentModel;
using System.Numerics;
using Neo;
using Neo.SmartContract.Framework;
using Neo.SmartContract.Framework.Attributes;
using Neo.SmartContract.Framework.Services;

namespace DuoTestToken;

[DisplayName("TestToken")]
[SupportedStandards("NEP-17")]
[ContractPermission("*", new[] { "*" })]
public class TestTokenContract : Nep17Token
{
    private static StorageMap Metadata => new(Storage.CurrentContext, "Metadata");
    private static Transaction Tx => (Transaction)Runtime.Transaction;

    private const string InitKey = "Init";
    private const string OwnerKey = "Owner";
    private const string TreasuryKey = "Treasury";
    private static readonly BigInteger InitialTokenSupply = 10_000_000_000_000;

    public static void _deploy(object data, bool update)
    {
        Assert(!update, "Not Upgradable.");
        Assert(Tx.Sender.IsValid && !Tx.Sender.IsZero, "Invalid Sender.");

        Metadata.Put(OwnerKey, Tx.Sender);
        Metadata.Put(InitKey, 0);
    }

    public static void Initialise(UInt160 treasury)
    {
        Assert(Runtime.CheckWitness(GetOwner()), "Forbidden.");
        Assert((BigInteger)Metadata.Get(InitKey) == 0, "Initialised.");
        Assert(treasury.IsValid && !treasury.IsZero, "Invalid Treasury.");

        Metadata.Put(InitKey, 1);
        Metadata.Put(TreasuryKey, treasury);
        Mint(treasury, InitialTokenSupply);
    }

    [Safe]
    public static UInt160 GetOwner()
    {
        ByteString owner = Metadata.Get(OwnerKey);
        return owner?.Length == 20 ? (UInt160)owner : UInt160.Zero;
    }

    [Safe]
    public static UInt160 GetTreasury()
    {
        ByteString treasury = Metadata.Get(TreasuryKey);
        return treasury?.Length == 20 ? (UInt160)treasury : UInt160.Zero;
    }

    [Safe]
    public static bool IsInitialised() => (BigInteger)Metadata.Get(InitKey) != 0;

    public override byte Decimals
    {
        [Safe]
        get => 4;
    }

    public override string Symbol
    {
        [Safe]
        get => "TEST";
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            ExecutionEngine.Assert(false);
    }
}
