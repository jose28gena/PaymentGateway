using PaymentGateway.Client;

namespace PaymentGateway.Tests;

public class WebhookSignatureTests
{
    private const string Secret = "whsec_solo_para_tests";
    private const string Payload = "{\"id\":\"evt_1\",\"type\":\"charge.succeeded\",\"amountMinor\":15000}";
    private static readonly DateTimeOffset Now = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);

    [Fact]
    public void Valid_signature_is_accepted()
    {
        var header = WebhookSignature.CreateHeader(Secret, Payload, Now);

        Assert.True(WebhookSignature.Verify(Secret, header, Payload, Now, Tolerance));
    }

    [Fact]
    public void Tampered_payload_is_rejected()
    {
        var header = WebhookSignature.CreateHeader(Secret, Payload, Now);
        var tampered = Payload.Replace("15000", "1");

        Assert.False(WebhookSignature.Verify(Secret, header, tampered, Now, Tolerance));
    }

    [Fact]
    public void Wrong_secret_is_rejected()
    {
        var header = WebhookSignature.CreateHeader("otro-secreto", Payload, Now);

        Assert.False(WebhookSignature.Verify(Secret, header, Payload, Now, Tolerance));
    }

    [Fact]
    public void Old_messages_are_rejected_to_prevent_replay_attacks()
    {
        var header = WebhookSignature.CreateHeader(Secret, Payload, Now.AddMinutes(-6));

        Assert.False(WebhookSignature.Verify(Secret, header, Payload, Now, Tolerance));
    }

    [Fact]
    public void Messages_from_the_future_are_rejected()
    {
        var header = WebhookSignature.CreateHeader(Secret, Payload, Now.AddMinutes(6));

        Assert.False(WebhookSignature.Verify(Secret, header, Payload, Now, Tolerance));
    }

    [Fact]
    public void Secret_rotation_accepts_any_matching_v1_signature()
    {
        var oldHeader = WebhookSignature.CreateHeader("secreto-viejo", Payload, Now);
        var newHeader = WebhookSignature.CreateHeader(Secret, Payload, Now);
        var oldSig = oldHeader[(oldHeader.IndexOf("v1=", StringComparison.Ordinal) + 3)..];
        var newSig = newHeader[(newHeader.IndexOf("v1=", StringComparison.Ordinal) + 3)..];
        var t = Now.ToUnixTimeSeconds();

        // El proveedor envía ambas firmas durante la rotación.
        var header = $"t={t},v1={oldSig},v1={newSig}";

        Assert.True(WebhookSignature.Verify(Secret, header, Payload, Now, Tolerance));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("basura")]
    [InlineData("t=abc,v1=zzzz")]
    [InlineData("t=1768478400")]
    [InlineData("v1=abcdef")]
    public void Malformed_headers_are_rejected_without_throwing(string? header)
    {
        Assert.False(WebhookSignature.Verify(Secret, header, Payload, Now, Tolerance));
    }
}
