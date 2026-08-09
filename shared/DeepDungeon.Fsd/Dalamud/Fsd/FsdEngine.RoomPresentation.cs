using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Map;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using global::Dalamud.Bindings.ImGui;
using global::Dalamud.Interface.Textures;
using global::Dalamud.Interface.Utility;
using Lumina.Data.Files;

namespace DeepDungeon.Fsd.Dalamud
{
	internal partial class FsdEngine
	{
		private enum DeepDungeonLandmarkKind
		{
			Home,
			Return,
			Passage
		}

		private readonly record struct RoomPresentationScope(
			uint TerritoryId,
			byte Floor,
			int LayoutIndex);

		private sealed class DeepDungeonLandmarkSprite
		{
			public required ISharedImmediateTexture Texture { get; init; }
			public required Vector2 UvTopLeftPixels { get; init; }
			public required Vector2 UvSizePixels { get; init; }
			public required string Source { get; init; }
		}

		private readonly PalacePalProvider _roomPresentationPalacePalProvider = new();
		private RoomPresentationScope? _roomPresentationScope;
		private int _selectedRoomIndex = -1;
		private bool _deepDungeonLandmarkSpritesInitialized;
		private bool _deepDungeonLandmarkSpriteFailureLogged;
		private DeepDungeonLandmarkSprite? _deepDungeonHomeSprite;
		private DeepDungeonLandmarkSprite? _deepDungeonReturnSprite;
		private DeepDungeonLandmarkSprite? _deepDungeonPassageSprite;
		private string _deepDungeonLandmarkSpriteError = string.Empty;

		private unsafe RoomPresentationScope GetRoomPresentationScope(InstanceContentDeepDungeon* deepDungeon) =>
			new(
				Service.ClientState.TerritoryType,
				deepDungeon->Floor,
				deepDungeon->ActiveLayoutIndex);

		private unsafe void ValidateRoomPresentationScope(InstanceContentDeepDungeon* deepDungeon)
		{
			RoomPresentationScope current = GetRoomPresentationScope(deepDungeon);
			if (_roomPresentationScope == current)
				return;

			_roomPresentationScope = current;
			_selectedRoomIndex = -1;
		}

		private unsafe void ToggleRoomPresentation(
			InstanceContentDeepDungeon* deepDungeon,
			int roomIndex)
		{
			ValidateRoomPresentationScope(deepDungeon);
			_selectedRoomIndex = _selectedRoomIndex == roomIndex ? -1 : roomIndex;
		}

		private void ResetRoomPresentation()
		{
			_roomPresentationScope = null;
			_selectedRoomIndex = -1;
		}

		private unsafe DetailedMapRoomGraphPresentationState GetRoomPresentationState(
			InstanceContentDeepDungeon* deepDungeon,
			int roomIndex)
		{
			if (_detailedMapCatalogManager.PresentationCatalog != null)
			{
				if (_detailedMapCatalogManager.TryGetPresentation(
				    deepDungeon->ActiveLayoutIndex,
				    roomIndex,
				    out DetailedMapRoomGraphPresentation presentation))
				{
					return presentation.State;
				}

				return DetailedMapRoomGraphPresentationState.NoPositions;
			}

			return _roomPresentationPalacePalProvider
				.GetCandidatePositionsForRoom(deepDungeon, roomIndex).Count > 0
				? DetailedMapRoomGraphPresentationState.Candidate
				: DetailedMapRoomGraphPresentationState.NoPositions;
		}

		private static string GetRoomPresentationStateName(DetailedMapRoomGraphPresentationState state) =>
			state switch
			{
				DetailedMapRoomGraphPresentationState.NoPositions => "無位置",
				DetailedMapRoomGraphPresentationState.Candidate => "候選",
				DetailedMapRoomGraphPresentationState.Partial => "部分完成",
				DetailedMapRoomGraphPresentationState.Complete => "完整",
				DetailedMapRoomGraphPresentationState.Conflict => "衝突",
				_ => state.ToString()
			};

