using System.IO;

namespace Bitchat.Windows.Protocol;

public static class BitchatConstants
{
    public const byte ProtocolVersion = 1;

    public const byte MessageTtlHops = 7;
    public const byte SyncTtlHops = 0;

    public static readonly byte[] BroadcastRecipient = { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF };

    public static readonly Guid GattServiceUuid = new("F47B5E2D-4A9E-4C5A-9B3F-8E1D2C3A4B5C");
    public static readonly Guid GattCharacteristicUuid = new("A1B2C3D4-E5F6-4A5B-8C9D-0E1F2A3B4C5D");

    public const int HeaderSizeV1 = 14;
    public const int SenderIdSize = 8;
    public const int RecipientIdSize = 8;
    public const int SignatureSize = 64;

    // MessagePadding block sizes (iOS compatible)
    public static readonly int[] PaddingBlockSizes = { 256, 512, 1024, 2048 };

    // Fragmentation (iOS compatible)
    public const int FragmentSizeThreshold = 512;
    public const int MaxFragmentSize = 469;
    public const int FragmentHeaderSize = 13;
    public static readonly TimeSpan FragmentTimeout = TimeSpan.FromSeconds(30);

    // Compression (iOS compatible)
    public const int CompressionThresholdBytes = 100;
    public const int MaxPayloadLength = 10_485_760;
    public const double MaxCompressionRatio = 50_000.0;

    // Noise (iOS compatible)
    public const int MaxNoisePayloadSize = 256;

    // Security
    public static readonly TimeSpan MessageTimeout = TimeSpan.FromMinutes(5);
    public const int MaxProcessedMessages = 10_000;
    public static readonly TimeSpan AnnounceClockSkewTolerance = TimeSpan.FromMinutes(10);

    public const int MaxNicknameLength = 15;
}
