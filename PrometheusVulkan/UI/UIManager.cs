using System.Numerics;
using PrometheusVulkan.Graphics;
using PrometheusVulkan.UI.Screens;
using ImGuiNET;
using PrometheusVulkan.Core;
using PrometheusVulkan.State;
using PrometheusVulkan.UI;

namespace PrometheusVulkan.UI;

/// <summary>
/// Manages all UI screens and rendering using ImGui with Overwatch-inspired styling.
/// Most colours are yellow, because Overwatch 1 colour schemes are mostly like that
/// I just don't like Overwatch 2 colour schemes in general
/// By the way, I'm not good at designing, so this is all I got.
/// </summary>
public sealed class UIManager : IDisposable
{
    private readonly GameManager _gameManager;
    private readonly VulkanRenderer _renderer;
    private readonly ImGuiController _imGuiController;
    private readonly ResourceManager _resourceManager;

    // Screens
    private IScreen? _currentScreen;
    private readonly LoginScreen _loginScreen;
    private readonly LobbyScreen _lobbyScreen;
    private readonly GameScreen _gameScreen;
    private readonly SettingsScreen _settingsScreen;

    // UI State
    private bool _showDebugOverlay;
    private bool _showSettings;
    private bool _isDisposed;

    public UIManager(GameManager gameManager, VulkanRenderer renderer, ImGuiController imGuiController)
    {
        _gameManager = gameManager ?? throw new ArgumentNullException(nameof(gameManager));
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        _imGuiController = imGuiController ?? throw new ArgumentNullException(nameof(imGuiController));

        // Initialize Resources
        _resourceManager = new ResourceManager(_imGuiController);
        _resourceManager.LoadTextures();

        // Initialize Screens
        _loginScreen = new LoginScreen(_gameManager, this);
        _lobbyScreen = new LobbyScreen(_gameManager, this);
        _gameScreen = new GameScreen(_gameManager, this, _resourceManager);
        _settingsScreen = new SettingsScreen(this);

        // Connect events
        _gameManager.StateChanged += OnGameStateChanged;

        // Set initial screen
        UpdateScreenForState(_gameManager.CurrentState);

        // Initial Theme Apply
        ThemeManager.ApplyTheme();
    }

    public void Render()
    {
        if (_isDisposed) return;

        // Update Time
        float deltaTime = ImGui.GetIO().DeltaTime;

        // Global Style Enforce (in case it gets reset)
        // ThemeManager.ApplyTheme(); // Optimization: Only call if needed or once per frame if we support dynamic theme switching

        // Update & Render Current Screen
        _currentScreen?.Update(deltaTime);
        _currentScreen?.Render();

        // Render Settings Overlay (Modal-ish)
        if (_showSettings)
        {
            _settingsScreen.Update(deltaTime);
            _settingsScreen.Render();
        }

        // Global Overlays
        if (SettingsManager.Instance.ShowDebugStats)
        {
            RenderDebugOverlay();
        }

        // Memory Mode Warning (Global) - only show after successful login
        if (_gameManager.IsServerInMemoryMode && _gameManager.CurrentState >= GameState.InLobby)
        {
            RenderMemoryModeWarning();
        }
    }

    private void OnGameStateChanged(GameState newState)
    {
        UpdateScreenForState(newState);
    }

    private void UpdateScreenForState(GameState state)
    {
        IScreen? nextScreen = null;

        switch (state)
        {
            case GameState.Disconnected:
            case GameState.Connecting:
            case GameState.Connected:
            case GameState.LoggingIn:
                nextScreen = _loginScreen;
                break;

            case GameState.InLobby:
            case GameState.Matchmaking:
                nextScreen = _lobbyScreen;
                break;

            case GameState.InGame:
            case GameState.GameOver:
                nextScreen = _gameScreen;
                break;
        }

        if (nextScreen != null && nextScreen != _currentScreen)
        {
            _currentScreen?.OnHide();
            _currentScreen = nextScreen;
            _currentScreen.OnShow();
        }
    }

    public void ToggleSettings()
    {
        _showSettings = !_showSettings;
        if (_showSettings) _settingsScreen.OnShow();
        else _settingsScreen.OnHide();
    }

    public void ToggleDebugOverlay()
    {
        SettingsManager.Instance.ShowDebugStats = !SettingsManager.Instance.ShowDebugStats;
    }

    public void TogglePauseMenu()
    {
        // Simple toggle settings for now as pause menu
        ToggleSettings();
    }

