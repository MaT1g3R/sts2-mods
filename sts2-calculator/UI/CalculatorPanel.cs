using System;
using System.Collections.Generic;
using Godot;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Helpers;
using Sts2Calculator.Core;

namespace Sts2Calculator.UI;

/// <summary>
/// Draggable in-game calculator popup window.
/// Supports typing mathematical expressions directly as well as clicking on-screen calculator buttons.
/// </summary>
public sealed class CalculatorPanel
{
	public const string NodeName = "Sts2CalculatorPanel";

	public PanelContainer Root { get; }

	private readonly LineEdit _inputEdit;
	private readonly Label _expressionEchoLabel;
	private readonly Label _resultLabel;
	private readonly VBoxContainer _keypadContainer;
	private readonly Button _toggleKeypadBtn;
	private readonly VBoxContainer _historyContainer;
	private readonly List<(string Expr, string Result)> _history = new();

	private static bool _isKeypadCollapsed;

	private readonly Font? _regularFont;
	private readonly Font? _boldFont;

	private bool _isDragging;
	private Vector2 _dragStartMouse;
	private Vector2 _dragStartPanel;
	private bool _hasCustomPosition;

	public event Action<bool>? VisibilityChanged;

	public bool IsOpen => Root.Visible;

	public CalculatorPanel()
	{
		if (ResourceLoader.Exists("res://themes/kreon_regular_shared.tres"))
		{
			_regularFont = ResourceLoader.Load<Font>("res://themes/kreon_regular_shared.tres");
		}
		if (ResourceLoader.Exists("res://themes/kreon_bold_shared.tres"))
		{
			_boldFont = ResourceLoader.Load<Font>("res://themes/kreon_bold_shared.tres");
		}

		Root = new PanelContainer
		{
			Name = NodeName,
			Visible = false,
			CustomMinimumSize = new Vector2(360f, 0f),
			Position = new Vector2(1320f, 88f),
			ZIndex = 90,
			MouseFilter = Control.MouseFilterEnum.Stop
		};

		var panelStyle = new StyleBoxFlat
		{
			BgColor = new Color(0.06f, 0.08f, 0.12f, 0.96f),
			BorderColor = new Color(0.937f, 0.784f, 0.318f, 1.0f),
			BorderWidthLeft = 2,
			BorderWidthTop = 2,
			BorderWidthRight = 2,
			BorderWidthBottom = 2,
			CornerRadiusTopLeft = 10,
			CornerRadiusTopRight = 10,
			CornerRadiusBottomLeft = 10,
			CornerRadiusBottomRight = 10,
			ContentMarginLeft = 14f,
			ContentMarginRight = 14f,
			ContentMarginTop = 12f,
			ContentMarginBottom = 14f,
			ShadowColor = new Color(0f, 0f, 0f, 0.5f),
			ShadowSize = 8,
			ShadowOffset = new Vector2(0f, 4f)
		};
		Root.AddThemeStyleboxOverride("panel", panelStyle);

		var mainVbox = new VBoxContainer();
		mainVbox.AddThemeConstantOverride("separation", 10);
		Root.AddChild(mainVbox);

		// 1. Header Bar (draggable + close button)
		var header = new HBoxContainer
		{
			MouseFilter = Control.MouseFilterEnum.Stop
		};
		header.AddThemeConstantOverride("separation", 8);
		header.Connect(Control.SignalName.GuiInput, Callable.From<InputEvent>(OnHeaderGuiInput));
		mainVbox.AddChild(header);

		var titleLabel = new Label
		{
			Text = "Calculator",
			SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
			VerticalAlignment = VerticalAlignment.Center,
			MouseFilter = Control.MouseFilterEnum.Ignore
		};
		ApplyFont(titleLabel, _boldFont, 20, new Color(0.937f, 0.784f, 0.318f, 1.0f));
		header.AddChild(titleLabel);

		_toggleKeypadBtn = CreateStyledButton(
			_isKeypadCollapsed ? "Show Pad" : "Hide Pad",
			new Vector2(82f, 28f),
			isAccent: false,
			isSoftAccent: true
		);
		_toggleKeypadBtn.TooltipText = "Collapse / Expand calculator buttons (keyboard-only mode)";
		_toggleKeypadBtn.Connect(BaseButton.SignalName.Pressed, Callable.From(ToggleKeypad));
		header.AddChild(_toggleKeypadBtn);

		var closeBtn = CreateStyledButton("X", new Vector2(30f, 28f), isAccent: false);
		closeBtn.TooltipText = "Close Calculator (Esc)";
		closeBtn.Connect(BaseButton.SignalName.Pressed, Callable.From(() => SetOpen(false)));
		header.AddChild(closeBtn);

		// 2. Display Screen (Expression echo + Evaluated Result)
		var displayPanel = new PanelContainer
		{
			CustomMinimumSize = new Vector2(0f, 76f)
		};
		var displayStyle = new StyleBoxFlat
		{
			BgColor = new Color(0.09f, 0.14f, 0.17f, 1.0f),
			BorderColor = new Color(0.24f, 0.34f, 0.40f, 1.0f),
			BorderWidthLeft = 1,
			BorderWidthTop = 1,
			BorderWidthRight = 1,
			BorderWidthBottom = 1,
			CornerRadiusTopLeft = 6,
			CornerRadiusTopRight = 6,
			CornerRadiusBottomLeft = 6,
			CornerRadiusBottomRight = 6,
			ContentMarginLeft = 12f,
			ContentMarginRight = 12f,
			ContentMarginTop = 8f,
			ContentMarginBottom = 8f
		};
		displayPanel.AddThemeStyleboxOverride("panel", displayStyle);
		mainVbox.AddChild(displayPanel);

		var displayVbox = new VBoxContainer();
		displayVbox.AddThemeConstantOverride("separation", 2);
		displayPanel.AddChild(displayVbox);

		_expressionEchoLabel = new Label
		{
			Text = "Type an expression (e.g. 2 x 4 + 8 or int(7 x 1.5))",
			HorizontalAlignment = HorizontalAlignment.Right,
			VerticalAlignment = VerticalAlignment.Center,
			AutowrapMode = TextServer.AutowrapMode.Off,
			ClipText = true
		};
		ApplyFont(_expressionEchoLabel, _regularFont, 13, new Color(0.68f, 0.76f, 0.80f, 1.0f));
		displayVbox.AddChild(_expressionEchoLabel);

		_resultLabel = new Label
		{
			Text = "0",
			HorizontalAlignment = HorizontalAlignment.Right,
			VerticalAlignment = VerticalAlignment.Center,
			AutowrapMode = TextServer.AutowrapMode.Off,
			ClipText = true
		};
		ApplyFont(_resultLabel, _boldFont, 30, new Color(1.0f, 0.965f, 0.886f, 1.0f));
		displayVbox.AddChild(_resultLabel);

		// 3. Expression Input Field Row
		var inputRow = new HBoxContainer();
		inputRow.AddThemeConstantOverride("separation", 6);
		mainVbox.AddChild(inputRow);

		_inputEdit = new LineEdit
		{
			Name = "CalculatorExpressionInput",
			PlaceholderText = "2 x 4 + 8  or  int(7 x 1.5)",
			SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
			CustomMinimumSize = new Vector2(0f, 38f),
			KeepEditingOnTextSubmit = true,
			ClearButtonEnabled = false,
			ContextMenuEnabled = false,
			CaretBlink = true
		};
		if (_regularFont != null)
		{
			_inputEdit.AddThemeFontOverride("font", _regularFont);
		}
		_inputEdit.AddThemeFontSizeOverride("font_size", 17);
		_inputEdit.AddThemeColorOverride("font_color", new Color(1.0f, 0.965f, 0.886f, 1.0f));
		_inputEdit.AddThemeColorOverride("font_placeholder_color", new Color(0.50f, 0.56f, 0.62f, 0.85f));
		_inputEdit.AddThemeColorOverride("caret_color", new Color(0.937f, 0.784f, 0.318f, 1.0f));

		var inputNormalStyle = new StyleBoxFlat
		{
			BgColor = new Color(0.04f, 0.06f, 0.09f, 1.0f),
			BorderColor = new Color(0.35f, 0.45f, 0.55f, 1.0f),
			BorderWidthLeft = 1,
			BorderWidthTop = 1,
			BorderWidthRight = 1,
			BorderWidthBottom = 1,
			CornerRadiusTopLeft = 5,
			CornerRadiusTopRight = 5,
			CornerRadiusBottomLeft = 5,
			CornerRadiusBottomRight = 5,
			ContentMarginLeft = 10f,
			ContentMarginRight = 10f,
			ContentMarginTop = 6f,
			ContentMarginBottom = 6f
		};
		var inputFocusStyle = (StyleBoxFlat)inputNormalStyle.Duplicate();
		inputFocusStyle.BorderColor = new Color(0.937f, 0.784f, 0.318f, 1.0f);
		inputFocusStyle.BorderWidthLeft = 2;
		inputFocusStyle.BorderWidthTop = 2;
		inputFocusStyle.BorderWidthRight = 2;
		inputFocusStyle.BorderWidthBottom = 2;

		_inputEdit.AddThemeStyleboxOverride("normal", inputNormalStyle);
		_inputEdit.AddThemeStyleboxOverride("focus", inputFocusStyle);

		_inputEdit.Connect(LineEdit.SignalName.TextChanged, Callable.From<string>(OnInputTextChanged));
		_inputEdit.Connect(LineEdit.SignalName.TextSubmitted, Callable.From<string>(OnInputTextSubmitted));
		_inputEdit.Connect(Control.SignalName.GuiInput, Callable.From<InputEvent>(OnInputGuiInput));
		inputRow.AddChild(_inputEdit);

		var evalBtn = CreateStyledButton("=", new Vector2(44f, 38f), isAccent: true);
		evalBtn.TooltipText = "Evaluate expression (Enter)";
		evalBtn.Connect(BaseButton.SignalName.Pressed, Callable.From(EvaluateAndCommit));
		inputRow.AddChild(evalBtn);

		var clearBtn = CreateStyledButton("C", new Vector2(40f, 38f), isAccent: false);
		clearBtn.TooltipText = "Clear expression";
		clearBtn.Connect(BaseButton.SignalName.Pressed, Callable.From(ClearInput));
		inputRow.AddChild(clearBtn);

		// 4. Collapsible On-Screen Keypad Container (Function Bar + Quick Shortcuts + 4x4 Button Grid)
		_keypadContainer = new VBoxContainer
		{
			Name = "KeypadContainer",
			Visible = !_isKeypadCollapsed
		};
		_keypadContainer.AddThemeConstantOverride("separation", 6);
		mainVbox.AddChild(_keypadContainer);

		var funcRow = new HBoxContainer();
		funcRow.AddThemeConstantOverride("separation", 6);
		_keypadContainer.AddChild(funcRow);

		AddInsertButton(funcRow, "int(", "int(", "Insert int(...)");
		AddInsertButton(funcRow, "(", "(", "Open bracket");
		AddInsertButton(funcRow, ")", ")", "Close bracket");

		var backBtn = CreateStyledButton("Del", new Vector2(0f, 32f), isAccent: false);
		backBtn.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
		backBtn.TooltipText = "Backspace";
		backBtn.Connect(BaseButton.SignalName.Pressed, Callable.From(BackspaceInput));
		funcRow.AddChild(backBtn);

		var shortcutRow = new HBoxContainer();
		shortcutRow.AddThemeConstantOverride("separation", 6);
		_keypadContainer.AddChild(shortcutRow);

		AddInsertButton(shortcutRow, "x0.75", "x0.75", "Multiply by 0.75");
		AddInsertButton(shortcutRow, "x1.5", "x1.5", "Multiply by 1.5");
		AddInsertButton(shortcutRow, "x1.75", "x1.75", "Multiply by 1.75");

		// 5. Keypad Grid
		var grid = new GridContainer
		{
			Columns = 4
		};
		grid.AddThemeConstantOverride("h_separation", 6);
		grid.AddThemeConstantOverride("v_separation", 6);
		_keypadContainer.AddChild(grid);

		string[,] keys =
		{
			{ "7", "8", "9", "/" },
			{ "4", "5", "6", "x" },
			{ "1", "2", "3", "-" }
		};

		for (int r = 0; r < 3; r++)
		{
			for (int c = 0; c < 4; c++)
			{
				string keyText = keys[r, c];
				bool isOperator = c == 3;
				var keyBtn = CreateStyledButton(keyText, new Vector2(0f, 36f), isAccent: isOperator, isSoftAccent: isOperator);
				keyBtn.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
				string insertStr = keyText;
				keyBtn.Connect(BaseButton.SignalName.Pressed, Callable.From(() => InsertTextAtCaret(insertStr)));
				grid.AddChild(keyBtn);
			}
		}

		var bottomRow = new HBoxContainer();
		bottomRow.AddThemeConstantOverride("separation", 6);
		_keypadContainer.AddChild(bottomRow);

		var zeroBtn = CreateStyledButton("0", new Vector2(0f, 36f), isAccent: false);
		zeroBtn.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
		zeroBtn.Connect(BaseButton.SignalName.Pressed, Callable.From(() => InsertTextAtCaret("0")));
		bottomRow.AddChild(zeroBtn);

		var bottomRightRow = new HBoxContainer
		{
			SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
		};
		bottomRightRow.AddThemeConstantOverride("separation", 6);
		bottomRow.AddChild(bottomRightRow);

		var dotBtn = CreateStyledButton(".", new Vector2(0f, 36f), isAccent: false);
		dotBtn.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
		dotBtn.Connect(BaseButton.SignalName.Pressed, Callable.From(() => InsertTextAtCaret(".")));
		bottomRightRow.AddChild(dotBtn);

		var plusBtn = CreateStyledButton("+", new Vector2(0f, 36f), isAccent: true, isSoftAccent: true);
		plusBtn.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
		plusBtn.Connect(BaseButton.SignalName.Pressed, Callable.From(() => InsertTextAtCaret("+")));
		bottomRightRow.AddChild(plusBtn);

		// 6. Recent Calculations History
		_historyContainer = new VBoxContainer
		{
			Visible = false
		};
		_historyContainer.AddThemeConstantOverride("separation", 4);
		mainVbox.AddChild(_historyContainer);
	}

