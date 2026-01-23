using System.Numerics;
using PrometheusVulkan.Graphics;
using PrometheusVulkan.UI;
using ImGuiNET;
using PrometheusVulkan.State;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.Windowing;

namespace PrometheusVulkan.Core;

public sealed class Application : IDisposable
{
    private readonly ApplicationOptions _options;
    private IWindow? _window;
    private IInputContext? _input;
    private VulkanRenderer? _renderer;
    private ImGuiController? _imGuiController;
    private GameManager? _gameManager;
    private UIManager? _uiManager;

    private bool _isRunning;
    private bool _isDisposed;
    private double _lastFrameTime;
    private double _deltaTime;
    private int _frameCount;
    private double _fpsTimer;
    private int _currentFps;

    public Application(ApplicationOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public void Run()
    {
        Initialize();

        _isRunning = true;
        _window?.Run();
    }

    private void Initialize()
    {
        Console.WriteLine("[Application] Initialising...");

        // Create window options
        var windowOptions = WindowOptions.DefaultVulkan with
        {
            Title = "Prometheus",
            Size = new Vector2D<int>(_options.WindowWidth, _options.WindowHeight),
            VSync = _options.VSync,
            WindowState = _options.Fullscreen ? WindowState.Fullscreen : WindowState.Normal,
            API = GraphicsAPI.DefaultVulkan,
            ShouldSwapAutomatically = false,
            IsContextControlDisabled = true
        };

        // Create the window
        _window = Window.Create(windowOptions);

        // Hook up window events
        _window.Load += OnLoad;
        _window.Update += OnUpdate;
        _window.Render += OnRender;
        _window.Closing += OnClosing;
        _window.Resize += OnResize;
        _window.FocusChanged += OnFocusChanged;

        Console.WriteLine("[Application] Window created successfully");
    }

    private void OnLoad()
    {
        Console.WriteLine("[Application] Loading resources...");

        try
        {
            // Initialize input
            _input = _window!.CreateInput();

            // Set up keyboard event handling
            foreach (var keyboard in _input.Keyboards)
            {
                keyboard.KeyDown += OnKeyDown;
                keyboard.KeyUp += OnKeyUp;
                keyboard.KeyChar += OnKeyChar;
            }

            // Set up mouse event handling
            foreach (var mouse in _input.Mice)
            {
                mouse.MouseDown += OnMouseDown;
                mouse.MouseUp += OnMouseUp;
                mouse.MouseMove += OnMouseMove;
                mouse.Scroll += OnScroll;
            }

            Console.WriteLine("[Application] Input system initialized");

            // Initialise Vulkan renderer
            _renderer = new VulkanRenderer(_window, _options.EnableValidationLayers);
            _renderer.Initialize();
            _renderer.VSyncEnabled = SettingsManager.Instance.VSync;
            Console.WriteLine("[Application] Vulkan renderer initialised");

            // Initialize ImGui
            _imGuiController = new ImGuiController(_renderer, _input, _window);
            _imGuiController.Initialize();
            SetupImGuiStyle();
            Console.WriteLine("[Application] ImGui initialised");

            // Initialize game manager
            _gameManager = new GameManager(_options.ServerHost, _options.ServerPort);
            Console.WriteLine("[Application] Game manager initialised");

            // Initialize UI manager
            _uiManager = new UIManager(_gameManager, _renderer, _imGuiController!);
            Console.WriteLine("[Application] UI manager initialised");

            Console.WriteLine("[Application] All systems loaded successfully!");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Application] Error during loading: {ex.Message}");
            throw;
        }
    }

    private void OnUpdate(double deltaTime)
    {
        _deltaTime = deltaTime;

        // Update FPS counter
        _frameCount++;
        _fpsTimer += deltaTime;
        if (_fpsTimer >= 1.0)
        {
            _currentFps = _frameCount;
            _frameCount = 0;
            _fpsTimer = 0;
        }

        // Update game state
        _gameManager?.Update((float)deltaTime);

        // Update ImGui
        _imGuiController?.Update((float)deltaTime);
    }

    private void OnRender(double deltaTime)
    {
        if (_renderer == null || _imGuiController == null || _uiManager == null)
            return;

        // Begin frame
        if (!_renderer.BeginFrame())
            return;

        // Begin ImGui frame
        _imGuiController.BeginFrame();

        // Render UI based on current game state
        _uiManager.Render();

        // End ImGui frame and render
        _imGuiController.EndFrame();

        // End frame and present
        _renderer.EndFrame();
    }

    private void OnResize(Vector2D<int> newSize)
    {
        if (newSize.X <= 0 || newSize.Y <= 0)
            return;

        Console.WriteLine($"[Application] Window resized to {newSize.X}x{newSize.Y}");

        _renderer?.OnResize(newSize.X, newSize.Y);
        _imGuiController?.OnResize(newSize.X, newSize.Y);
    }

    private void OnClosing()
    {
        Console.WriteLine("[Application] Window closing...");
        _isRunning = false;
    }

