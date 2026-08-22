using System;

namespace MarkSmith.Models;

/// <summary>
/// Options for configuring headless document PNG snapshot rasterization.
/// </summary>
public class ImageRenderOptions
{
    /// <summary>
    /// Logical width of the output image in pixels (before scale multiplier). Default is 1200.
    /// </summary>
    public int Width { get; set; } = 1200;

    /// <summary>
    /// Logical height of the output image in pixels. Set to 0 for auto-height based on content. Default is 0.
    /// </summary>
    public int Height { get; set; } = 0;

    /// <summary>
    /// High-DPI device scale multiplier (e.g. 2.0 for Retina/2x rendering). Default is 2.0.
    /// </summary>
    public double Scale { get; set; } = 2.0;

    /// <summary>
    /// PNG compression quality level (1-100). Default is 100.
    /// </summary>
    public int Quality { get; set; } = 100;

    /// <summary>
    /// Name of the theme to apply for background and typography colors. Default is "GitHub Light".
    /// </summary>
    public string Theme { get; set; } = "GitHub Light";

    public ImageRenderOptions()
    {
    }

    public ImageRenderOptions(int width, int height, double scale, int quality = 100, string theme = "GitHub Light")
    {
        Width = width;
        Height = height;
        Scale = scale;
        Quality = quality;
        Theme = theme;
    }
}