    private void RenderDebugOverlay()
    {
        var io = ImGui.GetIO();
        float headerOffset = 100f; // Below the header

        ImGui.SetNextWindowPos(new Vector2(10, headerOffset));
        ImGui.SetNextWindowBgAlpha(0.85f);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0.08f, 0.1f, 0.12f, 0.95f));
        ImGui.PushStyleColor(ImGuiCol.Border, ThemeManager.PrimaryOrange);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1.0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 6.0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(12, 10));

        if (ImGui.Begin("##DebugStats", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav))
        {
            ImGui.TextColored(ThemeManager.PrimaryOrange, "DEBUG INFO");
            ImGui.Separator();
            ImGui.Spacing();

            // Performance
            ImGui.TextColored(ThemeManager.TextHighlight, "PERFORMANCE");
            ImGui.Text($"  FPS: {io.Framerate:F1}");
            ImGui.Text($"  Frame Time: {1000.0f / io.Framerate:F2} ms");
            ImGui.Text($"  Frame: {_renderer.CurrentFrame}");

            ImGui.Spacing();

            // Vulkan Info
            ImGui.TextColored(ThemeManager.TextHighlight, "VULKAN");
            ImGui.Text($"  Device: {_renderer.DeviceName ?? "Unknown"}");
            ImGui.Text($"  Swapchain: {_renderer.SwapchainImageCount} images");
            ImGui.Text($"  Resolution: {(int)io.DisplaySize.X}x{(int)io.DisplaySize.Y}");

            ImGui.Spacing();

            // Game State
            ImGui.TextColored(ThemeManager.TextHighlight, "GAME STATE");
            ImGui.Text($"  State: {_gameManager.CurrentState}");
            ImGui.Text($"  User: {_gameManager.Username ?? "Not logged in"}");
            ImGui.Text($"  Rating: {_gameManager.Rating}");

            if (_gameManager.CurrentState >= GameState.InGame)
            {
                ImGui.Text($"  GameID: {_gameManager.CurrentGameId ?? "N/A"}");
                ImGui.Text($"  Playing as: {_gameManager.PlayerColor}");
                ImGui.Text($"  Opponent: {_gameManager.OpponentName ?? "N/A"}");
                ImGui.Text($"  Moves: {_gameManager.MoveHistory.Count}");
            }

            ImGui.Spacing();

            // Network
            ImGui.TextColored(ThemeManager.TextHighlight, "NETWORK");
            ImGui.Text($"  Connected: {_gameManager.CurrentState >= GameState.Connected}");
            ImGui.Text($"  Memory Mode: {_gameManager.IsServerInMemoryMode}");

            ImGui.End();
        }
        ImGui.PopStyleVar(3);
        ImGui.PopStyleColor(2);
    }

    private void RenderMemoryModeWarning()
    {
        var io = ImGui.GetIO();
        float width = 360;
        float headerOffset = 100f; // Below header
        float padding = 16;

        ImGui.SetNextWindowPos(new Vector2(io.DisplaySize.X - width - padding, headerOffset), ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(width, 0));

        ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0.2f, 0.08f, 0.02f, 0.95f));
        ImGui.PushStyleColor(ImGuiCol.Border, ThemeManager.WarningYellow);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 2.0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 6.0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(14, 12));

        if (ImGui.Begin("##MemoryWarning", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoInputs | ImGuiWindowFlags.AlwaysAutoResize))
        {
            float windowWidth = ImGui.GetWindowSize().X;

            string title = "⚠ MEMORY MODE";
            ImGui.PushFont(io.Fonts.Fonts[0]);
            float titleWidth = ImGui.CalcTextSize(title).X;
            ImGui.SetCursorPosX((windowWidth - titleWidth) / 2);
            ImGui.TextColored(ThemeManager.WarningYellow, title);
            ImGui.PopFont();

            ImGui.Spacing();
            ImGui.PushTextWrapPos(windowWidth - 20);
            ImGui.TextColored(ThemeManager.TextMuted, "Server is running in memory mode. Progress will be lost on restart.");
            ImGui.PopTextWrapPos();
        }
        ImGui.End();
        ImGui.PopStyleVar(3);
        ImGui.PopStyleColor(2);
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _gameManager.StateChanged -= OnGameStateChanged;

        _resourceManager.Dispose();

        Console.WriteLine("[UIManager] Disposed");
    }
}
