using Godot;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.PauseMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.addons.mega_text;

namespace Sts2LanCoop.Mod;

internal sealed class UiAction
{
    public NPauseMenuButton Node { get; }
    public string Text { get => Node.GetNode<MegaLabel>("Label").Text; set => Node.GetNode<MegaLabel>("Label").SetTextAutoSize(value); }
    public bool Disabled { get => !Node.IsEnabled; set => Node.SetEnabled(!value); }
    public UiAction(Node parent, string text, Action action, string? name = null)
    {
        Node = GD.Load<PackedScene>("res://scenes/pause_menu/pause_menu_button.tscn").Instantiate<NPauseMenuButton>();
        Node.Name = name ?? "Action"; Node.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter;
        parent.AddChild(Node); Text = text;
        Node.Connect(NClickableControl.SignalName.Released, Callable.From<Godot.Control>(_ => action()));
    }
}

internal static class NativeUi
{
    public static LineEdit Input(Node parent, string value, int max = 160)
    {
        var input = new LineEdit { Text = value, MaxLength = max, CustomMinimumSize = new Vector2(0, 54) };
        var normal = new StyleBoxFlat { BgColor = new Color("ead5ac"), BorderColor = new Color("775a35"),
            BorderWidthLeft = 2, BorderWidthRight = 2, BorderWidthTop = 2, BorderWidthBottom = 2,
            ContentMarginLeft = 14, ContentMarginRight = 14, ContentMarginTop = 8, ContentMarginBottom = 8 };
        var focus = (StyleBoxFlat)normal.Duplicate(); focus.BorderColor = new Color("efc851");
        input.AddThemeStyleboxOverride("normal", normal); input.AddThemeStyleboxOverride("focus", focus);
        input.AddThemeColorOverride("font_color", new Color("382410"));
        input.AddThemeFontSizeOverride("font_size", 26); parent.AddChild(input); return input;
    }
    private static readonly List<NativePage> Pages = [];
    public static void Track(NativePage page)
    { Pages.Add(page); page.TreeExiting += () => Pages.Remove(page); }
    public static bool HandleInput(InputEvent e)
    {
        if (Pages.Count == 0) return false;
        if (!e.IsActionPressed("ui_cancel") && e is not InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape }) return false;
        var top = Pages.Last(); if (!top.Busy) top.CloseRequested?.Invoke();
        top.GetViewport().SetInputAsHandled(); return true;
    }
    public static Label Label(Node parent, string text, int size = 24, bool heading = false)
    {
        var label = new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart,
            MouseFilter = Control.MouseFilterEnum.Ignore, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        label.AddThemeFontOverride("font", GD.Load<Font>(heading ? "res://themes/spectral_bold_shared.tres" : "res://themes/kreon_regular_shared.tres"));
        label.AddThemeFontSizeOverride("font_size", size);
        label.AddThemeColorOverride("font_color", heading ? new Color("efc851") : new Color("382410"));
        label.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, .75f));
        label.AddThemeConstantOverride("outline_size", heading ? 3 : 0);
        parent.AddChild(label); return label;
    }
    public static void FocusLoop(Node root)
    {
        var controls = Walk(root).OfType<Control>().Where(c => c.FocusMode != Control.FocusModeEnum.None && c.IsVisibleInTree() &&
            (c is not MegaCrit.Sts2.Core.Nodes.GodotExtensions.NButton b || b.IsEnabled)).ToArray();
        for (var i = 0; i < controls.Length; i++)
        {
            controls[i].FocusNeighborTop = controls[(i + controls.Length - 1) % controls.Length].GetPath();
            controls[i].FocusNeighborBottom = controls[(i + 1) % controls.Length].GetPath();
        }
    }
    public static IEnumerable<Node> Walk(Node root)
    { foreach (var child in root.GetChildren()) { yield return child; foreach (var node in Walk(child)) yield return node; } }
    public static void Dismiss(string name) { foreach (var page in Pages.Where(p => p.Name == name).ToArray()) page.QueueFree(); }
    public static void Notice(Node parent, string title, string text) => _ = Confirm(parent, title, text, null);
    public static Task<bool> Confirm(Node parent, string title, string text, string? confirm = "确认", string pageName = "LanNotice")
    {
        var done = new TaskCompletionSource<bool>();
        var page = new NativePage { Name = pageName, Heading = title, Dialog = true };
        parent.GetTree().Root.AddChild(page);
        Label(page.Body, text, 26);
        var cancel = new UiAction(page.Footer, confirm == null ? "知道了" : "取消", () => { done.TrySetResult(false); page.QueueFree(); });
        if (confirm != null) new UiAction(page.Footer, confirm, () => { done.TrySetResult(true); page.QueueFree(); });
        page.CloseRequested = () => { done.TrySetResult(false); page.QueueFree(); };
        page.TreeExiting += () => done.TrySetResult(false);
        FocusLoop(page); cancel.Node.GrabFocus(); return done.Task;
    }
    public static NSubmenuButton Card(Node parent, string name, string title, string description, string icon, Action action)
    {
        var card = GD.Load<PackedScene>("res://scenes/ui/submenu_button.tscn").Instantiate<NSubmenuButton>();
        card.Name = name; parent.AddChild(card); card.SetIconAndLocalization("HOST");
        card.GetNode<MegaLabel>("%Title").SetTextAutoSize(title);
        card.GetNode<MegaRichTextLabel>("%Description").Text = description;
        card.GetNode<TextureRect>("Icon").Texture = GD.Load<Texture2D>($"res://images/ui/main_menu/submenu_{icon}.png");
        card.Connect(NClickableControl.SignalName.Released, Callable.From<Control>(_ => action()));
        return card;
    }
}

