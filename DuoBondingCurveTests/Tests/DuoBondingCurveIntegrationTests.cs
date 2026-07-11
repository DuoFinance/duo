#pragma warning disable CS8632

using System.Numerics;
using System.Text;
using Neo;
using Neo.Cryptography.ECC;
using Neo.Extensions;
using Neo.IO;
using Neo.Json;
using Neo.Network.P2P.Payloads;
using Neo.SmartContract;
using Neo.SmartContract.Manifest;
using Neo.SmartContract.Native;
using Neo.VM;
using Neo.Wallets;

using Xunit;

namespace DuoBondingCurveTests;

public class DuoBondingCurveIntegrationTests : IClassFixture<DuoBondingCurveIntegrationTests.BondingCurveFixture>
{
    private readonly BondingCurveFixture _fixture;

    public DuoBondingCurveIntegrationTests(BondingCurveFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact(Skip = "Will be replaced by TS integration tests")]
    public async Task Integration_DeployAllContracts_Success()
    {
        Assert.True(_fixture.IsInitialized, $"Fixture 未初始化: {_fixture.InitError?.Message ?? "(unknown)"}");

        var ownerHex = await _fixture.TestInvokeAsync(_fixture.BondingCurveHash, "getOwner");
        Assert.NotNull(ownerHex);
        Assert.Equal(40, ownerHex.Length);

        var factoryHex = await _fixture.TestInvokeAsync(_fixture.BondingCurveHash, "getFactory");
        Assert.Equal(_fixture.FactoryHash, new UInt160(Convert.FromHexString(factoryHex)));

        var routerHex = await _fixture.TestInvokeAsync(_fixture.BondingCurveHash, "getRouter");
        Assert.Equal(_fixture.RouterHash, new UInt160(Convert.FromHexString(routerHex)));
    }

    [Fact(Skip = "Will be replaced by TS integration tests")]
    public async Task Integration_DebugCreateSale()
    {
        Assert.True(_fixture.IsInitialized, $"Fixture 未初始化: {_fixture.InitError?.Message ?? "(unknown)"}");

        // Step 1: Use already-deployed modified BondingCurve
        var bcHash = UInt160.Parse("0xf0f03d0dc97dc6110501e6fd0aa1c3301da66c79");
        Console.Error.WriteLine($"[DebugCreateSale] Using modified BondingCurve at: {bcHash}");

        // Step 2: Verify factory and router
        var factory = await _fixture.TestInvokeAsync(bcHash, "getFactory");
        Console.Error.WriteLine($"[DebugCreateSale] BC factory: {factory}, expected: {_fixture.FactoryHash}");

        var router = await _fixture.TestInvokeAsync(bcHash, "getRouter");
        Console.Error.WriteLine($"[DebugCreateSale] BC router: {router}, expected: {_fixture.RouterHash}");

        // Check factory's getDeployFee
        {
            var factoryScript = _fixture.FactoryHash.MakeScript("getDeployFee");
            Console.Error.WriteLine($"[DebugCreateSale] Factory addr: {_fixture.FactoryHash}");
            var feeResult = (JObject)await _fixture.RpcSendAsync("invokescript", Convert.ToBase64String(factoryScript));
            Console.Error.WriteLine($"[DebugCreateSale] Factory getDeployFee invokescript: {feeResult}");
        }

        // Step 3: Call CreateSale through the modified contract
        string name = "TestSaleToken";
        string symbol = "TST1";
        BigInteger totalSupplyWithDecimals = (1_000_000_000L << 8) | 8;
        BigInteger targetRaise = 10_000_000_000;
        BigInteger graduationBps = 6500;

        var script = bcHash.MakeScript("createSale", name, symbol, totalSupplyWithDecimals, targetRaise, graduationBps);
        var scriptBase64 = Convert.ToBase64String(script);

        Console.Error.WriteLine($"[DebugCreateSale] Invokescript (should FAIL, see runtime logs)...");
        var scriptHex = Convert.ToHexString(script);
        var invokeResult = (JObject)await _fixture.RpcSendAsync("invokescript", scriptBase64);
        Console.Error.WriteLine($"[DebugCreateSale] Invokescript: {invokeResult}");

        Console.Error.WriteLine($"[DebugCreateSale] Sending real TX...");
        var txHash = await _fixture.SendTransactionAsync(script);
        Console.Error.WriteLine($"[DebugCreateSale] TX hash: {txHash}");

        var log = await _fixture.GetApplicationLogAsync(txHash);
        if (log != null)
        {
            Console.Error.WriteLine($"[DebugCreateSale] App log: {log}");
            var execs = (JArray?)log["executions"];
            if (execs != null)
            {
                foreach (var exec in execs)
                {
                    var vmstate = ((JObject)exec)["vmstate"]?.AsString();
                    var exception = ((JObject)exec)["exception"]?.AsString();
                    var notifications = (JArray?)((JObject)exec)["notifications"];
                    var logs = (JArray?)((JObject)exec)["logs"];
                    Console.Error.WriteLine($"[DebugCreateSale] Execution: vmstate={vmstate}, exception={exception}");

                    if (notifications != null)
                    {
                        foreach (JObject n in notifications)
                        {
                            Console.Error.WriteLine($"[DebugCreateSale] Event: {n["eventname"]?.AsString()}, state: {n["state"]}");
                        }
                    }

                    if (logs != null)
                    {
                        foreach (JObject l in logs)
                        {
                            Console.Error.WriteLine($"[DebugCreateSale] Log: {l}");
                        }
                    }
                    else
                    {
                        Console.Error.WriteLine("[DebugCreateSale] No logs array in execution");
                    }
                }
            }
        }

        Console.Error.WriteLine($"[DebugCreateSale] Done. Check the app log for Runtime.Log messages.");
    }