    private void OnFocusChanged(bool focused)
    {
        if (focused)
        {
            Console.WriteLine("[Application] Window gained focus");
        }
        else
        {
            Console.WriteLine("[Application] Window lost focus");
        }
    }

    #region Input Handling

    private void OnKeyDown(IKeyboard keyboard, Key key, int scanCode)
    {
        // Handle global key shortcuts
        if (key == Key.Escape)
        {
            if (_gameManager?.CurrentState == GameState.InGame)
            {
                // Show pause menu or similar
                _uiManager?.TogglePauseMenu();
            }
            else if (_gameManager?.CurrentState == GameState.InLobby)
            {
                // Confirm quit dialog
            }
        }

        // Debug toggle with F3
        if (key == Key.F3)
        {
            _uiManager?.ToggleDebugOverlay();
        }

        // Fullscreen toggle with F11
        if (key == Key.F11)
        {
            ToggleFullscreen();
        }

        // Pass to ImGui
        _imGuiController?.OnKeyDown(key);
    }

    private void OnKeyUp(IKeyboard keyboard, Key key, int scanCode)
    {
        _imGuiController?.OnKeyUp(key);
    }

    private void OnKeyChar(IKeyboard keyboard, char character)
    {
        _imGuiController?.OnKeyChar(character);
    }

    private void OnMouseDown(IMouse mouse, MouseButton button)
    {
        _imGuiController?.OnMouseDown(button);
    }

    private void OnMouseUp(IMouse mouse, MouseButton button)
    {
        _imGuiController?.OnMouseUp(button);
    }

    private void OnMouseMove(IMouse mouse, Vector2 position)
    {
        _imGuiController?.OnMouseMove(position);
    }

    private void OnScroll(IMouse mouse, ScrollWheel scroll)
    {
        _imGuiController?.OnScroll(scroll.Y);
    }

    #endregion

    private void ToggleFullscreen()
    {
        if (_window == null) return;

        _window.WindowState = _window.WindowState == WindowState.Fullscreen
            ? WindowState.Normal
            : WindowState.Fullscreen;
    }

