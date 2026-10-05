namespace PaymentGateway.Client;

public abstract class PaymentGatewayException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// El proveedor rechazó la petición por un error del llamador (credenciales, datos inválidos, conflicto de
/// idempotencia). Reintentar no ayuda: hay que corregir la petición.
/// </summary>
public class PaymentRequestException(int statusCode, string message) : PaymentGatewayException(message)
{
    public int StatusCode { get; } = statusCode;
}

/// <summary>
/// El proveedor no está disponible: errores 5xx tras agotar reintentos, fallos de red o circuito abierto.
/// El estado del cobro es desconocido; se puede reintentar más tarde con la MISMA clave de idempotencia.
/// </summary>
public class PaymentProviderUnavailableException(string message, Exception? inner = null)
    : PaymentGatewayException(message, inner);

public class PaymentTimeoutException(string message, Exception? inner = null)
    : PaymentProviderUnavailableException(message, inner);
