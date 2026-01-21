using System.Numerics;
using ImGuiNET;

namespace PrometheusVulkan.UI;

public static class ThemeManager
{
    // Overwatch-inspired Palette
    public static readonly Vector4 PrimaryOrange = new(1.0f, 0.61f, 0.0f, 1.0f);
    public static readonly Vector4 PrimaryOrangeHover = new(1.0f, 0.75f, 0.2f, 1.0f);
    public static readonly Vector4 PrimaryOrangeActive = new(0.9f, 0.5f, 0.0f, 1.0f);
    
    public static readonly Vector4 BackgroundDark = new(0.11f, 0.13f, 0.17f, 1.0f);
    public static readonly Vector4 BackgroundDarker = new(0.08f, 0.09f, 0.12f, 1.0f);
    
    public static readonly Vector4 PanelBg = new(0.13f, 0.15f, 0.20f, 0.95f);
    public static readonly Vector4 PanelBorder = new(0.3f, 0.35f, 0.45f, 0.5f);
    public static readonly Vector4 PanelBackgroundLight = new(0.18f, 0.20f, 0.25f, 1.0f);
    
    public static readonly Vector4 TextWhite = new(0.95f, 0.96f, 0.98f, 1.0f);
    public static readonly Vector4 TextMuted = new(0.6f, 0.65f, 0.75f, 1.0f);
    public static readonly Vector4 TextHighlight = new(0.4f, 0.8f, 1.0f, 1.0f);
    public static readonly Vector4 TextDark = new(0.1f, 0.1f, 0.1f, 1.0f);
    
    // Status Colors
    public static readonly Vector4 SuccessGreen = new(0.2f, 0.85f, 0.4f, 1.0f);
    public static readonly Vector4 WarningYellow = new(0.95f, 0.85f, 0.2f, 1.0f);
    public static readonly Vector4 DangerRed = new(0.9f, 0.25f, 0.25f, 1.0f);
    public static readonly Vector4 DangerRedHover = new(1.0f, 0.4f, 0.4f, 1.0f);

    // Board Colors
    public static readonly Vector4 LightSquareColor = new(0.9f, 0.85f, 0.8f, 1.0f);
    public static readonly Vector4 DarkSquareColor = new(0.4f, 0.5f, 0.6f, 1.0f);
    public static readonly Vector4 SelectedSquareColor = new(0.3f, 0.8f, 0.9f, 0.6f);
    public static readonly Vector4 LastMoveColor = new(0.9f, 0.8f, 0.2f, 0.5f);
    public static readonly Vector4 LegalMoveColor = new(0.2f, 0.8f, 0.2f, 0.5f);
    public static readonly Vector4 CheckColor = new(1.0f, 0.2f, 0.2f, 0.6f);

    public static void ApplyTheme()
    {
        var style = ImGui.GetStyle();
        var colors = style.Colors;

        // Styling
        style.WindowRounding = 0.0f; 
        style.FrameRounding = 2.0f;
        style.PopupRounding = 2.0f;
        style.ScrollbarRounding = 0.0f;
        style.GrabRounding = 2.0f;
        style.WindowBorderSize = 0.0f;
        style.FrameBorderSize = 0.0f;
        style.WindowPadding = new Vector2(16, 16);
        style.FramePadding = new Vector2(10, 8);
        style.ItemSpacing = new Vector2(10, 10);
        
        // Base Colors
        colors[(int)ImGuiCol.WindowBg] = BackgroundDark;
        colors[(int)ImGuiCol.ChildBg] = BackgroundDarker;
        colors[(int)ImGuiCol.PopupBg] = PanelBg;
        colors[(int)ImGuiCol.Border] = PanelBorder;
        colors[(int)ImGuiCol.Text] = TextWhite;
        
        // Headers / Accents
        colors[(int)ImGuiCol.Header] = new Vector4(PrimaryOrange.X, PrimaryOrange.Y, PrimaryOrange.Z, 0.3f);
        colors[(int)ImGuiCol.HeaderHovered] = new Vector4(PrimaryOrange.X, PrimaryOrange.Y, PrimaryOrange.Z, 0.5f);
        colors[(int)ImGuiCol.HeaderActive] = new Vector4(PrimaryOrange.X, PrimaryOrange.Y, PrimaryOrange.Z, 0.7f);
        
        // Buttons
        colors[(int)ImGuiCol.Button] = PrimaryOrange;
        colors[(int)ImGuiCol.ButtonHovered] = PrimaryOrangeHover;
        colors[(int)ImGuiCol.ButtonActive] = PrimaryOrangeActive;
        
        // Frame
        colors[(int)ImGuiCol.FrameBg] = PanelBackgroundLight;
        colors[(int)ImGuiCol.FrameBgHovered] = new Vector4(0.22f, 0.24f, 0.30f, 1.0f);
        colors[(int)ImGuiCol.FrameBgActive] = new Vector4(0.25f, 0.27f, 0.35f, 1.0f);
        
        // Tabs
        colors[(int)ImGuiCol.Tab] = PanelBackgroundLight;
        colors[(int)ImGuiCol.TabHovered] = PrimaryOrangeHover;
        colors[(int)ImGuiCol.TabSelected] = PrimaryOrange;
        colors[(int)ImGuiCol.TabSelectedOverline] = PrimaryOrangeActive;
        
        // Scrollbar
        colors[(int)ImGuiCol.ScrollbarBg] = BackgroundDarker;
        colors[(int)ImGuiCol.ScrollbarGrab] = PanelBackgroundLight;
        colors[(int)ImGuiCol.ScrollbarGrabHovered] = PrimaryOrangeHover;
        colors[(int)ImGuiCol.ScrollbarGrabActive] = PrimaryOrange;
        
        // Separator
        colors[(int)ImGuiCol.Separator] = PanelBorder;
        colors[(int)ImGuiCol.SeparatorHovered] = PrimaryOrange;
        colors[(int)ImGuiCol.SeparatorActive] = PrimaryOrangeActive;
    }
    
    public static void PushOverwatchButtonStyle(bool isPrimary = true)
    {
        if (isPrimary)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, PrimaryOrange);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, PrimaryOrangeHover);
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, PrimaryOrangeActive);
            ImGui.PushStyleColor(ImGuiCol.Text, TextDark);
        }
        else
        {
            ImGui.PushStyleColor(ImGuiCol.Button, PanelBackgroundLight);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.22f, 0.24f, 0.30f, 1.0f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.18f, 0.20f, 0.26f, 1.0f));
            ImGui.PushStyleColor(ImGuiCol.Text, TextWhite);
        }
    }

    public static void PopOverwatchButtonStyle()
    {
        ImGui.PopStyleColor(4);
    }
    
    public static void PushDangerButtonStyle()
    {
        ImGui.PushStyleColor(ImGuiCol.Button, DangerRed);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, DangerRedHover);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.7f, 0.15f, 0.15f, 1.0f));
        ImGui.PushStyleColor(ImGuiCol.Text, TextWhite);
    }
}
