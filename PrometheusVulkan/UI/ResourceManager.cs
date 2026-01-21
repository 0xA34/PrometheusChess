using ChessCore.Models;
using ImGuiNET;
using Silk.NET.Vulkan;
using PrometheusVulkan.Graphics;

namespace PrometheusVulkan.UI;

public sealed class ResourceManager : IDisposable
{
    private readonly ImGuiController _imGuiController;
    private readonly Dictionary<PieceType, IntPtr> _whitePieceTextures = new();
    private readonly Dictionary<PieceType, IntPtr> _blackPieceTextures = new();
    private bool _isDisposed;

    public ResourceManager(ImGuiController imGuiController)
    {
        _imGuiController = imGuiController ?? throw new ArgumentNullException(nameof(imGuiController));
    }

    public void LoadTextures()
    {
        Console.WriteLine("[ResourceManager] Loading textures...");
        
        string basePath = AppDomain.CurrentDomain.BaseDirectory;
        string texturePath = Path.Combine(basePath, "Assets", "Textures");

        if (!Directory.Exists(texturePath))
        {
            Console.WriteLine("[ResourceManager] Texture directory not found.");
            return;
        }

        foreach (PieceType type in Enum.GetValues(typeof(PieceType)))
        {
            string pieceName = type.ToString().ToLower();
            
            // White
            LoadTexture(Path.Combine(texturePath, $"white-{pieceName}.png"), type, _whitePieceTextures);
            // Black
            LoadTexture(Path.Combine(texturePath, $"black-{pieceName}.png"), type, _blackPieceTextures);
        }
    }

    private void LoadTexture(string path, PieceType type, Dictionary<PieceType, IntPtr> cache)
    {
        if (File.Exists(path))
        {
            try
            {
                var id = _imGuiController.LoadTexture(path);
                cache[type] = id;
            }
            catch (Exception e)
            {
                Console.WriteLine($"[ResourceManager] Failed to load {path}: {e.Message}");
            }
        }
    }

    public IntPtr GetPieceTexture(PieceType type, PieceColor color)
    {
        var cache = color == PieceColor.White ? _whitePieceTextures : _blackPieceTextures;
        return cache.TryGetValue(type, out var textureId) ? textureId : IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        // Note: Actual texture memory cleanup should ideally happen in ImGuiController/VulkanRenderer
        // But ImGuiController handles the Vulkan descriptor cleanup mostly globally or doesn't expose individual cleanup
        _whitePieceTextures.Clear();
        _blackPieceTextures.Clear();
        Console.WriteLine("[ResourceManager] Disposed");
    }
}
