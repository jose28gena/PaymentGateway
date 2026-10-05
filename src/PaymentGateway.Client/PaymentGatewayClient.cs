using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace PaymentGateway.Client;

public interface IPaymentGateway
{
    Task<ChargeResult> ChargeAsync(ChargeRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Cliente tipado para un proveedor de pagos externo. La resiliencia (reintentos, timeouts, circuit breaker)
/// la aporta el handler registrado en <see cref="ServiceCollectionExtensions.AddPaymentGateway"/>;
/// esta clase traduce el resultado HTTP a un modelo de dominio y a excepciones con significado.
/// </summary>
public sealed class PaymentGatewayClient(HttpClient http) : IPaymentGateway
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<ChargeResult> ChargeAsync(ChargeRequest request, CancellationToken cancellationToken = default)
    {
        Validate(request);

        var body = new ChargeBody((long)(request.Amount * 100m), request.Currency.ToUpperInvariant(), request.Source, request.Description);
        using var message = new HttpRequestMessage(HttpMethod.Post, "v1/charges")
        {
            Content = JsonContent.Create(body, options: Json)
        };
        message.Headers.Add("Idempotency-Key", request.IdempotencyKey);

        try
        {
            using var response = await http.SendAsync(message, cancellationToken);
            return await MapAsync(response, cancellationToken);
        }
        catch (BrokenCircuitException ex)
        {
            throw new PaymentProviderUnavailableException("Circuito abierto: el proveedor se considera caído; no se envió la petición.", ex);
        }
        catch (TimeoutRejectedException ex)
        {
            throw new PaymentTimeoutException("El proveedor no respondió dentro del tiempo permitido.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new PaymentProviderUnavailableException("No fue posible comunicarse con el proveedor.", ex);
        }
    }

    private static async Task<ChargeResult> MapAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var replayed = response.Headers.Contains("Idempotent-Replayed");

        if (response.IsSuccessStatusCode)
        {
            var ok = await response.Content.ReadFromJsonAsync<ProviderCharge>(Json, ct)
                     ?? throw new PaymentProviderUnavailableException("Respuesta vacía del proveedor.");
            var status = ok.Status == "pending" ? ChargeStatus.Pending : ChargeStatus.Succeeded;
            return new ChargeResult(ok.Id, status, null, replayed);
        }

        // Un rechazo de la tarjeta es un resultado de negocio esperado, no un error técnico.
        if (response.StatusCode == HttpStatusCode.PaymentRequired)
        {
            var declined = await response.Content.ReadFromJsonAsync<ProviderCharge>(Json, ct);
            return new ChargeResult(declined?.Id, ChargeStatus.Declined, declined?.FailureCode ?? "declined", replayed);
        }

        // 5xx, 408 y 429 que llegan aquí ya agotaron los reintentos del handler de resiliencia.
        if ((int)response.StatusCode >= 500 || response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests)
            throw new PaymentProviderUnavailableException($"El proveedor respondió {(int)response.StatusCode} tras agotar los reintentos.");

        var detail = await response.Content.ReadAsStringAsync(ct);
        throw new PaymentRequestException((int)response.StatusCode, $"El proveedor rechazó la petición ({(int)response.StatusCode}): {detail}");
    }

    private static void Validate(ChargeRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
            throw new ArgumentException("La clave de idempotencia es obligatoria.", nameof(request));
        if (request.Amount <= 0 || decimal.Round(request.Amount, 2) != request.Amount)
            throw new ArgumentException("El monto debe ser mayor a cero y tener como máximo 2 decimales.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Currency) || string.IsNullOrWhiteSpace(request.Source))
            throw new ArgumentException("Moneda y origen del pago son obligatorios.", nameof(request));
    }

    private sealed record ChargeBody(long AmountMinor, string Currency, string Source, string? Description);

    private sealed record ProviderCharge(string? Id, string? Status, string? FailureCode);
}
