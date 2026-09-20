using System.Text;

namespace Bitchat.Windows.Protocol;

/// <summary>
/// AuthenticatedPeerState: Noise payload type 0x21.
/// Sent after handshake establishment so the peer knows we support private media.
/// Wire format: [version=0x01][TLV 0x01: capabilities][TLV 0x02: Ed25519 pubkey 32B].
/// </summary>
public sealed class AuthenticatedPeerState
{
    private const byte Version = 0x01;
    private const byte CapabilitiesTlv = 0x01;
    private const byte SigningPublicKeyTlv = 0x02;

    public PeerCapabilities Capabilities { get; }
    public byte[] SigningPublicKey { get; }

    public AuthenticatedPeerState(PeerCapabilities capabilities, byte[] signingPublicKey)
    {
        Capabilities = capabilities;
        SigningPublicKey = signingPublicKey;
    }

    public byte[] Encode()
    {
        var capBytes = Capabilities.Encode();
        var result = new byte[1 + 2 + capBytes.Length + 2 + 32];
        var off = 0;
        result[off++] = Version;
        result[off++] = CapabilitiesTlv;
        result[off++] = (byte)capBytes.Length;
        Array.Copy(capBytes, 0, result, off, capBytes.Length); off += capBytes.Length;
        result[off++] = SigningPublicKeyTlv;
        result[off++] = 32;
        Array.Copy(SigningPublicKey, 0, result, off, 32);
        return result;
    }

    public static AuthenticatedPeerState? Decode(byte[] data)
    {
        try
        {
            if (data.Length < 3 || data[0] != Version) return null;
            var off = 1;
            PeerCapabilities? caps = null;
            byte[]? signingKey = null;

            while (off + 2 <= data.Length)
            {
                var type = data[off++];
                var len = data[off++];
                if (off + len > data.Length) return null;
                var value = data[off..(off + len)];
                off += len;

                if (type == CapabilitiesTlv && len is >= 1 and <= 8 && caps == null)
                    caps = PeerCapabilities.Decode(value);
                else if (type == SigningPublicKeyTlv && len == 32 && signingKey == null)
                    signingKey = value;
            }
            if (caps == null || signingKey == null || signingKey.Length != 32) return null;
            return new AuthenticatedPeerState(caps, signingKey);
        }
        catch
        {
            return null;
        }
    }
}
