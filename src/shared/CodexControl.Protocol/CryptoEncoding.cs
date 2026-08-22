using System.Security.Cryptography;
using System.Text;

namespace CodexControl.Protocol;

public static class Base64Url
{
    public static string Encode(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Decode(string value)
    {
        var base64 = value.Replace('-', '+').Replace('_', '/');
        base64 += (base64.Length % 4) switch
        {
            2 => "==",
            3 => "=",
            0 => string.Empty,
            _ => throw new FormatException("Invalid base64url length."),
        };
        return Convert.FromBase64String(base64);
    }
}

public static class P256Keys
{
    public static string ExportPublicKey(ECDsa key)
    {
        var parameters = key.ExportParameters(includePrivateParameters: false);
        if (parameters.Q.X is not { Length: 32 } x || parameters.Q.Y is not { Length: 32 } y)
        {
            throw new CryptographicException("Expected a P-256 public key.");
        }

        var bytes = new byte[65];
        bytes[0] = 0x04;
        x.CopyTo(bytes, 1);
        y.CopyTo(bytes, 33);
        return Base64Url.Encode(bytes);
    }

    public static ECDsa ImportPublicKey(string encoded)
    {
        var bytes = Base64Url.Decode(encoded);
        if (bytes.Length != 65 || bytes[0] != 0x04)
        {
            throw new CryptographicException("Public key must be an uncompressed P-256 SEC1 point.");
        }

        var parameters = new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint
            {
                X = bytes.AsSpan(1, 32).ToArray(),
                Y = bytes.AsSpan(33, 32).ToArray(),
            },
        };
        return ECDsa.Create(parameters);
    }

    public static string Sign(ECDsa key, string canonicalPayload) =>
        Base64Url.Encode(key.SignData(
            Encoding.UTF8.GetBytes(canonicalPayload),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

    public static bool Verify(ECDsa key, string canonicalPayload, string signature)
    {
        try
        {
            return key.VerifyData(
                Encoding.UTF8.GetBytes(canonicalPayload),
                Base64Url.Decode(signature),
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

public static class PairingProofCanonicalPayload
{
    public static string Build(
        string code,
        string controllerId,
        string publicKey,
        string proofNonce) =>
        string.Join(
            '\n',
            "codex-control-pairing-proof-v1",
            code,
            controllerId,
            publicKey,
            proofNonce);
}
