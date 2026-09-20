using Bitchat.Windows.Voice;
using Xunit;

namespace BitchatWindows.Tests;

public sealed class VoiceTests
{
    [Fact]
    public void VoiceBurstPacket_roundtrip()
    {
        var burstId = VoiceBurstPacket.MakeBurstId();
        var frames = new List<byte[]> { new byte[] { 1, 2, 3 }, new byte[] { 4, 5, 6 } };
        var packet = VoiceBurstPacket.Create(burstId, 5, new VoiceBurstPacket.Kind.Frames(frames));
        Assert.NotNull(packet);
        var encoded = packet!.Encode();
        var decoded = VoiceBurstPacket.Decode(encoded);
        Assert.NotNull(decoded);
        Assert.Equal(5, decoded!.Sequence);
        var fr = Assert.IsType<VoiceBurstPacket.Kind.Frames>(decoded.Value);
        Assert.Equal(2, fr.FrameList.Count);
    }

    [Fact]
    public void Packetizer_budget_respected()
    {
        var packetizer = new VoiceBurstPacketizer(VoiceBurstPacket.MakeBurstId());
        var total = 0;
        for (var i = 0; i < 100; i++)
        {
            var frame = new byte[50];
            var packets = packetizer.Add(frame);
            foreach (var p in packets)
            {
                total++;
                Assert.True(p.Encode().Length <= VoiceBurstPacket.HeaderSize + VoiceBurstPacket.MaxContentBytes + 2);
            }
        }
        foreach (var p in packetizer.Flush()) total++;
        Assert.True(total > 0);
    }

    [Fact]
    public void AdtsFramer_produces_valid_header()
    {
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        var framed = AdtsFramer.Frame(payload);
        Assert.Equal(12, framed.Length);
        Assert.Equal(0xFF, framed[0]);
        Assert.Equal(0xF1, framed[1]);
        Assert.Equal(0x60, framed[2]);
    }
}
