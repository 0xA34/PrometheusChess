using System.Numerics;
using PrometheusVulkan.Core;
using ImGuiNET;
using PrometheusVulkan.State;

namespace PrometheusVulkan.UI.Screens;

public class LobbyScreen : IScreen
{
    private readonly GameManager _gameManager;
    private readonly UIManager _uiManager;

    private int _selectedFormat = 1; // 0=Bullet, 1=Blitz, 2=Rapid, 3=Classical
    private int _ratingRange = 200;

    // Pulse animation for matchmaking search
    private float _searchPulse = 0f;
    private float _searchTime = 0f;

    public LobbyScreen(GameManager gameManager, UIManager uiManager)
    {
        _gameManager = gameManager;
        _uiManager = uiManager;
    }

    public void OnShow()
    {
        _searchTime = 0;
    }

    public void OnHide()
    {
    }

    public void Update(float deltaTime)
    {
        if (_gameManager.CurrentState == GameState.Matchmaking)
        {
            _searchTime += deltaTime;
            _searchPulse += deltaTime * 2.0f;
        }
    }

    public void Render()
    {
        var io = ImGui.GetIO();
        var windowSize = io.DisplaySize;

        // === HEADER BAR ===
        RenderHeader(windowSize);

        // === MAIN CONTENT ===
        if (_gameManager.CurrentState == GameState.Matchmaking)
        {
            RenderMatchmakingOverlay(windowSize);
        }
        else
        {
            RenderPlayMenu(windowSize);
        }

        // === FOOTER BUTTONS ===
        RenderFooter(windowSize);
    }

