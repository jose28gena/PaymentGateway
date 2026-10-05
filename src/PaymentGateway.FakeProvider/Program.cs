using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

// Proveedor de pagos simulado, con fallos deterministas para poder probar la resiliencia del cliente:
//   tok_visa      -> cobro exitoso
//   tok_declined  -> 402 (tarjeta rechazada)
//   tok_flaky     -> 503 en los 2 primeros intentos de cada Idempotency-Key, luego éxito
//   tok_down      -> 503 siempre
var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var apiKey = app.Configuration["Provider:ApiKey"];
if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Length < 8)
    throw new InvalidOperationException("Define Provider__ApiKey (mínimo 8 caracteres) como variable de entorno.");
var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(apiKey));

var charges = new ConcurrentDictionary<string, Charge>();      // Idempotency-Key -> cargo creado
var attempts = new ConcurrentDictionary<string, int>();        // Idempotency-Key -> intentos recibidos
var totalRequests = 0;

app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/v1"))
    {
        var provided = SHA256.HashData(Encoding.UTF8.GetBytes(ctx.Request.Headers["X-Api-Key"].ToString()));
        if (!CryptographicOperations.FixedTimeEquals(provided, expectedHash))
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
    }
    await next();
});

app.MapPost("/v1/charges", (ChargeBody body, HttpContext ctx) =>
{
    Interlocked.Increment(ref totalRequests);

    var key = ctx.Request.Headers["Idempotency-Key"].ToString();
    if (string.IsNullOrWhiteSpace(key))
        return Results.BadRequest(new { error = "Idempotency-Key requerida" });
    if (body.AmountMinor <= 0)
        return Results.BadRequest(new { error = "amountMinor inválido" });

    var attempt = attempts.AddOrUpdate(key, 1, (_, n) => n + 1);

    // Idempotencia: una clave ya procesada devuelve el mismo cargo sin crear otro.
    if (charges.TryGetValue(key, out var existing))
    {
        ctx.Response.Headers["Idempotent-Replayed"] = "true";
        return Results.Ok(existing);
    }

    switch (body.Source)
    {
        case "tok_down":
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        case "tok_flaky" when attempt <= 2:
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        case "tok_declined":
            return Results.Json(new Charge($"ch_{Guid.NewGuid():N}", "failed", "card_declined"), statusCode: StatusCodes.Status402PaymentRequired);
    }

    var charge = new Charge($"ch_{Guid.NewGuid():N}", "succeeded", null);
    charges[key] = charge;
    return Results.Created($"/v1/charges/{charge.Id}", charge);
});

// Endpoints de inspección para que los tests comprueben qué vio realmente el proveedor.
app.MapGet("/v1/stats", () => Results.Ok(new { totalRequests, chargesCreated = charges.Count }));
app.MapGet("/v1/stats/{key}", (string key) => Results.Ok(new
{
    attempts = attempts.GetValueOrDefault(key),
    chargesCreated = charges.ContainsKey(key) ? 1 : 0
}));

app.Run();

internal record ChargeBody(long AmountMinor, string Currency, string Source, string? Description);

internal record Charge(string Id, string Status, string? FailureCode);

// Necesario para WebApplicationFactory<Program> en los tests.
public partial class Program;
