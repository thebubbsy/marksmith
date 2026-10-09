namespace MarkSmith.Services.Mermaid;

/// <summary>
/// Word export's "Arrowhead style" option (<c>AppSettings.ConnectorArrowhead</c>), applied the same
/// way by every native-shape renderer. "default" keeps each diagram's own arrowheads. Any other
/// style redraws the arrowheads a diagram already has in that shape, and "none" removes them.
/// It never adds a head to a plain line (<c>---</c>), and it leaves diamonds and circles alone,
/// because in class and ER diagrams those mean composition or aggregation, not direction.
/// </summary>
/// <remarks>
/// The option used to be read only by the rarely used fallback renderer, and as "fill in the head
/// where the source has none": choosing Triangle put a second head on the start of every ordinary
/// arrow and arrows on plain lines, and None did nothing.
/// </remarks>
public static class ArrowheadStyle
{
    /// <summary>The stored values in the order the side panel lists them.</summary>
    public static readonly IReadOnlyList<string> Values = ["default", "triangle", "open", "stealth", "diamond", "oval", "none"];

    public static bool IsDefault(string? setting) =>
        string.IsNullOrWhiteSpace(setting) || setting.Trim().Equals("default", StringComparison.OrdinalIgnoreCase);

    /// <summary>The head to draw where the diagram asked for <paramref name="head"/>.</summary>
    public static ArrowHead Apply(ArrowHead head, string? setting)
    {
        if (IsDefault(setting) || !IsDirectional(head)) return head;
        return setting!.Trim().ToLowerInvariant() switch
        {
            "triangle" => ArrowHead.Triangle,
            "open" => ArrowHead.Open,
            "stealth" => ArrowHead.Stealth,
            "diamond" => ArrowHead.Diamond,
            "oval" => ArrowHead.Oval,
            "none" => ArrowHead.None,
            _ => head, // an unknown stored value changes nothing
        };
    }

    /// <summary>Restyles every connector of a diagram in place.</summary>
    public static void Apply(MDiagram diagram, string? setting)
    {
        if (IsDefault(setting)) return;
        foreach (var c in diagram.Connectors)
        {
            c.StartHead = Apply(c.StartHead, setting);
            c.EndHead = Apply(c.EndHead, setting);
        }
    }

    /// <summary>Heads that show which way a line goes. Diamonds and circles carry other meanings.</summary>
    private static bool IsDirectional(ArrowHead head) => head is ArrowHead.Triangle or ArrowHead.Open or ArrowHead.Stealth;
}
