using System.Numerics;
using System.Runtime.InteropServices;
using ClickableTransparentOverlay;
using ImGuiNET;
using OotTrainer.Core.Features;

namespace OotTrainer.Gui;

/// <summary>F1-toggled ImGui window drawn in a transparent topmost overlay over the (windowed/borderless) game.</summary>
public sealed class DebugMenu(
    MenuActions actions, RupeesFeature rupeesLock, IReadOnlyList<CatalogGroup> catalog, string attachedTo) : Overlay("Hookshot Mod Menu")
{
    private const int VkToggle = 0x71; // F2: Hookshot's own settings menu already owns F1 and Insert

    private bool _visible = true, _keyDown, _holdRupees;
    private string _status = "ready";

    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);

    protected override void Render()
    {
        // Edge-triggered global poll, so the hotkey works while the game keeps keyboard focus.
        bool down = (GetAsyncKeyState(VkToggle) & 0x8000) != 0;
        if (down && !_keyDown) _visible = !_visible;
        _keyDown = down;
        if (!_visible) return;

        ImGui.SetNextWindowSize(new Vector2(400, 340), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Hookshot Mod Menu  [F2]", ref _visible)) { ImGui.End(); return; }

        ImGui.TextDisabled($"Attached: {attachedTo}");

        ImGui.SeparatorText("Rupees");
        if (!actions.HasRupees)
            ImGui.TextDisabled("'rupees' is not saved in offsets.json yet");
        else
        {
            if (ImGui.Button("Max Rupees")) _status = actions.MaxRupees();
            ImGui.SameLine();
            if (ImGui.Checkbox("Hold at max", ref _holdRupees))
            {
                try { if (_holdRupees) rupeesLock.Enable(); else rupeesLock.Disable(); _status = _holdRupees ? "rupee lock on" : "rupee lock off"; }
                catch (Exception ex) { _holdRupees = false; _status = "error: " + ex.Message; }
            }
        }

        ImGui.SeparatorText("Items");
        foreach (var group in catalog)
        {
            ImGui.Text(group.Name + ":");
            foreach (var item in group.Items)
            {
                ImGui.SameLine();
                string? blocker = actions.Blocker(item);
                ImGui.BeginDisabled(blocker is not null);
                if (ImGui.Button($"{item.Label}##{group.Name}")) _status = actions.Give(item);
                ImGui.EndDisabled();
                if (blocker is not null && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip(blocker);
            }
        }

        ImGui.Separator();
        ImGui.TextDisabled($"Status: {_status}");
        ImGui.End();
    }
}
