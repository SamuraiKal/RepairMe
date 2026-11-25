using System.Numerics;
using Dalamud.Bindings.ImGui;
using Xunit;

namespace RepairMe.Tests.UI;

public class ImGuiGuardTests
{
    public enum Fault { None, Migrate, Drag, Writer, Draw }

    private sealed class InjectedFailure : Exception
    {
    }

    /// <summary>
    /// Models the Dear ImGui rules the pipelines must respect and records every violation instead of throwing,
    /// so a broken guard cannot hide behind the exception it was supposed to survive.
    /// Windows must always be ended. Tables, tab bars and tab items are ended only when begun successfully.
    /// Containers must be ended innermost first. Content may only be drawn inside an open container.
    /// Style pushes must be popped and never popped below zero.
    /// </summary>
    private sealed class StrictImGui : IImGuiApi
    {
        private readonly Stack<(string Kind, bool Open)> _containers = new();
        private int _styleDepth;

        public readonly List<string> Violations = new();
        public bool WindowResult = true;
        public bool TabBarResult = true;
        public bool TabItemResult = true;
        public bool TableResult = true;

        public Vector2 WindowPadding => new(4, 4);
        public void SetNextWindowSize(Vector2 size) { }

        public bool Begin(string name, ImGuiWindowFlags flags)
        {
            _containers.Push(("window", WindowResult));
            return WindowResult;
        }

        public void End() => Pop("window");

        public bool BeginTabBar(string id) => BeginConditional("tabbar", TabBarResult);
        public void EndTabBar() => Pop("tabbar");
        public bool BeginTabItem(string label) => BeginConditional("tabitem", TabItemResult);
        public void EndTabItem() => Pop("tabitem");
        public bool BeginTable(string id, int columns) => BeginConditional("table", TableResult);
        public void EndTable() => Pop("table");
        public void TableNextColumn() => RequireOpenContainer("next column");

        public void PushStyle() => _styleDepth++;

        public void PopStyle()
        {
            if (_styleDepth == 0)
                Violations.Add("popped a style that was never pushed");
            else
                _styleDepth--;
        }

        public void RequireOpenContainer(string what)
        {
            if (_containers.Count == 0 || !_containers.Peek().Open)
                Violations.Add($"{what} outside an open container");
        }

        public void AssertBalanced()
        {
            if (_containers.Count != 0)
                Violations.Add($"{_containers.Count} container(s) left open: {string.Join(", ", _containers.Select(c => c.Kind))}");
            if (_styleDepth != 0)
                Violations.Add($"{_styleDepth} style push(es) left unpopped");

            Assert.Empty(Violations);
        }

        private bool BeginConditional(string kind, bool result)
        {
            if (result)
                _containers.Push((kind, true));
            return result;
        }

        private void Pop(string kind)
        {
            if (_containers.Count == 0)
                Violations.Add($"end {kind} with nothing open");
            else if (_containers.Peek().Kind != kind)
                Violations.Add($"end {kind} while {_containers.Peek().Kind} is innermost");
            else
                _containers.Pop();
        }
    }

    private sealed class FakeHost : IOverlayHost
    {
        private readonly StrictImGui _imGui;
        private readonly Fault _fault;

        public int Drawn;
        public int Interactions;

        public FakeHost(StrictImGui imGui, Fault fault)
        {
            _imGui = imGui;
            _fault = fault;
        }

        public bool UnlockedUiMode => false;
        public ImGuiWindowFlags PrepareWindow(Vector2 position) => ImGuiWindowFlags.None;
        public void PushWindowEditingStyle(bool minSize = false) => _imGui.PushStyle();
        public void PopWindowEditingStyle(bool minSize = false) => _imGui.PopStyle();

        public void MigratePositions(ref Vector2 position)
        {
            Interact("migrate", Fault.Migrate);
        }

        public void CheckDrag(ref Vector2 position)
        {
            Interact("drag", Fault.Drag);
        }

        public void DrawBar(BarState state, int orientation, Vector2 size, float rounding, float borderSize, Vector4 borderColor)
        {
            Interact("draw bar", Fault.Draw);
            Drawn++;
        }

        public void DrawTextOverlay(string text, Vector4 color, Vector4 background, ref Vector2 position, float? scale = null, Action? clickAction = null)
        {
            Interact("draw text", Fault.Draw);
            Drawn++;
        }

        private void Interact(string what, Fault at)
        {
            Interactions++;
            _imGui.RequireOpenContainer(what);
            if (_fault == at)
                throw new InjectedFailure();
        }
    }

    private sealed class ProbeItem : WindowItem
    {
        private readonly StrictImGui _imGui;
        private readonly bool _throws;

        public int Drawn;

        public ProbeItem(StrictImGui imGui, bool throws)
        {
            _imGui = imGui;
            _throws = throws;
        }