    private void RenderHeader(Vector2 windowSize)
    {
        float headerHeight = 70f;

        ImGui.SetNextWindowPos(Vector2.Zero);
        ImGui.SetNextWindowSize(new Vector2(windowSize.X, headerHeight));
        ImGui.PushStyleColor(ImGuiCol.WindowBg, ThemeManager.PanelBg);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(24, 16));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0);

        if (ImGui.Begin("##LobbyHeader", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove))
        {
            // Left: Game Title
            ImGui.PushFont(ImGui.GetIO().Fonts.Fonts[0]);
            ImGui.TextColored(ThemeManager.PrimaryOrange, "PROMETHEUS");
            ImGui.PopFont();

            // Right: Player Info
            string playerText = $"{_gameManager.Username} ({_gameManager.Rating})";
            float textWidth = ImGui.CalcTextSize(playerText).X;
            ImGui.SameLine(windowSize.X - textWidth - 48);
            ImGui.SetCursorPosY(20);
            ImGui.TextColored(ThemeManager.TextWhite, playerText);
        }
        ImGui.End();
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor();

        // Bottom border line
        var drawList = ImGui.GetBackgroundDrawList();
        drawList.AddLine(
            new Vector2(0, headerHeight),
            new Vector2(windowSize.X, headerHeight),
            ImGui.ColorConvertFloat4ToU32(ThemeManager.PanelBorder),
            2.0f
        );
    }

    private void RenderPlayMenu(Vector2 windowSize)
    {
        float headerHeight = 70f;
        float footerHeight = 60f;
        float contentHeight = windowSize.Y - headerHeight - footerHeight;

        // Centered content panel
        float panelWidth = Math.Min(900f, windowSize.X - 80f);
        float panelX = (windowSize.X - panelWidth) / 2;

        ImGui.SetNextWindowPos(new Vector2(panelX, headerHeight + 30));
        ImGui.SetNextWindowSize(new Vector2(panelWidth, contentHeight - 60));
        ImGui.PushStyleColor(ImGuiCol.WindowBg, Vector4.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(20, 20));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0);

        if (ImGui.Begin("##PlayMenu", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove))
        {
            // Section Title
            ImGui.PushFont(ImGui.GetIO().Fonts.Fonts[0]);
            string title = "SELECT GAME MODE";
            float titleW = ImGui.CalcTextSize(title).X;
            ImGui.SetCursorPosX((panelWidth - titleW) / 2 - 20);
            ImGui.TextColored(ThemeManager.TextHighlight, title);
            ImGui.PopFont();

            ImGui.Spacing();
            ImGui.Spacing();

            // === GAME MODE CARDS (2x2 Grid) ===
            float cardWidth = (panelWidth - 80) / 2;
            float cardHeight = 140f;
            float cardSpacing = 20f;

            float gridWidth = cardWidth * 2 + cardSpacing;
            float gridStartX = (panelWidth - gridWidth) / 2 - 20;

            ImGui.SetCursorPosX(gridStartX);
            ImGui.BeginGroup();

            // Row 1
            RenderGameModeCard("BULLET", "1+0", "Super fast games", 0, cardWidth, cardHeight);
            ImGui.SameLine(0, cardSpacing);
            RenderGameModeCard("BLITZ", "3+2", "Fast paced action", 1, cardWidth, cardHeight);

            ImGui.Spacing();
            ImGui.Spacing();

            // Row 2
            RenderGameModeCard("RAPID", "10+0", "Standard play", 2, cardWidth, cardHeight);
            ImGui.SameLine(0, cardSpacing);
            RenderGameModeCard("CLASSICAL", "30+0", "Slow & strategic", 3, cardWidth, cardHeight);

            ImGui.EndGroup();

            ImGui.Spacing();
            ImGui.Spacing();
            ImGui.Spacing();

            // === RATING RANGE ===
            ImGui.Separator();
            ImGui.Spacing();

            float sliderWidth = Math.Min(400f, panelWidth - 40);
            ImGui.SetCursorPosX((panelWidth - sliderWidth) / 2 - 20);
            ImGui.BeginGroup();
            ImGui.Text($"Opponent Rating Range: ± {_ratingRange}");
            ImGui.SetNextItemWidth(sliderWidth);
            ImGui.SliderInt("##RatingRange", ref _ratingRange, 50, 500);
            ImGui.EndGroup();

            ImGui.Spacing();
            ImGui.Spacing();
            ImGui.Spacing();

            // === FIND MATCH BUTTON ===
            float buttonWidth = 280f;
            float buttonHeight = 56f;
            ImGui.SetCursorPosX((panelWidth - buttonWidth) / 2 - 20);

            ThemeManager.PushOverwatchButtonStyle(true);
            if (ImGui.Button("FIND MATCH", new Vector2(buttonWidth, buttonHeight)))
            {
                StartMatchmaking();
            }
            ThemeManager.PopOverwatchButtonStyle();
        }
        ImGui.End();
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor();
    }

    private void RenderGameModeCard(string title, string timeControl, string desc, int formatId, float width, float height)
    {
        bool isSelected = _selectedFormat == formatId;

        var bgColor = isSelected ? ThemeManager.PanelBackgroundLight : ThemeManager.BackgroundDarker;
        ImGui.PushStyleColor(ImGuiCol.ChildBg, bgColor);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 8.0f);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildBorderSize, isSelected ? 2.0f : 1.0f);
        ImGui.PushStyleColor(ImGuiCol.Border, isSelected ? ThemeManager.PrimaryOrange : ThemeManager.PanelBorder);

        if (ImGui.BeginChild($"##Mode{formatId}", new Vector2(width, height), ImGuiChildFlags.Borders))
        {
            float innerWidth = ImGui.GetContentRegionAvail().X;

            ImGui.Spacing();

            // Time Control (Large)
            ImGui.PushFont(ImGui.GetIO().Fonts.Fonts[0]);
            float tcWidth = ImGui.CalcTextSize(timeControl).X;
            ImGui.SetCursorPosX((innerWidth - tcWidth) / 2);
            ImGui.TextColored(isSelected ? ThemeManager.PrimaryOrange : ThemeManager.TextWhite, timeControl);
            ImGui.PopFont();

            ImGui.Spacing();

            // Title
            float titleWidth = ImGui.CalcTextSize(title).X;
            ImGui.SetCursorPosX((innerWidth - titleWidth) / 2);
            ImGui.TextColored(ThemeManager.TextMuted, title);

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            // Description
            ImGui.PushTextWrapPos(innerWidth);
            float descWidth = ImGui.CalcTextSize(desc).X;
            if (descWidth < innerWidth)
                ImGui.SetCursorPosX((innerWidth - descWidth) / 2);
            ImGui.TextColored(ThemeManager.TextMuted, desc);
            ImGui.PopTextWrapPos();

            // Click detection
            if (ImGui.IsWindowHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                _selectedFormat = formatId;
            }
        }
        ImGui.EndChild();
        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar(2);
    }

    private void RenderMatchmakingOverlay(Vector2 windowSize)
    {
        float headerHeight = 70f;

        // Full center overlay
        ImGui.SetNextWindowPos(new Vector2(0, headerHeight));
        ImGui.SetNextWindowSize(new Vector2(windowSize.X, windowSize.Y - headerHeight - 60));
        ImGui.PushStyleColor(ImGuiCol.WindowBg, Vector4.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0);

        if (ImGui.Begin("##Matchmaking", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove))
        {
            float contentWidth = ImGui.GetContentRegionAvail().X;
            float contentHeight = ImGui.GetContentRegionAvail().Y;

            // Center everything vertically
            ImGui.SetCursorPosY(contentHeight / 3);

            // Searching Text
            string text = "SEARCHING FOR OPPONENT";
            ImGui.PushFont(ImGui.GetIO().Fonts.Fonts[0]);
            float textWidth = ImGui.CalcTextSize(text).X;
            ImGui.SetCursorPosX((contentWidth - textWidth) / 2);

            float alpha = (float)(0.5 + 0.5 * Math.Sin(_searchPulse));
            ImGui.TextColored(new Vector4(ThemeManager.PrimaryOrange.X, ThemeManager.PrimaryOrange.Y, ThemeManager.PrimaryOrange.Z, alpha), text);
            ImGui.PopFont();

            ImGui.Spacing();
            ImGui.Spacing();

            // Timer
            string timerText = $"{_searchTime:F1}s";
            float timerWidth = ImGui.CalcTextSize(timerText).X;
            ImGui.SetCursorPosX((contentWidth - timerWidth) / 2);
            ImGui.TextColored(ThemeManager.TextWhite, timerText);

            ImGui.Spacing();

            // Queue info
            if (_gameManager.QueuePosition > 0)
            {
                string queueText = $"Queue position: {_gameManager.QueuePosition}";
                float qWidth = ImGui.CalcTextSize(queueText).X;
                ImGui.SetCursorPosX((contentWidth - qWidth) / 2);
                ImGui.TextColored(ThemeManager.TextMuted, queueText);
            }

            ImGui.Spacing();
            ImGui.Spacing();
            ImGui.Spacing();

            // Cancel Button
            float btnWidth = 220f;
            ImGui.SetCursorPosX((contentWidth - btnWidth) / 2);
            ThemeManager.PushOverwatchButtonStyle(false);
            if (ImGui.Button("CANCEL SEARCH", new Vector2(btnWidth, 48)))
            {
                _ = _gameManager.CancelMatchmakingAsync();
            }
            ThemeManager.PopOverwatchButtonStyle();
        }
        ImGui.End();
        ImGui.PopStyleVar();
        ImGui.PopStyleColor();
    }

    private void RenderFooter(Vector2 windowSize)
    {
        float footerHeight = 50f;
        float footerY = windowSize.Y - footerHeight - 10;

        ImGui.SetNextWindowPos(new Vector2(windowSize.X - 260, footerY));
        ImGui.SetNextWindowSize(new Vector2(250, footerHeight));
        ImGui.PushStyleColor(ImGuiCol.WindowBg, Vector4.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0);

        if (ImGui.Begin("##LobbyFooter", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove))
        {
            ThemeManager.PushOverwatchButtonStyle(false);

            if (ImGui.Button("SETTINGS", new Vector2(100, 36)))
            {
                _uiManager.ToggleSettings();
            }
            ImGui.SameLine(0, 12);
            if (ImGui.Button("LOGOUT", new Vector2(100, 36)))
            {
                _ = _gameManager.LogoutAsync();
            }

            ThemeManager.PopOverwatchButtonStyle();
        }
        ImGui.End();
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor();
    }

    private async void StartMatchmaking()
    {
        int timeMs = 600000;
        int incMs = 0;

        switch (_selectedFormat)
        {
            case 0: timeMs = 60000; incMs = 0; break;
            case 1: timeMs = 180000; incMs = 2000; break;
            case 2: timeMs = 600000; incMs = 0; break;
            case 3: timeMs = 1800000; incMs = 0; break;
        }

        var type = ChessCore.Models.TimeControlType.Rapid;
        switch (_selectedFormat)
        {
            case 0: type = ChessCore.Models.TimeControlType.Bullet; break;
            case 1: type = ChessCore.Models.TimeControlType.Blitz; break;
            case 2: type = ChessCore.Models.TimeControlType.Rapid; break;
            case 3: type = ChessCore.Models.TimeControlType.Classical; break;
        }

        await _gameManager.FindMatchAsync(type, timeMs, incMs, _ratingRange);
    }
}
