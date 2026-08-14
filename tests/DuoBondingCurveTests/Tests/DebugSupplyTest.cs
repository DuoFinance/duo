using System.Numerics;
using System.Text;
using Xunit;
using Neo;
using Neo.IO;
using Neo.Persistence;
using Neo.SmartContract;
using Neo.SmartContract.Testing;
using Neo.VM;
using Neo.VM.Types;

namespace DuoBondingCurveTests;

using static TestUtils;

public class DebugSupplyTest
{
    [Fact]
    public void Debug_WriteAndReadSaleStorage()
    {
        var engine = CreateEngine();
        engine.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.Global, Owner);

        var (fn, fm) = LoadContract("DuoSwapFactory");
        var factory = DeployContract(engine, fn, fm, Owner);
        var (rn, rm) = LoadContract("DuoSwapRouter");
        var router = DeployContract(engine, rn, rm, new object[] { factory });
        Invoke(engine, factory, "setRouter", router);
        var (bn, bm) = LoadContract("DuoBondingCurve");
        var bondingCurve = DeployContract(engine, bn, bm, new object[] { factory, router });
        Invoke(engine, factory, "setBonding", bondingCurve);

        var bcState = engine.Native.ContractManagement.GetContract(bondingCurve);
        int bcId = bcState.Id;

        var (ln, lm) = LoadContract("DuoLaunchpad");
        BigInteger totalSupply = 1_000_000_000;
        byte decimals = 8;
        BigInteger rawSupply = totalSupply * BigInteger.Pow(10, decimals);
        string symbol = "TST";
        var tokenHash = DeployContract(engine, ln, lm,
            new object[] { bondingCurve, 8, symbol, rawSupply });

        BigInteger targetRaise = 100_000_000_000;
        BigInteger graduationBps = 5001;
        BigInteger twoPminusOne = 2 * graduationBps - 10000;
        BigInteger pSquared = graduationBps * graduationBps;
        BigInteger vToken = rawSupply * pSquared / (10000 * twoPminusOne);
        BigInteger vGas = targetRaise * (10000 - graduationBps) / twoPminusOne;

        var map = new Map();
        map[(BigInteger)0] = (Neo.VM.Types.ByteString)Owner.GetSpan().ToArray();
        map[(BigInteger)1] = new Integer(BigInteger.Zero);
        map[(BigInteger)2] = new Integer(BigInteger.Zero);
        map[(BigInteger)3] = new Integer(targetRaise);
        map[(BigInteger)4] = new Integer(vGas);
        map[(BigInteger)5] = new Integer(vToken);
        map[(BigInteger)6] = new Integer(graduationBps);
        map[(BigInteger)7] = Neo.VM.Types.Boolean.False;

        byte[] serializedSale = BinarySerializer.Serialize(map, new ExecutionEngineLimits());

        byte[] keyBytes = new byte[24];
        Encoding.UTF8.GetBytes("Sale", 0, 4, keyBytes, 0);
        tokenHash.GetSpan().CopyTo(keyBytes.AsSpan(4));

        var store = engine.Storage.Snapshot;
        store.Add(new StorageKey { Id = bcId, Key = keyBytes }, new StorageItem { Value = serializedSale });
        store.Commit();

        var result = Invoke(engine, bondingCurve, "getSale", tokenHash);
        var resultMap = (Neo.VM.Types.Map)result;

        Assert.Equal(BigInteger.Zero, resultMap[(BigInteger)1].GetInteger());
        Assert.Equal(BigInteger.Zero, resultMap[(BigInteger)2].GetInteger());
        Assert.Equal(targetRaise, resultMap[(BigInteger)3].GetInteger());
        Assert.Equal(vGas, resultMap[(BigInteger)4].GetInteger());
        Assert.Equal(vToken, resultMap[(BigInteger)5].GetInteger());
        Assert.Equal(graduationBps, resultMap[(BigInteger)6].GetInteger());
        Assert.False(resultMap[(BigInteger)7].GetBoolean());

        var buyResult = Invoke(engine, bondingCurve, "getTokensForGAS", tokenHash, (BigInteger)10_000_000_000);
        Assert.True(buyResult.GetInteger() > 0);
    }
}
