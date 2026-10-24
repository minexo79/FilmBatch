using FilmBatch.App.Services.Loupe;

namespace FilmBatch.App.Tests;

/// <summary>窗口有效性規則（<see cref="RegionView.IsCurrent"/>）：決定舊窗口能不能留在畫面上。</summary>
public sealed class RegionViewTests
{
    private static readonly PixelWindow Win = new(0, 0, 256, 256);

    private static RegionView Region(string photo = "a.nef", int w = 4000, int h = 3000, long key = 1) =>
        new(photo, false, w, h, Win, key);

    [Fact]
    public void Same_photo_frame_and_settings_is_current()
    {
        Assert.True(Region().IsCurrent("a.nef", (4000, 3000), 1, drafting: false));
    }

    [Fact]
    public void Different_photo_is_not_current()
    {
        Assert.False(Region().IsCurrent("b.nef", (4000, 3000), 1, drafting: false));
    }

    [Fact]
    public void Different_frame_size_is_not_current()
    {
        Assert.False(Region().IsCurrent("a.nef", (4000, 2999), 1, drafting: false));
    }

    [Fact]
    public void Different_settings_is_not_current_when_not_drafting()
    {
        Assert.False(Region().IsCurrent("a.nef", (4000, 3000), 2, drafting: false));
    }

    [Fact]
    public void Different_settings_stays_current_while_drafting()
    {
        Assert.True(Region().IsCurrent("a.nef", (4000, 3000), 2, drafting: true));
    }
}
