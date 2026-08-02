using System.Numerics;
using global::Dalamud.Bindings.ImGui;

namespace DeepDungeon.Fsd.Dalamud
{
	public static class UiHelpers
	{
		public static void DrawGrayTipText(string text, float fontScale = 0.85f)
		{
			ImGui.PushFont(ImGui.GetIO().FontDefault);
			ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.6f, 0.6f, 0.6f, 1.0f));
			ImGui.SetWindowFontScale(fontScale);
			if (!string.IsNullOrEmpty(text))
			{
				if (text.Contains("\n") || text.Length > 80)
				{
					ImGui.TextWrapped(text);
				}
				else
				{
					ImGui.Text(text);
				}
			}
			ImGui.SetWindowFontScale(1.0f);
			ImGui.PopStyleColor();
			ImGui.PopFont();
		}
	}
}


