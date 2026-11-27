using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using OdinIranianIpg;
using Xunit;

namespace OdinIranianIpg.Tests;

public class GatewayTests
{
    private const string Authority = "A00000000000000000000000000000000000";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly PaymentRequest Request = new(123, 12000, new Uri("https://example.com/callback"), "Test & order");
    private static readonly PaymentAttempt ParsianAttempt = new("Parsian", 123, 12000, "888");
    private static readonly PaymentAttempt ZarinAttempt = new("Zarinpal", 123, 12000, Authority);
    private static ZarinpalGateway Zarin(FakeHandler handler) => new(new HttpClient(handler), new() { MerchantId = "2ed23e0b-1111-4444-9999-aaaaaaaaaaaa" });
    private static ParsianGateway Parsian(FakeHandler handler) => new(new HttpClient(handler), new() { LoginAccount = "secret&pin", TerminalNo = 55 });
    private static Dictionary<string, string> Callback(string amount = "12,000", string order = "123", string rrn = "456", string terminal = "55", string status = "0") =>
        new() { ["Token"] = "888", ["OrderId"] = order, ["Amount"] = amount, ["RRN"] = rrn, ["TerminalNo"] = terminal, ["status"] = status };

    [Fact]
    public async Task ParsianRequestUsesCallerOrderAndExactSoapContract()
    {
        var handler = new FakeHandler((request, body) =>
        {
            Assert.Equal("https://pec.shaparak.ir/NewIPGServices/Sale/SaleService.asmx", request.RequestUri!.AbsoluteUri);
            XNamespace ns = "https://pec.Shaparak.ir/NewIPGServices/Sale/SaleService";
            Assert.Equal("\"" + ns + "/SalePaymentRequest\"", request.Headers.GetValues("SOAPAction").Single());
            var data = XDocument.Parse(body).Descendants(ns + "requestData").Single();
            Assert.Equal(new[] { "LoginAccount", "Amount", "OrderId", "CallBackUrl", "AdditionalData" }, data.Elements().Select(e => e.Name.LocalName));
            Assert.Equal("123", data.Element(ns + "OrderId")!.Value);
            Assert.Equal("12000", data.Element(ns + "Amount")!.Value);
            Assert.Equal("secret&pin", data.Element(ns + "LoginAccount")!.Value);
            return SoapResult("Sale/SaleService", "SalePaymentRequest", "<Status>0</Status><Token>888</Token>");
        });
        var result = await Parsian(handler).RequestAsync(Request, Ct);
        Assert.True(result.Success);
        Assert.Equal(ParsianAttempt, result.Attempt);
        Assert.Equal("https://pec.shaparak.ir/NewIPG/?token=888", result.RedirectUrl!.AbsoluteUri);
    }

    [Theory]
    [InlineData("12,001", "123", "456", "55")]
    [InlineData("1,2000", "123", "456", "55")]
    [InlineData("12,000", "124", "456", "55")]
    [InlineData("12,000", "123", "0", "55")]
    [InlineData("12,000", "123", "456", "56")]
    public async Task ParsianRejectsMismatchedCallbackBeforeConfirm(string amount, string order, string rrn, string terminal)
    {
        var handler = new FakeHandler((_, _) => throw new InvalidOperationException());
        var gateway = Parsian(handler);
        Assert.Equal(PaymentOutcome.InvalidInput, (await gateway.VerifyAsync(ParsianAttempt, gateway.ParseCallback(Callback(amount, order, rrn, terminal)), Ct)).Outcome);
        Assert.Equal(0, handler.Count);
    }

    [Theory]
    [InlineData("888", "456", true)]
    [InlineData("889", "456", false)]
    [InlineData("888", "457", false)]
    public async Task ParsianConfirmChecksResponseTokenAndRrn(string token, string rrn, bool success)
    {
        var handler = new FakeHandler((_, _) => SoapResult("Confirm/ConfirmService", "ConfirmPayment", $"<Status>0</Status><Token>{token}</Token><RRN>{rrn}</RRN><CardNumberMasked>123456******1234</CardNumberMasked>"));
        var gateway = Parsian(handler);
        Assert.Equal(success, (await gateway.VerifyAsync(ParsianAttempt, gateway.ParseCallback(Callback()), Ct)).Success);
    }

