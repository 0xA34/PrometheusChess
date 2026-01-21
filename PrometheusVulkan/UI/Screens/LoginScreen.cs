using System.Numerics;
using ImGuiNET;
using PrometheusVulkan.Core;
using PrometheusVulkan.State;
using PrometheusVulkan.UI.Screens;

namespace PrometheusVulkan.UI.Screens;

public class LoginScreen : IScreen
{
    private readonly GameManager _gameManager;
    private readonly UIManager _uiManager;

    private string _username = "";
    private string _password = "";
    private string _email = "";
    
    private bool _showRegisterForm;
    private float _statusMessageTimer;
    private string _statusMessage = "";
    private bool _statusIsError;

    public LoginScreen(GameManager gameManager, UIManager uiManager)
    {
        _gameManager = gameManager;
        _uiManager = uiManager;
        
        // Auto-fill username if available
        if (!string.IsNullOrEmpty(SettingsManager.Instance.LastUsername))
        {
            _username = SettingsManager.Instance.LastUsername;
        }

        _gameManager.Client.RegisterSuccess += (_) => OnRegisterSuccess();
    }

    public void OnShow() 
    {
        _password = "";
        _statusMessage = "";
    }

    public void OnHide() { }

    public void Update(float deltaTime)
    {
        if (_statusMessageTimer > 0)
        {
            _statusMessageTimer -= deltaTime;
            if (_statusMessageTimer <= 0) _statusMessage = "";
        }
    }

    public void Render()
    {
        var io = ImGui.GetIO();
        var windowSize = io.DisplaySize;

        // Background (handled by UIManager or just draw a localized one?)
        // UIManager typically handles the global background.

        // Center the login window
        ImGui.SetNextWindowPos(new Vector2(windowSize.X / 2, windowSize.Y / 2), ImGuiCond.Always, new Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSize(new Vector2(380, 0));
        
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 8.0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(28, 24));
        ImGui.PushStyleColor(ImGuiCol.WindowBg, ThemeManager.PanelBg);
        ImGui.PushStyleColor(ImGuiCol.Border, ThemeManager.PrimaryOrange);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1.0f);

        var flags = ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoTitleBar;
        
        if (ImGui.Begin("##LoginWindow", flags))
        {
            RenderHeader();
            
            bool isConnecting = _gameManager.CurrentState == GameState.Connecting;

            if (isConnecting)
            {
                RenderConnectingState();
            }
            else
            {
                if (!_showRegisterForm)
                    RenderLoginForm();
                else
                    RenderRegisterForm();
            }

            RenderStatusMessage();

            ImGui.Spacing();
            ImGui.TextColored(new Vector4(0.35f, 0.38f, 0.44f, 1.0f), "v0.0.4-alpha-devr");
            
            ImGui.End();
        }

        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar(3);
        
        // Settings Button (Bottom Right)
        float padding = 20;
        ImGui.SetNextWindowPos(new Vector2(windowSize.X - padding, windowSize.Y - padding), ImGuiCond.Always, new Vector2(1.0f, 1.0f));
        ImGui.SetNextWindowBgAlpha(0.0f); // Transparent bg
        if (ImGui.Begin("##SettingsBtn", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoInputs | ImGuiWindowFlags.AlwaysAutoResize)) // NoInputs needs to be off for button.. wait
        {
            // Actually, just draw a button on top-layer window or a dedicated simplistic window
            // Since we need to click it, we need a normal window
        }
        ImGui.End();
        
