# OdinIranianIpg

A small, extensible .NET 10 SDK for Iranian Internet Payment Gateways. No PersianIPG dependency; providers communicate directly with their respective APIs.

## Install (after publication)

```shell
dotnet add package OdinIranianIpg --version 1.0.1
```

## Providers

| Provider | Request | Verify | Reversal | Inquiry |
|---|---|---|---|---|
| Zarinpal REST v4 | Yes | Yes | Not implemented | Yes |
| Parsian SOAP | Yes | Yes | Yes | Awaiting provider's sales inquiry specification |

All monetary values are `long` in **IRR** (rials). Convert tomans using checked multiplication by 10. Credentials belong in server-side secrets; no credentials are needed to run unit tests. The library does not store business data, issue invoices, or activate subscriptions.

## Dependency injection

```csharp
using OdinIranianIpg;

builder.Services.AddZarinpal(new ZarinpalOptions
{
    MerchantId = builder.Configuration["Payments:Zarinpal:MerchantId"]!,
    Sandbox = false
});
builder.Services.AddParsian(new ParsianOptions
{
    LoginAccount = builder.Configuration["Payments:Parsian:LoginAccount"]!,
    TerminalNo = 12345 // actual terminal number; optional
});

// Inject ZarinpalGateway or ParsianGateway directly,
// or resolve IPaymentGateway by provider name:
var gateway = serviceProvider.GetRequiredKeyedService<IPaymentGateway>("Parsian");
```

Standalone applications may instantiate either provider with a configured `HttpClient`. Use HTTPS, disable automatic redirects, do not install blanket POST retry policies, and do not disable certificate validation. Do not allow sandbox payments to activate real entitlements.

## Request a payment

```csharp
var result = await gateway.RequestAsync(new PaymentRequest(
    OrderId: order.Id, // positive, unique and persisted before request
    AmountRial: order.AmountRial,
    CallbackUrl: new Uri("https://example.com/api/payments/parsian/callback"),
    Description: "Order payment"), cancellationToken);

if (result.Success)
{
    await repository.SaveAttemptAsync(result.Attempt!); // your own durable storage
    return Redirect(result.RedirectUrl!.AbsoluteUri);
}
```

`PaymentAttempt` must be persisted exactly as returned. Use unique constraints for provider+token and order ID. An ambiguous start response never implies that a new payment request is safe to create automatically.

## Callback and verification

- Zarinpal sends `Authority` and `Status` in query parameters (`GET`).
- Parsian sends `Token`, `OrderId`, `status`, `RRN`, `TerminalNo`, `Amount` and optional other fields as form-urlencoded `POST`. Provide a separate callback endpoint; no login cookie should be required. Exempt the external callback from antiforgery only on that specific endpoint. Reject repeated values for a key before building the dictionary.

```csharp
var callback = gateway.ParseCallback(fields); // untrusted query/form dictionary
var attempt = await repository.FindAttemptAsync(gateway.Name, callback.Token);
if (attempt is null) return BadRequest();

// Serialize by order/attempt in your database so a duplicate callback cannot fulfill twice.
var verification = await gateway.VerifyAsync(attempt, callback, cancellationToken);
if (verification.Success)
{
    // Save the provider reference and grant the entitlement exactly once in one transaction.
}
else if (verification.Outcome == PaymentOutcome.Unknown)
{
    // Record for reconciliation. Do NOT grant goods or blindly create a replacement payment.
}
```

The verification method sends the stored amount for Zarinpal; for Parsian it validates callback order, token, amount and terminal, then verifies server-to-server by token and checks returned RRN/token. Parsian's supplied Confirm API does not return an amount, so do not describe this as an independent amount inquiry. A cancellation callback is not authoritative proof that the account was never charged. Never overwrite an already verified payment with a later callback. `AlreadyVerified` requires a positive reference. `InquiryAsync` reports a status only, never authorizes fulfillment.

When using `IReversibleGateway.ReverseAsync`, call it only for an actual successful payment candidate for which no service will be delivered and after deciding that reversal is appropriate. Parsian reversal is time-limited and not a general refund. On uncertain Confirm/Reversal outcomes, reconcile with the provider before taking competing actions. Configure expected terminal where possible. Discount/club terminals (`SwAmount`) are intentionally not supported; confirm terminal arrangements before deployment.

## Extending the SDK

Implement `IPaymentGateway` and register a keyed provider. Implement `IReversibleGateway` and/or `IInquirableGateway` only when the corresponding feature is actually supported and documented. No central switch or application-specific dependency is required. Add mock HTTP contract tests for every provider.

## Verification and release

```shell
dotnet restore OdinIranianIpg.slnx
dotnet build OdinIranianIpg.slnx -c Release --no-restore
dotnet test OdinIranianIpg.slnx -c Release --no-build
dotnet pack src/OdinIranianIpg/OdinIranianIpg.csproj -c Release --no-build -o artifacts
```

CI runs on pushes and pull requests to `main`. The release workflow runs only on a published GitHub Release, derives package version from its tag (`v1.0.0` becomes `1.0.0`), runs tests, packs, then publishes with short-lived credentials using NuGet Trusted Publishing (same mechanism as OdinCore).

Configure the NuGet trusted publishing policy once:

| Field | Value |
|---|---|
| GitHub owner | Amindada021 |
| Repository | OdinIranianIpg |
| Workflow | publish.yml |
| Scope | Push new packages and package versions |
| Package pattern | OdinIranianIpg |
| NuGet account | Amindada |

Create a published GitHub Release tagged `v1.0.0` only after approval and provider integration checks. No permanent NuGet API key is required.

## Contract sources and production readiness

- Zarinpal: current REST v4 integration contract and [gateway documentation](https://www.zarinpal.com/docs/paymentGateway/connectToGateway/).
- Parsian: provider documents 10010101 v2.0.0 (sale), 10011101 v1.0.0 (confirm) and 10012101 v1.0.0 (reversal). SOAP sale action matches the published client contract's `SalePaymentRequestAsync`.
- Before production: validate live endpoint/WSDL for sale, confirm and reversal, obtain document 10018 (status codes) and sales inquiry contract, whitelist server outbound IP and verify the terminal with Parsian. Mock tests are not real bank transactions. Do not publish an official stable package until live contracts are confirmed.

## License

MIT. See [LICENSE](LICENSE).
