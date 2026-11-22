using Godot;
using HarmonyLib;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;

namespace RunHistoryPlus.UI;

/// <summary>
/// Wraps STS2's native settings_dropdown.tscn + dropdown_item.tscn scenes so dropdowns
/// match the game's exact visual language (fonts, colors, arrow, scrollbar, dismisser).
/// Supports lazy item instantiation so large lists (like the Run Selector) cost zero
/// scene instantiations until the user actually opens the dropdown.
/// </summary>
internal sealed class NativeDropdown
{
    private const string DropdownScenePath = "res://scenes/screens/settings_dropdown.tscn";
    private const string DropdownItemScenePath = "res://scenes/ui/dropdown_item.tscn";
    private const string KreonBoldPath = "res://themes/kreon_bold_glyph_space_one.tres";

    private static readonly Color RestBgColor = new("2C434F");
    private static readonly Color HoverBgColor = new("3C5B6B");
    private static readonly Color PressBgColor = new("20323B");
    private static readonly Color HeaderGoldColor = new("EFC851");
    private static readonly Color ItemCreamColor = new("FFF6E2");

    private static readonly AccessTools.FieldRef<NDropdownContainer, float> MaxHeightRef =
        AccessTools.FieldRefAccess<NDropdownContainer, float>("_maxHeight");

    private static readonly AccessTools.FieldRef<NDropdownContainer, float> ContentHeightRef =
        AccessTools.FieldRefAccess<NDropdownContainer, float>("_contentHeight");

    private static readonly AccessTools.FieldRef<NDropdownContainer, float> ContainerScrollLimitBottomRef =
        AccessTools.FieldRefAccess<NDropdownContainer, float>("_scrollLimitBottom");

    private static readonly AccessTools.FieldRef<NDropdownContainer, Vector2> TargetDragPosRef =
        AccessTools.FieldRefAccess<NDropdownContainer, Vector2>("_targetDragPos");

    private static readonly AccessTools.FieldRef<NDropdownScrollbar, float> ScrollbarLimitTopRef =
        AccessTools.FieldRefAccess<NDropdownScrollbar, float>("_scrollLimitTop");

    private static readonly AccessTools.FieldRef<NDropdownScrollbar, float> ScrollbarLimitBottomRef =
        AccessTools.FieldRefAccess<NDropdownScrollbar, float>("_scrollLimitBottom");

    private float _width;
    private readonly float _height;
    private readonly float _itemHeight;
    private readonly float _maxListHeight;
    private readonly int _fontSize;
    private readonly int _itemFontSize;
    private readonly bool _useRichText;
    private readonly Font? _boldFont;

    private readonly Control _root;
    private readonly Control _highlight;
    private readonly MegaLabel _currentLabel;
    private RichTextLabel? _currentRichLabel;
    private readonly NDropdownContainer _dropdownContainer;
    private readonly VBoxContainer _itemsContainer;
    private readonly NButton _dismisser;

    private bool _isOpen;
    private bool _isHovered;
    private int _itemCount;
    private Func<IReadOnlyList<(string Label, Action OnSelected)>>? _lazyItemsProvider;
    private bool _lazyItemsDirty;

    public Control Root => _root;
    public bool IsOpen => _isOpen;
    public float Width => _width;

    public event Action<NativeDropdown>? Opened;

    private NativeDropdown(
        Control root,
        float width,
        float height,
        float itemHeight,
        float maxListHeight,
        int fontSize,
        int itemFontSize,
        bool useRichText)
    {
        _root = root;
        _width = width;
        _height = height;
        _itemHeight = itemHeight;
        _maxListHeight = maxListHeight;
        _fontSize = fontSize;
        _itemFontSize = itemFontSize;
        _useRichText = useRichText;
        _boldFont = useRichText ? GD.Load<Font>(KreonBoldPath) : null;

        _highlight = root.GetNode<Control>("%Highlight");
        _currentLabel = root.GetNode<MegaLabel>("%Label");
        _dropdownContainer = root.GetNode<NDropdownContainer>("%DropdownContainer");
        _itemsContainer = _dropdownContainer.GetNode<VBoxContainer>("VBoxContainer");
        _dismisser = root.GetNode<NButton>("%Dismisser");

        ConfigureGeometry();
        WireEvents();
    }

