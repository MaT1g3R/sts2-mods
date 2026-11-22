using System;
using Godot;

namespace Sts2Calculator.UI;

/// <summary>
/// Generates a Slay the Spire 2 styled calculator icon texture for the TopBar.
/// Uses the same dark-slate outline, warm gold/parchment palette, and drop shadow as native TopBar icons.
/// </summary>
public static class CalculatorIconGenerator
{
	private static ImageTexture? _cachedTexture;

	public static ImageTexture GetOrCreateIconTexture()
	{
		if (_cachedTexture != null)
		{
			return _cachedTexture;
		}

		const int size = 72;
		Image img = Image.CreateEmpty(size, size, useMipmaps: false, Image.Format.Rgba8);
		img.Fill(new Color(0f, 0f, 0f, 0f));

		// STS2 TopBar palette
		Color shadowColor = new(0.0f, 0.0f, 0.0f, 0.32f);
		Color outlineColor = new(0.098f, 0.161f, 0.188f, 1.0f);
		Color frameTop = new(0.93f, 0.79f, 0.42f, 1.0f);
		Color frameBottom = new(0.68f, 0.50f, 0.22f, 1.0f);
		Color frameHighlight = new(0.99f, 0.93f, 0.70f, 1.0f);
		Color bodyPlate = new(0.15f, 0.21f, 0.26f, 1.0f);
		Color screenBorder = new(0.08f, 0.13f, 0.16f, 1.0f);
		Color screenBgTop = new(0.12f, 0.30f, 0.29f, 1.0f);
		Color screenBgBottom = new(0.17f, 0.40f, 0.37f, 1.0f);
		Color screenDigit = new(0.88f, 0.98f, 0.90f, 0.95f);

		Color keyCream = new(0.95f, 0.90f, 0.80f, 1.0f);
		Color keyCreamShadow = new(0.72f, 0.66f, 0.56f, 1.0f);
		Color keyOpGold = new(0.94f, 0.74f, 0.30f, 1.0f);
		Color keyOpGoldShadow = new(0.72f, 0.52f, 0.16f, 1.0f);
		Color keyEqAmber = new(0.95f, 0.48f, 0.22f, 1.0f);
		Color keyEqAmberShadow = new(0.72f, 0.30f, 0.12f, 1.0f);

		// 1. Soft drop shadow
		DrawRoundedRect(img, 16, 10, 44, 56, 9f, shadowColor, blend: true);

		// 2. Outer dark-slate outline (matches STS2 top bar icons)
		DrawRoundedRect(img, 13, 7, 46, 58, 9f, outlineColor);

		// 3. Metallic gold/brass casing with vertical gradient
		DrawRoundedRectGradient(img, 16, 10, 40, 52, 6.5f, frameTop, frameBottom);

		// Top bevel highlight on casing
		DrawRoundedRect(img, 17, 11, 38, 3, 2f, frameHighlight);

		// 4. Inner dark slate face plate
		DrawRoundedRect(img, 19, 14, 34, 44, 4f, bodyPlate);

		// 5. Recessed LCD screen at top of face plate
		DrawRoundedRect(img, 21, 16, 30, 14, 2.5f, screenBorder);
		DrawRoundedRectGradient(img, 22, 17, 28, 12, 2f, screenBgTop, screenBgBottom);

		// Draw crisp stylized digits ("16") on the right side of the LCD screen
		// Digit '1'
		FillRect(img, 38, 19, 2, 8, screenDigit);
		SetPixelSafe(img, 37, 20, screenDigit);
		// Digit '6'
		FillRect(img, 42, 19, 5, 2, screenDigit);
		FillRect(img, 42, 19, 2, 8, screenDigit);
		FillRect(img, 42, 22, 5, 2, screenDigit);
		FillRect(img, 45, 22, 2, 5, screenDigit);
		FillRect(img, 42, 25, 5, 2, screenDigit);

		// 6. 3x3 Keypad Grid below screen
		// Columns: x = 21..29 (w=8), 32..40 (w=8), 43..51 (w=8)
		// Rows:    y = 33..39 (h=6), 41..47 (h=6), 49..55 (h=6)
		int[] colX = { 21, 32, 43 };
		int[] rowY = { 33, 41, 49 };
		const int keyW = 8;
		const int keyH = 6;

		for (int r = 0; r < 3; r++)
		{
			for (int c = 0; c < 3; c++)
			{
				int kx = colX[c];
				int ky = rowY[r];

				Color topCol;
				Color botCol;
				if (r == 2 && c == 2)
				{
					// Bottom-right '=' button in warm ember-orange
					topCol = keyEqAmber;
					botCol = keyEqAmberShadow;
				}
				else if (c == 2)
				{
					// Right column operator buttons in gold
					topCol = keyOpGold;
					botCol = keyOpGoldShadow;
				}
				else
				{
					// Number buttons in parchment cream
					topCol = keyCream;
					botCol = keyCreamShadow;
				}

				// Key outline / slot
				FillRect(img, kx - 1, ky - 1, keyW + 2, keyH + 2, screenBorder);
				// Key bevel shadow
				FillRect(img, kx, ky + 1, keyW, keyH - 1, botCol);
				// Key top face
				FillRect(img, kx, ky, keyW, keyH - 1, topCol);
			}
		}

		_cachedTexture = ImageTexture.CreateFromImage(img);
		return _cachedTexture;
	}