    private void SetupImGuiStyle()
    {
        var style = ImGui.GetStyle();
        var colors = style.Colors;

        // Overwatch-inspired dark theme with orange accents
        // I'm really bad at designing menus so don't blame me.
        // yeah yeah there's another approach to this but, whatever.
        style.WindowRounding = 4.0f;
        style.FrameRounding = 4.0f;
        style.GrabRounding = 4.0f;
        style.PopupRounding = 4.0f;
        style.ScrollbarRounding = 4.0f;
        style.TabRounding = 4.0f;

        style.WindowPadding = new Vector2(12, 12);
        style.FramePadding = new Vector2(8, 6);
        style.ItemSpacing = new Vector2(8, 6);
        style.ItemInnerSpacing = new Vector2(6, 4);

        // Background colors
        colors[(int)ImGuiCol.WindowBg] = new Vector4(0.075f, 0.09f, 0.12f, 0.98f);
        colors[(int)ImGuiCol.ChildBg] = new Vector4(0.06f, 0.07f, 0.09f, 0.95f);
        colors[(int)ImGuiCol.PopupBg] = new Vector4(0.08f, 0.09f, 0.11f, 0.98f);

        // Border
        colors[(int)ImGuiCol.Border] = new Vector4(0.2f, 0.24f, 0.3f, 0.6f);
        colors[(int)ImGuiCol.BorderShadow] = new Vector4(0, 0, 0, 0);

        // Frame backgrounds
        colors[(int)ImGuiCol.FrameBg] = new Vector4(0.12f, 0.14f, 0.18f, 1.0f);
        colors[(int)ImGuiCol.FrameBgHovered] = new Vector4(0.16f, 0.18f, 0.24f, 1.0f);
        colors[(int)ImGuiCol.FrameBgActive] = new Vector4(0.2f, 0.22f, 0.28f, 1.0f);

        // Title bar
        colors[(int)ImGuiCol.TitleBg] = new Vector4(0.06f, 0.07f, 0.09f, 1.0f);
        colors[(int)ImGuiCol.TitleBgActive] = new Vector4(0.08f, 0.1f, 0.13f, 1.0f);
        colors[(int)ImGuiCol.TitleBgCollapsed] = new Vector4(0.06f, 0.07f, 0.09f, 0.5f);

        // Menu bar
        colors[(int)ImGuiCol.MenuBarBg] = new Vector4(0.08f, 0.1f, 0.13f, 1.0f);

        // Scrollbar
        colors[(int)ImGuiCol.ScrollbarBg] = new Vector4(0.05f, 0.06f, 0.08f, 0.8f);
        colors[(int)ImGuiCol.ScrollbarGrab] = new Vector4(0.25f, 0.28f, 0.35f, 1.0f);
        colors[(int)ImGuiCol.ScrollbarGrabHovered] = new Vector4(0.35f, 0.38f, 0.45f, 1.0f);
        colors[(int)ImGuiCol.ScrollbarGrabActive] = new Vector4(0.98f, 0.61f, 0.08f, 1.0f);

        // Primary orange accent (buttons, sliders, etc.)
        colors[(int)ImGuiCol.CheckMark] = new Vector4(0.98f, 0.61f, 0.08f, 1.0f);
        colors[(int)ImGuiCol.SliderGrab] = new Vector4(0.98f, 0.61f, 0.08f, 1.0f);
        colors[(int)ImGuiCol.SliderGrabActive] = new Vector4(1.0f, 0.68f, 0.2f, 1.0f);

        // Buttons
        colors[(int)ImGuiCol.Button] = new Vector4(0.98f, 0.61f, 0.08f, 1.0f);
        colors[(int)ImGuiCol.ButtonHovered] = new Vector4(1.0f, 0.68f, 0.2f, 1.0f);
        colors[(int)ImGuiCol.ButtonActive] = new Vector4(0.82f, 0.5f, 0.03f, 1.0f);

        // Headers
        colors[(int)ImGuiCol.Header] = new Vector4(0.98f, 0.61f, 0.08f, 0.3f);
        colors[(int)ImGuiCol.HeaderHovered] = new Vector4(0.98f, 0.61f, 0.08f, 0.5f);
        colors[(int)ImGuiCol.HeaderActive] = new Vector4(0.98f, 0.61f, 0.08f, 0.7f);

        // Separator
        colors[(int)ImGuiCol.Separator] = new Vector4(0.25f, 0.28f, 0.35f, 0.8f);
        colors[(int)ImGuiCol.SeparatorHovered] = new Vector4(0.98f, 0.61f, 0.08f, 0.8f);
        colors[(int)ImGuiCol.SeparatorActive] = new Vector4(0.98f, 0.61f, 0.08f, 1.0f);

        // Resize grip
        colors[(int)ImGuiCol.ResizeGrip] = new Vector4(0.98f, 0.61f, 0.08f, 0.2f);
        colors[(int)ImGuiCol.ResizeGripHovered] = new Vector4(0.98f, 0.61f, 0.08f, 0.5f);
        colors[(int)ImGuiCol.ResizeGripActive] = new Vector4(0.98f, 0.61f, 0.08f, 0.9f);

        // Tabs
        colors[(int)ImGuiCol.Tab] = new Vector4(0.12f, 0.14f, 0.18f, 1.0f);
        colors[(int)ImGuiCol.TabHovered] = new Vector4(0.98f, 0.61f, 0.08f, 0.8f);
        colors[(int)ImGuiCol.Tab] = new Vector4(0.98f, 0.61f, 0.08f, 1.0f);
        colors[(int)ImGuiCol.TabSelected] = new Vector4(0.1f, 0.12f, 0.15f, 1.0f);
        colors[(int)ImGuiCol.TabSelectedOverline] = new Vector4(0.15f, 0.17f, 0.22f, 1.0f);

        // Text
        colors[(int)ImGuiCol.Text] = new Vector4(0.92f, 0.94f, 0.98f, 1.0f);
        colors[(int)ImGuiCol.TextDisabled] = new Vector4(0.55f, 0.58f, 0.65f, 1.0f);
        colors[(int)ImGuiCol.TextSelectedBg] = new Vector4(0.98f, 0.61f, 0.08f, 0.35f);

        // Plots
        colors[(int)ImGuiCol.PlotLines] = new Vector4(0.98f, 0.61f, 0.08f, 1.0f);
        colors[(int)ImGuiCol.PlotLinesHovered] = new Vector4(1.0f, 0.7f, 0.2f, 1.0f);
        colors[(int)ImGuiCol.PlotHistogram] = new Vector4(0.98f, 0.61f, 0.08f, 1.0f);
        colors[(int)ImGuiCol.PlotHistogramHovered] = new Vector4(1.0f, 0.7f, 0.2f, 1.0f);

        // Nav
        colors[(int)ImGuiCol.NavWindowingHighlight] = new Vector4(0.98f, 0.61f, 0.08f, 1.0f);

        // Drag drop
        colors[(int)ImGuiCol.DragDropTarget] = new Vector4(0.98f, 0.61f, 0.08f, 0.9f);
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        Console.WriteLine("[Application] Disposing resources...");

        _uiManager?.Dispose();
        _gameManager?.Dispose();
        _imGuiController?.Dispose();
        _renderer?.Dispose();

        // Clean up input handlers
        if (_input != null)
        {
            foreach (var keyboard in _input.Keyboards)
            {
                keyboard.KeyDown -= OnKeyDown;
                keyboard.KeyUp -= OnKeyUp;
                keyboard.KeyChar -= OnKeyChar;
            }
            foreach (var mouse in _input.Mice)
            {
                mouse.MouseDown -= OnMouseDown;
                mouse.MouseUp -= OnMouseUp;
                mouse.MouseMove -= OnMouseMove;
                mouse.Scroll -= OnScroll;
            }
            _input.Dispose();
        }

        try
        {
            _window?.Dispose();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Application] Note: Window dispose error suppressed: {ex.Message}");
        }

        Console.WriteLine("[Application] Disposed successfully");
    }
}
