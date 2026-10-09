using MarkSmith.Services.MindMap;
using Xunit;
using Box = MarkSmith.Services.MindMap.PreviewCardPlacement.Box;
using Corner = MarkSmith.Services.MindMap.PreviewCardPlacement.Corner;

namespace MarkSmith.Tests;

// The Galaxy preview card used to sit bottom-right whatever was there, covering a hovered node
// in that corner (found with a real mouse in run #47). Sizes match the window that showed it:
// a 1090 x 620 canvas, a 400 x 210 card, overlays down to y=130 and a legend from y=572.
public class PreviewCardPlacementTests
{
    private const double W = 1090, H = 620, CardW = 400, CardH = 210, Top = 130, Bottom = 60;

    private static Corner Choose(Box node) => PreviewCardPlacement.Choose(node, W, H, CardW, CardH, Top, Bottom);

    [Fact]
    public void Bottom_right_when_the_node_is_elsewhere()
    {
        Assert.Equal(Corner.BottomRight, Choose(new Box(230, 315, 155, 40)));
    }

    [Fact]
    public void Moves_up_when_the_node_is_in_the_bottom_right_corner()
    {
        // "Kickoff Deck" in the sample map.
        var node = new Box(720, 450, 150, 36);
        var corner = Choose(node);
        Assert.Equal(Corner.TopRight, corner);
        var card = PreviewCardPlacement.At(corner, W, H, CardW, CardH, Top, Bottom);
        Assert.Equal(0, card.OverlapArea(node.Inflate(PreviewCardPlacement.NodeClearance)));
    }

    [Fact]
    public void Goes_left_when_both_right_corners_are_taken()
    {
        // A tall node (zoomed in) spanning the whole right side.
        Assert.Equal(Corner.TopLeft, Choose(new Box(700, 120, 300, 480)));
    }

    [Fact]
    public void Picks_the_least_covered_corner_when_none_is_free()
    {
        // Zoomed right in: the node fills the canvas. Any corner covers it, so take the first
        // with the smallest overlap rather than giving up.
        var corner = Choose(new Box(0, 0, W, H));
        Assert.Equal(Corner.BottomRight, corner);
    }

    [Fact]
    public void Stays_between_the_overlays_and_the_legend()
    {
        var bottom = PreviewCardPlacement.At(Corner.BottomRight, W, H, CardW, CardH, Top, Bottom);
        Assert.Equal(H - Bottom - CardH, bottom.Y);
        Assert.Equal(W - PreviewCardPlacement.Edge - CardW, bottom.X);

        var top = PreviewCardPlacement.At(Corner.TopRight, W, H, CardW, CardH, Top, Bottom);
        Assert.Equal(Top, top.Y);

        // Without overlays the card keeps its usual 24px margin.
        var plain = PreviewCardPlacement.At(Corner.TopLeft, W, H, CardW, CardH, 0, 0);
        Assert.Equal(PreviewCardPlacement.Edge, plain.X);
        Assert.Equal(PreviewCardPlacement.Edge, plain.Y);
    }

    [Fact]
    public void A_short_window_keeps_the_card_on_screen()
    {
        var card = PreviewCardPlacement.At(Corner.TopRight, W, 300, CardW, CardH, Top, Bottom);
        Assert.True(card.Bottom <= 300 - PreviewCardPlacement.Edge + 0.001);
        Assert.True(card.Y >= 0);
    }
}
