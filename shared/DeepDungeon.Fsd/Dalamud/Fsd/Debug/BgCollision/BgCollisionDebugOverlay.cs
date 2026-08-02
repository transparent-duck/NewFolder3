using DeepDungeon.Fsd.Dalamud;
using global::Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision.Math;
using System;
using System.Globalization;
using System.Numerics;

namespace DeepDungeon.Fsd.Dalamud.Debug.BgCollision;

internal unsafe class BgCollisionDebugOverlay
{
	private const ulong DefaultMaterialValue = 0x6400;
	private const ulong DefaultMaterialMask = 0x1FFFFFFFFF;

	private ulong _materialValue = DefaultMaterialValue;
	private ulong _materialMask = DefaultMaterialMask;
	private string _materialValueText = DefaultMaterialValue.ToString("X");
	private string _materialMaskText = DefaultMaterialMask.ToString("X");
	private bool _materialValueInvalid;
	private bool _materialMaskInvalid;

	private static readonly uint BoxColor = ImGui.ColorConvertFloat4ToU32(new Vector4(0.96f, 0.54f, 0.23f, 1f));
	private static readonly uint CylinderColor = ImGui.ColorConvertFloat4ToU32(new Vector4(0.65f, 0.93f, 0.29f, 1f));
	private static readonly uint SphereColor = ImGui.ColorConvertFloat4ToU32(new Vector4(0.35f, 0.82f, 1.0f, 1f));
	private static readonly uint PlaneColor = ImGui.ColorConvertFloat4ToU32(new Vector4(0.98f, 0.88f, 0.25f, 0.95f));
	private static readonly uint PlaneTwoSidedColor = ImGui.ColorConvertFloat4ToU32(new Vector4(0.93f, 0.46f, 0.82f, 0.95f));
	private static readonly (int, int)[] BoxEdges =
	{
		(0, 1), (0, 2), (1, 3), (2, 3),
		(4, 5), (4, 6), (5, 7), (6, 7),
		(0, 4), (1, 5), (2, 6), (3, 7)
	};

	public void DrawConfigUi()
	{
		ImGui.Spacing();
		ImGui.TextColored(new Vector4(0.65f, 0.85f, 1f, 1f), "BG collision overlay");
		ImGui.TextWrapped("Shows analytic BGCollision primitives (box, cylinder, sphere, plane) whose materials satisfy (value & mask) == (collider & mask).");

		DrawHexInput("Material value (hex)", ref _materialValueText, ref _materialValue, ref _materialValueInvalid);
		DrawHexInput("Material mask (hex)", ref _materialMaskText, ref _materialMask, ref _materialMaskInvalid);

		ImGui.SameLine();
		if (ImGui.SmallButton("Reset##bgcollision"))
			ResetFilter();
	}

	public void DrawWorldOverlay()
	{
		var framework = Framework.Instance();
		if (framework == null)
			return;
		var module = framework->BGCollisionModule;
		if (module == null)
			return;
		var sceneManager = module->SceneManager;
		if (sceneManager == null)
			return;

		foreach (var sceneWrapper in sceneManager->Scenes)
		{
			var scene = sceneWrapper->Scene;
			if (scene == null)
				continue;

			foreach (var collider in scene->Colliders)
			{
				if (collider == null)
					continue;

				var type = collider->GetColliderType();
				if (!IsAnalytic(type) || !MatchesMaterial(collider))
					continue;

				switch (type)
				{
					case ColliderType.Box:
						DrawBox((ColliderBox*)collider);
						break;
					case ColliderType.Cylinder:
						DrawCylinder((ColliderCylinder*)collider);
						break;
					case ColliderType.Sphere:
						DrawSphere((ColliderSphere*)collider);
						break;
					case ColliderType.Plane:
						DrawPlane((ColliderPlane*)collider, PlaneColor);
						break;
					case ColliderType.PlaneTwoSided:
						DrawPlane((ColliderPlane*)collider, PlaneTwoSidedColor);
						break;
				}
			}
		}
	}

	private static bool IsAnalytic(ColliderType type) =>
		type is ColliderType.Box or ColliderType.Cylinder or ColliderType.Sphere or ColliderType.Plane or ColliderType.PlaneTwoSided;

	private bool MatchesMaterial(Collider* collider)
	{
		if (_materialMask == 0)
			return true;

		var material = collider->ObjectMaterialValue;
		return ((material ^ _materialValue) & _materialMask) == 0;
	}

	private void DrawBox(ColliderBox* collider)
	{
		var matrix = collider->World;
		Span<Vector3> corners = stackalloc Vector3[8];
		int idx = 0;
		for (int sx = -1; sx <= 1; sx += 2)
		{
			for (int sy = -1; sy <= 1; sy += 2)
			{
				for (int sz = -1; sz <= 1; sz += 2)
				{
					corners[idx++] = Transform(ref matrix, sx, sy, sz);
				}
			}
		}

		foreach (var (start, end) in BoxEdges)
			WorldDrawHelper.DrawWorldLine(corners[start], corners[end], BoxColor);
	}

