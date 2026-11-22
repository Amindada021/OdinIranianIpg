using System.Net.Http.Json;
using System.Text.Json;
using System.Globalization;
using OdinIranianIpg.Internal;

namespace OdinIranianIpg;

public sealed class ZarinpalOptions
{
    public required string MerchantId { get; init; }
    public bool Sandbox { get; init; }
}

public sealed class ZarinpalGateway : IInquirableGateway
{
    public const string GatewayName = "Zarinpal";
    public string Name => GatewayName;
    private readonly HttpClient _http;
    private readonly ZarinpalOptions _options;
    private string BaseUrl => _options.Sandbox ? "https://sandbox.zarinpal.com/" : "https://payment.zarinpal.com/";

    public ZarinpalGateway(HttpClient http, ZarinpalOptions options)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (!Guid.TryParseExact(options.MerchantId, "D", out var id) || id == Guid.Empty)
            throw new ArgumentException("Invalid Zarinpal merchant ID.", nameof(options));
    }

    public async Task<PaymentStartResult> RequestAsync(PaymentRequest request, CancellationToken cancellationToken = default)
    {
        if (!Validation.ValidRequest(request)) return new(PaymentOutcome.InvalidInput);
        var response = await PostAsync("request", new
        {
            merchant_id = _options.MerchantId, amount = request.AmountRial, currency = "IRR",
            callback_url = request.CallbackUrl.AbsoluteUri, description = request.Description,
            metadata = new Dictionary<string, string> { ["order_id"] = request.OrderId.ToString(CultureInfo.InvariantCulture),
                ["mobile"] = request.Mobile ?? "" }.Where(p => p.Value.Length > 0).ToDictionary()
        }, cancellationToken);
        if (response.Code != 100 || !response.HttpSuccess)
            return new(response.Code is < 0 && response.HttpSuccess ? PaymentOutcome.Rejected : PaymentOutcome.Unknown, response.Code, response.Message);
        var authority = ReadString(response.Data, "authority");
        if (!IsAuthority(authority)) return new(PaymentOutcome.Unknown, response.Code);
        return new(PaymentOutcome.Succeeded, response.Code, Attempt: new(Name, request.OrderId, request.AmountRial, authority!),
            RedirectUrl: new Uri(BaseUrl + "pg/StartPay/" + authority));
    }

    public PaymentCallback ParseCallback(IReadOnlyDictionary<string, string> fields)
    {
        var authority = Validation.Field(fields, "Authority");
        var status = Validation.Field(fields, "Status");
        var disposition = !IsAuthority(authority) ? CallbackDisposition.Invalid : status switch
        {
            "OK" => CallbackDisposition.Candidate, "NOK" => CallbackDisposition.Cancelled, _ => CallbackDisposition.Invalid
        };
        return new(Name, disposition, authority);
    }

    public async Task<PaymentResult> VerifyAsync(PaymentAttempt attempt, PaymentCallback callback, CancellationToken cancellationToken = default)
    {
        if (!Validation.Matches(attempt, callback, Name) || !IsAuthority(attempt.Token)) return Validation.Invalid();
        if (callback.Disposition == CallbackDisposition.Cancelled) return new(PaymentOutcome.Cancelled);
        if (callback.Disposition != CallbackDisposition.Candidate) return Validation.Invalid();
        var response = await PostAsync("verify", new { merchant_id = _options.MerchantId, amount = attempt.AmountRial,
            authority = attempt.Token }, cancellationToken);
        var reference = ReadString(response.Data, "ref_id");
        if (response.HttpSuccess && response.Code is 100 or 101 && Validation.Positive(reference) is not null)
            return new(response.Code == 100 ? PaymentOutcome.Succeeded : PaymentOutcome.AlreadyVerified,
                response.Code, Reference: reference, MaskedCard: SafeCard(ReadString(response.Data, "card_pan")));
        return Validation.Unknown(response.Code);
    }

    public async Task<PaymentInquiryResult> InquiryAsync(PaymentAttempt attempt, CancellationToken cancellationToken = default)
    {
        if (attempt.Gateway != Name || !IsAuthority(attempt.Token) || attempt.OrderId <= 0 || attempt.AmountRial <= 0)
            return new(PaymentOutcome.InvalidInput);
        var response = await PostAsync("inquiry", new { merchant_id = _options.MerchantId, authority = attempt.Token }, cancellationToken);
        var status = ReadString(response.Data, "status");
        return response.HttpSuccess && response.Code == 100 && status is "PAID" or "VERIFIED" or "IN_BANK" or "FAILED" or "REVERSED"
            ? new(PaymentOutcome.Succeeded, response.Code, status)
            : new(PaymentOutcome.Unknown, response.Code);
    }

    private bool IsAuthority(string? value) => value is { Length: 36 } && value[0] == (_options.Sandbox ? 'S' : 'A') && value.All(char.IsAsciiLetterOrDigit);
    internal static string? SafeCard(string? card) => card is { Length: <= 30 } && card.Contains('*') &&
        card.All(c => char.IsAsciiDigit(c) || c is '*' or '-' or ' ') ? card : null;

    private async Task<Response> PostAsync(string operation, object body, CancellationToken ct)
    {
        try
        {
            using var response = await _http.PostAsJsonAsync(BaseUrl + "pg/v4/payment/" + operation + ".json", body, ct);
            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = document.RootElement;
            var data = Property(root, "data");
            var error = Property(root, "errors");
            var code = Validation.Code(ReadString(data, "code")) ?? Validation.Code(ReadString(error, "code"));
            return new(response.IsSuccessStatusCode, code, data.ValueKind == JsonValueKind.Undefined ? default : data.Clone(),
                code is null ? "Unrecognized gateway response." : $"Zarinpal status: {code}");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException ||
            (ex is OperationCanceledException && !ct.IsCancellationRequested))
        { return new(false, null, default, "Gateway response unavailable."); }
    }

    private static JsonElement Property(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var p) ? p : default;
    private static string? ReadString(JsonElement value, string name)
    {
        var p = Property(value, name);
        return p.ValueKind switch { JsonValueKind.String => p.GetString(), JsonValueKind.Number => p.GetRawText(), _ => null };
    }
    private sealed record Response(bool HttpSuccess, int? Code, JsonElement Data, string? Message);
}