	/// <summary>
	/// Returns true if the calculator's LineEdit currently has GUI focus and is actively editing.
	/// </summary>
	public static bool IsCalculatorInputEditing(Viewport? viewport)
	{
		if (viewport == null)
		{
			return false;
		}
		Control? focusOwner = viewport.GuiGetFocusOwner();
		if (focusOwner is LineEdit lineEdit && lineEdit.Name == "CalculatorExpressionInput")
		{
			return lineEdit.IsEditing() || lineEdit.HasFocus();
		}
		return false;
	}

	/// <summary>
	/// Returns true if the calculator's input box currently has focus or is actively editing.
	/// </summary>
	public bool IsInputEditing()
	{
		return GodotObject.IsInstanceValid(_inputEdit) && (_inputEdit.IsEditing() || _inputEdit.HasFocus());
	}

	/// <summary>
	/// Returns true if the current mouse position lies within the calculator panel bounds.
	/// </summary>
	public bool ContainsGlobalMousePosition()
	{
		return GodotObject.IsInstanceValid(Root) && Root.GetGlobalRect().HasPoint(Root.GetGlobalMousePosition());
	}

	/// <summary>
	/// Exits editing and releases GUI focus from the calculator input box while keeping the panel open.
	/// </summary>
	public void ReleaseInputFocus()
	{
		if (!GodotObject.IsInstanceValid(_inputEdit))
		{
			return;
		}
		if (_inputEdit.IsEditing())
		{
			_inputEdit.Unedit();
		}
		if (_inputEdit.HasFocus())
		{
			_inputEdit.ReleaseFocus();
		}
	}