    [Fact(Skip = "Will be replaced by TS integration tests")]
    public async Task Integration_CreateSale_Success()
    {
        Assert.True(_fixture.IsInitialized, $"Fixture 未初始化: {_fixture.InitError?.Message ?? "(unknown)"}");

        string name = "TestSaleToken";
        string symbol = "TST1";
        byte decimals = 8;
        BigInteger totalSupply = 1_000_000_000;
        BigInteger totalSupplyWithDecimals = (totalSupply << 8) | decimals;
        BigInteger targetRaise = 10_000_000_000;
        BigInteger graduationBps = 6500;

        var tokenHash = await _fixture.CreateSaleAsync(name, symbol, totalSupplyWithDecimals, targetRaise, graduationBps);
        Assert.NotEqual(UInt160.Zero, tokenHash);

        var saleInfo = await _fixture.GetSaleInfoAsync(tokenHash);
        Assert.Equal(tokenHash, saleInfo.TokenHash);
        Assert.Equal(symbol, saleInfo.Symbol);
        Assert.Equal(totalSupply, saleInfo.TotalSupply);
        Assert.Equal(BigInteger.Zero, saleInfo.TokensSold);
        Assert.Equal(BigInteger.Zero, saleInfo.GASRaised);
        Assert.Equal(targetRaise, saleInfo.TargetRaise);
        Assert.Equal(graduationBps, saleInfo.GraduationBps);

        BigInteger twoPminusOne = 2 * graduationBps - 10000;
        BigInteger pSquared = graduationBps * graduationBps;
        BigInteger expectedVToken = totalSupply * pSquared / (10000 * twoPminusOne);
        BigInteger expectedVGas = targetRaise * (10000 - graduationBps) / twoPminusOne;
        Assert.Equal(expectedVToken, saleInfo.VirtualToken);
        Assert.Equal(expectedVGas, saleInfo.VirtualGas);
    }

    [Fact(Skip = "Requires redeployed contracts on Neo-Express")]
    public async Task Integration_Buy_Tokens_Through_GAS_Transfer()
    {
        Assert.True(_fixture.IsInitialized, $"Fixture 未初始化: {_fixture.InitError?.Message ?? "(unknown)"}");

        var tokenHash = await _fixture.CreateSaleAsync("BuyToken", "BUY1",
            (1_000_000_000L << 8) | 8, 10_000_000_000, 6500);

        var priceBefore = await _fixture.GetCurrentPriceAsync(tokenHash);
        BigInteger gasAmount = 100_000_000;
        var expectedTokens = await _fixture.GetBuyAmountAsync(tokenHash, gasAmount);

        var balanceBefore = await _fixture.GetTokenBalanceAsync(tokenHash, _fixture.GenesisAddress);
        await _fixture.BuyWithGasAsync(tokenHash, gasAmount);
        var balanceAfter = await _fixture.GetTokenBalanceAsync(tokenHash, _fixture.GenesisAddress);

        BigInteger actualReceived = balanceAfter - balanceBefore;
        Assert.Equal(expectedTokens, actualReceived);

        var saleAfter = await _fixture.GetSaleInfoAsync(tokenHash);
        Assert.Equal(expectedTokens, saleAfter.TokensSold);
        Assert.Equal(gasAmount, saleAfter.GASRaised);

        var priceAfter = await _fixture.GetCurrentPriceAsync(tokenHash);
        Assert.True(priceAfter > priceBefore, $"price should increase after buy. before={priceBefore}, after={priceAfter}");
    }