		private static void DrawDeepDungeonRoomModelBadge(
			ImDrawListPtr drawList,
			Vector2 center,
			float unit,
			DetailedMapRoomGraphPresentationState state)
		{
			uint color = state == DetailedMapRoomGraphPresentationState.Conflict
				? ImGui.GetColorU32(new Vector4(1f, 0.34f, 0.26f, 1f))
				: ImGui.GetColorU32(new Vector4(0.84f, 0.88f, 0.94f, 1f));
			float badgeUnit = unit * 1.15f;
			Vector2 leftBottom = center + new Vector2(-5.5f, 4.4f) * badgeUnit;
			Vector2 top = center + new Vector2(0f, -5.1f) * badgeUnit;
			Vector2 rightBottom = center + new Vector2(5.5f, 4.4f) * badgeUnit;
			float radius = 2.2f * badgeUnit;
			float lineThickness = 1.5f * badgeUnit;
			switch (state)
			{
				case DetailedMapRoomGraphPresentationState.NoPositions:
					Vector2 crossOffset = new(2.3f * badgeUnit, 2.3f * badgeUnit);
					drawList.AddLine(
						rightBottom - crossOffset,
						rightBottom + crossOffset,
						color,
						lineThickness);
					drawList.AddLine(
						rightBottom + new Vector2(-crossOffset.X, crossOffset.Y),
						rightBottom + new Vector2(crossOffset.X, -crossOffset.Y),
						color,
						lineThickness);
					break;
				case DetailedMapRoomGraphPresentationState.Candidate:
					drawList.AddCircle(rightBottom, radius, color, 12, lineThickness);
					break;
				case DetailedMapRoomGraphPresentationState.Partial:
					drawList.AddLine(rightBottom, top, color, lineThickness);
					drawList.AddCircle(rightBottom, radius, color, 12, lineThickness);
					drawList.AddCircle(top, radius, color, 12, lineThickness);
					break;
				case DetailedMapRoomGraphPresentationState.Complete:
					drawList.AddLine(rightBottom, top, color, lineThickness);
					drawList.AddLine(top, leftBottom, color, lineThickness);
					drawList.AddCircle(rightBottom, radius, color, 12, lineThickness);
					drawList.AddCircle(top, radius, color, 12, lineThickness);
					drawList.AddCircle(leftBottom, radius, color, 12, lineThickness);
					break;
				case DetailedMapRoomGraphPresentationState.Conflict:
					drawList.AddLine(leftBottom, top, color, lineThickness);
					drawList.AddLine(top, rightBottom, color, lineThickness);
					drawList.AddLine(rightBottom, leftBottom, color, lineThickness);
					drawList.AddCircle(leftBottom, radius, color, 12, lineThickness);
					drawList.AddCircle(top, radius, color, 12, lineThickness);
					drawList.AddCircle(rightBottom, radius, color, 12, lineThickness);
					break;
			}
		}

