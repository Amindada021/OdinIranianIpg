using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using OdinIranianIpg.Internal;

namespace OdinIranianIpg;

public sealed class ParsianOptions
{
    public required string LoginAccount { get; init; }
    public int? TerminalNo { get; init; }
}

public sealed class ParsianGateway : IReversibleGateway
{
    public const string GatewayName = "Parsian";
    public string Name => GatewayName;
    private readonly HttpClient _http;
    private readonly ParsianOptions _options;
    private static readonly XNamespace Soap = "http://schemas.xmlsoap.org/soap/envelope/";
    private const string Root = "https://pec.shaparak.ir/NewIPGServices/";
    private const string ContractRoot = "https://pec.Shaparak.ir/NewIPGServices/";

    public ParsianGateway(HttpClient http, ParsianOptions options)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (string.IsNullOrWhiteSpace(options.LoginAccount) || options.TerminalNo is <= 0)
            throw new ArgumentException("Invalid Parsian options.", nameof(options));
    }

    public async Task<PaymentStartResult> RequestAsync(PaymentRequest request, CancellationToken cancellationToken = default)
    {
        if (!Validation.ValidRequest(request) || request.AmountRial < 1000 || request.Description.Length > 500 || request.Mobile?.Length > 50)
            return new(PaymentOutcome.InvalidInput);
        var fields = new List<(string, object)> { ("LoginAccount", _options.LoginAccount), ("Amount", request.AmountRial),
            ("OrderId", request.OrderId), ("CallBackUrl", request.CallbackUrl.AbsoluteUri), ("AdditionalData", request.Description) };
        if (!string.IsNullOrWhiteSpace(request.Mobile)) fields.Add(("Originator", request.Mobile));
        var result = await CallAsync("Sale/SaleService", "SalePaymentRequest", fields, false, cancellationToken);
        var code = Validation.Code(result?.ElementValue("Status"));
        if (code != 0) return new(code is null ? PaymentOutcome.Unknown : PaymentOutcome.Rejected, code, Message(code));
        var token = Validation.Positive(result?.ElementValue("Token"));
        if (token is null) return new(PaymentOutcome.Unknown, code);
        var tokenText = token.Value.ToString(CultureInfo.InvariantCulture);
        return new(PaymentOutcome.Succeeded, code, Attempt: new(Name, request.OrderId, request.AmountRial, tokenText),
            RedirectUrl: new Uri("https://pec.shaparak.ir/NewIPG/?token=" + tokenText));
    }

    public PaymentCallback ParseCallback(IReadOnlyDictionary<string, string> fields)
    {
        string? Value(string key) => Validation.Field(fields, key);
        var token = Validation.Positive(Value("Token"));
        var order = Validation.Positive(Value("OrderId"));
        var code = Validation.Code(Value("status"));
        var rrn = Validation.Positive(Value("RRN"));
        var amount = Validation.Amount(Value("Amount"));
        var terminalNo = Validation.Code(Value("TerminalNo"));
        var disposition = token is null || order is null || code is null ? CallbackDisposition.Invalid : code switch
        {
            -138 => CallbackDisposition.Cancelled,
            0 when rrn > 0 && amount > 0 && terminalNo > 0 => CallbackDisposition.Candidate,
            0 => CallbackDisposition.Invalid,
            _ => CallbackDisposition.Rejected
        };
        return new(Name, disposition, token?.ToString(CultureInfo.InvariantCulture), order, amount, rrn, terminalNo, code);
    }

    public async Task<PaymentResult> VerifyAsync(PaymentAttempt attempt, PaymentCallback callback, CancellationToken cancellationToken = default)
    {
        if (!MatchOrder(attempt, callback)) return Validation.Invalid();
        if (callback.Disposition == CallbackDisposition.Cancelled) return new(PaymentOutcome.Cancelled, callback.ProviderCode);
        if (callback.Disposition != CallbackDisposition.Candidate || !MatchSuccessful(attempt, callback)) return Validation.Invalid();
        var response = await CallAsync("Confirm/ConfirmService", "ConfirmPayment", TokenFields(attempt), true, cancellationToken);
        var code = Validation.Code(response?.ElementValue("Status"));
        var token = Validation.Positive(response?.ElementValue("Token"));
        var rrn = Validation.Positive(response?.ElementValue("RRN"));
        if (code == 0 && token == Validation.Positive(attempt.Token) && rrn == callback.Rrn && rrn > 0)
            return new(PaymentOutcome.Succeeded, code, Reference: rrn.Value.ToString(CultureInfo.InvariantCulture),
                MaskedCard: ZarinpalGateway.SafeCard(response?.ElementValue("CardNumberMasked")));
        // The supplied bank documents lack the complete status-code list. Do not assume failure or automatically reverse.
        return Validation.Unknown(code);
    }

    public async Task<PaymentResult> ReverseAsync(PaymentAttempt attempt, PaymentCallback callback, CancellationToken cancellationToken = default)
    {
        if (!MatchOrder(attempt, callback) || callback.Disposition != CallbackDisposition.Candidate || !MatchSuccessful(attempt, callback))
            return Validation.Invalid();
        var response = await CallAsync("Reverse/ReversalService", "ReversalRequest", TokenFields(attempt), true, cancellationToken);
        var code = Validation.Code(response?.ElementValue("Status"));
        var token = Validation.Positive(response?.ElementValue("Token"));
        return code == 0 && token == Validation.Positive(attempt.Token)
            ? new(PaymentOutcome.Succeeded, code) : Validation.Unknown(code);
    }

    private bool MatchOrder(PaymentAttempt attempt, PaymentCallback callback) => Validation.Matches(attempt, callback, Name) &&
        Validation.Positive(attempt.Token) is not null && attempt.OrderId == callback.OrderId;
    private bool MatchSuccessful(PaymentAttempt attempt, PaymentCallback callback) => callback.ProviderCode == 0 &&
        callback.Rrn > 0 && callback.AmountRial == attempt.AmountRial && callback.TerminalNo > 0 &&
        (_options.TerminalNo is null || _options.TerminalNo == callback.TerminalNo);
    private (string, object)[] TokenFields(PaymentAttempt attempt) => [("LoginAccount", _options.LoginAccount), ("Token", long.Parse(attempt.Token, CultureInfo.InvariantCulture))];
    private static string? Message(int? code) => code is null ? "Gateway response unavailable." : $"Parsian status: {code}";

    private async Task<SoapResult?> CallAsync(string service, string operation, IEnumerable<(string Name, object Value)> fields,
        bool retryTransport, CancellationToken ct)
    {
        XNamespace ns = ContractRoot + service;
        var envelope = new XDocument(new XElement(Soap + "Envelope", new XAttribute(XNamespace.Xmlns + "soap", Soap),
            new XElement(Soap + "Body", new XElement(ns + operation,
                new XElement(ns + "requestData", fields.Select(f => new XElement(ns + f.Name, f.Value)))))));
        for (var i = 0; i < (retryTransport ? 2 : 1); i++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, Root + service + ".asmx");
                request.Headers.TryAddWithoutValidation("SOAPAction", "\"" + ns.NamespaceName + "/" + operation + "\"");
                request.Content = new StringContent(envelope.ToString(SaveOptions.DisableFormatting), Encoding.UTF8, "text/xml");
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
                if (!response.IsSuccessStatusCode)
                {
                    if (retryTransport && i == 0 && ((int)response.StatusCode >= 500 || (int)response.StatusCode == 408)) continue;
                    return null;
                }
                using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var reader = XmlReader.Create(stream, new XmlReaderSettings { Async = true, DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1_000_000 });
                var xml = await XDocument.LoadAsync(reader, LoadOptions.None, ct);
                var result = xml.Root?.Element(Soap + "Body")?.Element(ns + (operation + "Response"))?.Element(ns + (operation + "Result"));
                return result is null ? null : new(result, ns);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or XmlException ||
                (ex is OperationCanceledException && !ct.IsCancellationRequested)) { }
        }
        return null;
    }

    private sealed record SoapResult(XElement Element, XNamespace Namespace)
    {
        internal string? ElementValue(string name) => Element.Elements(Namespace + name).SingleOrDefaultSafe()?.Value;
    }
}

internal static class XmlExtensions
{
    internal static XElement? SingleOrDefaultSafe(this IEnumerable<XElement> values)
    {
        using var enumerator = values.GetEnumerator();
        if (!enumerator.MoveNext()) return null;
        var first = enumerator.Current;
        return enumerator.MoveNext() ? null : first;
    }
}
