using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Polly;

namespace PaymentGateway.Client;

public class PaymentGatewayOptions
{
    [Required, Url] public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Credencial del proveedor. Debe venir de configuración segura (variables de entorno, secret store).</summary>
    [Required, MinLength(8)] public string ApiKey { get; set; } = string.Empty;
}

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registra <see cref="IPaymentGateway"/> con una política de resiliencia pensada para cobros:
    /// reintentos con retroceso exponencial y jitter, timeout por intento y total, y circuit breaker.
    /// Reintentar un POST solo es seguro porque cada cobro lleva una clave de idempotencia.
    /// </summary>
    public static IHttpClientBuilder AddPaymentGateway(
        this IServiceCollection services,
        Action<PaymentGatewayOptions> configure,
        Action<HttpStandardResilienceOptions>? configureResilience = null)
    {
        services.AddOptions<PaymentGatewayOptions>()
            .Configure(configure)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        var builder = services.AddHttpClient<IPaymentGateway, PaymentGatewayClient>((sp, http) =>
        {
            var options = sp.GetRequiredService<IOptions<PaymentGatewayOptions>>().Value;
            http.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
            http.DefaultRequestHeaders.Add("X-Api-Key", options.ApiKey);
        });

        builder.AddStandardResilienceHandler(o =>
        {
            o.Retry.MaxRetryAttempts = 3;
            o.Retry.BackoffType = DelayBackoffType.Exponential;
            o.Retry.UseJitter = true;
            o.Retry.Delay = TimeSpan.FromMilliseconds(200);

            o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(5);
            o.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(30);

            // Si más de la mitad de las llamadas recientes fallan, dejamos de golpear al proveedor un rato.
            o.CircuitBreaker.FailureRatio = 0.5;
            o.CircuitBreaker.MinimumThroughput = 10;
            o.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(30);
            o.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(15);

            configureResilience?.Invoke(o);
        });

        return builder;
    }
}
