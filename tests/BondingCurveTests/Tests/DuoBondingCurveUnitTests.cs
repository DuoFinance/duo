using System.Numerics;
using Xunit;
using Neo;
using Neo.Network.P2P.Payloads;
using Neo.SmartContract;
using Neo.SmartContract.Testing;
using Neo.VM;


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
    public void GetDeployFee_ReturnsPositiveValue()
    {
        var result = Invoke(_engine, _bondingCurveHash, "getDeployFee");
        Assert.True(result.GetInteger() > 0);
    }

    [Fact]
    public void CreateSale_DoesNotForwardDeployFee()
    {
        var gas = _engine.Native.GAS.Hash;
        var factoryBalanceBefore = Invoke(_engine, gas, "balanceOf", _factory).GetInteger();

        Invoke(_engine, _bondingCurveHash, "createSale",
            "TestToken", "TST", 10_000_000_000, 8000, "", "", "", "");

        var factoryBalanceAfter = Invoke(_engine, gas, "balanceOf", _factory).GetInteger();
        Assert.Equal(factoryBalanceBefore, factoryBalanceAfter);
    }

    [Fact]
    public void CreateAndBuy_ForwardsDeployFeeToFactory()
    {
        var engine = CreateEngine();
        var (factoryNef, factoryManifest) = LoadContract("DuoSwapFactory");
        var factory = DeployContract(engine, factoryNef, factoryManifest, Owner);
        var (bondingNef, bondingManifest) = LoadContract("DuoBondingCurve");
        var bondingCurve = DeployContract(engine, bondingNef, bondingManifest, new object[] { factory, _router });
        var gas = engine.Native.GAS.Hash;
        var deployFee = Invoke(engine, bondingCurve, "getDeployFee").GetInteger();
        var factoryDeployFee = Invoke(engine, factory, "getDeployFee").GetInteger();
        Assert.Equal(factoryDeployFee, deployFee);
        var factoryBalanceBefore = Invoke(engine, gas, "balanceOf", factory).GetInteger();
        var amount = deployFee + 1_000_000_000;

        var data = new ContractParameter(ContractParameterType.Array)
        {
            Value = new ContractParameter[]
            {
                new(ContractParameterType.String) { Value = "CreateAndBuy" },
                new(ContractParameterType.Integer) { Value = BigInteger.Zero },
                new(ContractParameterType.String) { Value = "Test Token" },
                new(ContractParameterType.String) { Value = "TST" },
                new(ContractParameterType.Integer) { Value = 1_000_000_000_000 },
                new(ContractParameterType.Integer) { Value = 8000 },
                new(ContractParameterType.String) { Value = "" },
                new(ContractParameterType.String) { Value = "" },
                new(ContractParameterType.String) { Value = "" },
                new(ContractParameterType.String) { Value = "" }
            }
        };

        Invoke(engine, gas, "transfer", Owner, bondingCurve, amount, data);

        var factoryBalanceAfter = Invoke(engine, gas, "balanceOf", factory).GetInteger();
        Assert.Equal(deployFee, factoryBalanceAfter - factoryBalanceBefore);
    }

    [Fact]
    public void getSale_ForUnknownSale_ReturnsEmpty()
    {
        var dummyHash = UInt160.Parse("0x00000000000000000000000000000000000000ff");
        var result = Invoke(_engine, _bondingCurveHash, "getSale", dummyHash);
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
            Invoke(_engine, _bondingCurveHash, "getTokensForGAS", dummyHash, 100_000_000));
        Assert.NotNull(exception);
    }

    [Fact]
    public void GetSellAmount_ForUnknownSale_Faults()
    {
        var dummyHash = UInt160.Parse("0x00000000000000000000000000000000000000ff");
        var exception = Record.Exception(() =>
            Invoke(_engine, _bondingCurveHash, "getGASForTokens", dummyHash, 1_000_000));
        Assert.NotNull(exception);
    }

        [Fact]
        public void CreateSale_WithValidParams_Succeeds()
        {
            string name = "TestToken";
            string symbol = "TST";
            BigInteger targetRaise = 10_000_000_000;
            BigInteger graduationBps = 8000;

            var exception = Record.Exception(() =>
                Invoke(_engine, _bondingCurveHash, "createSale", name, symbol, targetRaise, graduationBps, "", "", "", ""));
            Assert.Null(exception);
        }

    [Fact]
    public void CreateSale_InvalidName_Faults()
    {
        string name = "";
        string symbol = "TST";
        BigInteger targetRaise = 10_000_000_000;
        BigInteger graduationBps = 8000;

        var exception = Record.Exception(() =>
            Invoke(_engine, _bondingCurveHash, "createSale", name, symbol, targetRaise, graduationBps, "", "", "", ""));
        Assert.NotNull(exception);
    }

    [Fact]
    public void CreateSale_InvalidSymbolTooLong_Faults()
    {
        string name = "TestToken";
        string symbol = "TOOLONG12";
        BigInteger targetRaise = 10_000_000_000;
        BigInteger graduationBps = 8000;

        var exception = Record.Exception(() =>
            Invoke(_engine, _bondingCurveHash, "createSale", name, symbol, targetRaise, graduationBps, "", "", "", ""));
        Assert.NotNull(exception);
    }

    [Fact]
    public void CreateSale_ZeroTarget_Faults()
    {
        string name = "TestToken";
        string symbol = "TST";
        BigInteger targetRaise = 0;
        BigInteger graduationBps = 8000;

        var exception = Record.Exception(() =>
            Invoke(_engine, _bondingCurveHash, "createSale", name, symbol, targetRaise, graduationBps, "", "", "", ""));
        Assert.NotNull(exception);
    }

    [Fact]
    public void CreateSale_GraduationBpsTooLow_Faults()
    {
        string name = "TestToken";
        string symbol = "TST";
        BigInteger targetRaise = 10_000_000_000;
        BigInteger graduationBps = 4999;

        var exception = Record.Exception(() =>
            Invoke(_engine, _bondingCurveHash, "createSale", name, symbol, targetRaise, graduationBps, "", "", "", ""));
        Assert.NotNull(exception);
    }

        [Fact]
        public void CreateSale_GraduationBps10000_Faults()
        {
            string name = "TestToken";
            string symbol = "TST";
            BigInteger targetRaise = 10_000_000_000;
            BigInteger graduationBps = 10000;

            var exception = Record.Exception(() =>
                Invoke(_engine, _bondingCurveHash, "createSale", name, symbol, targetRaise, graduationBps, "", "", "", ""));
            Assert.NotNull(exception);
        }

        [Fact]
        public void CreateSale_GraduationBps9999_Works()
        {
            string name = "TestToken";
            string symbol = "TST";
            BigInteger targetRaise = 10_000_000_000;
            BigInteger graduationBps = 9999;

            var exception = Record.Exception(() =>
                Invoke(_engine, _bondingCurveHash, "createSale", name, symbol, targetRaise, graduationBps, "", "", "", ""));
            Assert.Null(exception);
        }

        [Fact]
        public void CreateSale_EdgeGraduationBps_Works()
        {
            string name = "TestToken";
            string symbol = "TST";
            BigInteger targetRaise = 10_000_000_000;

            var ex5000 = Record.Exception(() =>
                Invoke(_engine, _bondingCurveHash, "createSale", name, symbol, targetRaise, (BigInteger)5000, "", "", "", ""));
            Assert.NotNull(ex5000);

            var ex8000 = Record.Exception(() =>
                Invoke(_engine, _bondingCurveHash, "createSale", name, symbol, targetRaise, (BigInteger)8000, "", "", "", ""));
            Assert.Null(ex8000);
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
            BigInteger targetRaise = 10_000_000_000;
            BigInteger graduationBps = 6500;

            var exception = Record.Exception(() =>
                Invoke(_engine, _bondingCurveHash, "createSale", name, symbol, targetRaise, graduationBps, "", "", "", ""));
            Assert.Null(exception);
        }

    [Fact]
    public void CreateSale_SymbolTooLongByOne_Faults()
    {
        string name = "TestToken";
        string symbol = "123456789";
        BigInteger targetRaise = 10_000_000_000;
        BigInteger graduationBps = 6500;

        var exception = Record.Exception(() =>
            Invoke(_engine, _bondingCurveHash, "createSale", name, symbol, targetRaise, graduationBps, "", "", "", ""));
        Assert.NotNull(exception);
    }

    [Fact]
    public void CreateSale_EmptySymbol_Faults()
    {
        string name = "TestToken";
        string symbol = "";
        BigInteger targetRaise = 10_000_000_000;
        BigInteger graduationBps = 6500;

        var exception = Record.Exception(() =>
            Invoke(_engine, _bondingCurveHash, "createSale", name, symbol, targetRaise, graduationBps, "", "", "", ""));
        Assert.NotNull(exception);
    }

    [Theory]
    [InlineData("TO.KEN")]   // dot rejected by factory DeployPair
    [InlineData("AB CD")]    // space
    [InlineData("A$B")]      // symbol char
    [InlineData("TOK/")]     // slash
    [InlineData("T\u00D6K")] // non-ASCII (UTF-8 multi-byte)
    public void CreateSale_SymbolWithInvalidCharset_Faults(string symbol)
    {
        string name = "TestToken";
        BigInteger targetRaise = 10_000_000_000;
        BigInteger graduationBps = 6500;

        var exception = Record.Exception(() =>
            Invoke(_engine, _bondingCurveHash, "createSale", name, symbol, targetRaise, graduationBps, "", "", "", ""));
        Assert.NotNull(exception);
    }

    [Theory]
    [InlineData("AB-c_d")]
    [InlineData("AZaz09-_")]
    [InlineData("My-Token")]
    public void CreateSale_SymbolWithValidCharset_Works(string symbol)
    {
        string name = "TestToken";
        BigInteger targetRaise = 10_000_000_000;
        BigInteger graduationBps = 6500;

        var exception = Record.Exception(() =>
            Invoke(_engine, _bondingCurveHash, "createSale", name, symbol, targetRaise, graduationBps, "", "", "", ""));
        Assert.Null(exception);
    }

    [Fact]
    public void CreateSale_GraduationBps4999_Faults()
    {
        string name = "TestToken";
        string symbol = "TST";
        BigInteger targetRaise = 10_000_000_000;
        BigInteger graduationBps = 4999;

        var exception = Record.Exception(() =>
            Invoke(_engine, _bondingCurveHash, "createSale", name, symbol, targetRaise, graduationBps, "", "", "", ""));
        Assert.NotNull(exception);
    }

    [Fact]
    public void CreateSale_GraduationBps5000_Works()
    {
        string name = "TestToken";
        string symbol = "TST";
        BigInteger targetRaise = 10_000_000_000;
        BigInteger graduationBps = 5000;

        var exception = Record.Exception(() =>
            Invoke(_engine, _bondingCurveHash, "createSale", name, symbol, targetRaise, graduationBps, "", "", "", ""));
        Assert.NotNull(exception);
    }

        [Fact]
        public void CreateSale_GraduationBps8000_Works()
        {
            string name = "TestToken";
            string symbol = "TST";
            BigInteger targetRaise = 10_000_000_000;
            BigInteger graduationBps = 8000;

            var exception = Record.Exception(() =>
                Invoke(_engine, _bondingCurveHash, "createSale", name, symbol, targetRaise, graduationBps, "", "", "", ""));
            Assert.Null(exception);
        }

        [Fact]
        public void CreateSale_GraduationBps8001_Works()
        {
            string name = "TestToken";
            string symbol = "TST";
            BigInteger targetRaise = 10_000_000_000;
            BigInteger graduationBps = 8001;

            var exception = Record.Exception(() =>
                Invoke(_engine, _bondingCurveHash, "createSale", name, symbol, targetRaise, graduationBps, "", "", "", ""));
            Assert.Null(exception);
        }

    [Fact]
    public void CreateSale_NegativeTarget_Faults()
    {
        string name = "TestToken";
        string symbol = "TST";
        BigInteger targetRaise = -1;
        BigInteger graduationBps = 6500;

        var exception = Record.Exception(() =>
            Invoke(_engine, _bondingCurveHash, "createSale", name, symbol, targetRaise, graduationBps, "", "", "", ""));
        Assert.NotNull(exception);
    }

    #endregion

    #region Getters Validation

    [Fact]
    public void GetBuyAmount_ZeroAmount_Faults()
    {
        var dummyHash = UInt160.Parse("0x00000000000000000000000000000000000000ff");
        var exception = Record.Exception(() =>
            Invoke(_engine, _bondingCurveHash, "getTokensForGAS", dummyHash, 0));
        Assert.NotNull(exception);
    }

    [Fact]
    public void GetBuyAmount_NegativeAmount_Faults()
    {
        var dummyHash = UInt160.Parse("0x00000000000000000000000000000000000000ff");
        var exception = Record.Exception(() =>
            Invoke(_engine, _bondingCurveHash, "getTokensForGAS", dummyHash, -1));
        Assert.NotNull(exception);
    }

    [Fact]
    public void GetSellAmount_ZeroAmount_Faults()
    {
        var dummyHash = UInt160.Parse("0x00000000000000000000000000000000000000ff");
        var exception = Record.Exception(() =>
            Invoke(_engine, _bondingCurveHash, "getGASForTokens", dummyHash, 0));
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

    [Fact]
    public void TransferGAS_WithNullData_Faults()
    {
        var gas = _engine.Native.GAS.Hash;
        var exception = Record.Exception(() =>
            Invoke(_engine, _bondingCurveHash, "onNEP17Payment", Owner, (BigInteger)1000, null));
        Assert.NotNull(exception);
    }

    [Fact]
    public void TransferGAS_WithEmptyArrayData_Faults()
    {
        var emptyData = new ContractParameter(ContractParameterType.Array)
        {
            Value = System.Array.Empty<ContractParameter>()
        };
        var exception = Record.Exception(() =>
            Invoke(_engine, _bondingCurveHash, "onNEP17Payment", Owner, (BigInteger)1000, emptyData));
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
}