	public void Toggle(Control anchorButton)
	{
		SetOpen(!Root.Visible, anchorButton);
	}

	public void ToggleKeypad()
	{
		PlayClickSfx();
		SetKeypadCollapsed(!_isKeypadCollapsed);
	}

	public void SetKeypadCollapsed(bool collapsed)
	{
		_isKeypadCollapsed = collapsed;
		_keypadContainer.Visible = !collapsed;
		_toggleKeypadBtn.Text = collapsed ? "Show Pad" : "Hide Pad";
		Root.ResetSize();
		Callable.From(() =>
		{
			if (GodotObject.IsInstanceValid(Root))
			{
				Root.ResetSize();
			}
		}).CallDeferred();
		FocusInput();
	}

	public void SetOpen(bool open, Control? anchorButton = null)
	{
		if (Root.Visible == open)
		{
			return;
		}

		if (open && !_hasCustomPosition && anchorButton != null && GodotObject.IsInstanceValid(anchorButton))
		{
			PositionBelowAnchor(anchorButton);
		}

		Root.Visible = open;
		VisibilityChanged?.Invoke(open);

		if (open)
		{
			Callable.From(FocusInput).CallDeferred();
		}
		else
		{
			ReleaseInputFocus();
		}
	}

	private void PositionBelowAnchor(Control anchorButton)
	{
		Vector2 viewportSize = Root.GetViewportRect().Size;
		if (viewportSize.X <= 0f)
		{
			viewportSize = new Vector2(1920f, 1080f);
		}

		float panelWidth = Math.Max(Root.Size.X, Root.CustomMinimumSize.X);
		float targetX = anchorButton.GlobalPosition.X + anchorButton.Size.X - panelWidth;
		targetX = Mathf.Clamp(targetX, 16f, Math.Max(16f, viewportSize.X - panelWidth - 16f));
		float targetY = Math.Max(88f, anchorButton.GlobalPosition.Y + anchorButton.Size.Y + 8f);

		Root.Position = new Vector2(targetX, targetY);
	}