    [Fact]
    public async Task ParsianConfirmRetriesNetworkErrorOnce()
    {
        var count = 0;
        var handler = new FakeHandler((_, _) => ++count == 1 ? throw new HttpRequestException() : SoapResult("Confirm/ConfirmService", "ConfirmPayment", "<Status>0</Status><Token>888</Token><RRN>456</RRN>"));
        var gateway = Parsian(handler);
        Assert.True((await gateway.VerifyAsync(ParsianAttempt, gateway.ParseCallback(Callback()), Ct)).Success);
        Assert.Equal(2, handler.Count);
    }

    [Fact]
    public async Task ParsianReverseUsesReversalServiceAndNeverFabricatesSuccess()
    {
        var handler = new FakeHandler((request, body) =>
        {
            Assert.Equal("https://pec.shaparak.ir/NewIPGServices/Reverse/ReversalService.asmx", request.RequestUri!.AbsoluteUri);
            XNamespace ns = "https://pec.Shaparak.ir/NewIPGServices/Reversal/ReversalService";
            Assert.Equal("\"" + ns + "/ReversalRequest\"", request.Headers.GetValues("SOAPAction").Single());
            var data = XDocument.Parse(body).Descendants(ns + "ReversalRequest").Single().Element(ns + "requestData")!;
            Assert.Equal("secret&pin", data.Element(ns + "LoginAccount")!.Value);
            Assert.Equal("888", data.Element(ns + "Token")!.Value);
            return SoapResult("Reversal/ReversalService", "ReversalRequest", "<Status>0</Status><Token>888</Token>");
        });
        var gateway = Parsian(handler);
        Assert.True((await gateway.ReverseAsync(ParsianAttempt, gateway.ParseCallback(Callback()), Ct)).Success);
    }

    [Fact]
    public async Task ParsianCancellationDoesNotNeedOptionalCallbackFields()
    {
        var handler = new FakeHandler((_, _) => throw new InvalidOperationException());
        var gateway = Parsian(handler);
        var callback = gateway.ParseCallback(new Dictionary<string, string> { ["Token"] = "888", ["OrderId"] = "123", ["status"] = "-138" });
        Assert.Equal(PaymentOutcome.Cancelled, (await gateway.VerifyAsync(ParsianAttempt, callback, Ct)).Outcome);
        Assert.Equal(0, handler.Count);
    }

    [Fact]
    public async Task ZarinpalRequestUsesRialAndMerchantOrder()
    {
        var handler = new FakeHandler((request, body) =>
        {
            Assert.EndsWith("/pg/v4/payment/request.json", request.RequestUri!.AbsoluteUri);
            using var json = JsonDocument.Parse(body);
            Assert.Equal(12000, json.RootElement.GetProperty("amount").GetInt64());
            Assert.Equal("IRR", json.RootElement.GetProperty("currency").GetString());
            Assert.Equal("123", json.RootElement.GetProperty("metadata").GetProperty("order_id").GetString());
            return JsonResponse(JsonSerializer.Serialize(new { data = new { code = 100, authority = Authority } }));
        });
        var result = await Zarin(handler).RequestAsync(Request, Ct);
        Assert.True(result.Success);
        Assert.Equal(ZarinAttempt, result.Attempt);
    }