        // Easier: Just a freestanding window for the button
        ImGui.SetNextWindowPos(new Vector2(windowSize.X - 20, windowSize.Y - 20), ImGuiCond.Always, new Vector2(1.0f, 1.0f));
        ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0,0,0,0));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        if (ImGui.Begin("##LoginSettingsBtn", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.AlwaysAutoResize))
        {
            ThemeManager.PushOverwatchButtonStyle(false);
            if (ImGui.Button("SETTINGS"))
            {
                _uiManager.ToggleSettings();
            }
            ThemeManager.PopOverwatchButtonStyle();
            ImGui.End();
        }
        ImGui.PopStyleColor();
        ImGui.PopStyleVar();
    }

    private void RenderHeader()
    {
        // Title
        ImGui.PushFont(ImGui.GetIO().Fonts.Fonts[0]);
        ImGui.TextColored(ThemeManager.PrimaryOrange, "PROMETHEUS");
        ImGui.PopFont();
            
        ImGui.SameLine();
        float width = ImGui.GetContentRegionAvail().X;
        float textW = ImGui.CalcTextSize("CONFIDENTIAL").X;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + width - textW);
        ImGui.TextColored(ThemeManager.TextMuted, "CONFIDENTIAL");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
    }

    private void RenderConnectingState()
    {
        ImGui.TextColored(ThemeManager.WarningYellow, "Connecting to server...");
        ImGui.Spacing();
        ThemeManager.PushOverwatchButtonStyle(false);
        if (ImGui.Button("CANCEL", new Vector2(-1, 40)))
        {
            _gameManager.CancelConnection();
        }
        ThemeManager.PopOverwatchButtonStyle();
    }

    private void RenderLoginForm()
    {
        ImGui.TextColored(ThemeManager.TextMuted, "LOGIN");
        ImGui.Spacing();

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##Username", "Username", ref _username, 64);

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##Password", "Password", ref _password, 64, ImGuiInputTextFlags.Password);

        ImGui.Spacing();

        ThemeManager.PushOverwatchButtonStyle();
        bool isLoggingIn = _gameManager.CurrentState == GameState.LoggingIn;
        if (ImGui.Button(isLoggingIn ? "LOGGING IN..." : "LOGIN", new Vector2(-1, 48)))
        {
            if (!string.IsNullOrWhiteSpace(_username))
            {
                SettingsManager.Instance.LastUsername = _username;
                SettingsManager.Save();
            }
            
            _ = LoginAsync();
        }
        ThemeManager.PopOverwatchButtonStyle();

        ImGui.Spacing();

        // Switch to register
        RenderLinkButton("Need an account? Register here", () => 
        {
            _showRegisterForm = true;
            _statusMessage = "";
        });

        // Check if we just registered successfully (hacky but simple without event wiring right here)
        // Ideally we wire up the event in the constructor.
    }

    private void OnRegisterSuccess()
    {
        _showRegisterForm = false;
        SetStatus("Registration successful! Please login.", false);
    }

    private void RenderRegisterForm()
    {
        ImGui.TextColored(ThemeManager.TextMuted, "CREATE ACCOUNT");
        ImGui.Spacing();

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##RegUsername", "Username", ref _username, 64);

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##RegEmail", "Email", ref _email, 128);

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##RegPassword", "Password", ref _password, 64, ImGuiInputTextFlags.Password);

        ImGui.Spacing();

        ThemeManager.PushOverwatchButtonStyle();
        if (ImGui.Button("CREATE ACCOUNT", new Vector2(-1, 48)))
        {
            _ = RegisterAsync();
        }
        ThemeManager.PopOverwatchButtonStyle();

        ImGui.Spacing();

        RenderLinkButton("Already have an account? Login", () => 
        {
            _showRegisterForm = false;
            _statusMessage = "";
        });
    }

    private void RenderLinkButton(string text, Action onClick)
    {
        float windowWidth = ImGui.GetWindowSize().X;
        float buttonWidth = ImGui.CalcTextSize(text).X + 20;
        ImGui.SetCursorPosX((windowWidth - buttonWidth) / 2);

        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0, 0, 0, 0));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0, 0, 0, 0));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0, 0, 0, 0));
        ImGui.PushStyleColor(ImGuiCol.Text, ThemeManager.TextMuted);

        if (ImGui.Button(text))
        {
            onClick();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            var drawList = ImGui.GetWindowDrawList();
            var min = ImGui.GetItemRectMin();
            var max = ImGui.GetItemRectMax();
            drawList.AddLine(new Vector2(min.X, max.Y), new Vector2(max.X, max.Y), ImGui.ColorConvertFloat4ToU32(ThemeManager.PrimaryOrange));
        }

        ImGui.PopStyleColor(4);
    }

    private void RenderStatusMessage()
    {
        if (string.IsNullOrEmpty(_statusMessage)) return;

        var color = _statusIsError ? ThemeManager.DangerRed : ThemeManager.PrimaryOrange;
        if (_statusMessageTimer < 1.0f) color.W = _statusMessageTimer;

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        float textWidth = ImGui.CalcTextSize(_statusMessage).X;
        float windowWidth = ImGui.GetWindowWidth();
        ImGui.SetCursorPosX((windowWidth - textWidth) / 2);

        ImGui.TextColored(color, _statusMessage);
    }

    private void SetStatus(string message, bool isError)
    {
        _statusMessage = message;
        _statusIsError = isError;
        _statusMessageTimer = 5.0f;
    }

    private async Task LoginAsync()
    {
        if (string.IsNullOrWhiteSpace(_username) || string.IsNullOrWhiteSpace(_password))
        {
            SetStatus("Please enter username and password", true);
            return;
        }

        if (_gameManager.CurrentState == GameState.Disconnected)
        {
            SetStatus("Connecting to server...", false);
            // Defaulting to stored host/port or hardcoded if logic dictates
            await _gameManager.ConnectToServerAsync();
            
            // Allow some time for connection
            await Task.Delay(500);
            if (_gameManager.CurrentState != GameState.Connected)
            {
                 // Wait a bit more or fail? GameManager handles state, so we just check.
                 // We'll let the UI update show "Connecting..." if it's still connecting.
                 // But preventing deadlock:
            }
        }

        SetStatus("Authenticating...", false);
        await _gameManager.LoginAsync(_username, _password);
    }

    private async Task RegisterAsync()
    {
        // Validation logic similar to before...
        if (string.IsNullOrWhiteSpace(_username) || string.IsNullOrWhiteSpace(_email) || string.IsNullOrWhiteSpace(_password))
        {
            SetStatus("Please fill all fields", true);
            return;
        }

        if (_gameManager.CurrentState == GameState.Disconnected)
        {
            SetStatus("Connecting to server...", false);
            await _gameManager.ConnectToServerAsync();
        }

        SetStatus("Creating account...", false);
        await _gameManager.RegisterAsync(_username, _email, _password);
    }
}
