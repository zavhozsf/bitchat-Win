using Bitchat.Windows;
using Xunit;

namespace BitchatWindows.Tests;

public sealed class GeoTests
{
    [Fact]
    public void Encode_known_values()
    {
        // Classic geohash.org example
        Assert.Equal("ezs42", GeoUtils.Encode(42.6, -5.6, 5));
        // San Francisco (5 chars)
        Assert.Equal("9q8yy", GeoUtils.Encode(37.7749, -122.4194, 5));
        // Moscow (5 chars)
        Assert.Equal("ucftp", GeoUtils.Encode(55.7500, 37.6167, 5));
    }
}