    [Theory]
    [InlineData(100, "42", PaymentOutcome.Succeeded)]
    [InlineData(101, "42", PaymentOutcome.AlreadyVerified)]
    [InlineData(101, "0", PaymentOutcome.Unknown)]
    [InlineData(-50, "42", PaymentOutcome.Unknown)]
    public async Task ZarinpalVerifyUsesStoredAmountAndRequiresReference(int code, string reference, PaymentOutcome expected)
    {
        var handler = new FakeHandler((_, body) =>
        {
            using var json = JsonDocument.Parse(body);
            Assert.Equal(12000, json.RootElement.GetProperty("amount").GetInt64());
            return JsonResponse(JsonSerializer.Serialize(new { data = new { code, ref_id = reference } }));
        });
        var gateway = Zarin(handler);
        var callback = gateway.ParseCallback(new Dictionary<string, string> { ["Authority"] = Authority, ["Status"] = "OK" });
        Assert.Equal(expected, (await gateway.VerifyAsync(ZarinAttempt, callback, Ct)).Outcome);
    }

    [Fact]
    public async Task ZarinpalTransportFailureDoesNotRetryRequest()
    {
        var handler = new FakeHandler((_, _) => throw new HttpRequestException());
        Assert.Equal(PaymentOutcome.Unknown, (await Zarin(handler).RequestAsync(Request, Ct)).Outcome);
        Assert.Equal(1, handler.Count);
    }

    [Fact]
    public async Task ZarinpalInquiryIsStatusOnly()
    {
        var gateway = Zarin(new FakeHandler((request, body) =>
        {
            Assert.EndsWith("/inquiry.json", request.RequestUri!.AbsoluteUri);
            using var json = JsonDocument.Parse(body);
            Assert.Equal(Authority, json.RootElement.GetProperty("authority").GetString());
            return JsonResponse("{\"data\":{\"code\":100,\"status\":\"VERIFIED\"}}");
        }));
        var result = await gateway.InquiryAsync(ZarinAttempt, Ct);
        Assert.Equal(PaymentOutcome.Succeeded, result.Outcome);
        Assert.Equal("VERIFIED", result.ProviderStatus);
    }

    [Fact]
    public async Task ParsianUnknownAfterTwoTransportFailures()
    {
        var handler = new FakeHandler((_, _) => throw new HttpRequestException());
        var gateway = Parsian(handler);
        Assert.Equal(PaymentOutcome.Unknown, (await gateway.VerifyAsync(ParsianAttempt, gateway.ParseCallback(Callback()), Ct)).Outcome);
        Assert.Equal(2, handler.Count);
    }

    [Fact]
    public async Task ParsianRejectsDuplicateCallbackKeys()
    {
        var handler = new FakeHandler((_, _) => throw new InvalidOperationException());
        var gateway = Parsian(handler);
        var fields = Callback();
        fields["TOKEN"] = "889";
        Assert.Equal(PaymentOutcome.InvalidInput, (await gateway.VerifyAsync(ParsianAttempt, gateway.ParseCallback(fields), Ct)).Outcome);
        Assert.Equal(0, handler.Count);
    }

    [Fact]
    public void ProvidersCanCoexistInDependencyInjection()
    {
        var services = new ServiceCollection();
        services.AddZarinpal(new() { MerchantId = "2ed23e0b-1111-4444-9999-aaaaaaaaaaaa" });
        services.AddParsian(new() { LoginAccount = "test" });
        using var provider = services.BuildServiceProvider();
        Assert.IsType<ZarinpalGateway>(provider.GetRequiredKeyedService<IPaymentGateway>("Zarinpal"));
        Assert.IsType<ParsianGateway>(provider.GetRequiredKeyedService<IPaymentGateway>("Parsian"));
    }

    private static HttpResponseMessage JsonResponse(string content) => new(HttpStatusCode.OK) { Content = new StringContent(content, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage SoapResult(string service, string operation, string contents) => new(HttpStatusCode.OK)
    {
        Content = new StringContent($"<soap:Envelope xmlns:soap='http://schemas.xmlsoap.org/soap/envelope/'><soap:Body><{operation}Response xmlns='https://pec.Shaparak.ir/NewIPGServices/{service}'><{operation}Result>{contents}</{operation}Result></{operation}Response></soap:Body></soap:Envelope>", Encoding.UTF8, "text/xml")
    };

    private sealed class FakeHandler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Count { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Count++;
            return respond(request, await request.Content!.ReadAsStringAsync(cancellationToken));
        }
    }
}