	private void DrawCylinder(ColliderCylinder* collider)
	{
		const int segments = 32;
		var matrix = collider->World;

		Vector3 firstTop = default;
		Vector3 firstBottom = default;
		bool haveFirst = false;
		Vector3 prevTop = default;
		Vector3 prevBottom = default;

		for (int i = 0; i <= segments; i++)
		{
			float angle = (float)(2 * Math.PI * i / segments);
			float s = MathF.Sin(angle);
			float c = MathF.Cos(angle);

			var top = Transform(ref matrix, s, 1, c);
			var bottom = Transform(ref matrix, s, -1, c);

			if (!haveFirst)
			{
				firstTop = top;
				firstBottom = bottom;
				haveFirst = true;
			}
			else
			{
				WorldDrawHelper.DrawWorldLine(prevTop, top, CylinderColor);
				WorldDrawHelper.DrawWorldLine(prevBottom, bottom, CylinderColor);
			}

			int verticalStride = segments / 4;
			if (verticalStride > 0 && i % verticalStride == 0)
				WorldDrawHelper.DrawWorldLine(top, bottom, CylinderColor);

			prevTop = top;
			prevBottom = bottom;
		}

		if (haveFirst)
		{
			WorldDrawHelper.DrawWorldLine(prevTop, firstTop, CylinderColor);
			WorldDrawHelper.DrawWorldLine(prevBottom, firstBottom, CylinderColor);
		}
	}

	private void DrawSphere(ColliderSphere* collider)
	{
		var matrix = collider->World;
		DrawCircle(ref matrix, AxisPair.XY, SphereColor);
		DrawCircle(ref matrix, AxisPair.XZ, SphereColor);
		DrawCircle(ref matrix, AxisPair.YZ, SphereColor);
	}

	private void DrawPlane(ColliderPlane* collider, uint color)
	{
		var matrix = collider->World;
		var a = Transform(ref matrix, -1, -1, 0);
		var b = Transform(ref matrix, -1, 1, 0);
		var c = Transform(ref matrix, 1, 1, 0);
		var d = Transform(ref matrix, 1, -1, 0);

		WorldDrawHelper.DrawWorldLine(a, b, color);
		WorldDrawHelper.DrawWorldLine(b, c, color);
		WorldDrawHelper.DrawWorldLine(c, d, color);
		WorldDrawHelper.DrawWorldLine(d, a, color);

		var center = Transform(ref matrix, 0, 0, 0);
		var normalTip = Transform(ref matrix, 0, 0, 1.5f);
		WorldDrawHelper.DrawWorldLine(center, normalTip, color);
	}

	private static void DrawCircle(ref Matrix4x3 matrix, AxisPair plane, uint color)
	{
		const int segments = 48;
		Vector3 start = default;
		Vector3 prev = default;
		for (int i = 0; i <= segments; i++)
		{
			float angle = (float)(2 * Math.PI * i / segments);
			float s = MathF.Sin(angle);
			float c = MathF.Cos(angle);

			Vector3 local = plane switch
			{
				AxisPair.XY => new Vector3(c, s, 0),
				AxisPair.XZ => new Vector3(c, 0, s),
				AxisPair.YZ => new Vector3(0, c, s),
				_ => Vector3.Zero
			};

			var world = Transform(ref matrix, local.X, local.Y, local.Z);
			if (i == 0)
				start = world;
			else
				WorldDrawHelper.DrawWorldLine(prev, world, color);
			prev = world;
		}

		WorldDrawHelper.DrawWorldLine(prev, start, color);
	}

	private static Vector3 Transform(ref Matrix4x3 matrix, float x, float y, float z) =>
		matrix.TransformCoordinate(new Vector3(x, y, z));

	private static void DrawHexInput(string label, ref string text, ref ulong value, ref bool invalidFlag)
	{
		var flags = ImGuiInputTextFlags.CharsHexadecimal | ImGuiInputTextFlags.CharsUppercase | ImGuiInputTextFlags.AutoSelectAll;
		if (ImGui.InputText(label, ref text, 24, flags))
		{
			if (string.IsNullOrWhiteSpace(text))
			{
				value = 0;
				invalidFlag = false;
			}
			else if (ulong.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsed))
			{
				value = parsed;
				invalidFlag = false;
			}
			else
			{
				invalidFlag = true;
			}
		}

		if (invalidFlag)
		{
			ImGui.SameLine();
			ImGui.TextColored(new Vector4(1f, 0.5f, 0.4f, 1f), "Invalid hex");
		}
	}

	private void ResetFilter()
	{
		_materialValue = DefaultMaterialValue;
		_materialMask = DefaultMaterialMask;
		_materialValueText = DefaultMaterialValue.ToString("X");
		_materialMaskText = DefaultMaterialMask.ToString("X");
		_materialValueInvalid = false;
		_materialMaskInvalid = false;
	}

	private enum AxisPair
	{
		XY,
		XZ,
		YZ
	}
}

