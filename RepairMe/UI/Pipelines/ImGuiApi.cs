using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RepairMe
{
    internal interface IImGuiApi
    {
        Vector2 WindowPadding { get; }
        void SetNextWindowSize(Vector2 size);
        bool Begin(string name, ImGuiWindowFlags flags);
        void End();
        bool BeginTabBar(string id);
        void EndTabBar();
        bool BeginTabItem(string label);
        void EndTabItem();
        bool BeginTable(string id, int columns);
        void EndTable();
        void TableNextColumn();
    }

    internal sealed class DalamudImGuiApi : IImGuiApi
    {
        internal static readonly DalamudImGuiApi Instance = new();

        public Vector2 WindowPadding => ImGui.GetStyle().WindowPadding;
        public void SetNextWindowSize(Vector2 size) => ImGui.SetNextWindowSize(size);
        public bool Begin(string name, ImGuiWindowFlags flags) => ImGui.Begin(name, flags);
        public void End() => ImGui.End();
        public bool BeginTabBar(string id) => ImGui.BeginTabBar(id);
        public void EndTabBar() => ImGui.EndTabBar();
        public bool BeginTabItem(string label) => ImGui.BeginTabItem(label);
        public void EndTabItem() => ImGui.EndTabItem();
        public bool BeginTable(string id, int columns) => ImGui.BeginTable(id, columns);
        public void EndTable() => ImGui.EndTable();
        public void TableNextColumn() => ImGui.TableNextColumn();
    }
}
