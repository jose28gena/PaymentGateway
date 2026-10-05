namespace PaymentGateway.Client;

public enum ChargeStatus
{
    Succeeded,
    Pending,
    Declined
}

/// <param name="IdempotencyKey">
/// Identifica la intención de cobro. Se reenvía idéntica en cada reintento, de modo que el proveedor
/// nunca crea dos cargos aunque la red falle a mitad de camino.
/// </param>
public record ChargeRequest(string IdempotencyKey, decimal Amount, string Currency, string Source, string? Description = null);

public record ChargeResult(string? ChargeId, ChargeStatus Status, string? FailureCode, bool Replayed);
