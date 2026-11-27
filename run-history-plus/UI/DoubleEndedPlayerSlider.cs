using Godot;
using RunHistoryPlus.Core;

namespace RunHistoryPlus.UI;

/// <summary>
/// Composition-based double-ended discrete range slider for steps 1..4 (displayed as 1, 2, 3, 4+).
/// Uses standard Godot Control/Panel/Label nodes and signal handlers (no Control subclass overrides)
/// so it renders and receives input reliably in dynamically loaded STS2 mods.
/// </summary>
internal sealed class DoubleEndedPlayerSlider
{
    private const int MinStep = RunFilterState.MinPlayerCountBound;
    private const int MaxStep = RunFilterState.MaxPlayerCountCap;
    private const int StepCount = MaxStep - MinStep + 1;
    private const float DefaultWidth = 220f;
    private const float ControlHeight = 58f;
    private const float SidePadding = 14f;
    private const float TrackY = 16f;
    private const float TrackHeight = 8f;
    private const float HandleSize = 20f;
    private const float HandleRadius = HandleSize * 0.5f;
    private const float OverlapRingSize = 26f;

    private static readonly Color TrackBgColor = new("15242C");
    private static readonly Color TrackBorderColor = new("2C434F");
    private static readonly Color ActiveFillColor = new("B5943B");
    private static readonly Color ActiveAccentColor = new("C9A94E");
    private static readonly Color MutedColor = new(0.68f, 0.74f, 0.72f, 0.78f);
    private static readonly Color HandleFillColor = new("C9C0AE");
    private static readonly Color HandleHoverColor = new("D4B455");
    private static readonly Color HandleBorderColor = new("182831");

    private enum DragTarget
    {
        None,
        Min,
        Max,
        UndecidedOverlap
    }

    private readonly Action<int, int> _onRangePreview;
    private readonly Action<int, int> _onRangeCommitted;
    private readonly Control _root;
    private readonly Panel _trackBg;
    private readonly Panel _activeFill;
    private readonly ColorRect[] _ticks = new ColorRect[StepCount];
    private readonly Label[] _stepLabels = new Label[StepCount];
    private readonly Panel _overlapRing;
    private readonly Panel _minHandle;
    private readonly Panel _maxHandle;
    private readonly StyleBoxFlat _handleRestStyle;
    private readonly StyleBoxFlat _handleHoverStyle;

    private int _minVal = MinStep;
    private int _maxVal = MaxStep;
    private int _dragStartMin = MinStep;
    private int _dragStartMax = MaxStep;
    private DragTarget _dragTarget = DragTarget.None;
    private DragTarget _hoverTarget = DragTarget.None;
    private float _pressX;

    public Control Root => _root;

