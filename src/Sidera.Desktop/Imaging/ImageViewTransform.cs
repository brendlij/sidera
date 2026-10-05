using System;

namespace Sidera.Desktop.Imaging;

/// <summary>
/// Where an image is in its view: a scale (view pixels per image pixel) and the position of the image's top left corner in the view. Pure arithmetic, so that zooming around the pointer, panning,
/// fitting and 1:1 can be tested without a window. A new transform is made by each operation; the image itself is never resampled for a zoom step: the picture is made once at its own size
/// and the view only scales it.
/// </summary>
public readonly record struct ImageViewTransform(double Scale, double OffsetX, double OffsetY)
{
    /// <summary>The smallest zoom: the whole image of a big camera still fits a small view (about 1/100).</summary>
    public const double MinScale = 0.01;

    /// <summary>The largest zoom: 32 view pixels for one image pixel, where single pixels are clearly visible and nothing is gained by more.</summary>
    public const double MaxScale = 32;

    /// <summary>The image fitted into the view, centered, with a margin of <paramref name="margin"/> view pixels; never zoomed in beyond 1:1.</summary>
    public static ImageViewTransform Fit(double imageWidth, double imageHeight, double viewWidth, double viewHeight, double margin = 0)
    {
        if (imageWidth <= 0 || imageHeight <= 0 || viewWidth <= 0 || viewHeight <= 0)
        {
            return new ImageViewTransform(1, 0, 0);
        }

        var scale = Math.Min((viewWidth - 2 * margin) / imageWidth, (viewHeight - 2 * margin) / imageHeight);
        scale = Math.Clamp(scale, MinScale, 1);
        return Centered(scale, imageWidth, imageHeight, viewWidth, viewHeight);
    }

    /// <summary>One image pixel for one view pixel, centered.</summary>
    public static ImageViewTransform ActualSize(double imageWidth, double imageHeight, double viewWidth, double viewHeight) =>
        Centered(1, imageWidth, imageHeight, viewWidth, viewHeight);

    /// <summary>Zooms by <paramref name="factor"/> around the view point (<paramref name="x"/>, <paramref name="y"/>): the image point under it stays under it.</summary>
    public ImageViewTransform ZoomAt(double x, double y, double factor)
    {
        if (!double.IsFinite(factor) || factor <= 0)
        {
            return this;
        }

        var scale = Math.Clamp(Scale * factor, MinScale, MaxScale);
        if (scale == Scale)
        {
            return this;
        }

        var ratio = scale / Scale;
        return new ImageViewTransform(scale, x - (x - OffsetX) * ratio, y - (y - OffsetY) * ratio);
    }

    /// <summary>Moves the image by a number of view pixels.</summary>
    public ImageViewTransform Pan(double dx, double dy) => this with { OffsetX = OffsetX + dx, OffsetY = OffsetY + dy };

    /// <summary>
    /// Keeps at least <paramref name="keepVisible"/> view pixels of the image in the view on each axis, so that it cannot be dragged out of reach.
    /// </summary>
    public ImageViewTransform Clamp(double imageWidth, double imageHeight, double viewWidth, double viewHeight, double keepVisible = 48)
    {
        var width = imageWidth * Scale;
        var height = imageHeight * Scale;
        var x = Math.Clamp(OffsetX, keepVisible - width, viewWidth - keepVisible);
        var y = Math.Clamp(OffsetY, keepVisible - height, viewHeight - keepVisible);
        return this with { OffsetX = x, OffsetY = y };
    }

    /// <summary>The point of the image (in image pixels, the top left corner of the first pixel is 0, 0) at a point of the view.</summary>
    public (double X, double Y) ToImage(double viewX, double viewY) => ((viewX - OffsetX) / Scale, (viewY - OffsetY) / Scale);

    /// <summary>The point of the view at a point of the image.</summary>
    public (double X, double Y) ToView(double imageX, double imageY) => (OffsetX + imageX * Scale, OffsetY + imageY * Scale);

    /// <summary>Whether the image is shown at 1:1.</summary>
    public bool IsActualSize => Math.Abs(Scale - 1) < 1e-9;

    private static ImageViewTransform Centered(double scale, double imageWidth, double imageHeight, double viewWidth, double viewHeight) =>
        new(scale, (viewWidth - imageWidth * scale) / 2, (viewHeight - imageHeight * scale) / 2);
}