	private static void FillRect(Image img, int x, int y, int w, int h, Color color)
	{
		int maxX = Math.Min(img.GetWidth(), x + w);
		int maxY = Math.Min(img.GetHeight(), y + h);
		int startX = Math.Max(0, x);
		int startY = Math.Max(0, y);

		for (int py = startY; py < maxY; py++)
		{
			for (int px = startX; px < maxX; px++)
			{
				img.SetPixel(px, py, color);
			}
		}
	}

	private static void SetPixelSafe(Image img, int x, int y, Color color)
	{
		if (x >= 0 && x < img.GetWidth() && y >= 0 && y < img.GetHeight())
		{
			img.SetPixel(x, y, color);
		}
	}

	private static void DrawRoundedRect(Image img, int x, int y, int w, int h, float radius, Color color, bool blend = false)
	{
		DrawRoundedRectGradient(img, x, y, w, h, radius, color, color, blend);
	}

	private static void DrawRoundedRectGradient(
		Image img,
		int x,
		int y,
		int w,
		int h,
		float radius,
		Color topColor,
		Color bottomColor,
		bool blend = false)
	{
		int minX = Math.Max(0, x - 1);
		int maxX = Math.Min(img.GetWidth() - 1, x + w + 1);
		int minY = Math.Max(0, y - 1);
		int maxY = Math.Min(img.GetHeight() - 1, y + h + 1);

		float left = x + radius;
		float right = x + w - radius;
		float top = y + radius;
		float bottom = y + h - radius;

		for (int py = minY; py <= maxY; py++)
		{
			float t = h > 1 ? Mathf.Clamp((py - y) / (float)(h - 1), 0f, 1f) : 0f;
			Color baseCol = topColor.Lerp(bottomColor, t);

			for (int px = minX; px <= maxX; px++)
			{
				float cx = px + 0.5f;
				float cy = py + 0.5f;

				float dx = MathF.Max(MathF.Max(left - cx, cx - right), 0f);
				float dy = MathF.Max(MathF.Max(top - cy, cy - bottom), 0f);
				float dist = MathF.Sqrt(dx * dx + dy * dy) - radius;

				if (dist <= 0.5f)
				{
					float alpha = Mathf.Clamp(0.5f - dist, 0f, 1f);
					Color c = new(baseCol.R, baseCol.G, baseCol.B, baseCol.A * alpha);
					if (blend)
					{
						Color dst = img.GetPixel(px, py);
						img.SetPixel(px, py, BlendOver(dst, c));
					}
					else if (alpha >= 0.99f)
					{
						img.SetPixel(px, py, baseCol);
					}
					else
					{
						Color dst = img.GetPixel(px, py);
						img.SetPixel(px, py, BlendOver(dst, c));
					}
				}
			}
		}
	}

	private static Color BlendOver(Color dst, Color src)
	{
		float outA = src.A + dst.A * (1f - src.A);
		if (outA <= 1e-5f)
		{
			return new Color(0f, 0f, 0f, 0f);
		}
		float r = (src.R * src.A + dst.R * dst.A * (1f - src.A)) / outA;
		float g = (src.G * src.A + dst.G * dst.A * (1f - src.A)) / outA;
		float b = (src.B * src.A + dst.B * dst.A * (1f - src.A)) / outA;
		return new Color(r, g, b, outA);
	}
}
