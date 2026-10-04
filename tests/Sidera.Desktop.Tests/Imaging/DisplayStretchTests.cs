using Sidera.Core.Devices;
using Sidera.Desktop.Imaging;

namespace Sidera.Desktop.Tests.Imaging;

public class DisplayStretchTests
{
    [Fact]
    public void ToGray8_StretchesBackgroundToBlackAndBrightPixelsToWhite_WithoutChangingTheFrame()
    {
        var pixels = new ushort[100];
        Array.Fill(pixels, (ushort)500);
        pixels[42] = 60_000;
        var frame = new CameraFrame(10, 10, pixels, TimeSpan.FromSeconds(1));

        var gray = DisplayStretch.ToGray8(frame);

        Assert.Equal(100, gray.Length);
        Assert.Equal(0, gray[0]);
        Assert.Equal(255, gray[42]);
        Assert.Equal((ushort)500, frame.Pixels.Span[0]);
        Assert.Equal((ushort)60_000, frame.Pixels.Span[42]);
    }

    [Fact]
    public void ToGray8_HandlesFlatFrame()
    {
        var frame = new CameraFrame(2, 2, new ushort[4], TimeSpan.FromSeconds(1));

        Assert.All(DisplayStretch.ToGray8(frame), b => Assert.Equal(0, b));
    }
}
