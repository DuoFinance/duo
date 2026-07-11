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
        var factory = DeployContract(engine, fn, fm, null);
        var (rn, rm) = LoadContract("DuoSwapRouter");
        var router = DeployContract(engine, rn, rm, new object[] { factory });
        Invoke(engine, factory, "setRouter", router);
        var (bn, bm) = LoadContract("DuoBondingCurve");
        var bondingCurve = DeployContract(engine, bn, bm, new object[] { factory, router });

        var bcState = engine.Native.ContractManagement.GetContract(bondingCurve);
        int bcId = bcState.Id;

        var (ln, lm) = LoadContract("DuoLaunchpad");
        var tokenHash = DeployContract(engine, ln, lm, null);

        BigInteger totalSupply = 1_000_000_000;
        byte decimals = 8;
        string symbol = "TST";
        Invoke(engine, tokenHash, "initialise", bondingCurve, (byte)decimals, symbol, totalSupply);

        BigInteger targetRaise = 100_000_000_000;
        BigInteger graduationBps = 5001;
        BigInteger twoPminusOne = 2 * graduationBps - 10000;
        BigInteger pSquared = graduationBps * graduationBps;
        BigInteger vToken = totalSupply * pSquared / (10000 * twoPminusOne);
        BigInteger vGas = targetRaise * (10000 - graduationBps) / twoPminusOne;

        // Build the serialized SaleInfo map (matching StdLib.Serialize format for struct)
        var map = new Map();
        map[(BigInteger)0] = (Neo.VM.Types.ByteString)tokenHash.GetSpan().ToArray();
        map[(BigInteger)1] = (Neo.VM.Types.ByteString)Owner.GetSpan().ToArray();
        map[(BigInteger)2] = (Neo.VM.Types.ByteString)Encoding.UTF8.GetBytes(symbol);
        map[(BigInteger)3] = new Integer(totalSupply);
        map[(BigInteger)4] = new Integer(BigInteger.Zero);
        map[(BigInteger)5] = new Integer(BigInteger.Zero);
        map[(BigInteger)6] = new Integer(targetRaise);
        map[(BigInteger)7] = new Integer(vGas);
        map[(BigInteger)8] = new Integer(vToken);
        map[(BigInteger)9] = new Integer(graduationBps);
        map[(BigInteger)10] = Neo.VM.Types.Boolean.False;

        byte[] serializedSale = BinarySerializer.Serialize(map, new ExecutionEngineLimits());

        byte[] keyBytes = new byte[24];
        Encoding.UTF8.GetBytes("Sale", 0, 4, keyBytes, 0);
        tokenHash.GetSpan().CopyTo(keyBytes.AsSpan(4));

        var store = engine.Storage.Snapshot;
        store.Add(new StorageKey { Id = bcId, Key = keyBytes }, new StorageItem { Value = serializedSale });
        store.Commit();

        var result = Invoke(engine, bondingCurve, "getSaleInfo", tokenHash);
        var resultMap = (Neo.VM.Types.Map)result;

        Assert.Equal(totalSupply, resultMap[(BigInteger)3].GetInteger());
        Assert.Equal(BigInteger.Zero, resultMap[(BigInteger)4].GetInteger());
        Assert.Equal(BigInteger.Zero, resultMap[(BigInteger)5].GetInteger());
        Assert.Equal(targetRaise, resultMap[(BigInteger)6].GetInteger());
        Assert.Equal(vGas, resultMap[(BigInteger)7].GetInteger());
        Assert.Equal(vToken, resultMap[(BigInteger)8].GetInteger());
        Assert.Equal(graduationBps, resultMap[(BigInteger)9].GetInteger());
        Assert.False(resultMap[(BigInteger)10].GetBoolean());

        var buyResult = Invoke(engine, bondingCurve, "getBuyAmount", tokenHash, (BigInteger)10_000_000_000);
        Assert.True(buyResult.GetInteger() > 0);
    }
}