    public static NativeDropdown? Create(
        string name,
        float width,
        float height = 42f,
        float itemHeight = 38f,
        float maxListHeight = 360f,
        int fontSize = 20,
        int itemFontSize = 18,
        bool useRichText = false)
    {
        PackedScene? scene = LoadScene(DropdownScenePath);
        if (scene == null)
        {
            Log.Error($"[RunHistoryPlus] Could not load native dropdown scene at {DropdownScenePath}");
            return null;
        }

        Control root = scene.Instantiate<Control>(PackedScene.GenEditState.Disabled);
        root.Name = name;
        return new NativeDropdown(root, width, height, itemHeight, maxListHeight, fontSize, itemFontSize, useRichText);
    }

    private void ConfigureGeometry()
    {
        _root.CustomMinimumSize = new Vector2(_width, _height);
        _root.Size = new Vector2(_width, _height);
        _root.MouseFilter = Control.MouseFilterEnum.Stop;

        Control? currentOption = _root.GetNodeOrNull<Control>("Container/CurrentOption");
        if (currentOption != null)
        {
            currentOption.CustomMinimumSize = new Vector2(_width, _height);
        }

        _highlight.Modulate = RestBgColor;

        // Leave room on the right for the dropdown arrow icon
        _currentLabel.OffsetLeft = 10f;
        _currentLabel.OffsetRight = -32f;
        _currentLabel.AutoSizeEnabled = false;
        _currentLabel.AddThemeFontSizeOverride("font_size", _fontSize);
        _currentLabel.HorizontalAlignment = HorizontalAlignment.Left;
        _currentLabel.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        _currentLabel.ClipText = true;

        if (_useRichText && currentOption != null)
        {
            _currentLabel.Visible = false;
            _currentRichLabel = CreateRichLabel(_fontSize, HeaderGoldColor, 10f, -32f);
            currentOption.AddChild(_currentRichLabel);
        }

        Control? arrow = _root.GetNodeOrNull<Control>("%Arrow");
        if (arrow != null)
        {
            arrow.OffsetLeft = -30f;
            arrow.OffsetRight = -10f;
            arrow.OffsetTop = -10f;
            arrow.OffsetBottom = 10f;
            arrow.PivotOffset = new Vector2(10f, 10f);
        }

        _dropdownContainer.OffsetLeft = -_width * 0.5f;
        _dropdownContainer.OffsetRight = _width * 0.5f;
        _dropdownContainer.OffsetTop = _height;
        _dropdownContainer.OffsetBottom = _height + _maxListHeight;
        _dropdownContainer.Size = new Vector2(_width, _maxListHeight);
        _dropdownContainer.Visible = false;

        ConfigureScrollbarGeometry();

        _dismisser.Visible = false;
    }

