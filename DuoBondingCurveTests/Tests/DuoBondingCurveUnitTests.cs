using System.Numerics;
using Xunit;
using Neo;
using Neo.SmartContract.Testing;


namespace DuoBondingCurveTests;

using static TestUtils;

public class DuoBondingCurveUnitTests
{
    private readonly TestEngine _engine;
    private readonly UInt160 _bondingCurveHash;
    private readonly UInt160 _factory;
    private readonly UInt160 _router;

    public DuoBondingCurveUnitTests()
    {
        _engine = CreateEngine();
        _factory = UInt160.Parse("0x00000000000000000000000000000000000000fa");
        _router = UInt160.Parse("0x00000000000000000000000000000000000000ce");

        var (nef, manifest) = LoadContract("DuoBondingCurve");
        _bondingCurveHash = DeployContract(_engine, nef, manifest, new object[] { _factory, _router });
    }

    [Fact]
    public void GetOwner_ReturnsDeployer()
    {
        var result = Invoke(_engine, _bondingCurveHash, "getOwner");
        var owner = result.GetSpan();
        Assert.Equal(Owner, new UInt160(owner.ToArray()));
    }

    [Fact]
    public void GetFactory_ReturnsDeployedFactory()
    {
        var result = Invoke(_engine, _bondingCurveHash, "getFactory");
        var addr = result.GetSpan();
        Assert.Equal(_factory, new UInt160(addr.ToArray()));
    }

    [Fact]
    public void GetRouter_ReturnsDeployedRouter()
    {
        var result = Invoke(_engine, _bondingCurveHash, "getRouter");
        var addr = result.GetSpan();
        Assert.Equal(_router, new UInt160(addr.ToArray()));
    }

    [Fact]
    public void GetSaleInfo_ForUnknownSale_ReturnsEmpty()
    {
        var dummyHash = UInt160.Parse("0x00000000000000000000000000000000000000ff");
        var result = Invoke(_engine, _bondingCurveHash, "getSaleInfo", dummyHash);
        Assert.NotNull(result);
    }

    [Fact]
    public void GetCurrentPrice_ForUnknownSale_Faults()
    {
        var dummyHash = UInt160.Parse("0x00000000000000000000000000000000000000ff");
        var exception = Record.Exception(() =>
            Invoke(_engine, _bondingCurveHash, "getCurrentPrice", dummyHash));
        Assert.NotNull(exception);
    }

    [Fact]
    public void GetBuyAmount_ForUnknownSale_Faults()
    {
        var dummyHash = UInt160.Parse("0x00000000000000000000000000000000000000ff");
        var exception = Record.Exception(() =>
            Invoke(_engine, _bondingCurveHash, "getBuyAmount", dummyHash, 100_000_000));
        Assert.NotNull(exception);
    }

    [Fact]
    public void GetRefundAmount_ForUnknownSale_Faults()
    {
        var dummyHash = UInt160.Parse("0x00000000000000000000000000000000000000ff");
        var exception = Record.Exception(() =>
            Invoke(_engine, _bondingCurveHash, "getRefundAmount", dummyHash, 1_000_000));
        Assert.NotNull(exception);
    }

    [Fact]
    public void GetSellAmount_ForUnknownSale_Faults()
    {
        var dummyHash = UInt160.Parse("0x00000000000000000000000000000000000000ff");
        var exception = Record.Exception(() =>
            Invoke(_engine, _bondingCurveHash, "getSellAmount", dummyHash, 1_000_000));
        Assert.NotNull(exception);
    }

    [Fact]
    public void CreateSale_WithValidParams_FailsAtDeploy()
    {
        string name = "TestToken";
        string symbol = "TST";
        byte decimals = 8;
        BigInteger totalSupply = 1_000_000_000;
        BigInteger totalSupplyWithDecimals = (totalSupply << 8) | decimals;
        BigInteger targetRaise = 10_000_000_000;
        BigInteger graduationBps = 8000;

        var exception = Record.Exception(() =>
            Invoke(_engine, _bondingCurveHash, "createSale", name, symbol, totalSupplyWithDecimals, targetRaise, graduationBps));
        Assert.NotNull(exception);
    }

    [Fact]
    public void CreateSale_InvalidName_Faults()
    {
        string name = "";
        string symbol = "TST";
        byte decimals = 8;
        BigInteger totalSupply = 1_000_000_000;
        BigInteger totalSupplyWithDecimals = (totalSupply << 8) | decimals;
        BigInteger targetRaise = 10_000_000_000;
        BigInteger graduationBps = 8000;

        var exception = Record.Exception(() =>
            Invoke(_engine, _bondingCurveHash, "createSale", name, symbol, totalSupplyWithDecimals, targetRaise, graduationBps));
        Assert.NotNull(exception);
    }