    [Fact(Skip = "Requires redeployed contracts on Neo-Express")]
    public async Task Integration_Sell_Tokens_Through_Token_Transfer()
    {
        Assert.True(_fixture.IsInitialized, $"Fixture 未初始化: {_fixture.InitError?.Message ?? "(unknown)"}");

        var tokenHash = await _fixture.CreateSaleAsync("SellToken", "SELL1",
            (1_000_000_000L << 8) | 8, 10_000_000_000, 6500);

        BigInteger buyGasAmount = 200_000_000;
        await _fixture.BuyWithGasAsync(tokenHash, buyGasAmount);

        var buyerTokenBalance = await _fixture.GetTokenBalanceAsync(tokenHash, _fixture.GenesisAddress);
        Assert.True(buyerTokenBalance > 0, "should have token balance after buy");

        BigInteger gasBalanceBefore = await _fixture.GetGasBalanceAsync(_fixture.GenesisAddress);
        var sellAmount = buyerTokenBalance / 2;
        var expectedGasOut = await _fixture.GetSellAmountAsync(tokenHash, sellAmount);

        await _fixture.SellTokensAsync(tokenHash, sellAmount);

        BigInteger gasBalanceAfter = await _fixture.GetGasBalanceAsync(_fixture.GenesisAddress);
        BigInteger gasReceived = gasBalanceAfter - gasBalanceBefore;
        Assert.Equal(expectedGasOut, gasReceived);

        var saleAfter = await _fixture.GetSaleInfoAsync(tokenHash);
        Assert.Equal(buyerTokenBalance - sellAmount, saleAfter.TokensSold);
    }

    [Fact(Skip = "Requires redeployed contracts on Neo-Express")]
    public async Task Integration_Price_Monotonicity()
    {
        Assert.True(_fixture.IsInitialized, $"Fixture 未初始化: {_fixture.InitError?.Message ?? "(unknown)"}");

        var tokenHash = await _fixture.CreateSaleAsync("MonoToken", "MONO1",
            (1_000_000_000L << 8) | 8, 10_000_000_000, 6500);

        BigInteger previousPrice = await _fixture.GetCurrentPriceAsync(tokenHash);
        BigInteger gasAmount = 50_000_000;

        for (int i = 0; i < 5; i++)
        {
            await _fixture.BuyWithGasAsync(tokenHash, gasAmount);
            var currentPrice = await _fixture.GetCurrentPriceAsync(tokenHash);
            Assert.True(currentPrice >= previousPrice,
                $"price should increase on buy {i + 1}. prev={previousPrice}, cur={currentPrice}");
            previousPrice = currentPrice;
        }
    }

    [Fact(Skip = "Requires redeployed contracts on Neo-Express")]
    public async Task Integration_BuySell_RoundTrip_KConstant()
    {
        Assert.True(_fixture.IsInitialized, $"Fixture 未初始化: {_fixture.InitError?.Message ?? "(unknown)"}");

        var tokenHash = await _fixture.CreateSaleAsync("KConstToken", "KCONST",
            (1_000_000_000L << 8) | 8, 10_000_000_000, 6500);

        var saleInitial = await _fixture.GetSaleInfoAsync(tokenHash);
        BigInteger idealK = saleInitial.VirtualGas * saleInitial.VirtualToken;

        await _fixture.BuyWithGasAsync(tokenHash, 100_000_000);
        var saleAfterBuy = await _fixture.GetSaleInfoAsync(tokenHash);
        BigInteger actualKAfterBuy = (saleAfterBuy.VirtualGas + saleAfterBuy.GASRaised) *
                                      (saleAfterBuy.VirtualToken - saleAfterBuy.TokensSold);

        BigInteger tolerance = idealK / 10000 + 1;
        Assert.True(BigInteger.Abs(actualKAfterBuy - idealK) <= tolerance,
            $"K should be approx constant after buy. ideal={idealK}, actual={actualKAfterBuy}");

        BigInteger sellAmount = saleAfterBuy.TokensSold / 2;
        if (sellAmount > 0)
        {
            await _fixture.SellTokensAsync(tokenHash, sellAmount);
            var saleAfterSell = await _fixture.GetSaleInfoAsync(tokenHash);
            BigInteger actualKAfterSell = (saleAfterSell.VirtualGas + saleAfterSell.GASRaised) *
                                            (saleAfterSell.VirtualToken - saleAfterSell.TokensSold);

            Assert.True(BigInteger.Abs(actualKAfterSell - idealK) <= tolerance,
                $"K should be approx constant after sell. ideal={idealK}, actual={actualKAfterSell}");
        }
    }

