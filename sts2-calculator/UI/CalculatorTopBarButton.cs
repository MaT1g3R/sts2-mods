using System;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Nodes.HoverTips;

namespace Sts2Calculator.UI;

/// <summary>
/// TopBar icon button for opening and closing the in-game calculator.
/// Placed inside RightAlignedStuff to the left of the Map and Deck buttons.
/// Matches the size (80x80), HSV shader animations, SFX, and HoverTip behavior of native TopBar buttons.
/// </summary>
public sealed class CalculatorTopBarButton
{
	public const string NodeName = "CalculatorTopBarButton";

	private static readonly StringName _h = new("h");
	private static readonly StringName _s = new("s");
	private static readonly StringName _v = new("v");

	private const float DefaultV = 0.92f;
	private const float OpenV = 1.08f;
	private const float HoverV = 1.15f;
	private const float PressV = 0.45f;
	private const float HoverAngle = -(float)Math.PI / 15f;
	private static readonly Vector2 HoverScale = Vector2.One * 1.1f;

	public MarginContainer Root { get; }

	private readonly Control _innerControl;
	private readonly TextureRect _icon;
	private readonly ShaderMaterial? _hsv;

	private Tween? _animTween;
	private bool _isHovered;
	private bool _isPressed;
	private bool _isOpen;

	public event Action? Pressed;

	public CalculatorTopBarButton()
	{
		Root = new MarginContainer
		{
			Name = NodeName,
			CustomMinimumSize = new Vector2(80f, 80f),
			MouseFilter = Control.MouseFilterEnum.Stop,
			FocusMode = Control.FocusModeEnum.None
		};
		Root.AddThemeConstantOverride("margin_left", 6);
		Root.AddThemeConstantOverride("margin_right", 6);
		Root.AddThemeConstantOverride("margin_top", 8);
		Root.AddThemeConstantOverride("margin_bottom", 8);

		_innerControl = new Control
		{
			Name = "Control",
			MouseFilter = Control.MouseFilterEnum.Ignore
		};
		_innerControl.SetAnchorsPreset(Control.LayoutPreset.FullRect);
		Root.AddChild(_innerControl);

		_icon = new TextureRect
		{
			Name = "Icon",
			CustomMinimumSize = new Vector2(64f, 64f),
			PivotOffset = new Vector2(34f, 32f),
			MouseFilter = Control.MouseFilterEnum.Ignore,
			Texture = CalculatorIconGenerator.GetOrCreateIconTexture(),
			ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
			StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered
		};
		_icon.SetAnchorsPreset(Control.LayoutPreset.FullRect);

		if (ResourceLoader.Exists("res://shaders/hsv.gdshader"))
		{
			var shader = ResourceLoader.Load<Shader>("res://shaders/hsv.gdshader");
			if (shader != null)
			{
				_hsv = new ShaderMaterial { Shader = shader };
				_hsv.SetShaderParameter(_h, 1.0f);
				_hsv.SetShaderParameter(_s, 1.0f);
				_hsv.SetShaderParameter(_v, DefaultV);
				_icon.Material = _hsv;
			}
		}

		_innerControl.AddChild(_icon);

		Root.Connect(Control.SignalName.MouseEntered, Callable.From(OnMouseEntered));
		Root.Connect(Control.SignalName.MouseExited, Callable.From(OnMouseExited));
		Root.Connect(Control.SignalName.GuiInput, Callable.From<InputEvent>(OnGuiInput));
		Root.Connect(Node.SignalName.TreeExiting, Callable.From(OnTreeExiting));
	}

	public void SetOpenState(bool isOpen)
	{
		if (_isOpen == isOpen)
		{
			return;
		}
		_isOpen = isOpen;
		if (!_isHovered)
		{
			AnimateToRest();
		}
	}

	private void OnMouseEntered()
	{
		_isHovered = true;
		try
		{
			SfxCmd.Play("event:/sfx/ui/clicks/ui_hover");
		}
		catch
		{
			// Ignore if audio system is unavailable
		}

		AnimateHover();
		ShowHoverTip();
	}

	private void OnMouseExited()
	{
		_isHovered = false;
		_isPressed = false;
		HideHoverTip();
		AnimateToRest();
	}

