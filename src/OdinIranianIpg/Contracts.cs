namespace OdinIranianIpg;

/// <summary>All monetary values are whole Iranian rials (IRR).</summary>
public sealed record PaymentRequest(long OrderId, long AmountRial, Uri CallbackUrl, string Description,
    string? Mobile = null);

/// <summary>Persist after successful initiation; load from trusted application storage on callback.</summary>
public sealed record PaymentAttempt(string Gateway, long OrderId, long AmountRial, string Token);

public enum PaymentOutcome { Succeeded, AlreadyVerified, Rejected, Cancelled, InvalidInput, Unknown }

public sealed record PaymentStartResult(PaymentOutcome Outcome, int? ProviderCode = null,
    string? Message = null, PaymentAttempt? Attempt = null, Uri? RedirectUrl = null)
{
    public bool Success => Outcome == PaymentOutcome.Succeeded && Attempt is not null && RedirectUrl is not null;
}

public sealed record PaymentResult(PaymentOutcome Outcome, int? ProviderCode = null, string? Message = null,
    string? Reference = null, string? MaskedCard = null)
{
    public bool Success => Outcome is PaymentOutcome.Succeeded or PaymentOutcome.AlreadyVerified;
}

public enum CallbackDisposition { Candidate, Cancelled, Rejected, Invalid }

/// <summary>Untrusted callback data. Never use as proof of payment.</summary>
public sealed record PaymentCallback(string Gateway, CallbackDisposition Disposition, string? Token = null,
    long? OrderId = null, long? AmountRial = null, long? Rrn = null, int? TerminalNo = null,
    int? ProviderCode = null);

public sealed record PaymentInquiryResult(PaymentOutcome Outcome, int? ProviderCode = null,
    string? ProviderStatus = null, string? Message = null);

public interface IPaymentGateway
{
    string Name { get; }
    Task<PaymentStartResult> RequestAsync(PaymentRequest request, CancellationToken cancellationToken = default);
    PaymentCallback ParseCallback(IReadOnlyDictionary<string, string> fields);
    Task<PaymentResult> VerifyAsync(PaymentAttempt attempt, PaymentCallback callback,
        CancellationToken cancellationToken = default);
}

/// <summary>Reversal is subject to provider time windows and is not an arbitrary refund.</summary>
public interface IReversibleGateway : IPaymentGateway
{
    Task<PaymentResult> ReverseAsync(PaymentAttempt attempt, PaymentCallback callback,
        CancellationToken cancellationToken = default);
}

public interface IInquirableGateway : IPaymentGateway
{
    Task<PaymentInquiryResult> InquiryAsync(PaymentAttempt attempt, CancellationToken cancellationToken = default);
}
