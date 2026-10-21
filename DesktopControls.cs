using System.Drawing.Drawing2D;
namespace NetScope;
static class DesktopStyle
{
    public static readonly Color Accent = Color.FromArgb(0, 157, 218), Ink = Color.FromArgb(35, 49, 65), Muted = Color.FromArgb(117, 126, 137), Chrome = Color.FromArgb(244, 246, 248), Border = Color.FromArgb(224, 228, 233), Lime = Color.FromArgb(211, 225, 61);
    public static readonly Font Small = new("Segoe UI", 9.5f), Body = new("Segoe UI", 10.5f), Heading = new("Segoe UI", 11.5f);
    public static ToolStrip Strip() => new() { Dock = DockStyle.Fill, GripStyle = ToolStripGripStyle.Hidden, AutoSize = false, Padding = new(7, 3, 7, 3), BackColor = Chrome, ForeColor = Ink, Font = Small, Renderer = new FlatStripRenderer() };
    public static LinkLabel Link(string text) => new() { Text = text, AutoSize = true, LinkColor = Accent, ActiveLinkColor = Color.FromArgb(0, 111, 171), DisabledLinkColor = Color.FromArgb(170, 176, 183), LinkBehavior = LinkBehavior.HoverUnderline, Font = Small, Margin = new(0, 5, 14, 5) };
}
sealed class FlatStripRenderer : ToolStripProfessionalRenderer
{
    public FlatStripRenderer() : base(new Colors()) { RoundedEdges = false; }
    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e) { using var p = new Pen(DesktopStyle.Border); e.Graphics.DrawLine(p, 0, e.ToolStrip.Height - 1, e.ToolStrip.Width, e.ToolStrip.Height - 1); }
    protected override void OnRenderButtonBackground(ToolStripItemRenderEventArgs e)
    {
        if (e.Item is ToolStripButton b && b.Checked) { using var brush = new SolidBrush(Equals(b.Tag, "navigation") ? DesktopStyle.Lime : DesktopStyle.Accent); e.Graphics.FillRectangle(brush, new Rectangle(Point.Empty, e.Item.Size)); }
        else if (e.Item.Selected) { using var brush = new SolidBrush(Color.FromArgb(226, 236, 243)); e.Graphics.FillRectangle(brush, new Rectangle(Point.Empty, e.Item.Size)); }
    }
    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e) { if (e.Item is ToolStripButton b && b.Checked) e.TextColor = Equals(b.Tag, "navigation") ? DesktopStyle.Ink : Color.White; base.OnRenderItemText(e); }
    sealed class Colors : ProfessionalColorTable
    {
        public override Color ToolStripGradientBegin => DesktopStyle.Chrome;
        public override Color ToolStripGradientMiddle => DesktopStyle.Chrome;
        public override Color ToolStripGradientEnd => DesktopStyle.Chrome;
        public override Color MenuItemSelected => Color.FromArgb(226, 236, 243);
        public override Color MenuItemBorder => Color.Transparent;
        public override Color ButtonSelectedBorder => Color.Transparent;
        public override Color ToolStripBorder => DesktopStyle.Border;
    }
}
sealed class WorkspaceTabs : TabControl
{
    public WorkspaceTabs() { Appearance = TabAppearance.FlatButtons; ItemSize = new(1, 1); SizeMode = TabSizeMode.Fixed; TabStop = false; }
    public override Rectangle DisplayRectangle => ClientRectangle;
    protected override void WndProc(ref Message m) { if (m.Msg == 0x1328) { m.Result = IntPtr.Zero; return; } base.WndProc(ref m); }
}
sealed class CollapsibleSection : FlowLayoutPanel
{
    readonly Button heading;
    public FlowLayoutPanel Body { get; } = new() { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new(0), Padding = new(0, 0, 0, 3) };
    public bool Collapsed { get; private set; }
    public string Title { get; }
    public CollapsibleSection(string title)
    {
        Title = title; AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink; FlowDirection = FlowDirection.TopDown; WrapContents = false; Margin = new(0, 9, 0, 2);
        heading = new Button { Height = 27, Width = 360, TextAlign = ContentAlignment.MiddleLeft, Font = DesktopStyle.Small, FlatStyle = FlatStyle.Flat, ForeColor = DesktopStyle.Ink, BackColor = Color.White, Margin = new(0), Padding = new(1, 0, 0, 0), Cursor = Cursors.Hand, TabStop = true };
        heading.FlatAppearance.BorderSize = 0; heading.FlatAppearance.MouseOverBackColor = DesktopStyle.Chrome; heading.Click += (_, _) => SetCollapsed(!Collapsed);
        Controls.Add(heading); Controls.Add(Body); SetCollapsed(false);
    }
    public void SetCollapsed(bool collapsed) { Collapsed = collapsed; heading.Text = (collapsed ? "▸  " : "▾  ") + Title; Body.Visible = !collapsed; }
    public void Fit(int width) { heading.Width = width; Body.MinimumSize = new(width, 0); Body.MaximumSize = new(width, 0); }
}
