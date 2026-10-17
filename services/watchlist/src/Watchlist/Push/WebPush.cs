using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Watchlist.Data;

namespace Watchlist.Push;

public sealed class PushOptions
{
    /// <summary>
    /// Who's sending, as push services require: the app's own address, e.g. https://notes.example.co.uk.
    /// </summary>
    public string Subject { get; set; } = "https://localhost";
}

/// <summary>The app's own signing key for push (VAPID). Made on first start and kept in the database.</summary>
public sealed class VapidKeys
{
    private readonly ECParameters _parameters;

    private VapidKeys(ECParameters parameters)
    {
        _parameters = parameters;
        PublicKey = Base64Url.Encode(UncompressedPoint(parameters.Q));
    }

    /// <summary>The 65-byte public key, base64url: what the browser needs to subscribe.</summary>
    public string PublicKey { get; }

    public static async Task<VapidKeys> LoadOrCreateAsync(MetaStore meta)
    {
        if (await meta.GetAsync("vapid_private") is { } stored)
        {
            using var existing = ECDsa.Create();
            existing.ImportECPrivateKey(Convert.FromBase64String(stored), out _);
            return new VapidKeys(existing.ExportParameters(true));
        }

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await meta.SetAsync("vapid_private", Convert.ToBase64String(key.ExportECPrivateKey()));
        return new VapidKeys(key.ExportParameters(true));
    }

    public static VapidKeys Create()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return new VapidKeys(key.ExportParameters(true));
    }

    /// <summary>The Authorization header value: a short-lived ES256 token for one push service.</summary>
    public string Authorization(Uri endpoint, string subject, DateTimeOffset now)
    {
        var audience = endpoint.GetLeftPart(UriPartial.Authority);
        var header = Base64Url.Encode("""{"typ":"JWT","alg":"ES256"}"""u8);
        var claims = Base64Url.Encode(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["aud"] = audience,
            ["exp"] = now.AddHours(12).ToUnixTimeSeconds(),
            ["sub"] = subject,
        }));
        var signingInput = $"{header}.{claims}";
        using var key = ECDsa.Create(_parameters);
        var signature = key.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256);   // r || s, as JWT wants
        return $"vapid t={signingInput}.{Base64Url.Encode(signature)}, k={PublicKey}";
    }

    internal static byte[] UncompressedPoint(ECPoint q) => [0x04, .. q.X!, .. q.Y!];
}

/// <summary>
/// Message encryption for Web Push (RFC 8291, "aes128gcm"). Only the browser that subscribed
/// can read a message; the push service in between sees ciphertext.
/// </summary>
public static class PushEncryption
{
    private const int RecordSize = 4096;

    public static byte[] Encrypt(byte[] plaintext, byte[] userAgentPublicKey, byte[] authSecret)
    {
        using var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        return Encrypt(plaintext, userAgentPublicKey, authSecret, ephemeral, RandomNumberGenerator.GetBytes(16));
    }

    internal static byte[] Encrypt(byte[] plaintext, byte[] userAgentPublicKey, byte[] authSecret, ECDiffieHellman ephemeral, byte[] salt)
    {
        if (userAgentPublicKey.Length != 65 || userAgentPublicKey[0] != 0x04)
            throw new ArgumentException("The subscription's p256dh key isn't an uncompressed P-256 point.");

        using var peer = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = userAgentPublicKey[1..33], Y = userAgentPublicKey[33..65] },
        });
        var sharedSecret = ephemeral.DeriveRawSecretAgreement(peer.PublicKey);
        var serverPublicKey = VapidKeys.UncompressedPoint(ephemeral.ExportParameters(false).Q);

        var (key, nonce) = DeriveKeyAndNonce(sharedSecret, authSecret, salt, userAgentPublicKey, serverPublicKey);

        // One record holding everything, ended by the 0x02 "last record" delimiter.
        var padded = new byte[plaintext.Length + 1];
        plaintext.CopyTo(padded, 0);
        padded[^1] = 0x02;
        var ciphertext = new byte[padded.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(key, tag.Length))
            aes.Encrypt(nonce, padded, ciphertext, tag);

        // Header: salt (16) | record size (4) | key id length (1) | key id (the server's public key).
        var body = new byte[16 + 4 + 1 + serverPublicKey.Length + ciphertext.Length + tag.Length];
        var span = body.AsSpan();
        salt.CopyTo(span);
        BinaryPrimitives.WriteUInt32BigEndian(span[16..], RecordSize);
        span[20] = (byte)serverPublicKey.Length;
        serverPublicKey.CopyTo(span[21..]);
        ciphertext.CopyTo(span[(21 + serverPublicKey.Length)..]);
        tag.CopyTo(span[(21 + serverPublicKey.Length + ciphertext.Length)..]);
        return body;
    }

    internal static (byte[] Key, byte[] Nonce) DeriveKeyAndNonce(
        byte[] sharedSecret, byte[] authSecret, byte[] salt, byte[] userAgentPublicKey, byte[] serverPublicKey)
    {
        byte[] keyInfo = [.. "WebPush: info\0"u8, .. userAgentPublicKey, .. serverPublicKey];
        var ikm = HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, 32, authSecret, keyInfo);
        var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 16, salt, "Content-Encoding: aes128gcm\0"u8.ToArray());
        var nonce = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 12, salt, "Content-Encoding: nonce\0"u8.ToArray());
        return (key, nonce);
    }
}

public enum PushResult { Sent, Gone, Failed }

public interface IPushSender
{
    Task<PushResult> SendAsync(PushSubscriptionRow subscription, object payload, CancellationToken ct);
}

/// <summary>Sends one encrypted, signed message to one browser's push service.</summary>
public sealed class WebPushSender(HttpClient http, VapidKeys keys, IOptions<PushOptions> options, TimeProvider time, ILogger<WebPushSender> log) : IPushSender
{
    public async Task<PushResult> SendAsync(PushSubscriptionRow subscription, object payload, CancellationToken ct)
    {
        var endpoint = new Uri(subscription.Endpoint);
        var body = PushEncryption.Encrypt(
            JsonSerializer.SerializeToUtf8Bytes(payload, WatchlistService.Json),
            Base64Url.Decode(subscription.P256dh),
            Base64Url.Decode(subscription.Auth));

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Content.Headers.ContentEncoding.Add("aes128gcm");
        request.Headers.TryAddWithoutValidation("Authorization", keys.Authorization(endpoint, options.Value.Subject, time.GetUtcNow()));
        request.Headers.Add("TTL", "86400");
        request.Headers.Add("Urgency", "normal");

        try
        {
            using var response = await http.SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
                return PushResult.Sent;
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
                return PushResult.Gone;   // the browser unsubscribed or the app was uninstalled
            log.LogWarning("Push to {Host} failed: {Status} {Body}", endpoint.Host, (int)response.StatusCode,
                await response.Content.ReadAsStringAsync(ct));
            return PushResult.Failed;
        }
        catch (HttpRequestException ex)
        {
            log.LogWarning(ex, "Push to {Host} failed", endpoint.Host);
            return PushResult.Failed;
        }
    }
}

public static class Base64Url
{
    public static string Encode(ReadOnlySpan<byte> data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Decode(string s)
    {
        var b64 = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '='));
    }
}
