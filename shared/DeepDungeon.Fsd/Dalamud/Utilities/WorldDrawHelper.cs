using System;
using System.Numerics;
using global::Dalamud.Bindings.ImGui;
using global::Dalamud.Plugin.Services;

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
		/// FSD overload — forwards with the plugin's own Service.GameGui.
		/// </summary>
		public static void DrawWorldTriangleFilled(Vector3 a, Vector3 b, Vector3 c, uint color) =>
			DrawWorldTriangleFilled(ImGui.GetBackgroundDrawList(), Service.GameGui, a, b, c, color);

		/// <summary>
		/// Draw a world-space triangle clipped against the camera front
		/// half-space — the visible remainder is filled as a convex fan of
		/// the clipped boundary points. Projection uses the 3-argument
		/// WorldToScreen and rejects the WHOLE triangle on a nonfinite
		/// result — individual vertices are never dropped and reconnected.
		/// </summary>
		public static void DrawWorldTriangleFilled(
			ImDrawListPtr draw, IGameGui gameGui,
			Vector3 a, Vector3 b, Vector3 c, uint color)
		{
			Span<Vector3> clipped = stackalloc Vector3[4];
			var count = 0;
			Span<Vector3> tri = stackalloc Vector3[] { a, b, c };
			for (var i = 0; i < 3; i++)
			{
				var cur = tri[i];
				var nxt = tri[(i + 1) % 3];
				var curIn = TryProjectWorld(gameGui, cur, out _);
				var nxtIn = TryProjectWorld(gameGui, nxt, out _);
				if (curIn)
					clipped[count++] = cur;
				if (curIn != nxtIn && count < clipped.Length)
				{
					// bounded 16-step edge crossing, same math as line clip
					var lo = curIn ? nxt : cur;
					var hi = curIn ? cur : nxt;
					var crossing = hi;
					for (var s = 0; s < 16; s++)
					{
						var mid = (lo + hi) * 0.5f;
						if (TryProjectWorld(gameGui, mid, out _))
						{ crossing = mid; hi = mid; }
						else lo = mid;
					}
					clipped[count++] = crossing;
				}
			}
			if (count < 3)
				return;
			Span<Vector2> screen = stackalloc Vector2[4];
			for (var i = 0; i < count; i++)
				if (!TryProjectWorld(gameGui, clipped[i], out screen[i]))
					return;   // reject the whole triangle, never reconnect
			for (var i = 1; i < count - 1; i++)
				draw.AddTriangleFilled(screen[0], screen[i], screen[i + 1], color);
		}

		/// <summary>FSD overload — forwards with Service.GameGui.</summary>
		public static bool TryClipWorldLineToScreen(Vector3 a, Vector3 b, out Vector2 screenA, out Vector2 screenB) =>
			TryClipWorldLineToScreen(Service.GameGui, a, b, out screenA, out screenB);

		/// <summary>
		/// Clip a world-space line against the camera near plane and return screen endpoints.
		/// When one endpoint is behind the camera, binary-searches to find the near-plane crossing.
		/// </summary>
		public static bool TryClipWorldLineToScreen(
			IGameGui gameGui, Vector3 a, Vector3 b, out Vector2 screenA, out Vector2 screenB)
		{
			try
			{
				var aConv = TryProjectWorld(gameGui, a, out var aScreen);
				var bConv = TryProjectWorld(gameGui, b, out var bScreen);

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
					if (FindClippedScreenPoint(gameGui, b, a, out var clipped))
					{
						screenA = aScreen;
						screenB = clipped;
						return true;
					}
				}
				else
				{
					if (FindClippedScreenPoint(gameGui, a, b, out var clipped))
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

		/// <summary>3-argument WorldToScreen + finite check — the only
		/// projection primitive used by the clipped-fill paths.</summary>
		private static bool TryProjectWorld(IGameGui gameGui, Vector3 pos, out Vector2 screen)
		{
			var ok = gameGui.WorldToScreen(pos, out screen, out _);
			if (ok && float.IsFinite(screen.X) && float.IsFinite(screen.Y))
				return true;
			screen = default;
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

		private static bool FindClippedScreenPoint(IGameGui gameGui, Vector3 behind, Vector3 front, out Vector2 screen)
		{
			screen = default;
			try
			{
				if (!TryProjectWorld(gameGui, front, out var frontScreen))
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

					if (TryProjectWorld(gameGui, mid, out var midScreen))
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