    private void ConfigureScrollbarGeometry()
    {
        NDropdownScrollbar? scrollbar = _dropdownContainer.GetNodeOrNull<NDropdownScrollbar>("Scrollbar");
        if (scrollbar == null)
        {
            return;
        }

        // Fixed narrow 20px strip on the right edge instead of 12.5% of total width
        scrollbar.AnchorLeft = 1f;
        scrollbar.AnchorRight = 1f;
        scrollbar.AnchorTop = 0f;
        scrollbar.AnchorBottom = 1f;
        scrollbar.OffsetLeft = -20f;
        scrollbar.OffsetRight = -2f;
        scrollbar.OffsetTop = 0f;
        scrollbar.OffsetBottom = 0f;

        Control? track = scrollbar.GetNodeOrNull<Control>("Track");
        if (track != null)
        {
            track.Visible = false;
        }

        // Add a clean, thin full-height track background that scales to any dropdown height
        ColorRect thinTrack = new ColorRect
        {
            Name = "ThinTrack",
            Color = new Color(0f, 0f, 0f, 0.35f),
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        thinTrack.AnchorLeft = 0.5f;
        thinTrack.AnchorRight = 0.5f;
        thinTrack.AnchorTop = 0f;
        thinTrack.AnchorBottom = 1f;
        thinTrack.OffsetLeft = -3f;
        thinTrack.OffsetRight = 3f;
        thinTrack.OffsetTop = 6f;
        thinTrack.OffsetBottom = -6f;
        scrollbar.AddChild(thinTrack);
        scrollbar.MoveChild(thinTrack, 0);

        Control? train = scrollbar.GetNodeOrNull<Control>("Train");
        if (train != null)
        {
            train.AnchorLeft = 0.5f;
            train.AnchorRight = 0.5f;
            train.AnchorTop = 0f;
            train.AnchorBottom = 0f;
            train.OffsetLeft = -5f;
            train.OffsetRight = 5f;
            train.OffsetTop = 6f;
            train.OffsetBottom = 46f;
            train.Size = new Vector2(10f, 40f);
            train.PivotOffset = new Vector2(5f, 20f);
        }
    }

    private void WireEvents()
    {
        _root.MouseEntered += OnMouseEntered;
        _root.MouseExited += OnMouseExited;
        _root.GuiInput += OnGuiInput;

        _dismisser.Connect(
            NClickableControl.SignalName.Released,
            Callable.From<NButton>(_ => Close()));

        _root.VisibilityChanged += () =>
        {
            if (!_root.IsVisibleInTree() && _isOpen)
            {
                Close();
            }
        };
    }

    public void SetSelectedText(string text)
    {
        if (_currentRichLabel != null)
        {
            _currentRichLabel.Text = text;
        }
        else
        {
            _currentLabel.Text = text;
        }
    }

    public void SetWidth(float width)
    {
        if (Math.Abs(_width - width) < 1f)
        {
            return;
        }

        _width = width;
        _root.CustomMinimumSize = new Vector2(_width, _height);
        _root.Size = new Vector2(_width, _height);

        Control? currentOption = _root.GetNodeOrNull<Control>("Container/CurrentOption");
        if (currentOption != null)
        {
            currentOption.CustomMinimumSize = new Vector2(_width, _height);
        }

        foreach (NDropdownItem item in _itemsContainer.GetChildren().OfType<NDropdownItem>())
        {
            item.CustomMinimumSize = new Vector2(_width, _itemHeight);
            item.Size = new Vector2(_width, _itemHeight);
            item.OffsetRight = _width;
        }

        RefreshContainerBounds();
    }

    private RichTextLabel CreateRichLabel(int fontSize, Color defaultColor, float offsetLeft, float offsetRight)
    {
        RichTextLabel rtl = new RichTextLabel
        {
            BbcodeEnabled = true,
            ScrollActive = false,
            ClipContents = true,
            AutowrapMode = TextServer.AutowrapMode.Off,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        rtl.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        rtl.OffsetLeft = offsetLeft;
        rtl.OffsetRight = offsetRight;
        if (_boldFont != null)
        {
            rtl.AddThemeFontOverride("normal_font", _boldFont);
            rtl.AddThemeFontOverride("bold_font", _boldFont);
        }
        rtl.AddThemeFontSizeOverride("normal_font_size", fontSize);
        rtl.AddThemeFontSizeOverride("bold_font_size", fontSize);
        rtl.AddThemeColorOverride("default_color", defaultColor);
        rtl.AddThemeColorOverride("font_shadow_color", new Color(0f, 0f, 0f, 0.25f));
        rtl.AddThemeConstantOverride("shadow_offset_x", 3);
        rtl.AddThemeConstantOverride("shadow_offset_y", 2);
        return rtl;
    }

    public void SetItems<T>(IReadOnlyList<(string Label, T Value)> options, Action<T> onSelected)
    {
        _lazyItemsProvider = null;
        _lazyItemsDirty = false;
        var built = new List<(string Label, Action OnSelected)>(options.Count);
        for (int i = 0; i < options.Count; i++)
        {
            T captured = options[i].Value;
            built.Add((options[i].Label, () => onSelected(captured)));
        }
        BuildItemNodes(built);
    }

    /// <summary>
    /// Registers a provider that builds dropdown item nodes lazily when the dropdown is opened.
    /// Avoids instantiating hundreds of Godot scenes on screen open.
    /// </summary>
    public void SetLazyItems(Func<IReadOnlyList<(string Label, Action OnSelected)>> provider)
    {
        _lazyItemsProvider = provider;
        _lazyItemsDirty = true;
        if (_isOpen)
        {
            MaterializeLazyItems();
        }
    }

    public void InvalidateLazyItems()
    {
        _lazyItemsDirty = true;
        if (_isOpen)
        {
            MaterializeLazyItems();
        }
    }

    private void MaterializeLazyItems()
    {
        if (!_lazyItemsDirty || _lazyItemsProvider == null)
        {
            return;
        }

        _lazyItemsDirty = false;
        BuildItemNodes(_lazyItemsProvider());
    }

    private void BuildItemNodes(IReadOnlyList<(string Label, Action OnSelected)> options)
    {
        ClearItems();
        _itemCount = options.Count;

        if (options.Count == 0)
        {
            RefreshContainerBounds();
            return;
        }

        PackedScene? itemScene = LoadScene(DropdownItemScenePath);
        if (itemScene == null)
        {
            Log.Error($"[RunHistoryPlus] Could not load native dropdown item scene at {DropdownItemScenePath}");
            return;
        }

        for (int i = 0; i < options.Count; i++)
        {
            var (label, onSelected) = options[i];
            NDropdownItem item = itemScene.Instantiate<NDropdownItem>(PackedScene.GenEditState.Disabled);
            item.CustomMinimumSize = new Vector2(_width, _itemHeight);
            item.Size = new Vector2(_width, _itemHeight);
            item.OffsetLeft = 0f;
            item.OffsetTop = 0f;
            item.OffsetRight = _width;
            item.OffsetBottom = _itemHeight;
            item.MouseFilter = Control.MouseFilterEnum.Pass;
            _itemsContainer.AddChildSafely(item);

            MegaLabel? itemLabel = item.GetNodeOrNull<MegaLabel>("Label");
            if (_useRichText)
            {
                if (itemLabel != null)
                {
                    itemLabel.Visible = false;
                }
                RichTextLabel richItemLabel = CreateRichLabel(_itemFontSize, ItemCreamColor, 10f, -22f);
                richItemLabel.Text = label;
                item.AddChild(richItemLabel);
            }
            else if (itemLabel != null)
            {
                itemLabel.OffsetLeft = 10f;
                itemLabel.OffsetRight = -22f;
                itemLabel.HorizontalAlignment = HorizontalAlignment.Left;
                itemLabel.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
                itemLabel.ClipText = true;
                itemLabel.AutoSizeEnabled = false;
                itemLabel.AddThemeFontSizeOverride("font_size", _itemFontSize);
                itemLabel.Text = label;
            }
            else
            {
                item.Text = label;
            }

            Action captured = onSelected;
            item.Connect(
                NDropdownItem.SignalName.Selected,
                Callable.From<NDropdownItem>(_ =>
                {
                    Close();
                    captured();
                }));
        }

        RefreshContainerBounds();
    }

    public void ClearItems()
    {
        _itemCount = 0;
        foreach (Node child in _itemsContainer.GetChildren())
        {
            _itemsContainer.RemoveChildSafely(child);
            child.QueueFreeSafely();
        }
    }

    public void Open()
    {
        if (_isOpen)
        {
            return;
        }

        Opened?.Invoke(this);
        MaterializeLazyItems();

        // Bring this dropdown to the end of its parent so Godot GUI hit-testing prioritizes it
        // above sibling dropdowns (ZIndex alone only affects rendering, not mouse input order).
        Node? parent = _root.GetParent();
        if (parent != null)
        {
            parent.MoveChild(_root, parent.GetChildCount() - 1);
        }

        _isOpen = true;
        _root.ZIndex = 120;
        _itemsContainer.Position = Vector2.Zero;
        TargetDragPosRef(_dropdownContainer) = Vector2.Zero;
        _dismisser.Visible = true;
        _dropdownContainer.Visible = true;
        RefreshContainerBounds();

        NDropdownScrollbar? scrollbar = _dropdownContainer.GetNodeOrNull<NDropdownScrollbar>("Scrollbar");
        if (scrollbar != null && scrollbar.Visible)
        {
            scrollbar.SetTrainPositionFromPercentage(0f);
        }

        foreach (NDropdownItem item in _itemsContainer.GetChildren().OfType<NDropdownItem>())
        {
            item.UnhoverSelection();
        }
    }

    public void Close()
    {
        if (!_isOpen)
        {
            return;
        }

        _isOpen = false;
        _dismisser.Visible = false;
        _dropdownContainer.Visible = false;
        _root.ZIndex = 0;
        _highlight.Modulate = _isHovered ? HoverBgColor : RestBgColor;
    }

    private void RefreshContainerBounds()
    {
        if (!GodotObject.IsInstanceValid(_dropdownContainer))
        {
            return;
        }

        float contentHeight = _itemCount * _itemHeight;
        float actualHeight = Math.Min(contentHeight, _maxListHeight);
        bool needsScroll = contentHeight > _maxListHeight + 0.5f;
        float scrollLimitBottom = needsScroll ? actualHeight - contentHeight : 0f;

        MaxHeightRef(_dropdownContainer) = _maxListHeight;
        _dropdownContainer.OffsetLeft = -_width * 0.5f;
        _dropdownContainer.OffsetRight = _width * 0.5f;
        _dropdownContainer.OffsetTop = _height;
        _dropdownContainer.OffsetBottom = _height + actualHeight;
        _dropdownContainer.Size = new Vector2(_width, actualHeight);
        _dropdownContainer.RefreshLayout();

        // Enforce exact finite scroll bounds after RefreshLayout (overrides pre-layout 44px
        // item heights and NDropdownScrollbar's hardcoded 600px track height).
        _dropdownContainer.OffsetBottom = _height + actualHeight;
        _dropdownContainer.Size = new Vector2(_width, actualHeight);
        ContentHeightRef(_dropdownContainer) = contentHeight;
        ContainerScrollLimitBottomRef(_dropdownContainer) = scrollLimitBottom;

        Vector2 targetPos = TargetDragPosRef(_dropdownContainer);
        TargetDragPosRef(_dropdownContainer) = new Vector2(0f, Mathf.Clamp(targetPos.Y, scrollLimitBottom, 0f));

        NDropdownScrollbar? scrollbar = _dropdownContainer.GetNodeOrNull<NDropdownScrollbar>("Scrollbar");
        if (scrollbar != null)
        {
            scrollbar.Visible = needsScroll;
            scrollbar.MouseFilter = needsScroll
                ? Control.MouseFilterEnum.Stop
                : Control.MouseFilterEnum.Ignore;

            if (needsScroll)
            {
                const float pad = 6f;
                float trackHeight = Math.Max(24f, actualHeight - pad * 2f);
                float thumbHeight = Mathf.Clamp(trackHeight * (actualHeight / contentHeight), 28f, trackHeight * 0.5f);

                Control? train = scrollbar.GetNodeOrNull<Control>("Train");
                if (train != null)
                {
                    train.OffsetLeft = -5f;
                    train.OffsetRight = 5f;
                    train.Size = new Vector2(10f, thumbHeight);
                    train.PivotOffset = new Vector2(5f, thumbHeight * 0.5f);
                }

                ScrollbarLimitBottomRef(scrollbar) = pad;
                ScrollbarLimitTopRef(scrollbar) = Math.Max(pad + 1f, actualHeight - thumbHeight - pad);
            }
        }
    }

    private void OnMouseEntered()
    {
        _isHovered = true;
        _highlight.Modulate = HoverBgColor;
    }

    private void OnMouseExited()
    {
        _isHovered = false;
        if (!_isOpen)
        {
            _highlight.Modulate = RestBgColor;
        }
    }

    private void OnGuiInput(InputEvent inputEvent)
    {
        if (inputEvent is not InputEventMouseButton mouseEvent || mouseEvent.ButtonIndex != MouseButton.Left)
        {
            return;
        }

        if (mouseEvent.Pressed)
        {
            _highlight.Modulate = PressBgColor;
            _root.AcceptEvent();
            return;
        }

        _root.AcceptEvent();
        if (_isOpen)
        {
            Close();
        }
        else
        {
            Open();
        }
    }

    private static PackedScene? LoadScene(string path)
    {
        try
        {
            PackedScene? cached = PreloadManager.Cache.GetScene(path);
            if (cached != null)
            {
                return cached;
            }
        }
        catch
        {
            // Fall through to GD.Load
        }

        return GD.Load<PackedScene>(path);
    }
}
