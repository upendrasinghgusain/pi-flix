using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Watchlist.Push;

namespace Watchlist.Tests;

public class WebPushTests
{
    [Fact]
    public void Matches_the_worked_example_in_RFC_8291()
    {
        // RFC 8291, Appendix A.
        var plaintext = "When I grow up, I want to be a watermelon"u8.ToArray();
        var serverPrivate = Base64Url.Decode("yfWPiYE-n46HLnH0KqZOF1fJJU3MYrct3AELtAQ-oRw");
        var serverPublic = Base64Url.Decode("BP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A8");
        var userAgentPublic = Base64Url.Decode("BCVxsr7N_eNgVRqvHtD0zTZsEc6-VV-JvLexhqUzORcxaOzi6-AYWXvTBHm4bjyPjs7Vd8pZGH6SRpkNtoIAiw4");
        var salt = Base64Url.Decode("DGv6ra1nlYgDCS1FRnbzlw");
        var authSecret = Base64Url.Decode("BTBZMqHH6r4Tts7J_aSIgg");

        using var ephemeral = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = serverPrivate,
            Q = new ECPoint { X = serverPublic[1..33], Y = serverPublic[33..] },
        });

        var body = PushEncryption.Encrypt(plaintext, userAgentPublic, authSecret, ephemeral, salt);

        Assert.Equal(
            "DGv6ra1nlYgDCS1FRnbzlwAAEABBBP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A_yl95bQpu6cVPTpK4Mqgkf1CXztLVBSt2Ks3oZwbuwXPXLWyouBWLVWGNWQexSgSxsj_Qulcy4a-fN",
            Base64Url.Encode(body));
    }

    [Fact]
    public void A_browser_can_decrypt_what_is_sent()
    {
        // Play the browser: make its keys, then decrypt the way it would.
        using var browser = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var browserPublic = VapidKeys.UncompressedPoint(browser.ExportParameters(false).Q);
        var authSecret = RandomNumberGenerator.GetBytes(16);
        var message = Encoding.UTF8.GetBytes("""{"title":"Weapons (2025)","body":"🌍 Out at home"}""");

        var body = PushEncryption.Encrypt(message, browserPublic, authSecret);

        var salt = body[..16];
        Assert.Equal(4096u, BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(16, 4)));
        var keyLength = body[20];
        var serverPublic = body[21..(21 + keyLength)];
        var payload = body[(21 + keyLength)..];

        using var server = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = serverPublic[1..33], Y = serverPublic[33..] },
        });
        var shared = browser.DeriveRawSecretAgreement(server.PublicKey);
        var (key, nonce) = PushEncryption.DeriveKeyAndNonce(shared, authSecret, salt, browserPublic, serverPublic);

        var plain = new byte[payload.Length - 16];
        using (var aes = new AesGcm(key, 16))
            aes.Decrypt(nonce, payload[..^16], payload[^16..], plain);

        Assert.Equal(0x02, plain[^1]);
        Assert.Equal(message, plain[..^1]);
    }

    [Fact]
    public void Vapid_token_is_signed_for_the_push_service_and_verifies()
    {
        var keys = VapidKeys.Create();
        var now = new DateTimeOffset(2026, 10, 17, 9, 0, 0, TimeSpan.Zero);

        var header = keys.Authorization(new Uri("https://fcm.googleapis.com/fcm/send/abc123"), "https://notes.example.co.uk", now);

        Assert.StartsWith("vapid t=", header);
        Assert.EndsWith($", k={keys.PublicKey}", header);
        var token = header["vapid t=".Length..header.IndexOf(", k=", StringComparison.Ordinal)];
        var parts = token.Split('.');

        var claims = JsonDocument.Parse(Base64Url.Decode(parts[1])).RootElement;
        Assert.Equal("https://fcm.googleapis.com", claims.GetProperty("aud").GetString());
        Assert.Equal("https://notes.example.co.uk", claims.GetProperty("sub").GetString());
        Assert.Equal(now.AddHours(12).ToUnixTimeSeconds(), claims.GetProperty("exp").GetInt64());

        var publicKey = Base64Url.Decode(keys.PublicKey);
        using var verifier = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = publicKey[1..33], Y = publicKey[33..] },
        });
        Assert.True(verifier.VerifyData(Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"), Base64Url.Decode(parts[2]), HashAlgorithmName.SHA256));
    }

    [Fact]
    public async Task Signing_key_is_made_once_and_then_reused()
    {
        using var world = new TestWorld();

        var first = await VapidKeys.LoadOrCreateAsync(world.Meta);
        var again = await VapidKeys.LoadOrCreateAsync(world.Meta);

        Assert.Equal(first.PublicKey, again.PublicKey);
        Assert.Equal(65, Base64Url.Decode(first.PublicKey).Length);
    }
}
