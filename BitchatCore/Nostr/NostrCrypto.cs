using System.IO;

using System.Numerics;
using Org.BouncyCastle.Asn1.Sec;
using BigInteger = Org.BouncyCastle.Math.BigInteger;

namespace Bitchat.Windows.Nostr;

/// <summary>BIP-340 Schnorr signatures over secp256k1 (BouncyCastle EC primitives).</summary>
public static class NostrCrypto
{
    private static readonly BigInteger P = SecNamedCurves.GetByName("secp256k1").Curve.Field.Characteristic;
    private static readonly BigInteger N = SecNamedCurves.GetByName("secp256k1").N;
    private static readonly Org.BouncyCastle.Math.EC.ECPoint G = SecNamedCurves.GetByName("secp256k1").G;

    public static bool ValidPrivateKey(byte[] privateKey32)
    {
        var d = new BigInteger(1, privateKey32);
        return d.SignValue >= 1 && d.CompareTo(N) < 0;
    }

    /// <summary>X-only public key (32 bytes, big-endian).</summary>
    public static byte[] PublicKeyFromPrivate(byte[] privateKey32)
    {
        var d = new BigInteger(1, privateKey32);
        if (!ValidPrivateKey(privateKey32)) throw new ArgumentException("invalid private key");
        var point = G.Multiply(d).Normalize();
        return BigIntTo32(point.AffineXCoord.ToBigInteger());
    }

    /// <summary>BIP-340 deterministic signing (aux randomness must be 32 fresh bytes).</summary>
    public static byte[] Sign(byte[] message32, byte[] privateKey32, byte[] auxRand32)
    {
        var d0 = new BigInteger(1, privateKey32);
        if (!ValidPrivateKey(privateKey32)) throw new ArgumentException("invalid private key");

        var pubPoint = G.Multiply(d0).Normalize();
        var px = pubPoint.AffineXCoord.ToBigInteger();
        var py = pubPoint.AffineYCoord.ToBigInteger();

        // d = d0 if P.y is even, else n - d0
        var d = IsEven(py) ? d0 : N.Subtract(d0);

        var t = Xor(BigIntTo32(d), TaggedHash("BIP0340/aux", auxRand32));
        var rand = new BigInteger(1, TaggedHash("BIP0340/nonce", Concat(t, BigIntTo32(px), message32)))
            .Mod(N);
        if (rand.SignValue == 0) throw new InvalidOperationException("nonce overflow");

        var rPoint = G.Multiply(rand).Normalize();
        var rx = rPoint.AffineXCoord.ToBigInteger();
        var ry = rPoint.AffineYCoord.ToBigInteger();
        var k = IsEven(ry) ? rand : N.Subtract(rand);

        var e = new BigInteger(1, TaggedHash("BIP0340/challenge",
            Concat(BigIntTo32(rx), BigIntTo32(px), message32))).Mod(N);

        var sig = new byte[64];
        Array.Copy(BigIntTo32(rx), 0, sig, 0, 32);
        var tail = k.Add(e.Multiply(d)).Mod(N);
        Array.Copy(BigIntTo32(tail), 0, sig, 32, 32);
        return sig;
    }

    public static bool Verify(byte[] message32, byte[] publicKeyX32, byte[] signature64)
    {
        try
        {
            if (publicKeyX32.Length != 32 || signature64.Length != 64) return false;

            var px = new BigInteger(1, publicKeyX32);
            var rx = new BigInteger(1, signature64.AsSpan(0, 32).ToArray());
            var s = new BigInteger(1, signature64.AsSpan(32, 32).ToArray());

            if (px.CompareTo(P) >= 0 || rx.CompareTo(P) >= 0 || s.CompareTo(N) >= 0) return false;

            var pointP = LiftX(px);
            if (pointP == null) return false;

            var e = new BigInteger(1, TaggedHash("BIP0340/challenge",
                Concat(BigIntTo32(rx), BigIntTo32(px), message32))).Mod(N);

            // R = s·G - e·P
            var rPoint = G.Multiply(s).Add(pointP.Multiply(N.Subtract(e))).Normalize();
            if (rPoint.IsInfinity) return false;
            if (!IsEven(rPoint.AffineYCoord.ToBigInteger())) return false;
            return rPoint.AffineXCoord.ToBigInteger().CompareTo(rx) == 0;
        }
        catch
        {
            return false;
        }
    }

    private static Org.BouncyCastle.Math.EC.ECPoint? LiftX(BigInteger x)
    {
        if (x.CompareTo(P) >= 0) return null;
        var c = x.ModPow(BigInteger.Three, P).Add(BigInteger.ValueOf(7)).Mod(P);
        var y = c.ModPow(P.Add(BigInteger.One).ShiftRight(2), P);
        if (y.ModPow(BigInteger.Two, P).CompareTo(c) != 0) return null;
        if (!IsEven(y)) y = P.Subtract(y);
        return SecNamedCurves.GetByName("secp256k1").Curve.CreatePoint(x, y);
    }

    private static bool IsEven(BigInteger v) => !v.TestBit(0);

    private static byte[] TaggedHash(string tag, params byte[][] messages)
    {
        var tagHash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(tag));
        using var sha = System.Security.Cryptography.SHA256.Create();
        sha.TransformBlock(tagHash, 0, 32, null, 0);
        sha.TransformBlock(tagHash, 0, 32, null, 0);
        foreach (var m in messages)
            sha.TransformBlock(m, 0, m.Length, null, 0);
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return sha.Hash!;
    }

    private static byte[] Xor(byte[] a, byte[] b)
    {
        var r = new byte[a.Length];
        for (var i = 0; i < a.Length; i++) r[i] = (byte)(a[i] ^ b[i]);
        return r;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var total = parts.Sum(p => p.Length);
        var result = new byte[total];
        var offset = 0;
        foreach (var p in parts)
        {
            Array.Copy(p, 0, result, offset, p.Length);
            offset += p.Length;
        }
        return result;
    }

    private static byte[] BigIntTo32(BigInteger value)
    {
        var raw = value.ToByteArrayUnsigned(); // big-endian, no leading zeros
        var result = new byte[32];
        var copy = Math.Min(raw.Length, 32);
        Array.Copy(raw, raw.Length - copy, result, 32 - copy, copy);
        return result;
    }
}