        public override void Draw()
        {
            _imGui.RequireOpenContainer("item");
            Drawn++;
            if (_throws)
                throw new InjectedFailure();
        }
    }

    public static IEnumerable<object[]> OverlayCases(params Fault[] faults)
        => from windowOpen in new[] { true, false }
           from fault in faults
           select new object[] { windowOpen, fault };

    public static IEnumerable<object[]> BarCases()
        => OverlayCases(Fault.None, Fault.Migrate, Fault.Drag, Fault.Writer, Fault.Draw);

    public static IEnumerable<object[]> TextCases()
        => OverlayCases(Fault.None, Fault.Migrate, Fault.Writer, Fault.Draw);

    private static void AssertOutcome(bool windowOpen, Fault fault, Exception? thrown, FakeHost host)
    {
        var expectFailure = windowOpen && fault != Fault.None;
        Assert.Equal(expectFailure, thrown is InjectedFailure);
        Assert.True(thrown is null || thrown is InjectedFailure, $"unexpected {thrown}");
        if (!windowOpen)
        {
            Assert.Equal(0, host.Drawn);
            Assert.Equal(0, host.Interactions);
        }
        else if (fault == Fault.None)
            Assert.Equal(1, host.Drawn);
    }

    [Theory]
    [MemberData(nameof(BarCases))]
    public void Bar_LeavesImGuiBalanced_AndOnlyDrawsInsideAnOpenWindow(bool windowOpen, Fault fault)
    {
        var imGui = new StrictImGui { WindowResult = windowOpen };
        var host = new FakeHost(imGui, fault);
        var bar = new BarOverlayItem(host,
            shouldRender: () => true,
            positionProvider: () => Vector2.Zero,
            positionWriter: _ =>
            {
                if (fault == Fault.Writer)
                    throw new InjectedFailure();
            },
            windowName: "bar",
            sizeProvider: () => new Vector2(100, 10),
            orientationProvider: () => 0,
            roundingProvider: () => 0f,
            borderSizeProvider: () => 0f,
            borderColorProvider: () => Vector4.One,
            stateProvider: () => new BarState(0.5f, Vector4.One, Vector4.Zero, false, Array.Empty<float>(), Vector4.One),
            api: imGui);

        var thrown = Record.Exception(bar.Render);

        imGui.AssertBalanced();
        AssertOutcome(windowOpen, fault, thrown, host);
    }

    [Theory]
    [MemberData(nameof(TextCases))]
    public void Text_LeavesImGuiBalanced_AndOnlyDrawsInsideAnOpenWindow(bool windowOpen, Fault fault)
    {
        var imGui = new StrictImGui { WindowResult = windowOpen };
        var host = new FakeHost(imGui, fault);
        var text = new TextOverlayItem(host,
            shouldRender: () => true,
            textProvider: () => "text",
            positionProvider: () => Vector2.Zero,
            positionWriter: _ =>
            {
                if (fault == Fault.Writer)
                    throw new InjectedFailure();
            },
            windowName: "text",
            colorProvider: () => Vector4.One,
            backgroundProvider: () => Vector4.Zero,
            useMinimumWindowSize: false,
            scaleProvider: () => null,
            clickActionProvider: () => null,
            api: imGui);

        var thrown = Record.Exception(text.Render);

        imGui.AssertBalanced();
        AssertOutcome(windowOpen, fault, thrown, host);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public void ColorTable_LeavesImGuiBalanced_AndOnlyDrawsItemsInsideAnOpenTable(bool tableOpen, bool itemThrows)
    {
        var imGui = new StrictImGui { TableResult = tableOpen };
        var item = new ProbeItem(imGui, itemThrows);
        var table = new ColorTableItem("t", 2, new WindowItem[] { item }) { Api = imGui };

        var thrown = Record.Exception(table.Draw);

        imGui.AssertBalanced();
        Assert.Equal(tableOpen && itemThrows, thrown is InjectedFailure);
        Assert.True(thrown is null || thrown is InjectedFailure, $"unexpected {thrown}");
        Assert.Equal(tableOpen ? 1 : 0, item.Drawn);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void Tabs_LeaveImGuiBalanced_ForEveryOpenAndClosedCombination(bool tabBarOpen, bool tabItemOpen)
    {
        var imGui = new StrictImGui { TabBarResult = tabBarOpen, TabItemResult = tabItemOpen };
        var window = new WindowBuilder
        {
            Api = imGui,
            UseTabBar = true,
            Tabs = new[]
            {
                new SettingsTab { Name = "One", Sections = Array.Empty<SectionHeader>() },
                new SettingsTab { Name = "Two", Sections = Array.Empty<SectionHeader>() },
            },
        };

        window.DrawContents();

        imGui.AssertBalanced();
    }
}