    public DoubleEndedPlayerSlider(
        Font? font,
        Action<int, int> onRangePreview,
        Action<int, int> onRangeCommitted)
    {
        _onRangePreview = onRangePreview;
        _onRangeCommitted = onRangeCommitted;

        _root = new Control
        {
            Name = "DoubleEndedPlayerSlider",
            CustomMinimumSize = new Vector2(DefaultWidth, ControlHeight),
            MouseFilter = Control.MouseFilterEnum.Stop,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand
        };

        _trackBg = new Panel
        {
            Name = "TrackBg",
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        _trackBg.AddThemeStyleboxOverride("panel", CreateRoundedStyle(TrackBgColor, TrackBorderColor, 1, 4));
        _root.AddChild(_trackBg);

        _activeFill = new Panel
        {
            Name = "ActiveFill",
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        _activeFill.AddThemeStyleboxOverride("panel", CreateRoundedStyle(ActiveFillColor, ActiveFillColor, 0, 3));
        _root.AddChild(_activeFill);

        for (int i = 0; i < StepCount; i++)
        {
            int step = MinStep + i;

            ColorRect tick = new ColorRect
            {
                Name = $"Tick{step}",
                Color = ActiveAccentColor,
                MouseFilter = Control.MouseFilterEnum.Ignore
            };
            _ticks[i] = tick;
            _root.AddChild(tick);

            Label stepLabel = new Label
            {
                Name = $"StepLabel{step}",
                Text = step >= MaxStep ? "4+" : step.ToString(),
                HorizontalAlignment = HorizontalAlignment.Center,
                MouseFilter = Control.MouseFilterEnum.Ignore
            };
            if (font != null) stepLabel.AddThemeFontOverride("font", font);
            stepLabel.AddThemeFontSizeOverride("font_size", 16);
            stepLabel.AddThemeColorOverride("font_color", ActiveAccentColor);
            stepLabel.AddThemeColorOverride("font_shadow_color", new Color(0f, 0f, 0f, 0.5f));
            stepLabel.AddThemeConstantOverride("shadow_offset_x", 1);
            stepLabel.AddThemeConstantOverride("shadow_offset_y", 1);
            _stepLabels[i] = stepLabel;
            _root.AddChild(stepLabel);
        }

        _overlapRing = new Panel
        {
            Name = "OverlapRing",
            Visible = false,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        _overlapRing.AddThemeStyleboxOverride("panel", CreateRoundedStyle(Colors.Transparent, ActiveFillColor, 2, 13));
        _root.AddChild(_overlapRing);

        _handleRestStyle = CreateRoundedStyle(HandleFillColor, HandleBorderColor, 2, 10);
        _handleHoverStyle = CreateRoundedStyle(HandleHoverColor, HandleBorderColor, 2, 10);

        _minHandle = new Panel
        {
            Name = "MinHandle",
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        _minHandle.AddThemeStyleboxOverride("panel", _handleRestStyle);
        _root.AddChild(_minHandle);

        _maxHandle = new Panel
        {
            Name = "MaxHandle",
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        _maxHandle.AddThemeStyleboxOverride("panel", _handleRestStyle);
        _root.AddChild(_maxHandle);

        _root.GuiInput += OnGuiInput;
        _root.Resized += UpdateLayout;
        _root.MouseExited += OnMouseExited;

        UpdateLayout();
    }

    public void SetValues(int minVal, int maxVal)
    {
        int clampedMin = Math.Clamp(minVal, MinStep, MaxStep);
        int clampedMax = Math.Clamp(maxVal, clampedMin, MaxStep);
        if (_minVal == clampedMin && _maxVal == clampedMax)
        {
            return;
        }

        _minVal = clampedMin;
        _maxVal = clampedMax;
        UpdateLayout();
    }

    private void UpdateLayout()
    {
        float width = _root.Size.X > 1f ? _root.Size.X : DefaultWidth;
        float trackLeft = SidePadding;
        float trackRight = Math.Max(trackLeft + 40f, width - SidePadding);
        float trackWidth = trackRight - trackLeft;
        float trackTop = TrackY - TrackHeight * 0.5f;

        SetRect(_trackBg, trackLeft, trackTop, trackWidth, TrackHeight);

        float minX = StepToX(_minVal, trackLeft, trackWidth);
        float maxX = StepToX(_maxVal, trackLeft, trackWidth);

        float fillLeft = _minVal == _maxVal ? minX - 2f : minX;
        float fillWidth = Math.Max(4f, maxX - minX);
        SetRect(_activeFill, fillLeft, trackTop + 1f, fillWidth, TrackHeight - 2f);

        for (int i = 0; i < StepCount; i++)
        {
            int step = MinStep + i;
            float sx = StepToX(step, trackLeft, trackWidth);
            bool inRange = step >= _minVal && step <= _maxVal;

            _ticks[i].Color = inRange ? ActiveAccentColor : TrackBorderColor;
            SetRect(_ticks[i], sx - 1f, TrackY + TrackHeight * 0.5f + 3f, 2f, 6f);

            _stepLabels[i].AddThemeColorOverride("font_color", inRange ? ActiveAccentColor : MutedColor);
            SetRect(_stepLabels[i], sx - 16f, TrackY + 14f, 32f, 22f);
        }

        bool overlapped = _minVal == _maxVal;
        _overlapRing.Visible = overlapped;
        if (overlapped)
        {
            SetRect(_overlapRing, minX - OverlapRingSize * 0.5f, TrackY - OverlapRingSize * 0.5f, OverlapRingSize, OverlapRingSize);
        }

        SetRect(_minHandle, minX - HandleRadius, TrackY - HandleRadius, HandleSize, HandleSize);
        _maxHandle.Visible = !overlapped;
        if (!overlapped)
        {
            SetRect(_maxHandle, maxX - HandleRadius, TrackY - HandleRadius, HandleSize, HandleSize);
        }

        UpdateHandleStyles();
    }

    private void UpdateHandleStyles()
    {
        bool minActive = _dragTarget == DragTarget.Min || _dragTarget == DragTarget.UndecidedOverlap || _hoverTarget == DragTarget.Min;
        bool maxActive = _dragTarget == DragTarget.Max || _hoverTarget == DragTarget.Max;
        _minHandle.AddThemeStyleboxOverride("panel", minActive ? _handleHoverStyle : _handleRestStyle);
        _maxHandle.AddThemeStyleboxOverride("panel", maxActive ? _handleHoverStyle : _handleRestStyle);
    }

    private void OnMouseExited()
    {
        if (_dragTarget == DragTarget.None && _hoverTarget != DragTarget.None)
        {
            _hoverTarget = DragTarget.None;
            UpdateHandleStyles();
        }
    }

    private void OnGuiInput(InputEvent @event)
    {
        float width = _root.Size.X > 1f ? _root.Size.X : DefaultWidth;
        float trackLeft = SidePadding;
        float trackRight = Math.Max(trackLeft + 40f, width - SidePadding);
        float trackWidth = trackRight - trackLeft;

        if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Left } mb)
        {
            if (mb.Pressed)
            {
                _pressX = mb.Position.X;
                _dragStartMin = _minVal;
                _dragStartMax = _maxVal;
                float minX = StepToX(_minVal, trackLeft, trackWidth);
                float maxX = StepToX(_maxVal, trackLeft, trackWidth);

                if (_minVal == _maxVal && Math.Abs(mb.Position.X - minX) <= HandleRadius + 8f)
                {
                    if (_minVal == MinStep)
                    {
                        _dragTarget = DragTarget.Max;
                    }
                    else if (_maxVal == MaxStep)
                    {
                        _dragTarget = DragTarget.Min;
                    }
                    else
                    {
                        _dragTarget = DragTarget.UndecidedOverlap;
                    }
                }
                else
                {
                    float distMin = Math.Abs(mb.Position.X - minX);
                    float distMax = Math.Abs(mb.Position.X - maxX);
                    _dragTarget = distMin <= distMax ? DragTarget.Min : DragTarget.Max;
                    UpdateDraggedValue(mb.Position.X, trackLeft, trackWidth);
                }

                UpdateHandleStyles();
                _root.AcceptEvent();
            }
            else if (_dragTarget != DragTarget.None)
            {
                _dragTarget = DragTarget.None;
                UpdateHandleStyles();
                if (_minVal != _dragStartMin || _maxVal != _dragStartMax)
                {
                    _onRangeCommitted(_minVal, _maxVal);
                }
                _root.AcceptEvent();
            }
        }
        else if (@event is InputEventMouseMotion mm)
        {
            if (_dragTarget == DragTarget.UndecidedOverlap)
            {
                float delta = mm.Position.X - _pressX;
                if (Math.Abs(delta) >= 4f)
                {
                    _dragTarget = delta < 0f ? DragTarget.Min : DragTarget.Max;
                    UpdateDraggedValue(mm.Position.X, trackLeft, trackWidth);
                    UpdateHandleStyles();
                }
                _root.AcceptEvent();
            }
            else if (_dragTarget != DragTarget.None)
            {
                UpdateDraggedValue(mm.Position.X, trackLeft, trackWidth);
                _root.AcceptEvent();
            }
            else
            {
                float minX = StepToX(_minVal, trackLeft, trackWidth);
                float maxX = StepToX(_maxVal, trackLeft, trackWidth);
                DragTarget newHover = DragTarget.None;
                if (Math.Abs(mm.Position.Y - TrackY) <= HandleRadius + 8f)
                {
                    if (Math.Abs(mm.Position.X - minX) <= HandleRadius + 6f)
                    {
                        newHover = DragTarget.Min;
                    }
                    else if (Math.Abs(mm.Position.X - maxX) <= HandleRadius + 6f)
                    {
                        newHover = DragTarget.Max;
                    }
                }

                if (newHover != _hoverTarget)
                {
                    _hoverTarget = newHover;
                    UpdateHandleStyles();
                }
            }
        }
    }

    private void UpdateDraggedValue(float mouseX, float trackLeft, float trackWidth)
    {
        int step = XToNearestStep(mouseX, trackLeft, trackWidth);
        int newMin = _minVal;
        int newMax = _maxVal;

        if (_dragTarget == DragTarget.Min)
        {
            newMin = Math.Clamp(step, MinStep, _maxVal);
        }
        else if (_dragTarget == DragTarget.Max)
        {
            newMax = Math.Clamp(step, _minVal, MaxStep);
        }

        if (newMin != _minVal || newMax != _maxVal)
        {
            _minVal = newMin;
            _maxVal = newMax;
            UpdateLayout();
            _onRangePreview(_minVal, _maxVal);
        }
    }

    private static float StepToX(int step, float trackLeft, float trackWidth)
    {
        float t = (step - MinStep) / (float)(MaxStep - MinStep);
        return trackLeft + t * trackWidth;
    }

    private static int XToNearestStep(float x, float trackLeft, float trackWidth)
    {
        float t = Math.Clamp((x - trackLeft) / trackWidth, 0f, 1f);
        return Math.Clamp(MinStep + Mathf.RoundToInt(t * (MaxStep - MinStep)), MinStep, MaxStep);
    }

    private static void SetRect(Control control, float x, float y, float width, float height)
    {
        control.AnchorLeft = 0f;
        control.AnchorTop = 0f;
        control.AnchorRight = 0f;
        control.AnchorBottom = 0f;
        control.OffsetLeft = x;
        control.OffsetTop = y;
        control.OffsetRight = x + width;
        control.OffsetBottom = y + height;
    }

    private static StyleBoxFlat CreateRoundedStyle(Color bg, Color border, int borderWidth, int radius) => new()
    {
        BgColor = bg,
        BorderColor = border,
        BorderWidthLeft = borderWidth,
        BorderWidthTop = borderWidth,
        BorderWidthRight = borderWidth,
        BorderWidthBottom = borderWidth,
        CornerRadiusTopLeft = radius,
        CornerRadiusTopRight = radius,
        CornerRadiusBottomLeft = radius,
        CornerRadiusBottomRight = radius
    };
}