    [Fact(Skip = "Requires redeployed contracts on Neo-Express")]
    public async Task Integration_GetBuyAmount_GetSellAmount_Consistency()
    {
        Assert.True(_fixture.IsInitialized, $"Fixture 未初始化: {_fixture.InitError?.Message ?? "(unknown)"}");

        var tokenHash = await _fixture.CreateSaleAsync("ConsToken", "CONS1",
            (1_000_000_000L << 8) | 8, 10_000_000_000, 6500);

        await _fixture.BuyWithGasAsync(tokenHash, 200_000_000);

        BigInteger testGas = 50_000_000;
        BigInteger tokensOut = await _fixture.GetBuyAmountAsync(tokenHash, testGas);
        Assert.True(tokensOut > 0, "getBuyAmount should return positive");

        BigInteger gasBack = await _fixture.GetSellAmountAsync(tokenHash, tokensOut);

        BigInteger tol = testGas / 1000 + 10;
        Assert.True(BigInteger.Abs(gasBack - testGas) <= tol,
            $"Consistency failed. testGas={testGas}, gasBack={gasBack}");
        Assert.True(gasBack <= testGas + tol,
            $"Sell GAS should not exceed buy GAS. testGas={testGas}, gasBack={gasBack}");
    }

    public class BondingCurveFixture : IAsyncLifetime
    {
        private const string RpcUrl = "http://localhost:50012";
        private const uint Network = 611540645;
        private const string GenesisPrivKeyHex = "281d99b9733bd127e83deb937366645feb3939637de6c2754a3cdd15d55c0834";
        private const int TxTimeoutSeconds = 90;

        private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
        private int _nextId = 1;

        public KeyPair GenesisKey { get; private set; } = null!;
        public ECPoint GenesisPubKey { get; private set; } = null!;
        public UInt160 GenesisMultiSig { get; private set; }
        public UInt160 GenesisAddress { get; private set; }
        public ProtocolSettings ProtocolSettings { get; private set; } = null!;

        public UInt160 FactoryHash { get; set; }
        public UInt160 RouterHash { get; set; }
        public UInt160 BondingCurveHash { get; set; }
        public bool IsInitialized { get; private set; }

        public async Task InitializeAsync()
        {
            try
            {
                ProtocolSettings = ProtocolSettings.Default with { Network = Network };
                GenesisKey = new KeyPair(Convert.FromHexString(GenesisPrivKeyHex));
                GenesisPubKey = GenesisKey.PublicKey;
                GenesisMultiSig = Contract.CreateMultiSigContract(1, new[] { GenesisPubKey }).ScriptHash;
                var verificationScript = Contract.CreateSignatureContract(GenesisPubKey).Script;
                GenesisAddress = verificationScript.ToScriptHash();

                await DeployAllContractsAsync();
                IsInitialized = true;
            }
            catch (Exception ex)
            {
                InitError = ex;
                Console.Error.WriteLine($"[BondingCurveFixture] Init failed: {ex.Message}");
                IsInitialized = false;
            }
        }

        public Exception? InitError { get; private set; }

        public Task DisposeAsync()
        {
            _http.Dispose();
            return Task.CompletedTask;
        }

        #region RPC

        public async Task<JToken> RpcSendAsync(string method, params object[] paraArgs)
        {
            var request = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = _nextId++,
                ["method"] = method,
                ["params"] = new JArray(paraArgs.Select(ToJToken).ToArray())
            };

            var content = new StringContent(request.ToString(), Encoding.UTF8, "application/json");
            var response = await _http.PostAsync(RpcUrl, content);
            var responseText = await response.Content.ReadAsStringAsync();
            var result = JObject.Parse(responseText);

            if (result["error"] != null)
            {
                var errCode = result["error"]?["code"]?.ToString();
                var errMsg = result["error"]?["message"]?.ToString();
                throw new Exception($"RPC error [{method}]: code={errCode}, message={errMsg}");
            }

            return result["result"]!;
        }

        private static JToken ToJToken(object obj)
        {
            return obj switch
            {
                null => JToken.Null,
                string s => new JString(s),
                int i => new JNumber(i),
                long l => new JNumber(l),
                bool b => new JBoolean(b),
                UInt160 hash => new JString($"{hash}"),
                UInt256 hash => new JString($"{hash}"),
                byte[] bytes => new JString(Convert.ToHexString(bytes)),
                BigInteger n => new JString($"{n}"),
                JObject j => j,
                JToken j => j,
                _ => new JString(obj.ToString())
            };
        }

