using System;
using System.Numerics;
using global::Dalamud.Bindings.ImGui;

namespace DeepDungeon.Fsd.Dalamud
{
	public static class WorldDrawHelper
	{
		/// <summary>
		/// Draw a world-space line with near-plane clipping.
		/// </summary>
		public static void DrawWorldLine(Vector3 start, Vector3 end, uint color, float thickness = 2f)
		{
			if (TryClipWorldLineToScreen(start, end, out var a, out var b))
				ImGui.GetBackgroundDrawList().AddLine(a, b, color, thickness);
		}

		/// <summary>
		/// Draw a world-space circle (polygon outline on the XZ plane).
		/// </summary>
		public static void DrawWorldCircle(Vector3 center, float radius, uint color, int segments = 8, float thickness = 2f)
		{
			var prev = center + new Vector3(0, 0, radius);
			for (int i = 1; i <= segments; i++)
			{
				float angle = i * 2f * MathF.PI / segments;
				var curr = center + radius * new Vector3(MathF.Sin(angle), 0, MathF.Cos(angle));
				DrawWorldLine(curr, prev, color, thickness);
				prev = curr;
			}
		}

		/// <summary>
		/// Draw text at a world position (font size 32, matching DebugDrawer convention).
		/// </summary>
		public static void DrawWorldText(Vector3 pos, uint color, string text)
		{
			if (Service.GameGui.WorldToScreen(pos, out var screenPos))
				ImGui.GetBackgroundDrawList().AddText(ImGui.GetFont(), 32, screenPos, color, text);
		}

		/// <summary>
		/// Draw a filled circle flat on the XZ ground plane at a world position.
		/// </summary>
		public static void DrawWorldFilledCircle(Vector3 center, float worldRadius, uint color, int segments = 24)
		{
			Span<Vector2> screenPoints = stackalloc Vector2[segments];
			for (int i = 0; i < segments; i++)
			{
				float angle = i * 2f * MathF.PI / segments;
				var worldPt = center + worldRadius * new Vector3(MathF.Sin(angle), 0, MathF.Cos(angle));
				if (!Service.GameGui.WorldToScreen(worldPt, out screenPoints[i]))
					return;
			}
			var dl = ImGui.GetBackgroundDrawList();
			for (int i = 1; i < segments - 1; i++)
				dl.AddTriangleFilled(screenPoints[0], screenPoints[i], screenPoints[i + 1], color);
		}

		/// <summary>
		/// Clip a world-space line against the camera near plane and return screen endpoints.
		/// When one endpoint is behind the camera, binary-searches to find the near-plane crossing.
		/// </summary>
		public static bool TryClipWorldLineToScreen(Vector3 a, Vector3 b, out Vector2 screenA, out Vector2 screenB)
		{
			try
			{
				var aConv = Service.GameGui.WorldToScreen(a, out var aScreen, out var _);
				var bConv = Service.GameGui.WorldToScreen(b, out var bScreen, out var _);

				if (aConv && bConv)
				{
					screenA = aScreen;
					screenB = bScreen;
					return true;
				}

				if (!aConv && !bConv)
				{
					screenA = default;
					screenB = default;
					return false;
				}

				if (aConv)
				{
					if (FindClippedScreenPoint(b, a, out var clipped))
					{
						screenA = aScreen;
						screenB = clipped;
						return true;
					}
				}
				else
				{
					if (FindClippedScreenPoint(a, b, out var clipped))
					{
						screenA = clipped;
						screenB = bScreen;
						return true;
					}
				}
			}
			catch
			{
			}
			screenA = default;
			screenB = default;
			return false;
		}

		/// <summary>
		/// Draw a text label at a world position with background rect and optional border.
		/// <paramref name="anchorY"/> controls vertical alignment: 0.5 = centered on screen point,
		/// 1.0 = text bottom at screen point (label appears above).
		/// Returns false if the world position is not convertible to screen.
		/// </summary>
		public static bool DrawWorldLabel(
			ImDrawListPtr drawList,
			Vector3 worldPos,
			string text,
			uint textColor,
			uint bgColor,
			uint borderColor = 0,
			float anchorY = 0.5f,
			float rounding = 0f,
			Vector2? padding = null)
		{
			if (!Service.GameGui.WorldToScreen(worldPos, out var screenPos))
				return false;

			var textSize = ImGui.CalcTextSize(text);
			var textPos = new Vector2(screenPos.X - textSize.X / 2, screenPos.Y - textSize.Y * anchorY);
			var pad = padding ?? new Vector2(6f, 4f);
			var rectMin = textPos - pad;
			var rectMax = textPos + textSize + pad;

			drawList.AddRectFilled(rectMin, rectMax, bgColor, rounding);
			if (borderColor != 0)
				drawList.AddRect(rectMin, rectMax, borderColor, rounding);
			drawList.AddText(textPos, textColor, text);
			return true;
		}

		private static bool FindClippedScreenPoint(Vector3 behind, Vector3 front, out Vector2 screen)
		{
			screen = default;
			try
			{
				if (!Service.GameGui.WorldToScreen(front, out var frontScreen, out var _))
					return false;

				var lo = behind;
				var hi = front;
				var result = frontScreen;

				for (int i = 0; i < 16; i++)
				{
					var mid = new Vector3(
						(lo.X + hi.X) * 0.5f,
						(lo.Y + hi.Y) * 0.5f,
						(lo.Z + hi.Z) * 0.5f
					);

					if (Service.GameGui.WorldToScreen(mid, out var midScreen, out var _))
					{
						result = midScreen;
						hi = mid;
					}
					else
					{
						lo = mid;
					}
				}

				screen = result;
				return true;
			}
			catch
			{
				return false;
			}
		}
	}
}
