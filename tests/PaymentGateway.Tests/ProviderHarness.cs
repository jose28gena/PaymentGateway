using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using PaymentGateway.Client;

namespace PaymentGateway.Tests;

/// <summary>
/// Levanta el proveedor simulado en memoria y conecta el cliente real (con su política de resiliencia) a él.
/// Cada test crea su propio harness para que los contadores del proveedor estén aislados.
/// </summary>
public sealed class ProviderHarness : IDisposable
{
    private const string ProviderKey = "clave-solo-para-tests";

    private readonly WebApplicationFactory<Program> _provider;
    private readonly ServiceProvider _services;

    public ProviderHarness(
        Action<HttpStandardResilienceOptions>? tuneResilience = null,
        string? clientApiKey = null,
        HttpMessageHandler? transport = null)
    {
        _provider = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(
                new Dictionary<string, string?> { ["Provider:ApiKey"] = ProviderKey })));

        var services = new ServiceCollection();
        services.AddPaymentGateway(
                o => { o.BaseUrl = "http://fake-provider"; o.ApiKey = clientApiKey ?? ProviderKey; },
                resilience =>
                {
                    // Tiempos reducidos para que los tests sean rápidos; la lógica es la misma que en producción.
                    resilience.Retry.MaxRetryAttempts = 3;
                    resilience.Retry.Delay = TimeSpan.FromMilliseconds(1);
                    resilience.AttemptTimeout.Timeout = TimeSpan.FromMilliseconds(500);
                    resilience.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(15);
                    // Por defecto el breaker no interviene, para probar cada política de forma aislada;
                    // el test del circuit breaker baja este valor.
                    resilience.CircuitBreaker.MinimumThroughput = 100;
                    resilience.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(2);
                    resilience.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(30);
                    tuneResilience?.Invoke(resilience);
                })
            .ConfigurePrimaryHttpMessageHandler(() => transport ?? _provider.Server.CreateHandler());

        _services = services.BuildServiceProvider();
        Gateway = _services.GetRequiredService<IPaymentGateway>();
        Inspector = _provider.CreateClient();
        Inspector.DefaultRequestHeaders.Add("X-Api-Key", ProviderKey);
    }

    public IPaymentGateway Gateway { get; }

    private HttpClient Inspector { get; }

    public async Task<(int Attempts, int ChargesCreated)> StatsForAsync(string idempotencyKey)
    {
        var s = await Inspector.GetFromJsonAsync<KeyStats>($"/v1/stats/{idempotencyKey}");
        return (s!.Attempts, s.ChargesCreated);
    }

    public async Task<int> TotalRequestsAsync()
        => (await Inspector.GetFromJsonAsync<GlobalStats>("/v1/stats"))!.TotalRequests;

    public static ChargeRequest Charge(string key, string source, decimal amount = 150.00m)
        => new(key, amount, "mxn", source, "Compra en tienda");

    public void Dispose()
    {
        Inspector.Dispose();
        _services.Dispose();
        _provider.Dispose();
    }

    private sealed record KeyStats(int Attempts, int ChargesCreated);

    private sealed record GlobalStats(int TotalRequests);
}