        public async Task<string> TestInvokeAsync(UInt160 contractHash, string method, params object[] args)
        {
            var script = contractHash.MakeScript(method, args);
            var scriptBase64 = Convert.ToBase64String(script);
            var result = (JObject)await RpcSendAsync("invokescript", scriptBase64);
            var stack = (JArray)result["stack"]!;
            var item = (JObject)stack[0];
            var type = item["type"]?.AsString();

            if (type == "Integer")
                return item["value"]?.AsString() ?? "0";

            if (type == "Buffer" || type == "ByteString")
            {
                var base64 = item["value"]?.AsString() ?? "";
                Console.Error.WriteLine($"[TestInvokeAsync] type={type}, raw base64='{base64}'");
                var bytes = Convert.FromBase64String(base64);
                return Convert.ToHexString(bytes);
            }

            var value = item["value"]?.ToString() ?? "";
            Console.Error.WriteLine($"[TestInvokeAsync] type={type}, value='{value}'");
            return value;
        }

        public async Task<JObject> TestInvokeRawAsync(UInt160 contractHash, string method, params object[] args)
        {
            var script = contractHash.MakeScript(method, args);
            var scriptBase64 = Convert.ToBase64String(script);
            var result = (JObject)await RpcSendAsync("invokescript", scriptBase64);
            var stack = (JArray)result["stack"]!;
            return (JObject)stack[0];
        }

        public async Task<UInt256> SendTransactionAsync(byte[] script)
        {
            var blockCount = int.Parse((await RpcSendAsync("getblockcount")).ToString()!);

            var verificationScript = Contract.CreateSignatureContract(GenesisPubKey).Script;

            var tx = new Transaction
            {
                Version = 0,
                Nonce = (uint)(new Random().Next(1, int.MaxValue)),
                SystemFee = 20_000_000_000,
                NetworkFee = 10_000_000,
                ValidUntilBlock = (uint)blockCount + 100,
                Signers = new[]
                {
                    new Signer
                    {
                        Account = GenesisAddress,
                        Scopes = WitnessScope.CalledByEntry
                    }
                },
                Attributes = Array.Empty<TransactionAttribute>(),
                Script = script,
                Witnesses = Array.Empty<Witness>()
            };

            var signature = ((IVerifiable)tx).Sign(GenesisKey, ProtocolSettings.Network);
            
            using (ScriptBuilder sb = new ScriptBuilder())
            {
                sb.EmitPush(signature);
                var invocationScript = sb.ToArray();
                tx.Witnesses = new[] { new Witness { InvocationScript = invocationScript, VerificationScript = verificationScript } };
            }

            byte[] txBytes;
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                ((ISerializable)tx).Serialize(writer);
                txBytes = ms.ToArray();
            }

            var txBase64 = Convert.ToBase64String(txBytes);
            JObject hashResult;
            try
            {
                hashResult = (JObject)await RpcSendAsync("sendrawtransaction", txBase64);
            }
            catch (Exception ex)
            {
                var hexPreview = Convert.ToHexString(txBytes).Length > 400 ? Convert.ToHexString(txBytes).Substring(0, 200) + "..." + Convert.ToHexString(txBytes).Substring(Convert.ToHexString(txBytes).Length - 200) : Convert.ToHexString(txBytes);
                throw new Exception($"sendrawtransaction failed: {ex.Message}\nTxHex preview: {hexPreview}");
            }
            var txHash = UInt256.Parse(hashResult["hash"]?.AsString()!);