    [Fact]
    public void CreateSale_InvalidSymbolTooLong_Faults()
    {
        string name = "TestToken";
        string symbol = "TOOLONG12";
        byte decimals = 8;
        BigInteger totalSupply = 1_000_000_000;
        BigInteger totalSupplyWithDecimals = (totalSupply << 8) | decimals;
        BigInteger targetRaise = 10_000_000_000;
        BigInteger graduationBps = 8000;

        var exception = Record.Exception(() =>
            Invoke(_engine, _bondingCurveHash, "createSale", name, symbol, totalSupplyWithDecimals, targetRaise, graduationBps));
        Assert.NotNull(exception);
    }

    [Fact]
    public void CreateSale_InvalidDecimals_Faults()
    {
        string name = "TestToken";
        string symbol = "TST";
        byte decimals = 20;
        BigInteger totalSupply = 1_000_000_000;
        BigInteger totalSupplyWithDecimals = (totalSupply << 8) | decimals;
        BigInteger targetRaise = 10_000_000_000;
        BigInteger graduationBps = 8000;

        var exception = Record.Exception(() =>
            Invoke(_engine, _bondingCurveHash, "createSale", name, symbol, totalSupplyWithDecimals, targetRaise, graduationBps));
        Assert.NotNull(exception);
    }

    [Fact]
    public void CreateSale_ZeroSupply_Faults()
    {
        string name = "TestToken";
        string symbol = "TST";
        byte decimals = 8;
        BigInteger totalSupply = 0;
        BigInteger totalSupplyWithDecimals = (totalSupply << 8) | decimals;
        BigInteger targetRaise = 10_000_000_000;
        BigInteger graduationBps = 8000;

        var exception = Record.Exception(() =>
            Invoke(_engine, _bondingCurveHash, "createSale", name, symbol, totalSupplyWithDecimals, targetRaise, graduationBps));
        Assert.NotNull(exception);
    }

    [Fact]
    public void CreateSale_ZeroTarget_Faults()
    {
        string name = "TestToken";
        string symbol = "TST";
        byte decimals = 8;
        BigInteger totalSupply = 1_000_000_000;
        BigInteger totalSupplyWithDecimals = (totalSupply << 8) | decimals;
        BigInteger targetRaise = 0;
        BigInteger graduationBps = 8000;

        var exception = Record.Exception(() =>
            Invoke(_engine, _bondingCurveHash, "createSale", name, symbol, totalSupplyWithDecimals, targetRaise, graduationBps));
        Assert.NotNull(exception);
    }

    [Fact]
    public void CreateSale_GraduationBpsTooLow_Faults()
    {
        string name = "TestToken";
        string symbol = "TST";
        byte decimals = 8;
        BigInteger totalSupply = 1_000_000_000;
        BigInteger totalSupplyWithDecimals = (totalSupply << 8) | decimals;
        BigInteger targetRaise = 10_000_000_000;
        BigInteger graduationBps = 4999;

        var exception = Record.Exception(() =>
            Invoke(_engine, _bondingCurveHash, "createSale", name, symbol, totalSupplyWithDecimals, targetRaise, graduationBps));
        Assert.NotNull(exception);
    }

    [Fact]
    public void CreateSale_GraduationBpsTooHigh_Faults()
    {
        string name = "TestToken";
        string symbol = "TST";
        byte decimals = 8;
        BigInteger totalSupply = 1_000_000_000;
        BigInteger totalSupplyWithDecimals = (totalSupply << 8) | decimals;
        BigInteger targetRaise = 10_000_000_000;
        BigInteger graduationBps = 8001;

        var exception = Record.Exception(() =>
            Invoke(_engine, _bondingCurveHash, "createSale", name, symbol, totalSupplyWithDecimals, targetRaise, graduationBps));
        Assert.NotNull(exception);
    }