	private void FocusInput()
	{
		if (!GodotObject.IsInstanceValid(_inputEdit) || !Root.Visible)
		{
			return;
		}
		_inputEdit.GrabFocus();
		_inputEdit.Edit();
		_inputEdit.CaretColumn = _inputEdit.Text.Length;
	}

	private void OnInputGuiInput(InputEvent inputEvent)
	{
		if (inputEvent is InputEventKey keyEvent && keyEvent.Pressed && keyEvent.Keycode == Key.Escape)
		{
			SetOpen(false);
			Root.GetViewport()?.SetInputAsHandled();
		}
	}

	private void OnInputTextChanged(string newText)
	{
		if (string.IsNullOrWhiteSpace(newText))
		{
			_expressionEchoLabel.Text = "Type an expression (e.g. 2 x 4 + 8 or int(7 x 1.5))";
			_resultLabel.Text = "0";
			_resultLabel.AddThemeColorOverride("font_color", new Color(1.0f, 0.965f, 0.886f, 1.0f));
			return;
		}

		EvaluationResult eval = ExpressionEvaluator.Evaluate(newText);
		if (eval.IsSuccess)
		{
			_expressionEchoLabel.Text = $"{newText.Trim()} =";
			_resultLabel.Text = eval.FormattedValue;
			_resultLabel.AddThemeColorOverride("font_color", new Color(1.0f, 0.965f, 0.886f, 1.0f));
		}
		else
		{
			_expressionEchoLabel.Text = newText.Trim();
			// Keep showing "..." while user is actively typing an incomplete expression
			_resultLabel.Text = "...";
			_resultLabel.AddThemeColorOverride("font_color", new Color(0.65f, 0.72f, 0.78f, 1.0f));
		}
	}

