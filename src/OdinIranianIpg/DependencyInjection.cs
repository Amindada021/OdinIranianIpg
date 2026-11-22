using Microsoft.Extensions.DependencyInjection;

namespace OdinIranianIpg;

public static class DependencyInjection
{
    public static IServiceCollection AddZarinpal(this IServiceCollection services, ZarinpalOptions options)
    {
        services.AddHttpClient(ZarinpalGateway.GatewayName, Configure).ConfigurePrimaryHttpMessageHandler(NewHandler);
        services.AddTransient<ZarinpalGateway>(sp => new(sp.GetRequiredService<IHttpClientFactory>().CreateClient(ZarinpalGateway.GatewayName), options));
        services.AddKeyedTransient<IPaymentGateway>(ZarinpalGateway.GatewayName, (sp, _) => sp.GetRequiredService<ZarinpalGateway>());
        return services;
    }

    public static IServiceCollection AddParsian(this IServiceCollection services, ParsianOptions options)
    {
        services.AddHttpClient(ParsianGateway.GatewayName, Configure).ConfigurePrimaryHttpMessageHandler(NewHandler);
        services.AddTransient<ParsianGateway>(sp => new(sp.GetRequiredService<IHttpClientFactory>().CreateClient(ParsianGateway.GatewayName), options));
        services.AddKeyedTransient<IPaymentGateway>(ParsianGateway.GatewayName, (sp, _) => sp.GetRequiredService<ParsianGateway>());
        return services;
    }

    private static void Configure(HttpClient client)
    {
        client.Timeout = TimeSpan.FromSeconds(30);
        client.MaxResponseContentBufferSize = 1_000_000;
    }
    private static HttpMessageHandler NewHandler() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
}
