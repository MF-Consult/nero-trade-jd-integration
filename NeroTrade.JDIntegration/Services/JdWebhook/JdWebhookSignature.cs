using System.Security.Cryptography;
using System.Text;

namespace NeroTrade.JDIntegration.Services.JdWebhook;

/// <summary>
/// Verifies the <c>X-JD-Hash</c> header on inbound JD webhooks. JD's docs say the header is the job id hashed
/// with HMAC-SHA256 using the webhook's secret; they do not say whether the digest is hex or base64, so both
/// encodings are accepted. Comparison is constant-time and an empty secret never validates.
/// </summary>
public static class JdWebhookSignature
{
    public static bool IsValid(string? jobId, string? hash, string? secret)
    {
        if (string.IsNullOrWhiteSpace(jobId) || string.IsNullOrWhiteSpace(hash) || string.IsNullOrEmpty(secret))
            return false;

        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(jobId));
        var presented = hash.Trim();

        // Hex (either case)
        if (presented.Length == expected.Length * 2 && TryFromHex(presented, out var hexBytes)
            && CryptographicOperations.FixedTimeEquals(hexBytes, expected))
            return true;

        // Base64
        try
        {
            var b64 = Convert.FromBase64String(presented);
            return CryptographicOperations.FixedTimeEquals(b64, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static string ComputeHex(string jobId, string secret)
        => Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(jobId))).ToLowerInvariant();

    private static bool TryFromHex(string s, out byte[] bytes)
    {
        try { bytes = Convert.FromHexString(s); return true; }
        catch (FormatException) { bytes = []; return false; }
    }
}