	private void OnInputTextSubmitted(string _)
	{
		EvaluateAndCommit();
	}

	private void EvaluateAndCommit()
	{
		string expr = _inputEdit.Text.Trim();
		if (string.IsNullOrWhiteSpace(expr))
		{
			return;
		}

		PlayClickSfx();
		EvaluationResult eval = ExpressionEvaluator.Evaluate(expr);
		if (eval.IsSuccess)
		{
			_expressionEchoLabel.Text = $"{expr} =";
			_resultLabel.Text = eval.FormattedValue;
			_resultLabel.AddThemeColorOverride("font_color", new Color(0.55f, 0.95f, 0.65f, 1.0f));

			AddHistoryEntry(expr, eval.FormattedValue);
		}
		else
		{
			_expressionEchoLabel.Text = eval.ErrorMessage ?? "Invalid expression";
			_resultLabel.Text = "Error";
			_resultLabel.AddThemeColorOverride("font_color", new Color(1.0f, 0.42f, 0.42f, 1.0f));
		}

		FocusInput();
	}

	private void ClearInput()
	{
		PlayClickSfx();
		_inputEdit.Text = string.Empty;
		OnInputTextChanged(string.Empty);
		FocusInput();
	}

	private void BackspaceInput()
	{
		PlayClickSfx();
		string text = _inputEdit.Text;
		if (string.IsNullOrEmpty(text))
		{
			FocusInput();
			return;
		}

		int caret = Mathf.Clamp(_inputEdit.CaretColumn, 0, text.Length);
		if (caret > 0)
		{
			_inputEdit.Text = text.Remove(caret - 1, 1);
			_inputEdit.CaretColumn = caret - 1;
		}
		else
		{
			_inputEdit.Text = text.Substring(0, text.Length - 1);
			_inputEdit.CaretColumn = _inputEdit.Text.Length;
		}

		OnInputTextChanged(_inputEdit.Text);
		FocusInput();
	}