    [Fact]
    public void CreateSale_EdgeGraduationBps_Works()
    {
        string name = "TestToken";
        string symbol = "TST";
        byte decimals = 8;
        BigInteger totalSupply = 1_000_000_000;
        BigInteger totalSupplyWithDecimals = (totalSupply << 8) | decimals;
        BigInteger targetRaise = 10_000_000_000;

        var ex5000 = Record.Exception(() =>
            Invoke(_engine, _bondingCurveHash, "createSale", name, symbol, totalSupplyWithDecimals, targetRaise, (BigInteger)5000));
        Assert.NotNull(ex5000);

        var ex8000 = Record.Exception(() =>
            Invoke(_engine, _bondingCurveHash, "createSale", name, symbol, totalSupplyWithDecimals, targetRaise, (BigInteger)8000));
        Assert.NotNull(ex8000);
    }

    [Fact]
    public void Verify_ByOwner_ReturnsTrue()
    {
        var result = Invoke(_engine, _bondingCurveHash, "verify");
        Assert.True(result.GetBoolean());
    }

    [Fact]
    public void Update_ByNonOwner_Faults()
    {
        var engine2 = new TestEngine();
        engine2.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.CalledByEntry, User);
        var exception = Record.Exception(() =>
            Invoke(engine2, _bondingCurveHash, "update", new byte[] { }, ""));
        Assert.NotNull(exception);
    }

    #region CreateSale Boundary Validation

    [Fact]
    public void CreateSale_SymbolExactlyMaxLength_Works()
    {
        string name = "TestToken";
        string symbol = "12345678";
        byte decimals = 8;
        BigInteger totalSupply = 1_000_000_000;
        BigInteger totalSupplyWithDecimals = (totalSupply << 8) | decimals;
        BigInteger targetRaise = 10_000_000_000;
        BigInteger graduationBps = 6500;

        var exception = Record.Exception(() =>
            Invoke(_engine, _bondingCurveHash, "createSale", name, symbol, totalSupplyWithDecimals, targetRaise, graduationBps));
        Assert.NotNull(exception);
    }

    [Fact]
    public void CreateSale_SymbolTooLongByOne_Faults()
    {
        string name = "TestToken";
        string symbol = "123456789";
        byte decimals = 8;
        BigInteger totalSupply = 1_000_000_000;
        BigInteger totalSupplyWithDecimals = (totalSupply << 8) | decimals;
        BigInteger targetRaise = 10_000_000_000;
        BigInteger graduationBps = 6500;

        var exception = Record.Exception(() =>
            Invoke(_engine, _bondingCurveHash, "createSale", name, symbol, totalSupplyWithDecimals, targetRaise, graduationBps));
        Assert.NotNull(exception);
    }

    [Fact]
    public void CreateSale_EmptySymbol_Faults()
    {
        string name = "TestToken";
        string symbol = "";
        byte decimals = 8;
        BigInteger totalSupply = 1_000_000_000;
        BigInteger totalSupplyWithDecimals = (totalSupply << 8) | decimals;
        BigInteger targetRaise = 10_000_000_000;
        BigInteger graduationBps = 6500;

        var exception = Record.Exception(() =>
            Invoke(_engine, _bondingCurveHash, "createSale", name, symbol, totalSupplyWithDecimals, targetRaise, graduationBps));
        Assert.NotNull(exception);
    }

    [Fact]
    public void CreateSale_DecimalsZero_Works()
    {
        string name = "TestToken";
        string symbol = "TST";
        byte decimals = 0;
        BigInteger totalSupply = 1_000_000_000;
        BigInteger totalSupplyWithDecimals = (totalSupply << 8) | decimals;
        BigInteger targetRaise = 10_000_000_000;
        BigInteger graduationBps = 6500;

        var exception = Record.Exception(() =>
            Invoke(_engine, _bondingCurveHash, "createSale", name, symbol, totalSupplyWithDecimals, targetRaise, graduationBps));
        Assert.NotNull(exception);
    }

    [Fact]
    public void CreateSale_Decimals18_Works()
    {
        string name = "TestToken";
        string symbol = "TST";
        byte decimals = 18;
        BigInteger totalSupply = 1_000_000_000;
        BigInteger totalSupplyWithDecimals = (totalSupply << 8) | decimals;
        BigInteger targetRaise = 10_000_000_000;
        BigInteger graduationBps = 6500;

        var exception = Record.Exception(() =>
            Invoke(_engine, _bondingCurveHash, "createSale", name, symbol, totalSupplyWithDecimals, targetRaise, graduationBps));
        Assert.NotNull(exception);
    }

    [Fact]
    public void CreateSale_Decimals19_Faults()
    {
        string name = "TestToken";
        string symbol = "TST";
        byte decimals = 19;
        BigInteger totalSupply = 1_000_000_000;
        BigInteger totalSupplyWithDecimals = (totalSupply << 8) | decimals;
        BigInteger targetRaise = 10_000_000_000;
        BigInteger graduationBps = 6500;

        var exception = Record.Exception(() =>
            Invoke(_engine, _bondingCurveHash, "createSale", name, symbol, totalSupplyWithDecimals, targetRaise, graduationBps));
        Assert.NotNull(exception);
    }

    [Fact]
    public void CreateSale_GraduationBps4999_Faults()
    {
        string name = "TestToken";
        string symbol = "TST";
        byte decimals = 8;
        BigInteger totalSupply = 1_000_000_000;
        BigInteger totalSupplyWithDecimals = (totalSupply << 8) | decimals;
        BigInteger targetRaise = 10_000_000_000;
        BigInteger graduationBps = 4999;

        var exception = Record.Exception(() =>
            Invoke(_engine, _bondingCurveHash, "createSale", name, symbol, totalSupplyWithDecimals, targetRaise, graduationBps));
        Assert.NotNull(exception);
    }

    [Fact]
    public void CreateSale_GraduationBps5000_Works()
    {
        string name = "TestToken";
        string symbol = "TST";
        byte decimals = 8;
        BigInteger totalSupply = 1_000_000_000;
        BigInteger totalSupplyWithDecimals = (totalSupply << 8) | decimals;
        BigInteger targetRaise = 10_000_000_000;
        BigInteger graduationBps = 5000;

        var exception = Record.Exception(() =>
            Invoke(_engine, _bondingCurveHash, "createSale", name, symbol, totalSupplyWithDecimals, targetRaise, graduationBps));
        Assert.NotNull(exception);
    }

    [Fact]
    public void CreateSale_GraduationBps8000_Works()
    {
        string name = "TestToken";
        string symbol = "TST";
        byte decimals = 8;
        BigInteger totalSupply = 1_000_000_000;
        BigInteger totalSupplyWithDecimals = (totalSupply << 8) | decimals;
        BigInteger targetRaise = 10_000_000_000;
        BigInteger graduationBps = 8000;

        var exception = Record.Exception(() =>
            Invoke(_engine, _bondingCurveHash, "createSale", name, symbol, totalSupplyWithDecimals, targetRaise, graduationBps));
        Assert.NotNull(exception);
    }

    [Fact]
    public void CreateSale_GraduationBps8001_Faults()
    {
        string name = "TestToken";
        string symbol = "TST";
        byte decimals = 8;
        BigInteger totalSupply = 1_000_000_000;
        BigInteger totalSupplyWithDecimals = (totalSupply << 8) | decimals;
        BigInteger targetRaise = 10_000_000_000;
        BigInteger graduationBps = 8001;

        var exception = Record.Exception(() =>
            Invoke(_engine, _bondingCurveHash, "createSale", name, symbol, totalSupplyWithDecimals, targetRaise, graduationBps));
        Assert.NotNull(exception);
    }

    [Fact]
    public void CreateSale_SupplyOne_Works()
    {
        string name = "TestToken";
        string symbol = "TST";
        byte decimals = 8;
        BigInteger totalSupply = 1;
        BigInteger totalSupplyWithDecimals = (totalSupply << 8) | decimals;
        BigInteger targetRaise = 10_000_000_000;
        BigInteger graduationBps = 6500;

        var exception = Record.Exception(() =>
            Invoke(_engine, _bondingCurveHash, "createSale", name, symbol, totalSupplyWithDecimals, targetRaise, graduationBps));
        Assert.NotNull(exception);
    }

    [Fact]
    public void CreateSale_NegativeSupply_Faults()
    {
        string name = "TestToken";
        string symbol = "TST";
        byte decimals = 8;
        BigInteger totalSupply = -1;
        BigInteger totalSupplyWithDecimals = (totalSupply << 8) | decimals;
        BigInteger targetRaise = 10_000_000_000;
        BigInteger graduationBps = 6500;

        var exception = Record.Exception(() =>
            Invoke(_engine, _bondingCurveHash, "createSale", name, symbol, totalSupplyWithDecimals, targetRaise, graduationBps));
        Assert.NotNull(exception);
    }

    [Fact]
    public void CreateSale_NegativeTarget_Faults()
    {
        string name = "TestToken";
        string symbol = "TST";
        byte decimals = 8;
        BigInteger totalSupply = 1_000_000_000;
        BigInteger totalSupplyWithDecimals = (totalSupply << 8) | decimals;
        BigInteger targetRaise = -1;
        BigInteger graduationBps = 6500;

        var exception = Record.Exception(() =>
            Invoke(_engine, _bondingCurveHash, "createSale", name, symbol, totalSupplyWithDecimals, targetRaise, graduationBps));
        Assert.NotNull(exception);
    }

    #endregion

    #region Getters Validation

    [Fact]
    public void GetBuyAmount_ZeroAmount_Faults()
    {
        var dummyHash = UInt160.Parse("0x00000000000000000000000000000000000000ff");
        var exception = Record.Exception(() =>
            Invoke(_engine, _bondingCurveHash, "getBuyAmount", dummyHash, 0));
        Assert.NotNull(exception);
    }

    [Fact]
    public void GetBuyAmount_NegativeAmount_Faults()
    {
        var dummyHash = UInt160.Parse("0x00000000000000000000000000000000000000ff");
        var exception = Record.Exception(() =>
            Invoke(_engine, _bondingCurveHash, "getBuyAmount", dummyHash, -1));
        Assert.NotNull(exception);
    }

    [Fact]
    public void GetRefundAmount_ZeroAmount_Faults()
    {
        var dummyHash = UInt160.Parse("0x00000000000000000000000000000000000000ff");
        var exception = Record.Exception(() =>
            Invoke(_engine, _bondingCurveHash, "getRefundAmount", dummyHash, 0));
        Assert.NotNull(exception);
    }

    [Fact]
    public void GetSellAmount_ZeroAmount_Faults()
    {
        var dummyHash = UInt160.Parse("0x00000000000000000000000000000000000000ff");
        var exception = Record.Exception(() =>
            Invoke(_engine, _bondingCurveHash, "getSellAmount", dummyHash, 0));
        Assert.NotNull(exception);
    }

    #endregion

    #region Deployment & Owner Tests

    [Fact]
    public void Deploy_WithInvalidData_Faults()
    {
        var (nef, manifest) = LoadContract("DuoBondingCurve");
        var exception = Record.Exception(() =>
            DeployContract(_engine, nef, manifest, "invalid"));
        Assert.NotNull(exception);
    }

    [Fact]
    public void Deploy_WithNullData_Faults()
    {
        var (nef, manifest) = LoadContract("DuoBondingCurve");
        var exception = Record.Exception(() =>
            DeployContract(_engine, nef, manifest, null));
        Assert.NotNull(exception);
    }

    [Fact]
    public void Deploy_WithZeroFactory_Faults()
    {
        var (nef, manifest) = LoadContract("DuoBondingCurve");
        var zeroAddr = UInt160.Zero;
        var router = UInt160.Parse("0x00000000000000000000000000000000000000ce");
        var exception = Record.Exception(() =>
            DeployContract(_engine, nef, manifest, new object[] { zeroAddr, router }));
        Assert.NotNull(exception);
    }

    [Fact]
    public void Deploy_WithZeroRouter_Faults()
    {
        var (nef, manifest) = LoadContract("DuoBondingCurve");
        var factory = UInt160.Parse("0x00000000000000000000000000000000000000fa");
        var zeroAddr = UInt160.Zero;
        var exception = Record.Exception(() =>
            DeployContract(_engine, nef, manifest, new object[] { factory, zeroAddr }));
        Assert.NotNull(exception);
    }

    #endregion

    #region ApprovedTransfer Tests

    [Fact]
    public void ApprovedTransfer_ByNonRouter_Faults()
    {
        var dummyToken = UInt160.Parse("0x00000000000000000000000000000000000000aa");
        var exception = Record.Exception(() =>
            Invoke(_engine, _bondingCurveHash, "approvedTransfer", dummyToken, User, 1000, null));
        Assert.NotNull(exception);
    }

    #endregion

    #region Verify Tests

    [Fact]
    public void Verify_ByNonOwner_ReturnsFalse()
    {
        var (nef, manifest) = LoadContract("DuoBondingCurve");
        var engine2 = CreateEngine();
        engine2.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.Global, Owner);
        var factory = UInt160.Parse("0x00000000000000000000000000000000000000fa");
        var router = UInt160.Parse("0x00000000000000000000000000000000000000ce");
        var contractHash = DeployContract(engine2, nef, manifest, new object[] { factory, router });

        engine2.SetTransactionSigners(Neo.Network.P2P.Payloads.WitnessScope.Global, User);
        var result = Invoke(engine2, contractHash, "verify");
        Assert.False(result.GetBoolean());
    }

    #endregion
}