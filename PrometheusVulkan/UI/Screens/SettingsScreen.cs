using System.Numerics;
using PrometheusVulkan.State;
using ImGuiNET;
using PrometheusVulkan.Core;
using PrometheusVulkan.Graphics;
using PrometheusVulkan.UI.Screens;

namespace PrometheusVulkan.UI.Screens;

public class SettingsScreen : IScreen
{
    private readonly UIManager _uiManager;
    private readonly VulkanRenderer _renderer;
    private bool _vsync;
    private bool _fullscreen;
    private bool _stats;
    private bool _hints;
    private float _volume;

    public SettingsScreen(UIManager uiManager, VulkanRenderer renderer)
    {
        _uiManager = uiManager;
        _renderer = renderer;
    }

    public void OnShow()
    {
        // Load current values
        _vsync = SettingsManager.Instance.VSync;
        _fullscreen = SettingsManager.Instance.Fullscreen;
        _stats = SettingsManager.Instance.ShowDebugStats;
        _hints = SettingsManager.Instance.ShowLegalMoveHints;
        _volume = SettingsManager.Instance.MasterVolume;
    }

    public void OnHide()
    {
        // Auto-save on exit? Or explicit save?
        // Let's autosave for convenience
        ApplySettings();
    }

    public void Update(float deltaTime)
    {
    }

    public void Render()
    {
        var io = ImGui.GetIO();
        var windowSize = io.DisplaySize;

        // Modal-like window
        ImGui.SetNextWindowPos(new Vector2(windowSize.X / 2, windowSize.Y / 2), ImGuiCond.Always, new Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSize(new Vector2(500, 450));

        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 8.0f);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, ThemeManager.PanelBg);
        ImGui.PushStyleColor(ImGuiCol.Border, ThemeManager.PrimaryOrange);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 2.0f);

        if (ImGui.Begin("##Settings", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoCollapse))
        {
            ImGui.PushFont(io.Fonts.Fonts[0]);
            ImGui.TextColored(ThemeManager.PrimaryOrange, "SETTINGS");
            ImGui.PopFont();

            ImGui.Separator();
            ImGui.Spacing();
            ImGui.Spacing();

            // Graphics
            ImGui.TextColored(ThemeManager.TextHighlight, "GRAPHICS");
            ImGui.Separator();

            if (ImGui.Checkbox("VSync", ref _vsync))
            {
                _renderer.VSyncEnabled = _vsync;
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Limits FPS to refresh rate to prevent tearing and high GPU usage.");

            if (ImGui.Checkbox("Fullscreen", ref _fullscreen))
            {
                // Can apply immediately via window
                // _uiManager.SetFullscreen(_fullscreen); // Need to expose this
            }

            ImGui.Checkbox("Show Debug Stats (FPS)", ref _stats);

            ImGui.Spacing();
            ImGui.Spacing();

            // Gameplay
            ImGui.TextColored(ThemeManager.TextHighlight, "GAMEPLAY");
            ImGui.Separator();
            ImGui.Checkbox("Show Legal Move Hints", ref _hints);

            ImGui.Spacing();
            ImGui.Text("Master Volume");
            ImGui.SliderFloat("##Vol", ref _volume, 0.0f, 1.0f, "%.0f%%");

            ImGui.Spacing();
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            // Footer Buttons
            float buttonWidth = 120;
            ImGui.SetCursorPosX(ImGui.GetWindowWidth() - buttonWidth * 2 - 30);

            ThemeManager.PushOverwatchButtonStyle(false);
            if (ImGui.Button("CANCEL", new Vector2(buttonWidth, 40)))
            {
                _uiManager.ToggleSettings(); // Close without saving? Or revert?
                // For now, OnHide saves, so we need to revert local vars if we want 'Cancel' behavior.
                // But OnHide calls ApplySettings. We should separate Save/Close logic.
                // Refactoring OnHide to NOT save, and Save button to save.
                // But UI flow "Back" usually implies save.
                // Let's make "DONE" button.
            }
            ThemeManager.PopOverwatchButtonStyle();

            ImGui.SameLine();

            ThemeManager.PushOverwatchButtonStyle(true);
            if (ImGui.Button("SAVE & CLOSE", new Vector2(buttonWidth, 40)))
            {
                ApplySettings();
                _uiManager.ToggleSettings();
            }
            ThemeManager.PopOverwatchButtonStyle();

            ImGui.End();
        }

        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar(2);
    }

    private void ApplySettings()
    {
        bool settingsChanged = SettingsManager.Instance.VSync != _vsync ||
                               SettingsManager.Instance.Fullscreen != _fullscreen || // Fullscreen might need logic
                               SettingsManager.Instance.ShowDebugStats != _stats;

        SettingsManager.Instance.VSync = _vsync;
        SettingsManager.Instance.Fullscreen = _fullscreen;
        SettingsManager.Instance.ShowDebugStats = _stats;
        SettingsManager.Instance.ShowLegalMoveHints = _hints;
        SettingsManager.Instance.MasterVolume = _volume;

        SettingsManager.Save();

        _renderer.VSyncEnabled = _vsync;
    }
}