	private void InsertTextAtCaret(string insert)
	{
		PlayClickSfx();
		string current = _inputEdit.Text ?? string.Empty;
		int caret = Mathf.Clamp(_inputEdit.CaretColumn, 0, current.Length);
		string updated = current.Insert(caret, insert);
		_inputEdit.Text = updated;
		_inputEdit.CaretColumn = caret + insert.Length;
		OnInputTextChanged(updated);
		FocusInput();
	}

	private void AddHistoryEntry(string expr, string result)
	{
		if (_history.Count > 0 && _history[0].Expr == expr && _history[0].Result == result)
		{
			return;
		}

		_history.Insert(0, (expr, result));
		if (_history.Count > 3)
		{
			_history.RemoveAt(_history.Count - 1);
		}

		RebuildHistoryUi();
	}

	private void RebuildHistoryUi()
	{
		foreach (Node child in _historyContainer.GetChildren())
		{
			child.QueueFree();
		}

		if (_history.Count == 0)
		{
			_historyContainer.Visible = false;
			return;
		}

		_historyContainer.Visible = true;
		var sep = new HSeparator();
		_historyContainer.AddChild(sep);

		foreach (var (expr, res) in _history)
		{
			var itemBtn = new Button
			{
				Text = $"{expr} = {res}",
				Alignment = HorizontalAlignment.Right,
				CustomMinimumSize = new Vector2(0f, 26f),
				FocusMode = Control.FocusModeEnum.None,
				TooltipText = "Click to load expression"
			};
			if (_regularFont != null)
			{
				itemBtn.AddThemeFontOverride("font", _regularFont);
			}
			itemBtn.AddThemeFontSizeOverride("font_size", 13);
			itemBtn.AddThemeColorOverride("font_color", new Color(0.75f, 0.82f, 0.88f, 1.0f));
			itemBtn.AddThemeColorOverride("font_hover_color", new Color(0.937f, 0.784f, 0.318f, 1.0f));

			var emptyStyle = new StyleBoxEmpty();
			itemBtn.AddThemeStyleboxOverride("normal", emptyStyle);
			itemBtn.AddThemeStyleboxOverride("hover", emptyStyle);
			itemBtn.AddThemeStyleboxOverride("pressed", emptyStyle);

			string capturedExpr = expr;
			itemBtn.Connect(BaseButton.SignalName.Pressed, Callable.From(() =>
			{
				PlayClickSfx();
				_inputEdit.Text = capturedExpr;
				_inputEdit.CaretColumn = capturedExpr.Length;
				OnInputTextChanged(capturedExpr);
				FocusInput();
			}));
			_historyContainer.AddChild(itemBtn);
		}
	}

	private void AddInsertButton(HBoxContainer parent, string label, string insertText, string tooltip)
	{
		var btn = CreateStyledButton(label, new Vector2(0f, 32f), isAccent: false, isSoftAccent: true);
		btn.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
		btn.TooltipText = tooltip;
		btn.Connect(BaseButton.SignalName.Pressed, Callable.From(() => InsertTextAtCaret(insertText)));
		parent.AddChild(btn);
	}

