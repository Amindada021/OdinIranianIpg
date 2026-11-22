using System.Globalization;

namespace OdinIranianIpg.Internal;

internal static class Validation
{
    internal static bool ValidRequest(PaymentRequest request) => request.OrderId > 0 && request.AmountRial > 0 &&
        request.CallbackUrl is { IsAbsoluteUri: true, Scheme: "https" } &&
        string.IsNullOrEmpty(request.CallbackUrl.UserInfo) && string.IsNullOrEmpty(request.CallbackUrl.Fragment) &&
        !string.IsNullOrWhiteSpace(request.Description);

    internal static string? Field(IReadOnlyDictionary<string, string> fields, string name)
    {
        // Reject duplicate keys (including casing variants) rather than selecting one.
        string? value = null;
        var found = false;
        foreach (var entry in fields)
        {
            if (!string.Equals(entry.Key, name, StringComparison.OrdinalIgnoreCase)) continue;
            if (found) return null;
            found = true;
            value = entry.Value;
        }
        return value;
    }

    internal static long? Positive(string? value) => long.TryParse(value, NumberStyles.None,
        CultureInfo.InvariantCulture, out var result) && result > 0 ? result : null;

    internal static int? Code(string? value) => int.TryParse(value, NumberStyles.AllowLeadingSign,
        CultureInfo.InvariantCulture, out var result) ? result : null;

    internal static long? Amount(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        if (!value.Contains(',')) return Positive(value);
        var parts = value.Split(',');
        if (parts[0].Length is < 1 or > 3 || parts.Skip(1).Any(p => p.Length != 3) ||
            parts.Any(p => !p.All(char.IsAsciiDigit))) return null;
        return Positive(string.Concat(parts));
    }

    internal static bool Matches(PaymentAttempt attempt, PaymentCallback callback, string gateway) =>
        attempt.Gateway == gateway && callback.Gateway == gateway && attempt.OrderId > 0 && attempt.AmountRial > 0 &&
        !string.IsNullOrWhiteSpace(attempt.Token) && string.Equals(attempt.Token, callback.Token, StringComparison.Ordinal);

    internal static PaymentResult Invalid() => new(PaymentOutcome.InvalidInput, Message: "Payment or callback data is invalid.");
    internal static PaymentResult Unknown(int? code = null) => new(PaymentOutcome.Unknown, code,
        "Payment outcome is uncertain. Reconcile before fulfilling or retrying as a new payment.");
}