	private void OnGuiInput(InputEvent inputEvent)
	{
		if (inputEvent is not InputEventMouseButton mouseBtn || mouseBtn.ButtonIndex != MouseButton.Left)
		{
			return;
		}

		if (mouseBtn.Pressed)
		{
			_isPressed = true;
			try
			{
				SfxCmd.Play("event:/sfx/ui/clicks/ui_click");
			}
			catch
			{
				// Ignore if audio system is unavailable
			}
			AnimatePressDown();
			Root.GetViewport()?.SetInputAsHandled();
		}
		else if (_isPressed)
		{
			_isPressed = false;
			if (_isHovered)
			{
				AnimateHover();
				Pressed?.Invoke();
			}
			else
			{
				AnimateToRest();
			}
			Root.GetViewport()?.SetInputAsHandled();
		}
	}

	private void AnimateHover()
	{
		if (!GodotObject.IsInstanceValid(_icon))
		{
			return;
		}

		_animTween?.Kill();
		_animTween = Root.CreateTween();
		_animTween.SetParallel(true);
		_animTween.TweenProperty(_icon, "scale", HoverScale, 0.2f)
			.SetEase(Tween.EaseType.Out)
			.SetTrans(Tween.TransitionType.Back);
		_animTween.TweenProperty(_icon, "rotation", HoverAngle, 0.25f)
			.SetEase(Tween.EaseType.Out)
			.SetTrans(Tween.TransitionType.Back);
		_hsv?.SetShaderParameter(_v, HoverV);
	}

	private void AnimatePressDown()
	{
		if (!GodotObject.IsInstanceValid(_icon))
		{
			return;
		}

		_animTween?.Kill();
		_animTween = Root.CreateTween();
		float targetAngle = _icon.Rotation + (float)Math.PI * 2f / 15f;
		_animTween.TweenProperty(_icon, "rotation", targetAngle, 0.15f)
			.SetEase(Tween.EaseType.Out)
			.SetTrans(Tween.TransitionType.Cubic);
		_hsv?.SetShaderParameter(_v, PressV);
	}

	private void AnimateToRest()
	{
		if (!GodotObject.IsInstanceValid(_icon))
		{
			return;
		}

		_animTween?.Kill();
		_animTween = Root.CreateTween();
		_animTween.SetParallel(true);
		_animTween.TweenProperty(_icon, "scale", Vector2.One, 0.35f)
			.SetEase(Tween.EaseType.Out)
			.SetTrans(Tween.TransitionType.Expo);
		_animTween.TweenProperty(_icon, "rotation", _isOpen ? -0.06f : 0f, 0.5f)
			.SetEase(Tween.EaseType.Out)
			.SetTrans(Tween.TransitionType.Elastic);
		_hsv?.SetShaderParameter(_v, _isOpen ? OpenV : DefaultV);
	}

	private void ShowHoverTip()
	{
		try
		{
			var tip = CreateCustomHoverTip(
				"Calculator",
				"Open or close the math calculator.\nSupports [gold]+  -  *  x  /  ( )[/gold] and [gold]floor()[/gold], [gold]fl()[/gold], [gold]int()[/gold]."
			);
			NHoverTipSet? tipSet = NHoverTipSet.CreateAndShow(Root, tip);
			tipSet?.SetGlobalPosition(Root.GlobalPosition + new Vector2(Root.Size.X - tipSet.Size.X, Root.Size.Y + 20f));
		}
		catch
		{
			// Fallback if HoverTip container isn't ready
		}
	}

	private void HideHoverTip()
	{
		try
		{
			NHoverTipSet.Remove(Root);
		}
		catch
		{
			// Ignore
		}
	}

	private void OnTreeExiting()
	{
		_animTween?.Kill();
		HideHoverTip();
	}

	private static HoverTip CreateCustomHoverTip(string title, string description)
	{
		var tip = new HoverTip();
		object boxed = tip;
		AccessTools.Property(typeof(HoverTip), nameof(HoverTip.Title))?.SetValue(boxed, title);
		AccessTools.Property(typeof(HoverTip), nameof(HoverTip.Description))?.SetValue(boxed, description);
		AccessTools.Property(typeof(HoverTip), nameof(HoverTip.Id))?.SetValue(boxed, "Sts2Calculator.TopBarTip");
		return (HoverTip)boxed;
	}
}
