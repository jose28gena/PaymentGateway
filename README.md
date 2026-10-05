# PaymentGateway

[![CI](https://github.com/jose28gena/PaymentGateway/actions/workflows/ci.yml/badge.svg)](https://github.com/jose28gena/PaymentGateway/actions/workflows/ci.yml)
![.NET 8](https://img.shields.io/badge/.NET-8-512BD4?logo=dotnet&logoColor=white)
![License: MIT](https://img.shields.io/badge/license-MIT-green)

Cliente .NET para integrar un **proveedor de pagos externo** de forma resiliente. Muestra cómo tratar lo que sale mal en una integración real: el proveedor se cae, responde lento, la red falla a mitad de un cobro, o llega un webhook falsificado.

Incluye un **proveedor simulado** con fallos deterministas, de modo que cada comportamiento está probado de punta a punta, sin mocks del cliente.

## Qué problemas aborda

| Situación real | Qué hace el cliente |
| --- | --- |
| Error transitorio del proveedor (`503`, `408`, `429`, fallo de red) | Reintenta con **retroceso exponencial y jitter**, reenviando la **misma `Idempotency-Key`**: nunca se crean dos cargos. |
| El proveedor está caído de forma sostenida | **Circuit breaker**: tras demasiados fallos deja de llamarlo un rato y falla de inmediato, en vez de acumular peticiones colgadas. |
| El proveedor no responde | **Timeout por intento** y **timeout total**; el llamador recibe `PaymentTimeoutException`, no un cuelgue. |
| Tarjeta rechazada | Es un resultado de negocio (`ChargeStatus.Declined` con `FailureCode`), **no se reintenta** ni se trata como error técnico. |
| Credenciales o datos inválidos (`4xx`) | `PaymentRequestException` inmediata. Reintentar no ayuda. |
| Repetir un cobro con la misma clave | Devuelve el cargo original (`Replayed = true`) sin cobrar otra vez. |
| Webhook falsificado, alterado o reenviado | Verificación **HMAC-SHA256** en tiempo constante, con ventana de tolerancia contra *replay* y soporte de **rotación de secretos**. |

> Reintentar un `POST` solo es seguro porque cada cobro lleva una clave de idempotencia. Sin ella, un reintento podría cobrar dos veces.

## Uso

```csharp
services.AddPaymentGateway(o =>
{
    o.BaseUrl = "https://api.proveedor.example";
    o.ApiKey  = configuration["Payments:ApiKey"]!;   // desde variables de entorno / secret store
});
```

```csharp
try
{
    var result = await gateway.ChargeAsync(new ChargeRequest(
        IdempotencyKey: order.PaymentAttemptId,   // la MISMA clave para todos los reintentos de este pago
        Amount: 150.00m, Currency: "mxn", Source: "tok_visa", Description: "Compra en tienda"));

    if (result.Status == ChargeStatus.Declined)
        ShowDeclined(result.FailureCode);
}
catch (PaymentProviderUnavailableException)
{
    // El estado del cobro es desconocido: reintentar más tarde con la MISMA clave de idempotencia
    // o conciliar contra el proveedor. Nunca generar una clave nueva "por si acaso".
}
catch (PaymentRequestException ex) { /* error del llamador: ex.StatusCode */ }
```

Política por defecto (ajustable con el segundo parámetro de `AddPaymentGateway`): 3 reintentos con retroceso exponencial y jitter, timeout de 5 s por intento y 30 s en total, y breaker que abre con ≥ 50 % de fallos sobre al menos 10 llamadas en 30 s.

### Verificar webhooks

```csharp
app.MapPost("/webhooks/payments", async (HttpRequest request) =>
{
    var payload = await new StreamReader(request.Body).ReadToEndAsync();

    var valid = WebhookSignature.Verify(
        secret: webhookSecret,
        header: request.Headers["X-Signature"],     // t=1768478400,v1=<hex>
        payload: payload,
        now: DateTimeOffset.UtcNow,
        tolerance: TimeSpan.FromMinutes(5));

    return valid ? Results.Ok() : Results.Unauthorized();
});
```

Verifica el cuerpo **sin procesar** (tal como llegó), no uno re-serializado.

## Estructura

```
src/
├── PaymentGateway.Client          # Cliente tipado, política de resiliencia, firma de webhooks
└── PaymentGateway.FakeProvider    # Proveedor simulado con fallos deterministas
tests/
└── PaymentGateway.Tests           # 24 tests: resiliencia contra el proveedor simulado + webhooks
```

## Proveedor simulado

Se elige el comportamiento con el valor de `Source`:

| `Source` | Comportamiento |
| --- | --- |
| `tok_visa` | Cobro exitoso |
| `tok_declined` | `402`, tarjeta rechazada |
| `tok_flaky` | `503` en los 2 primeros intentos de cada `Idempotency-Key`, luego éxito |
| `tok_down` | `503` siempre |

También expone `/v1/stats` y `/v1/stats/{key}` para que los tests comprueben qué recibió realmente el proveedor.

Para ejecutarlo a mano:

```bash
export Provider__ApiKey="una-clave-de-prueba"
dotnet run --project src/PaymentGateway.FakeProvider
```

## Tests

```bash
dotnet test
```

Lo que demuestran, contra el proveedor simulado y la política real de resiliencia:

- Con `tok_flaky`, el cliente reintenta, el proveedor recibe **3 intentos con la misma clave** y se crea **un solo cargo**.
- Un rechazo de tarjeta se resuelve en **1 intento** y no se reintenta.
- Una caída sostenida agota **1 + 3 intentos** y lanza `PaymentProviderUnavailableException`.
- Con el breaker abierto, las llamadas siguientes **no llegan al proveedor**.
- Un transporte que nunca responde termina en `PaymentTimeoutException` en menos de 5 s.
- Webhooks: firma válida, cuerpo alterado, secreto incorrecto, mensaje viejo o del futuro, rotación de secretos y encabezados mal formados.

## Límites

- El proveedor es una simulación; un proveedor real tendrá su propio formato de errores y firma de webhooks. El cliente aísla ese mapeo en `PaymentGatewayClient`.
- El *breaker* es por cliente (proceso). Con varias instancias, cada una abre su propio circuito.
- Los webhooks verifican autenticidad y frescura; la **deduplicación** de eventos repetidos (por `id`) queda a cargo de la aplicación.

## Licencia

MIT. Consulta [LICENSE](LICENSE).
