using System.Security.Cryptography;
using System.Text;

namespace PaymentGateway.Client;

/// <summary>
/// Firma y verificación de webhooks con HMAC-SHA256. Encabezado: <c>t=&lt;unix&gt;,v1=&lt;hex&gt;</c>.
/// Se firma <c>"{t}.{cuerpo}"</c>. El timestamp acota la ventana de repetición (replay) y se admiten
/// varias firmas <c>v1</c> para rotar secretos sin interrumpir el servicio.
/// </summary>
public static class WebhookSignature
{
    public static string CreateHeader(string secret, string payload, DateTimeOffset timestamp)
    {
        var t = timestamp.ToUnixTimeSeconds();
        return $"t={t},v1={ComputeHex(secret, t, payload)}";
    }

    public static bool Verify(string secret, string? header, string payload, DateTimeOffset now, TimeSpan tolerance)
    {
        if (string.IsNullOrWhiteSpace(header) || string.IsNullOrEmpty(secret))
            return false;

        long? timestamp = null;
        var signatures = new List<string>();

        foreach (var part in header.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            if (pair.Length != 2) continue;

            if (pair[0] == "t" && long.TryParse(pair[1], out var t)) timestamp = t;
            else if (pair[0] == "v1") signatures.Add(pair[1]);
        }

        if (timestamp is null || signatures.Count == 0)
            return false;

        // Rechaza mensajes demasiado viejos o "del futuro": evita ataques de repetición.
        var age = now - DateTimeOffset.FromUnixTimeSeconds(timestamp.Value);
        if (age > tolerance || age < -tolerance)
            return false;

        var expected = Convert.FromHexString(ComputeHex(secret, timestamp.Value, payload));
        foreach (var candidate in signatures)
        {
            if (TryParseHex(candidate, out var bytes) && CryptographicOperations.FixedTimeEquals(bytes, expected))
                return true;
        }

        return false;
    }

    private static string ComputeHex(string secret, long timestamp, string payload)
    {
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp}.{payload}"));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static bool TryParseHex(string hex, out byte[] bytes)
    {
        try
        {
            bytes = Convert.FromHexString(hex);
            return true;
        }
        catch (FormatException)
        {
            bytes = [];
            return false;
        }
    }
}
