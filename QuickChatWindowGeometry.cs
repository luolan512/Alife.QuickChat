using System;
using System.Drawing;

namespace Marisa.QuickChat;

// BaseBounds includes the collapsed sidebar rail. Expanding the sidebar adds
// only panel-minus-rail; content size is never reconstructed from native events.
public sealed class QuickChatWindowGeometry
{
    // #app has 6 px inset and 1 px border on each side.
    public const int FrameWidth = 14;
    public static int DefaultBaseWidth(int chatWidth, bool groupChatEnabled, int railWidth)
        => chatWidth + (groupChatEnabled ? railWidth : 0) + FrameWidth;

    public Rectangle BaseBounds { get; private set; }
    public bool SidebarExpanded { get; private set; }
    public int SidebarExtra => SidebarExpanded ? Math.Max(0, PanelWidth - RailWidth) : 0;
    public int RailWidth { get; }
    public int PanelWidth { get; }
    public Rectangle WindowBounds => new(BaseBounds.X - SidebarExtra, BaseBounds.Y,
        BaseBounds.Width + SidebarExtra, BaseBounds.Height);

    public QuickChatWindowGeometry(Rectangle bounds, int railWidth, int panelWidth)
    {
        RailWidth = railWidth;
        PanelWidth = panelWidth;
        SetBaseBounds(bounds);
    }

    public void SetBaseBounds(Rectangle bounds) => BaseBounds = new(bounds.X, bounds.Y,
        Math.Max(240, bounds.Width), Math.Max(52, bounds.Height));
    public void SetSize(int width, int height) => SetBaseBounds(new(BaseBounds.Location, new Size(width, height)));
    public void SetHeight(int height) => SetSize(BaseBounds.Width, height);
    public void SetSidebar(bool expanded) => SidebarExpanded = expanded;
    public void MoveTo(Point location) => SetBaseBounds(new(location, BaseBounds.Size));
    public void SetPhysicalBounds(Rectangle bounds) => SetBaseBounds(new(bounds.X + SidebarExtra,
        bounds.Y, bounds.Width - SidebarExtra, bounds.Height));
}
