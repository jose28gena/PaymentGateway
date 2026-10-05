using PaymentGateway.Client;
using static PaymentGateway.Tests.ProviderHarness;

namespace PaymentGateway.Tests;

public class ResilienceTests
{
    [Fact]
    public async Task Successful_charge_needs_a_single_attempt()
    {
        using var h = new ProviderHarness();

        var result = await h.Gateway.ChargeAsync(Charge("ok-1", "tok_visa"));

        Assert.Equal(ChargeStatus.Succeeded, result.Status);
        Assert.NotNull(result.ChargeId);
        Assert.Equal((1, 1), await h.StatsForAsync("ok-1"));
    }

    [Fact]
    public async Task Transient_5xx_errors_are_retried_and_create_exactly_one_charge()
    {
        using var h = new ProviderHarness();

        // tok_flaky responde 503 a los 2 primeros intentos de la clave.
        var result = await h.Gateway.ChargeAsync(Charge("flaky-1", "tok_flaky"));

        Assert.Equal(ChargeStatus.Succeeded, result.Status);
        var (attempts, charges) = await h.StatsForAsync("flaky-1");
        Assert.Equal(3, attempts);   // 2 fallos + 1 éxito, todos con la misma Idempotency-Key
        Assert.Equal(1, charges);    // y aun así un solo cargo
    }

    [Fact]
    public async Task Card_decline_is_a_business_result_and_is_not_retried()
    {
        using var h = new ProviderHarness();

        var result = await h.Gateway.ChargeAsync(Charge("declined-1", "tok_declined"));

        Assert.Equal(ChargeStatus.Declined, result.Status);
        Assert.Equal("card_declined", result.FailureCode);
        Assert.Equal((1, 0), await h.StatsForAsync("declined-1"));
    }

    [Fact]
    public async Task Persistent_outage_exhausts_retries_and_surfaces_a_meaningful_exception()
    {
        using var h = new ProviderHarness();

        await Assert.ThrowsAsync<PaymentProviderUnavailableException>(
            () => h.Gateway.ChargeAsync(Charge("down-1", "tok_down")));

        var (attempts, charges) = await h.StatsForAsync("down-1");
        Assert.Equal(4, attempts);   // 1 intento + 3 reintentos
        Assert.Equal(0, charges);
    }

    [Fact]
    public async Task Slow_provider_triggers_a_timeout_instead_of_hanging()
    {
        // Transporte que nunca responde; el timeout por intento es de 500 ms.
        using var h = new ProviderHarness(r => r.Retry.MaxRetryAttempts = 1, transport: new HangingHandler());

        var started = DateTime.UtcNow;
        var ex = await Assert.ThrowsAsync<PaymentTimeoutException>(
            () => h.Gateway.ChargeAsync(Charge("slow-1", "tok_visa")));

        Assert.IsAssignableFrom<PaymentProviderUnavailableException>(ex);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5), "El cliente no debe quedarse colgado.");
    }

    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage();
        }
    }

    [Fact]
    public async Task Circuit_breaker_opens_and_stops_hitting_the_failing_provider()
    {
        using var h = new ProviderHarness(r => r.CircuitBreaker.MinimumThroughput = 2);

        for (var i = 0; i < 5; i++)
        {
            await Assert.ThrowsAsync<PaymentProviderUnavailableException>(
                () => h.Gateway.ChargeAsync(Charge($"breaker-{i}", "tok_down")));
        }

        // Sin breaker serían 5 llamadas x 4 intentos = 20 peticiones. Con el circuito abierto, las llamadas
        // posteriores fallan de inmediato sin llegar al proveedor.
        var total = await h.TotalRequestsAsync();
        Assert.True(total < 20, $"El proveedor recibió {total} peticiones; el circuito debió abrirse antes.");

        var before = await h.TotalRequestsAsync();
        await Assert.ThrowsAsync<PaymentProviderUnavailableException>(
            () => h.Gateway.ChargeAsync(Charge("breaker-extra", "tok_down")));
        Assert.Equal(before, await h.TotalRequestsAsync());
    }

    [Fact]
    public async Task Repeating_a_charge_with_the_same_key_returns_the_original_without_charging_again()
    {
        using var h = new ProviderHarness();

        var first = await h.Gateway.ChargeAsync(Charge("dup-1", "tok_visa"));
        var second = await h.Gateway.ChargeAsync(Charge("dup-1", "tok_visa"));

        Assert.False(first.Replayed);
        Assert.True(second.Replayed);
        Assert.Equal(first.ChargeId, second.ChargeId);
        Assert.Equal(1, (await h.StatsForAsync("dup-1")).ChargesCreated);
    }

    [Theory]
    [InlineData("", 100)]
    [InlineData("k", 0)]
    [InlineData("k", -5)]
    [InlineData("k", 10.555)]
    public async Task Invalid_requests_are_rejected_locally_without_calling_the_provider(string key, double amount)
    {
        using var h = new ProviderHarness();

        await Assert.ThrowsAsync<ArgumentException>(
            () => h.Gateway.ChargeAsync(Charge(key, "tok_visa", (decimal)amount)));

        Assert.Equal(0, await h.TotalRequestsAsync());
    }

    [Fact]
    public async Task Wrong_credentials_surface_as_a_request_error_not_as_an_outage()
    {
        using var h = new ProviderHarness(clientApiKey: "clave-incorrecta");

        var ex = await Assert.ThrowsAsync<PaymentRequestException>(
            () => h.Gateway.ChargeAsync(Charge("auth-1", "tok_visa")));

        Assert.Equal(401, ex.StatusCode);
    }
}