            await WaitForTxAsync(txHash);
            return txHash;
        }

        public async Task<UInt256> SendTransactionAsync(byte[] script, long systemFee, long networkFee)
        {
            var blockCount = int.Parse((await RpcSendAsync("getblockcount")).ToString()!);

            var verificationScript = Contract.CreateSignatureContract(GenesisPubKey).Script;

            var tx = new Transaction
            {
                Version = 0,
                Nonce = (uint)(new Random().Next(1, int.MaxValue)),
                SystemFee = systemFee,
                NetworkFee = networkFee,
                ValidUntilBlock = (uint)blockCount + 100,
                Signers = new[]
                {
                    new Signer
                    {
                        Account = GenesisAddress,
                        Scopes = WitnessScope.CalledByEntry
                    }
                },
                Attributes = Array.Empty<TransactionAttribute>(),
                Script = script,
                Witnesses = Array.Empty<Witness>()
            };

            var signature = ((IVerifiable)tx).Sign(GenesisKey, ProtocolSettings.Network);
            using (ScriptBuilder sb = new ScriptBuilder())
            {
                sb.EmitPush(signature);
                tx.Witnesses = new[] { new Witness { InvocationScript = sb.ToArray(), VerificationScript = verificationScript } };
            }

            byte[] txBytes;
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                ((ISerializable)tx).Serialize(writer);
                txBytes = ms.ToArray();
            }

            var txBase64 = Convert.ToBase64String(txBytes);
            JObject hashResult;
            try
            {
                hashResult = (JObject)await RpcSendAsync("sendrawtransaction", txBase64);
            }
            catch (Exception ex)
            {
                var hexPreview = Convert.ToHexString(txBytes).Length > 400 ? Convert.ToHexString(txBytes).Substring(0, 200) + "..." + Convert.ToHexString(txBytes).Substring(Convert.ToHexString(txBytes).Length - 200) : Convert.ToHexString(txBytes);
                throw new Exception($"sendrawtransaction failed: {ex.Message}\nTxHex preview: {hexPreview}");
            }
            var txHash = UInt256.Parse(hashResult["hash"]?.AsString()!);

            await WaitForTxAsync(txHash);
            return txHash;
        }

        private byte[] Sign(Transaction tx)
        {
            return ((IVerifiable)tx).Sign(GenesisKey, Network);
        }

        private async Task WaitForTxAsync(UInt256 txHash)
        {
            var deadline = DateTime.UtcNow.AddSeconds(TxTimeoutSeconds);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    var height = int.Parse((await RpcSendAsync("gettransactionheight", $"{txHash}")).ToString()!);
                    if (height > 0)
                    {
                        var blockCount = int.Parse((await RpcSendAsync("getblockcount")).ToString()!);
                        var target = blockCount + 1;
                        while (DateTime.UtcNow < deadline &&
                               int.Parse((await RpcSendAsync("getblockcount")).ToString()!) < target)
                        {
                            await Task.Delay(500);
                        }
                        return;
                    }
                }
                catch { }
                await Task.Delay(1000);
            }
            throw new TimeoutException($"Tx {txHash} not confirmed within {TxTimeoutSeconds}s");
        }

        public async Task<JObject?> GetApplicationLogAsync(UInt256 txHash)
        {
            try
            {
                return (JObject)await RpcSendAsync("getapplicationlog", $"{txHash}");
            }
            catch { return null; }
        }

        #endregion

        #region Contract Deployment

        private async Task DeployAllContractsAsync()
        {
            FactoryHash = UInt160.Parse("0x01cfbf79de820b1d87f496062d7554104653c486");
            RouterHash = UInt160.Parse("0xe8977968d6b2f372fdecac226b18375fdbf3f9bc");
            BondingCurveHash = UInt160.Parse("0xf0f03d0dc97dc6110501e6fd0aa1c3301da66c79");
        }

        public async Task DeployContractAsync(NefFile nef, ContractManifest manifest, object? data)
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            nef.Serialize(writer);
            var nefBytes = ms.ToArray();
            var manifestJson = manifest.ToJson().ToString();

            byte[] script;
            using (ScriptBuilder sb = new ScriptBuilder())
            {
                if (data == null)
                    sb.EmitDynamicCall(NativeContract.ContractManagement.Hash, "deploy", nefBytes, manifestJson);
                else
                    sb.EmitDynamicCall(NativeContract.ContractManagement.Hash, "deploy", nefBytes, manifestJson, data);
                script = sb.ToArray();
            }
            await SendTransactionAsync(script);
        }

        public async Task<UInt256> InvokeContractAsync(UInt160 contractHash, string method, params object[] args)
        {
            var script = contractHash.MakeScript(method, args);
            return await SendTransactionAsync(script);
        }

        public async Task<UInt160> CreateSaleAsync(string name, string symbol, BigInteger totalSupplyWithDecimals, BigInteger targetRaise, BigInteger graduationBps)
        {
            var script = BondingCurveHash.MakeScript("createSale", name, symbol, totalSupplyWithDecimals, targetRaise, graduationBps);
            var scriptBase64 = Convert.ToBase64String(script);
            var scriptHex = Convert.ToHexString(script);

            Console.Error.WriteLine($"[CreateSaleAsync] Parameters: name={name}, symbol={symbol}, totalSupplyWithDecimals={totalSupplyWithDecimals}, targetRaise={targetRaise}, graduationBps={graduationBps}");
            Console.Error.WriteLine($"[CreateSaleAsync] Script length: {script.Length} bytes");

            var factory = await TestInvokeAsync(BondingCurveHash, "getFactory");
            var router = await TestInvokeAsync(BondingCurveHash, "getRouter");
            Console.Error.WriteLine($"[CreateSaleAsync] BondingCurve factory (hex): {factory}");
            Console.Error.WriteLine($"[CreateSaleAsync] BondingCurve router (hex): {router}");
            Console.Error.WriteLine($"[CreateSaleAsync] FactoryHash (configured): {FactoryHash}");
            Console.Error.WriteLine($"[CreateSaleAsync] RouterHash (configured): {RouterHash}");
            
            var factoryUInt = new UInt160(Convert.FromHexString(factory));
            var routerUInt = new UInt160(Convert.FromHexString(router));
            Console.Error.WriteLine($"[CreateSaleAsync] factory == FactoryHash: {factoryUInt == FactoryHash}");
            Console.Error.WriteLine($"[CreateSaleAsync] router == RouterHash: {routerUInt == RouterHash}");

            var deployFeeStr = await TestInvokeAsync(FactoryHash, "getDeployFee");
            BigInteger deployFee = BigInteger.Parse(deployFeeStr);
            Console.Error.WriteLine($"[CreateSaleAsync] Factory deployFee: {deployFee}");
            Console.Error.WriteLine($"[CreateSaleAsync] targetRaise > deployFee * 2: {targetRaise > deployFee * 2} (target={targetRaise}, deployFee*2={deployFee * 2})");

            var owner = await TestInvokeAsync(BondingCurveHash, "getOwner");
            Console.Error.WriteLine($"[CreateSaleAsync] BondingCurve owner: {owner}");
            Console.Error.WriteLine($"[CreateSaleAsync] GenesisAddress: {GenesisAddress}");

            var invokeResult = (JObject)await RpcSendAsync("invokescript", scriptBase64);
            Console.Error.WriteLine($"[CreateSaleAsync] Invokescript full result: {invokeResult}");
            var invokeState = invokeResult["state"]?.AsString();
            Console.Error.WriteLine($"[CreateSaleAsync] Invokescript state: {invokeState ?? "NULL"}");
            var invokeException = invokeResult["exception"]?.AsString();
            Console.Error.WriteLine($"[CreateSaleAsync] Invokescript exception: {invokeException ?? "NULL"}");
            if (invokeState == "FAULT")
            {
                Console.Error.WriteLine($"[CreateSaleAsync] Invokescript FAULT (expected if script uses ContractManagement.Deploy) -- proceeding with real TX anyway: {invokeException}");
            }

            Console.Error.WriteLine($"[CreateSaleAsync] Sending transaction...");
            var txHash = await SendTransactionAsync(script);
            Console.Error.WriteLine($"[CreateSaleAsync] Transaction hash: {txHash}");

            var log = await GetApplicationLogAsync(txHash);
            if (log == null) throw new InvalidOperationException("Cannot get application log");

            Console.Error.WriteLine($"[CreateSaleAsync] Application log: {log}");

            var executions = (JArray?)log["executions"];
            if (executions == null || executions.Count == 0)
                throw new InvalidOperationException("No executions in log");

            Console.Error.WriteLine($"[CreateSaleAsync] Executions count: {executions.Count}");
            
            foreach (var exec in executions)
            {
                var notifications = (JArray?)((JObject)exec)["notifications"];
                var exception = ((JObject)exec)["exception"]?.AsString();
                var trigger = ((JObject)exec)["trigger"]?.AsString();
                var vmstate = ((JObject)exec)["vmstate"]?.AsString();
                var gasconsumed = ((JObject)exec)["gasconsumed"]?.AsString();
                
                Console.Error.WriteLine($"[CreateSaleAsync] Execution: trigger={trigger}, vmstate={vmstate}, gasconsumed={gasconsumed}, exception={exception}");

                if (notifications == null) continue;

                string? faultMsg = null;
                foreach (JObject n in notifications)
                {
                    var eventName = n["eventname"]?.AsString();
                    Console.Error.WriteLine($"[CreateSaleAsync] Event: {eventName}, state: {n["state"]}");
                    if (eventName == "Fault")
                    {
                        var stateArr = (JArray?)n["state"];
                        if (stateArr != null && stateArr.Count > 0)
                            faultMsg = stateArr[0]["value"]?.AsString();
                    }
                    if (eventName == "SaleCreated")
                    {
                        var state = (JArray?)n["state"];
                        if (state != null)
                        {
                            Console.Error.WriteLine($"[CreateSaleAsync] SaleCreated state count: {state.Count}");
                            for (int i = 0; i < state.Count; i++)
                            {
                                var item = state[i];
                                Console.Error.WriteLine($"[CreateSaleAsync] state[{i}]: type={item["type"]?.AsString()}, value={item["value"]?.AsString()}");
                            }
                            if (state.Count >= 2)
                            {
                                var tokenHex = state[1]["value"]?.AsString();
                                if (tokenHex != null)
                                {
                                    if (tokenHex.StartsWith("0x")) tokenHex = tokenHex.Substring(2);
                                    return new UInt160(Convert.FromHexString(tokenHex));
                                }
                            }
                        }
                    }
                }

                if (exception != null || vmstate == "FAULT")
                {
                    var msg = faultMsg ?? exception ?? "unknown";
                    throw new Exception($"CreateSale transaction failed: {msg}");
                }
            }

            throw new InvalidOperationException("Cannot extract tokenHash from SaleCreated event");
        }

        #endregion

        #region Trading

        public async Task BuyWithGasAsync(UInt160 tokenHash, BigInteger gasAmount)
        {
            byte[] script;
            using (ScriptBuilder sb = new ScriptBuilder())
            {
                sb.EmitDynamicCall(NativeContract.GAS.Hash, "transfer",
                    GenesisAddress, BondingCurveHash, gasAmount,
                    new object[] { tokenHash });
                sb.Emit(OpCode.ASSERT);
                script = sb.ToArray();
            }
            await SendTransactionAsync(script);
        }

        public async Task SellTokensAsync(UInt160 tokenHash, BigInteger tokenAmount)
        {
            byte[] script;
            using (ScriptBuilder sb = new ScriptBuilder())
            {
                sb.EmitDynamicCall(tokenHash, "transfer",
                    GenesisAddress, BondingCurveHash, tokenAmount, null);
                sb.Emit(OpCode.ASSERT);
                script = sb.ToArray();
            }
            await SendTransactionAsync(script);
        }

        #endregion

        #region Queries

        public async Task<SaleInfo> GetSaleInfoAsync(UInt160 tokenHash)
        {
            var result = await TestInvokeRawAsync(BondingCurveHash, "getSaleInfo", tokenHash);
            return ParseSaleInfo(result);
        }

        private static SaleInfo ParseSaleInfo(JObject item)
        {
            var type = item["type"]?.AsString();
            if (type == "Struct" || type == "Array")
            {
                var arr = (JArray)item["value"]!;
                return new SaleInfo
                {
                    TokenHash = new UInt160(Convert.FromHexString(arr[0]["value"]?.AsString() ?? "")),
                    Creator = new UInt160(Convert.FromHexString(arr[1]["value"]?.AsString() ?? "")),
                    Symbol = arr[2]["value"]?.AsString() ?? "",
                    TotalSupply = BigInteger.Parse(arr[3]["value"]?.AsString() ?? "0"),
                    TokensSold = BigInteger.Parse(arr[4]["value"]?.AsString() ?? "0"),
                    GASRaised = BigInteger.Parse(arr[5]["value"]?.AsString() ?? "0"),
                    TargetRaise = BigInteger.Parse(arr[6]["value"]?.AsString() ?? "0"),
                    VirtualGas = BigInteger.Parse(arr[7]["value"]?.AsString() ?? "0"),
                    VirtualToken = BigInteger.Parse(arr[8]["value"]?.AsString() ?? "0"),
                    GraduationBps = BigInteger.Parse(arr[9]["value"]?.AsString() ?? "0"),
                    State = byte.Parse(arr[10]["value"]?.AsString() ?? "0")
                };
            }
            throw new InvalidOperationException($"Cannot parse SaleInfo: {item}");
        }

        public async Task<BigInteger> GetCurrentPriceAsync(UInt160 tokenHash)
        {
            var result = await TestInvokeAsync(BondingCurveHash, "getCurrentPrice", tokenHash);
            return BigInteger.Parse(result);
        }

        public async Task<BigInteger> GetBuyAmountAsync(UInt160 tokenHash, BigInteger gasAmount)
        {
            var result = await TestInvokeAsync(BondingCurveHash, "getBuyAmount", tokenHash, gasAmount);
            return BigInteger.Parse(result);
        }

        public async Task<BigInteger> GetSellAmountAsync(UInt160 tokenHash, BigInteger tokenAmount)
        {
            var result = await TestInvokeAsync(BondingCurveHash, "getSellAmount", tokenHash, tokenAmount);
            return BigInteger.Parse(result);
        }

        public async Task<BigInteger> GetTokenBalanceAsync(UInt160 tokenHash, UInt160 account)
        {
            var result = await TestInvokeAsync(tokenHash, "balanceOf", account);
            return BigInteger.Parse(result);
        }

        public async Task<BigInteger> GetGasBalanceAsync(UInt160 account)
        {
            var result = await TestInvokeAsync(NativeContract.GAS.Hash, "balanceOf", account);
            return BigInteger.Parse(result);
        }

        #endregion
    }

    public struct SaleInfo
    {
        public UInt160 TokenHash;
        public UInt160 Creator;
        public string Symbol;
        public BigInteger TotalSupply;
        public BigInteger TokensSold;
        public BigInteger GASRaised;
        public BigInteger TargetRaise;
        public BigInteger VirtualGas;
        public BigInteger VirtualToken;
        public BigInteger GraduationBps;
        public byte State;
    }
}