// Native textures and controls; no copied signals or native scene state, no OS popup windows.
internal partial class NativePage : Control
{
    public NativePage() { Ready += Build; }
    public string Heading { get; init; } = "";
    public bool Dialog { get; init; }
    public VBoxContainer Body { get; private set; } = null!;
    public HBoxContainer Footer { get; private set; } = null!;
    public Action? CloseRequested { get; set; }
    private Control _layout = null!;
    public bool Busy { get; set; }
    private void Build()
    {
        if (_layout != null) return;
        NativeUi.Track(this);
        var previous = GetViewport().GuiGetFocusOwner();
        TreeExiting += () => { if (GodotObject.IsInstanceValid(previous) && !previous.IsQueuedForDeletion() && previous.IsVisibleInTree()) previous.GrabFocus(); };
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect); MouseFilter = MouseFilterEnum.Stop; ZIndex = 80;
        var mask = new ColorRect { Color = new Color(.025f, .045f, .065f, Dialog ? .94f : .88f) };
        AddChild(mask); mask.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _layout = new Control { Size = new Vector2(1400, Dialog ? 650 : 1000) }; AddChild(_layout);
        var paper = new TextureRect { Texture = GD.Load<Texture2D>("res://images/packed/common_ui/submenu_panel.png"),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, MouseFilter = MouseFilterEnum.Ignore };
        _layout.AddChild(paper); paper.Position = new Vector2(80, 90); paper.Size = new Vector2(1240, Dialog ? 420 : 770);
        var title = NativeUi.Label(_layout, Heading, 46, true); title.Position = new Vector2(100, 14); title.Size = new Vector2(1200, 65);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        var scroll = new ScrollContainer { Position = new Vector2(140, 125), Size = new Vector2(1120, Dialog ? 340 : 690),
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _layout.AddChild(scroll);
        Body = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; Body.AddThemeConstantOverride("separation", 12); scroll.AddChild(Body);
        Footer = new HBoxContainer { Position = new Vector2(100, Dialog ? 530 : 890), Size = new Vector2(1200, 85), Alignment = BoxContainer.AlignmentMode.Center };
        _layout.AddChild(Footer); Footer.AddThemeConstantOverride("separation", 24);
        Resized += Fit; Fit();
    }
    private void Fit()
    {
        if (_layout == null) return;
        var scale = Math.Min(Size.X / 1440, Size.Y / (Dialog ? 700 : 1020));
        _layout.Scale = Vector2.One * scale; _layout.Position = (Size - _layout.Size * scale) / 2;
    }
    public override void _Input(InputEvent e)
    {
        if (e.IsActionPressed("ui_cancel") || e is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape })
        { GetViewport().SetInputAsHandled(); if (!Busy) CloseRequested?.Invoke(); }
    }
}