		private static void DrawDeepDungeonRoomSelectionBrackets(
			ImDrawListPtr drawList,
			Vector2 topLeft,
			Vector2 bottomRight,
			float length,
			float thickness)
		{
			uint color = ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 1f));
			drawList.AddLine(topLeft, topLeft + new Vector2(length, 0f), color, thickness);
			drawList.AddLine(topLeft, topLeft + new Vector2(0f, length), color, thickness);
			drawList.AddLine(new Vector2(bottomRight.X - length, topLeft.Y), new Vector2(bottomRight.X, topLeft.Y), color, thickness);
			drawList.AddLine(new Vector2(bottomRight.X, topLeft.Y), new Vector2(bottomRight.X, topLeft.Y + length), color, thickness);
			drawList.AddLine(new Vector2(topLeft.X, bottomRight.Y - length), new Vector2(topLeft.X, bottomRight.Y), color, thickness);
			drawList.AddLine(new Vector2(topLeft.X, bottomRight.Y), new Vector2(topLeft.X + length, bottomRight.Y), color, thickness);
			drawList.AddLine(new Vector2(bottomRight.X - length, bottomRight.Y), bottomRight, color, thickness);
			drawList.AddLine(new Vector2(bottomRight.X, bottomRight.Y - length), bottomRight, color, thickness);
		}

		private void EnsureDeepDungeonLandmarkSprites()
		{
			if (_deepDungeonLandmarkSpritesInitialized)
				return;

			_deepDungeonLandmarkSpritesInitialized = true;
			try
			{
				const string uldPath = "ui/uld/DeepDungeonNaviMap.uld";
				UldFile? uld = Service.DataManager.GetFile<UldFile>(uldPath);
				if (uld == null)
					throw new InvalidDataException($"{uldPath} was not found.");

				_deepDungeonHomeSprite = ResolveDeepDungeonUldSprite(
					uld,
					partsSetId: 3,
					partIndex: 6,
					expectedTexturePath: "ui/uld/DeepDungeonNaviMap.tex");
				_deepDungeonReturnSprite = ResolveDeepDungeonUldSprite(
					uld,
					partsSetId: 2,
					partIndex: 10,
					expectedTexturePath: "ui/uld/DeepDungeonNaviMap_Ankh.tex");
				_deepDungeonPassageSprite = ResolveDeepDungeonUldSprite(
					uld,
					partsSetId: 5,
					partIndex: 10,
					expectedTexturePath: "ui/uld/DeepDungeonNaviMap_Key.tex");
			}
			catch (Exception ex)
			{
				_deepDungeonHomeSprite = null;
				_deepDungeonReturnSprite = null;
				_deepDungeonPassageSprite = null;
				_deepDungeonLandmarkSpriteError = $"ULD landmark icons unavailable: {ex.Message}";
				if (!_deepDungeonLandmarkSpriteFailureLogged)
				{
					_deepDungeonLandmarkSpriteFailureLogged = true;
					Service.Log.Error($"[FsdEngine] {_deepDungeonLandmarkSpriteError}");
				}
			}
		}

		private static DeepDungeonLandmarkSprite ResolveDeepDungeonUldSprite(
			UldFile uld,
			uint partsSetId,
			int partIndex,
			string expectedTexturePath)
		{
			int setIndex = Array.FindIndex(uld.Parts, parts => parts.Id == partsSetId);
			if (setIndex < 0)
				throw new InvalidDataException($"ULD parts set {partsSetId} was not found.");

			var partsSet = uld.Parts[setIndex];
			if (partIndex < 0 || partIndex >= partsSet.Parts.Length)
				throw new InvalidDataException($"ULD parts set {partsSetId} has no part {partIndex}.");

			var part = partsSet.Parts[partIndex];
			int assetIndex = Array.FindIndex(uld.AssetData, asset => asset.Id == part.TextureId);
			if (assetIndex < 0)
				throw new InvalidDataException($"ULD texture asset {part.TextureId} was not found.");

			string texturePath = new string(uld.AssetData[assetIndex].Path);
			int terminator = texturePath.IndexOf('\0');
			if (terminator >= 0)
				texturePath = texturePath[..terminator];
			if (!string.Equals(texturePath, expectedTexturePath, StringComparison.Ordinal))
			{
				throw new InvalidDataException(
					$"ULD set {partsSetId} part {partIndex} resolved to {texturePath}, expected {expectedTexturePath}.");
			}

			return new DeepDungeonLandmarkSprite
			{
				Texture = Service.TextureProvider.GetFromGame(texturePath),
				UvTopLeftPixels = new Vector2(part.U, part.V),
				UvSizePixels = new Vector2(part.W, part.H),
				Source = $"{texturePath} · set {partsSetId} / part {partIndex}"
			};
		}

		private DeepDungeonLandmarkSprite? GetDeepDungeonLandmarkSprite(DeepDungeonLandmarkKind kind) =>
			kind switch
			{
				DeepDungeonLandmarkKind.Home => _deepDungeonHomeSprite,
				DeepDungeonLandmarkKind.Return => _deepDungeonReturnSprite,
				DeepDungeonLandmarkKind.Passage => _deepDungeonPassageSprite,
				_ => null
			};

		private void DrawDeepDungeonLandmark(
			ImDrawListPtr drawList,
			Vector2 tileTopLeft,
			float unit,
			DeepDungeonLandmarkKind kind,
			bool active)
		{
			EnsureDeepDungeonLandmarkSprites();
			DeepDungeonLandmarkSprite? sprite = GetDeepDungeonLandmarkSprite(kind);
			Exception? textureError = null;
			if (sprite == null || !sprite.Texture.TryGetWrap(out var texture, out textureError))
			{
				if (textureError != null && !_deepDungeonLandmarkSpriteFailureLogged)
				{
					_deepDungeonLandmarkSpriteFailureLogged = true;
					Service.Log.Error($"[FsdEngine] ULD landmark texture unavailable: {textureError.Message}");
				}
				return;
			}

			const float plateWidth = 19f;
			const float plateHeight = 23f;
			Vector2 plateTopLeft = tileTopLeft + new Vector2(1f, 12f) * unit;
			Vector2 plateBottomRight = plateTopLeft + new Vector2(plateWidth, plateHeight) * unit;
			uint backplate = active
				? ImGui.GetColorU32(new Vector4(78f / 255f, 128f / 255f, 101f / 255f, 1f))
				: ImGui.GetColorU32(new Vector4(78f / 255f, 64f / 255f, 94f / 255f, 1f));
			drawList.AddRectFilled(plateTopLeft, plateBottomRight, backplate, 3f * unit);

			float sourceAspect = sprite.UvSizePixels.X / sprite.UvSizePixels.Y;
			float width = 18f * unit;
			float height = width / sourceAspect;
			if (height > 21f * unit)
			{
				height = 21f * unit;
				width = height * sourceAspect;
			}

			Vector2 iconBottomLeft = new(
				plateTopLeft.X + (plateWidth * unit - width) * 0.5f,
				plateBottomRight.Y - unit);
			Vector2 iconTopLeft = new(iconBottomLeft.X, iconBottomLeft.Y - height);
			Vector2 iconBottomRight = iconTopLeft + new Vector2(width, height);
			Vector2 plateCenter = (plateTopLeft + plateBottomRight) * 0.5f;
			Vector2 zoomedIconTopLeft = plateCenter + (iconTopLeft - plateCenter) * 1.35f;
			Vector2 zoomedIconBottomRight = plateCenter + (iconBottomRight - plateCenter) * 1.35f;
			Vector2 uvTopLeft = sprite.UvTopLeftPixels / texture.Size;
			Vector2 uvBottomRight = (sprite.UvTopLeftPixels + sprite.UvSizePixels) / texture.Size;
			drawList.PushClipRect(plateTopLeft, plateBottomRight, true);
			try
			{
				drawList.AddImage(
					texture.Handle,
					zoomedIconTopLeft,
					zoomedIconBottomRight,
					uvTopLeft,
					uvBottomRight,
					0xFFFFFFFF);
			}
			finally
			{
				drawList.PopClipRect();
			}
		}

		private static void DrawRoomGraphArrow(
			ImDrawListPtr drawList,
			Vector2 source,
			Vector2 target,
			float candidateRadius,
			uint color)
		{
			Vector2 delta = target - source;
			float length = delta.Length();
			float remainingLength = length - candidateRadius * 2f;
			if (remainingLength <= 0f)
				return;

			Vector2 direction = delta / length;
			Vector2 normal = new(-direction.Y, direction.X);
			source += direction * candidateRadius;
			target -= direction * candidateRadius;
			const float lineThickness = 2f;
			const float arrowLength = 8f;
			const float arrowWidth = 4f;
			drawList.AddLine(source, target, color, lineThickness);
			drawList.AddTriangleFilled(
				target,
				target - direction * arrowLength + normal * arrowWidth,
				target - direction * arrowLength - normal * arrowWidth,
				color);
		}

		private unsafe void DrawSelectedRoomPresentation(InstanceContentDeepDungeon* deepDungeon)
		{
			ValidateRoomPresentationScope(deepDungeon);
			if (_selectedRoomIndex < 0)
				return;

			int roomIndex = _selectedRoomIndex;
			var map = deepDungeon->MapData;
			if (roomIndex >= map.Length || map[roomIndex] == InstanceContentDeepDungeon.RoomFlags.None)
			{
				_selectedRoomIndex = -1;
				return;
			}

			DetailedMapCatalogRoom? detailedMapRoom = null;
			DetailedMapRoomGraphPresentation? detailedMapPresentation = null;
			IReadOnlyList<Vector3>? palaceCandidates = null;
			string source;
			DetailedMapCatalog? presentationCatalog =
				_detailedMapCatalogManager.PresentationCatalog;
			if (presentationCatalog?.TryGetRoom(
				    deepDungeon->ActiveLayoutIndex,
				    roomIndex,
				    out detailedMapRoom) == true)
			{
				if (!_detailedMapCatalogManager.TryGetPresentation(
					    deepDungeon->ActiveLayoutIndex,
					    roomIndex,
					    out detailedMapPresentation))
				{
					ImGui.Text($"房間 {roomIndex} · 無法顯示");
					ImGui.TextDisabled("已載入的房間缺少快取的顯示資料。");
					return;
				}

				source =
					$"{presentationCatalog.DisplayName} · {presentationCatalog.ReleaseId}";
			}
			else if (presentationCatalog != null)
			{
				source =
					$"{presentationCatalog.DisplayName} · {presentationCatalog.ReleaseId}";
			}
			else
			{
				palaceCandidates = _roomPresentationPalacePalProvider
					.GetCandidatePositionsForRoom(deepDungeon, roomIndex);
				source = "PalacePal 候選位置";
			}

			int candidateCount = detailedMapRoom?.Candidates.Length ?? palaceCandidates?.Count ?? 0;
			DetailedMapRoomGraphPresentationState state = detailedMapPresentation?.State ??
				(candidateCount > 0
					? DetailedMapRoomGraphPresentationState.Candidate
					: DetailedMapRoomGraphPresentationState.NoPositions);
			ImGui.Text($"房間 {roomIndex} · {GetRoomPresentationStateName(state)}");
			ImGui.SameLine();
			ImGui.TextDisabled("圖狀態:");
			ImGui.SameLine();
			float badgeSize = 24f * ImGuiHelpers.GlobalScale;
			Vector2 badgeTopLeft = ImGui.GetCursorScreenPos();
			ImGui.Dummy(new Vector2(badgeSize, badgeSize));
			DrawDeepDungeonRoomModelBadge(
				ImGui.GetWindowDrawList(),
				badgeTopLeft + new Vector2(badgeSize * 0.5f),
				ImGuiHelpers.GlobalScale,
				state);
			ImGui.SameLine();
			ImGui.Text(GetRoomPresentationStateName(state));
			ImGui.TextDisabled(source);

			bool hasRoomCenter = MapPos.TryGetRoomCenter(
				deepDungeon,
				roomIndex,
				out Vector3 roomCenter);
			if (!hasRoomCenter)
			{
				ImGui.TextDisabled("此房間沒有綁定中心點。");
				if (candidateCount == 0)
					ImGui.TextDisabled("此房間沒有可用的候選位置。");
				return;
			}
			if (candidateCount == 0)
			{
				ImGui.TextDisabled("此房間沒有可用的候選位置。");
				return;
			}

			Vector3 GetCandidatePosition(int index)
			{
				if (detailedMapRoom != null)
				{
					RawWorldPosition raw = detailedMapRoom.Candidates[index].Position;
					return new Vector3(raw.X, raw.Y, raw.Z);
				}
				return palaceCandidates![index];
			}

			float farthestDistance = 0f;
			for (int index = 0; index < candidateCount; index++)
			{
				Vector3 candidate = GetCandidatePosition(index);
				float distance = new Vector2(candidate.X - roomCenter.X, candidate.Z - roomCenter.Z).Length();
				farthestDistance = MathF.Max(farthestDistance, distance);
			}

			const float canvasSize = 330f;
			float canvasScale = ImGuiHelpers.GlobalScale;
			Vector2 size = new(canvasSize * canvasScale, canvasSize * canvasScale);
			Vector2 canvasTopLeft = ImGui.GetCursorScreenPos();
			Vector2 canvasBottomRight = canvasTopLeft + size;
			Vector2 canvasCenter = (canvasTopLeft + canvasBottomRight) * 0.5f;
			float halfExtent = (farthestDistance + 1.7f) * 1.1f;
			float pixelsPerMeter = (size.X * 0.5f - 16f * canvasScale) / halfExtent;
			Vector2 ToScreen(Vector3 position) =>
				canvasCenter + new Vector2(
					(position.X - roomCenter.X) * pixelsPerMeter,
					(position.Z - roomCenter.Z) * pixelsPerMeter);
			Vector2 RawToScreen(RawWorldPosition position) =>
				canvasCenter + new Vector2(
					(position.X - roomCenter.X) * pixelsPerMeter,
					(position.Z - roomCenter.Z) * pixelsPerMeter);

			ImDrawListPtr drawList = ImGui.GetWindowDrawList();
			uint canvasFill = ImGui.GetColorU32(new Vector4(0.08f, 0.08f, 0.09f, 0.88f));
			uint canvasBorder = ImGui.GetColorU32(new Vector4(0.64f, 0.66f, 0.70f, 1f));
			uint centerLine = ImGui.GetColorU32(new Vector4(0.42f, 0.44f, 0.48f, 0.65f));
			uint edgeColor = ImGui.GetColorU32(new Vector4(0.50f, 0.72f, 0.88f, 0.95f));
			uint candidateFill = ImGui.GetColorU32(new Vector4(0.34f, 0.36f, 0.40f, 1f));
			uint candidateOutline = ImGui.GetColorU32(new Vector4(0.84f, 0.88f, 0.94f, 1f));
			uint sightTrap = ImGui.GetColorU32(new Vector4(0.94f, 0.25f, 0.25f, 1f));
			uint exactHoard = ImGui.GetColorU32(new Vector4(1f, 0.78f, 0.18f, 1f));
			uint playerColor = ImGui.GetColorU32(new Vector4(1.0f, 0.95f, 0.2f, 1.0f));
			float candidateRadius = 1.7f * pixelsPerMeter;
			drawList.AddRectFilled(canvasTopLeft, canvasBottomRight, canvasFill, 4f * canvasScale);
			drawList.AddRect(canvasTopLeft, canvasBottomRight, canvasBorder, 4f * canvasScale);
			drawList.PushClipRect(canvasTopLeft, canvasBottomRight, true);
			try
			{
				drawList.AddLine(
					new Vector2(canvasCenter.X, canvasTopLeft.Y),
					new Vector2(canvasCenter.X, canvasBottomRight.Y),
					centerLine);
				drawList.AddLine(
					new Vector2(canvasTopLeft.X, canvasCenter.Y),
					new Vector2(canvasBottomRight.X, canvasCenter.Y),
					centerLine);

				if (detailedMapPresentation != null)
				{
					for (int edgeIndex = 0; edgeIndex < detailedMapPresentation.ObservedEdges.Length; edgeIndex++)
					{
						DetailedMapRoomObservedEdge edge = detailedMapPresentation.ObservedEdges[edgeIndex];
						DrawRoomGraphArrow(
							drawList,
							RawToScreen(edge.Source),
							RawToScreen(edge.Target),
							candidateRadius,
							edgeColor);
					}
				}

				var floorController = _ddHost?.FloorController;
				IReadOnlyList<Vector3> observedSightTraps =
					floorController?.ObservedSightTrapPositions ?? Array.Empty<Vector3>();
				Vector3? cachedHoardIndicator = floorController?.CachedHoardIndicatorPos;
				for (int candidateIndex = 0; candidateIndex < candidateCount; candidateIndex++)
				{
					Vector3 candidate = GetCandidatePosition(candidateIndex);
					bool observedTrap = false;
					for (int trapIndex = 0; trapIndex < observedSightTraps.Count; trapIndex++)
					{
						if (Vector3.DistanceSquared(
							    candidate,
							    observedSightTraps[trapIndex]) <=
						    0.100001f * 0.100001f)
						{
							observedTrap = true;
							break;
						}
					}
					bool exactH = cachedHoardIndicator is Vector3 hoard &&
					              Vector3.DistanceSquared(candidate, hoard) <= 0.100001f * 0.100001f;
					Vector2 point = ToScreen(candidate);
					uint fill = exactH ? exactHoard : observedTrap ? sightTrap : candidateFill;
					uint outline = exactH ? exactHoard : observedTrap ? sightTrap : candidateOutline;
					drawList.AddCircleFilled(point, candidateRadius, fill);
					drawList.AddCircle(point, candidateRadius, outline, 24, 1.25f);
				}

				if (detailedMapPresentation?.State == DetailedMapRoomGraphPresentationState.Complete)
				{
					for (int orderIndex = 0;
					     orderIndex < detailedMapPresentation.CompleteChainOrder.Length;
					     orderIndex++)
					{
						Vector2 point = RawToScreen(detailedMapPresentation.CompleteChainOrder[orderIndex]);
						drawList.AddText(
							point + new Vector2(5f, -14f) * canvasScale,
							candidateOutline,
							$"r{orderIndex}");
					}
				}

				if (RoomGraph.GetLocalPlayerRoomIndex(deepDungeon) == roomIndex)
				{
					var player = Service.LocalPlayer;
					if (player != null &&
						float.IsFinite(player.Position.X) &&
						float.IsFinite(player.Position.Y) &&
						float.IsFinite(player.Position.Z) &&
						float.IsFinite(player.Rotation))
					{
						DrawPlayerArrow(
							drawList,
							ToScreen(player.Position),
							MathF.PI - player.Rotation,
							playerColor);
					}
				}
			}
			finally
			{
				drawList.PopClipRect();
			}

			ImGui.Dummy(size);
		}
	}
}