	private Button CreateStyledButton(string text, Vector2 minSize, bool isAccent, bool isSoftAccent = false)
	{
		var btn = new Button
		{
			Text = text,
			CustomMinimumSize = minSize,
			FocusMode = Control.FocusModeEnum.None
		};

		Font? font = isAccent ? (_boldFont ?? _regularFont) : (_regularFont ?? _boldFont);
		if (font != null)
		{
			btn.AddThemeFontOverride("font", font);
		}
		btn.AddThemeFontSizeOverride("font_size", 16);

		Color textColor = isAccent && !isSoftAccent
			? new Color(0.08f, 0.10f, 0.14f, 1.0f)
			: (isSoftAccent ? new Color(0.95f, 0.82f, 0.42f, 1.0f) : new Color(0.95f, 0.92f, 0.85f, 1.0f));
		btn.AddThemeColorOverride("font_color", textColor);
		btn.AddThemeColorOverride("font_hover_color", isAccent && !isSoftAccent ? new Color(0.04f, 0.05f, 0.08f, 1.0f) : Colors.White);
		btn.AddThemeColorOverride("font_pressed_color", textColor);

		Color normalBg = isAccent && !isSoftAccent
			? new Color(0.90f, 0.72f, 0.26f, 1.0f)
			: (isSoftAccent ? new Color(0.16f, 0.22f, 0.29f, 1.0f) : new Color(0.12f, 0.17f, 0.23f, 1.0f));
		Color hoverBg = isAccent && !isSoftAccent
			? new Color(0.98f, 0.82f, 0.36f, 1.0f)
			: (isSoftAccent ? new Color(0.22f, 0.30f, 0.38f, 1.0f) : new Color(0.18f, 0.25f, 0.33f, 1.0f));
		Color pressedBg = isAccent && !isSoftAccent
			? new Color(0.75f, 0.58f, 0.18f, 1.0f)
			: new Color(0.09f, 0.13f, 0.18f, 1.0f);
		Color borderCol = isAccent || isSoftAccent
			? new Color(0.72f, 0.58f, 0.24f, 0.9f)
			: new Color(0.28f, 0.37f, 0.45f, 0.9f);

		btn.AddThemeStyleboxOverride("normal", CreateButtonBox(normalBg, borderCol));
		btn.AddThemeStyleboxOverride("hover", CreateButtonBox(hoverBg, new Color(0.937f, 0.784f, 0.318f, 1.0f)));
		btn.AddThemeStyleboxOverride("pressed", CreateButtonBox(pressedBg, borderCol));

		return btn;
	}

	private static StyleBoxFlat CreateButtonBox(Color bg, Color border) =>
		new()
		{
			BgColor = bg,
			BorderColor = border,
			BorderWidthLeft = 1,
			BorderWidthTop = 1,
			BorderWidthRight = 1,
			BorderWidthBottom = 1,
			CornerRadiusTopLeft = 5,
			CornerRadiusTopRight = 5,
			CornerRadiusBottomLeft = 5,
			CornerRadiusBottomRight = 5,
			ContentMarginLeft = 6f,
			ContentMarginRight = 6f,
			ContentMarginTop = 4f,
			ContentMarginBottom = 4f
		};

	private static void ApplyFont(Label label, Font? font, int size, Color color)
	{
		if (font != null)
		{
			label.AddThemeFontOverride("font", font);
		}
		label.AddThemeFontSizeOverride("font_size", size);
		label.AddThemeColorOverride("font_color", color);
	}

	private void OnHeaderGuiInput(InputEvent inputEvent)
	{
		if (inputEvent is InputEventMouseButton mouseBtn && mouseBtn.ButtonIndex == MouseButton.Left)
		{
			if (mouseBtn.Pressed)
			{
				_isDragging = true;
				_dragStartMouse = Root.GetGlobalMousePosition();
				_dragStartPanel = Root.Position;
				Root.GetViewport()?.SetInputAsHandled();
			}
			else
			{
				_isDragging = false;
			}
		}
		else if (inputEvent is InputEventMouseMotion && _isDragging)
		{
			Vector2 delta = Root.GetGlobalMousePosition() - _dragStartMouse;
			Vector2 target = _dragStartPanel + delta;
			Vector2 viewportSize = Root.GetViewportRect().Size;
			if (viewportSize.X > 0f && viewportSize.Y > 0f)
			{
				float maxX = Math.Max(0f, viewportSize.X - Root.Size.X);
				float maxY = Math.Max(80f, viewportSize.Y - Root.Size.Y);
				target.X = Mathf.Clamp(target.X, 0f, maxX);
				target.Y = Mathf.Clamp(target.Y, 80f, maxY);
			}
			Root.Position = target;
			_hasCustomPosition = true;
			Root.GetViewport()?.SetInputAsHandled();
		}
	}

	private static void PlayClickSfx()
	{
		try
		{
			SfxCmd.Play("event:/sfx/ui/clicks/ui_click");
		}
		catch
		{
			// Ignore if audio unavailable
		}
	}
}
