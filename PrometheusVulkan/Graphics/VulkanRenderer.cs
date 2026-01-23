using Silk.NET.Core;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Vulkan.Extensions.KHR;
using Silk.NET.Windowing;
using System.Runtime.InteropServices;
using Buffer = Silk.NET.Vulkan.Buffer;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace PrometheusVulkan.Graphics;
/// <summary>
/// Thank you Gladiator Engine for making this possible.
/// However, I don't like to write an engine in C#
/// This is just for fun, this is not gonna scaleable
/// Also note that this is just 2D, 3D will be harder.
/// </summary>

public sealed unsafe class VulkanRenderer : IDisposable
{
    private const int MaxFramesInFlight = 2;

    private readonly IWindow _window;
    // By default this is false.
    // You only need validation layer if you want to debug the client for specific vulkan stuff.
    // However, only enable if you really need them, depending on the GPU, validation layers could give different results.
    private readonly bool _enableValidation;

    // Vulkan API
    private Vk? _vk;
    private Instance _instance;
    private SurfaceKHR _surface;
    private PhysicalDevice _physicalDevice;
    private Device _device;

    // Extensions
    private KhrSurface? _khrSurface;
    private KhrSwapchain? _khrSwapchain;
    private ExtDebugUtils? _debugUtils;
    private DebugUtilsMessengerEXT _debugMessenger;

    // Queue handles
    private Queue _graphicsQueue;
    private Queue _presentQueue;
    private uint _graphicsQueueFamily;
    private uint _presentQueueFamily;

    // Swap chain
    private SwapchainKHR _swapchain;
    private Image[] _swapchainImages = Array.Empty<Image>();
    private ImageView[] _swapchainImageViews = Array.Empty<ImageView>();
    private Format _swapchainImageFormat;
    private Extent2D _swapchainExtent;

    // Render pass and framebuffers
    private RenderPass _renderPass;
    private Framebuffer[] _framebuffers = Array.Empty<Framebuffer>();

    // Command buffers
    private CommandPool _commandPool;
    private CommandBuffer[] _commandBuffers = Array.Empty<CommandBuffer>();

    // Synchronization
    private Semaphore[] _imageAvailableSemaphores = Array.Empty<Semaphore>();
    private Semaphore[] _renderFinishedSemaphores = Array.Empty<Semaphore>();
    private Fence[] _inFlightFences = Array.Empty<Fence>();
    private Fence[] _imagesInFlight = Array.Empty<Fence>();

    // Frame state
    private int _currentFrame;
    private uint _currentImageIndex;
    private bool _framebufferResized;
    private bool _isInitialized;
    private bool _isDisposed;

    // VSync state (separate from window, for runtime changes)
    private bool _vsyncEnabled;

    // Properties
    public Vk Vk => _vk ?? throw new InvalidOperationException("Vulkan not initialised");
    public Device Device => _device;
    public PhysicalDevice PhysicalDevice => _physicalDevice;
    public Instance Instance => _instance;
    public Queue GraphicsQueue => _graphicsQueue;
    public uint GraphicsQueueFamily => _graphicsQueueFamily;
    public CommandPool CommandPool => _commandPool;
    public RenderPass RenderPass => _renderPass;
    public Extent2D SwapchainExtent => _swapchainExtent;
    public Format SwapchainImageFormat => _swapchainImageFormat;
    public int CurrentFrame => _currentFrame;
    public uint CurrentImageIndex => _currentImageIndex;
    public int SwapchainImageCount => _swapchainImages.Length;
    public string? DeviceName { get; private set; }
    public bool VSyncEnabled
    {
        get => _vsyncEnabled;
        set
        {
            if (_vsyncEnabled != value)
            {
                _vsyncEnabled = value;
                _framebufferResized = true;
            }
        }
    }

    public VulkanRenderer(IWindow window, bool enableValidation = false)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _enableValidation = enableValidation;
        _vsyncEnabled = window.VSync;
    }

    public void Initialize()
    {
        Console.WriteLine("[VulkanRenderer] Initialising Vulkan...");

        _vk = Vk.GetApi();

        CreateInstance();
        SetupDebugMessenger();
        CreateSurface();
        PickPhysicalDevice();
        CreateLogicalDevice();
        CreateSwapChain();
        CreateImageViews();
        CreateRenderPass();
        CreateFramebuffers();
        CreateCommandPool();
        CreateCommandBuffers();
        CreateSyncObjects();

        _isInitialized = true;
        Console.WriteLine("[VulkanRenderer] Vulkan initialised successfully!");
    }

    #region Instance and Device Creation

    private void CreateInstance()
    {
        Console.WriteLine("[VulkanRenderer] Creating Vulkan instance...");

        var appInfo = new ApplicationInfo
        {
            SType = StructureType.ApplicationInfo,
            PApplicationName = (byte*)Marshal.StringToHGlobalAnsi("Prometheus Client"),
            ApplicationVersion = new Version32(1, 0, 0),
            PEngineName = (byte*)Marshal.StringToHGlobalAnsi("Prometheus Engine"),
            EngineVersion = new Version32(1, 0, 0),
            ApiVersion = Vk.Version12
        };

        // Get required extensions
        var extensions = GetRequiredExtensions();
        var extensionPtrs = new nint[extensions.Length];
        for (int i = 0; i < extensions.Length; i++)
        {
            extensionPtrs[i] = Marshal.StringToHGlobalAnsi(extensions[i]);
        }

        // Validation layers
        string[] validationLayers = _enableValidation
            ? new[] { "VK_LAYER_KHRONOS_validation" }
            : Array.Empty<string>();

        var layerPtrs = new nint[validationLayers.Length];
        for (int i = 0; i < validationLayers.Length; i++)
        {
            layerPtrs[i] = Marshal.StringToHGlobalAnsi(validationLayers[i]);
        }

        fixed (nint* extensionsPtr = extensionPtrs)
        fixed (nint* layersPtr = layerPtrs)
        {
            var createInfo = new InstanceCreateInfo
            {
                SType = StructureType.InstanceCreateInfo,
                PApplicationInfo = &appInfo,
                EnabledExtensionCount = (uint)extensions.Length,
                PpEnabledExtensionNames = (byte**)extensionsPtr,
                EnabledLayerCount = (uint)validationLayers.Length,
                PpEnabledLayerNames = (byte**)layersPtr
            };

            if (_vk!.CreateInstance(&createInfo, null, out _instance) != Result.Success)
            {
                throw new Exception("Failed to create Vulkan instance");
            }
        }

        // Free allocated strings
        Marshal.FreeHGlobal((nint)appInfo.PApplicationName);
        Marshal.FreeHGlobal((nint)appInfo.PEngineName);
        foreach (var ptr in extensionPtrs) Marshal.FreeHGlobal(ptr);
        foreach (var ptr in layerPtrs) Marshal.FreeHGlobal(ptr);

        Console.WriteLine("[VulkanRenderer] Instance created");
    }

    private string[] GetRequiredExtensions()
    {
        var extensions = new List<string>();

        // Get window surface extensions
        if (_window.VkSurface != null)
        {
            var windowExtensions = _window.VkSurface.GetRequiredExtensions(out var count);
            for (int i = 0; i < (int)count; i++)
            {
                extensions.Add(Marshal.PtrToStringAnsi((nint)windowExtensions[i]) ?? "");
            }
        }

        if (_enableValidation)
        {
            extensions.Add(ExtDebugUtils.ExtensionName);
        }

        return extensions.ToArray();
    }

    private void SetupDebugMessenger()
    {
        if (!_enableValidation) return;

        Console.WriteLine("[VulkanRenderer] Setting up debug messenger...");

        if (!_vk!.TryGetInstanceExtension(_instance, out _debugUtils))
        {
            Console.WriteLine("[VulkanRenderer] Debug utils extension not available");
            return;
        }

        var createInfo = new DebugUtilsMessengerCreateInfoEXT
        {
            SType = StructureType.DebugUtilsMessengerCreateInfoExt,
            MessageSeverity = DebugUtilsMessageSeverityFlagsEXT.VerboseBitExt |
                             DebugUtilsMessageSeverityFlagsEXT.WarningBitExt |
                             DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt,
            MessageType = DebugUtilsMessageTypeFlagsEXT.GeneralBitExt |
                         DebugUtilsMessageTypeFlagsEXT.ValidationBitExt |
                         DebugUtilsMessageTypeFlagsEXT.PerformanceBitExt,
            PfnUserCallback = (PfnDebugUtilsMessengerCallbackEXT)DebugCallback
        };

        if (_debugUtils.CreateDebugUtilsMessenger(_instance, &createInfo, null, out _debugMessenger) != Result.Success)
        {
            Console.WriteLine("[VulkanRenderer] Failed to set up debug messenger");
        }
    }

    private static uint DebugCallback(
        DebugUtilsMessageSeverityFlagsEXT messageSeverity,
        DebugUtilsMessageTypeFlagsEXT messageTypes,
        DebugUtilsMessengerCallbackDataEXT* pCallbackData,
        void* pUserData)
    {
        var message = Marshal.PtrToStringAnsi((nint)pCallbackData->PMessage);
        var severity = messageSeverity switch
        {
            DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt => "ERROR",
            DebugUtilsMessageSeverityFlagsEXT.WarningBitExt => "WARNING",
            DebugUtilsMessageSeverityFlagsEXT.InfoBitExt => "INFO",
            _ => "VERBOSE"
        };

        Console.WriteLine($"[Vulkan {severity}] {message}");
        return Vk.False;
    }

    private void CreateSurface()
    {
        Console.WriteLine("[VulkanRenderer] Creating surface...");

        if (_window.VkSurface == null)
        {
            throw new Exception("Window does not support Vulkan surface");
        }

        _surface = _window.VkSurface.Create<AllocationCallbacks>(_instance.ToHandle(), null).ToSurface();

        if (!_vk!.TryGetInstanceExtension(_instance, out _khrSurface))
        {
            throw new Exception("Failed to get KHR_surface extension");
        }

        Console.WriteLine("[VulkanRenderer] Surface created");
    }

    private void PickPhysicalDevice()
    {
        Console.WriteLine("[VulkanRenderer] Picking physical device...");

        uint deviceCount = 0;
        _vk!.EnumeratePhysicalDevices(_instance, &deviceCount, null);

        if (deviceCount == 0)
        {
            throw new Exception("No GPUs with Vulkan support found");
        }

        var devices = new PhysicalDevice[deviceCount];
        fixed (PhysicalDevice* devicesPtr = devices)
        {
            _vk.EnumeratePhysicalDevices(_instance, &deviceCount, devicesPtr);
        }

        // Pick the first suitable device (prefer discrete GPU)
        PhysicalDevice? selectedDevice = null;
        int bestScore = -1;

        foreach (var device in devices)
        {
            int score = RateDevice(device);
            if (score > bestScore && IsDeviceSuitable(device))
            {
                selectedDevice = device;
                bestScore = score;
            }
        }

        if (selectedDevice == null)
        {
            throw new Exception("Failed to find a suitable GPU");
        }

        _physicalDevice = selectedDevice.Value;

        // Log device info
        _vk.GetPhysicalDeviceProperties(_physicalDevice, out var properties);
        var deviceName = Marshal.PtrToStringAnsi((nint)properties.DeviceName);
        DeviceName = deviceName;
        Console.WriteLine($"[VulkanRenderer] Selected GPU: {deviceName}");
    }

    private int RateDevice(PhysicalDevice device)
    {
        _vk!.GetPhysicalDeviceProperties(device, out var properties);
        _vk.GetPhysicalDeviceFeatures(device, out var features);

        int score = 0;

        // Prefer discrete GPUs
        if (properties.DeviceType == PhysicalDeviceType.DiscreteGpu)
            score += 1000;

        // Max texture size
        score += (int)properties.Limits.MaxImageDimension2D;

        return score;
    }

    private bool IsDeviceSuitable(PhysicalDevice device)
    {
        var indices = FindQueueFamilies(device);
        if (!indices.IsComplete)
            return false;

        if (!CheckDeviceExtensionSupport(device))
            return false;

        var swapChainSupport = QuerySwapChainSupport(device);
        if (swapChainSupport.Formats.Length == 0 || swapChainSupport.PresentModes.Length == 0)
            return false;

        return true;
    }

    private QueueFamilyIndices FindQueueFamilies(PhysicalDevice device)
    {
        var indices = new QueueFamilyIndices();

        uint queueFamilyCount = 0;
        _vk!.GetPhysicalDeviceQueueFamilyProperties(device, &queueFamilyCount, null);

        var queueFamilies = new QueueFamilyProperties[queueFamilyCount];
        fixed (QueueFamilyProperties* queueFamiliesPtr = queueFamilies)
        {
            _vk.GetPhysicalDeviceQueueFamilyProperties(device, &queueFamilyCount, queueFamiliesPtr);
        }

        for (uint i = 0; i < queueFamilies.Length; i++)
        {
            if (queueFamilies[i].QueueFlags.HasFlag(QueueFlags.GraphicsBit))
            {
                indices.GraphicsFamily = i;
            }

            _khrSurface!.GetPhysicalDeviceSurfaceSupport(device, i, _surface, out var presentSupport);
            if (presentSupport)
            {
                indices.PresentFamily = i;
            }

            if (indices.IsComplete)
                break;
        }

        return indices;
    }

    private bool CheckDeviceExtensionSupport(PhysicalDevice device)
    {
        uint extensionCount = 0;
        _vk!.EnumerateDeviceExtensionProperties(device, (byte*)null, &extensionCount, null);

        var availableExtensions = new ExtensionProperties[extensionCount];
        fixed (ExtensionProperties* availableExtensionsPtr = availableExtensions)
        {
            _vk.EnumerateDeviceExtensionProperties(device, (byte*)null, &extensionCount, availableExtensionsPtr);
        }

        var requiredExtensions = new HashSet<string> { KhrSwapchain.ExtensionName };

        foreach (var extension in availableExtensions)
        {
            var name = Marshal.PtrToStringAnsi((nint)extension.ExtensionName);
            requiredExtensions.Remove(name ?? "");
        }

        return requiredExtensions.Count == 0;
    }

    private void CreateLogicalDevice()
    {
        Console.WriteLine("[VulkanRenderer] Creating logical device...");

        var indices = FindQueueFamilies(_physicalDevice);
        _graphicsQueueFamily = indices.GraphicsFamily!.Value;
        _presentQueueFamily = indices.PresentFamily!.Value;

        var uniqueQueueFamilies = new HashSet<uint> { _graphicsQueueFamily, _presentQueueFamily };
        var queueCreateInfos = new DeviceQueueCreateInfo[uniqueQueueFamilies.Count];

        float queuePriority = 1.0f;
        int index = 0;
        foreach (var queueFamily in uniqueQueueFamilies)
        {
            queueCreateInfos[index++] = new DeviceQueueCreateInfo
            {
                SType = StructureType.DeviceQueueCreateInfo,
                QueueFamilyIndex = queueFamily,
                QueueCount = 1,
                PQueuePriorities = &queuePriority
            };
        }

        var deviceFeatures = new PhysicalDeviceFeatures();

        var extensionName = Marshal.StringToHGlobalAnsi(KhrSwapchain.ExtensionName);

        fixed (DeviceQueueCreateInfo* queueCreateInfosPtr = queueCreateInfos)
        {
            var createInfo = new DeviceCreateInfo
            {
                SType = StructureType.DeviceCreateInfo,
                QueueCreateInfoCount = (uint)queueCreateInfos.Length,
                PQueueCreateInfos = queueCreateInfosPtr,
                PEnabledFeatures = &deviceFeatures,
                EnabledExtensionCount = 1,
                PpEnabledExtensionNames = (byte**)&extensionName
            };

            if (_vk!.CreateDevice(_physicalDevice, &createInfo, null, out _device) != Result.Success)
            {
                throw new Exception("Failed to create logical device");
            }
        }

        Marshal.FreeHGlobal(extensionName);

        _vk.GetDeviceQueue(_device, _graphicsQueueFamily, 0, out _graphicsQueue);
        _vk.GetDeviceQueue(_device, _presentQueueFamily, 0, out _presentQueue);

        if (!_vk.TryGetDeviceExtension(_instance, _device, out _khrSwapchain))
        {
            throw new Exception("Failed to get KHR_swapchain extension");
        }

        Console.WriteLine("[VulkanRenderer] Logical device created");
    }

    #endregion

    #region Swap Chain

    private SwapChainSupportDetails QuerySwapChainSupport(PhysicalDevice device)
    {
        var details = new SwapChainSupportDetails();

        _khrSurface!.GetPhysicalDeviceSurfaceCapabilities(device, _surface, out details.Capabilities);

        uint formatCount = 0;
        _khrSurface.GetPhysicalDeviceSurfaceFormats(device, _surface, &formatCount, null);
        if (formatCount > 0)
        {
            details.Formats = new SurfaceFormatKHR[formatCount];
            fixed (SurfaceFormatKHR* formatsPtr = details.Formats)
            {
                _khrSurface.GetPhysicalDeviceSurfaceFormats(device, _surface, &formatCount, formatsPtr);
            }
        }

        uint presentModeCount = 0;
        _khrSurface.GetPhysicalDeviceSurfacePresentModes(device, _surface, &presentModeCount, null);
        if (presentModeCount > 0)
        {
            details.PresentModes = new PresentModeKHR[presentModeCount];
            fixed (PresentModeKHR* presentModesPtr = details.PresentModes)
            {
                _khrSurface.GetPhysicalDeviceSurfacePresentModes(device, _surface, &presentModeCount, presentModesPtr);
            }
        }

        return details;
    }

    private void CreateSwapChain()
    {
        Console.WriteLine("[VulkanRenderer] Creating swap chain...");

        var swapChainSupport = QuerySwapChainSupport(_physicalDevice);

        var surfaceFormat = ChooseSwapSurfaceFormat(swapChainSupport.Formats);
        var presentMode = ChooseSwapPresentMode(swapChainSupport.PresentModes);
        var extent = ChooseSwapExtent(swapChainSupport.Capabilities);

        uint imageCount = swapChainSupport.Capabilities.MinImageCount + 1;
        if (swapChainSupport.Capabilities.MaxImageCount > 0 &&
            imageCount > swapChainSupport.Capabilities.MaxImageCount)
        {
            imageCount = swapChainSupport.Capabilities.MaxImageCount;
        }

        var createInfo = new SwapchainCreateInfoKHR
        {
            SType = StructureType.SwapchainCreateInfoKhr,
            Surface = _surface,
            MinImageCount = imageCount,
            ImageFormat = surfaceFormat.Format,
            ImageColorSpace = surfaceFormat.ColorSpace,
            ImageExtent = extent,
            ImageArrayLayers = 1,
            ImageUsage = ImageUsageFlags.ColorAttachmentBit,
            PreTransform = swapChainSupport.Capabilities.CurrentTransform,
            CompositeAlpha = CompositeAlphaFlagsKHR.OpaqueBitKhr,
            PresentMode = presentMode,
            Clipped = true,
            OldSwapchain = default
        };

        var indices = FindQueueFamilies(_physicalDevice);
        var queueFamilyIndices = stackalloc uint[] { indices.GraphicsFamily!.Value, indices.PresentFamily!.Value };

        if (indices.GraphicsFamily != indices.PresentFamily)
        {
            createInfo.ImageSharingMode = SharingMode.Concurrent;
            createInfo.QueueFamilyIndexCount = 2;
            createInfo.PQueueFamilyIndices = queueFamilyIndices;
        }
        else
        {
            createInfo.ImageSharingMode = SharingMode.Exclusive;
        }

        if (_khrSwapchain!.CreateSwapchain(_device, &createInfo, null, out _swapchain) != Result.Success)
        {
            throw new Exception("Failed to create swap chain");
        }

        _khrSwapchain.GetSwapchainImages(_device, _swapchain, &imageCount, null);
        _swapchainImages = new Image[imageCount];
        fixed (Image* swapchainImagesPtr = _swapchainImages)
        {
            _khrSwapchain.GetSwapchainImages(_device, _swapchain, &imageCount, swapchainImagesPtr);
        }

        _swapchainImageFormat = surfaceFormat.Format;
        _swapchainExtent = extent;

        Console.WriteLine($"[VulkanRenderer] Swap chain created ({_swapchainExtent.Width}x{_swapchainExtent.Height}, {_swapchainImages.Length} images)");
    }

    private SurfaceFormatKHR ChooseSwapSurfaceFormat(SurfaceFormatKHR[] availableFormats)
    {
        foreach (var format in availableFormats)
        {
            if (format.Format == Format.B8G8R8A8Srgb &&
                format.ColorSpace == ColorSpaceKHR.SpaceSrgbNonlinearKhr)
            {
                return format;
            }
        }
        return availableFormats[0];
    }

    private PresentModeKHR ChooseSwapPresentMode(PresentModeKHR[] availablePresentModes)
    {
        // If VSync is enabled, we must use FIFO (guaranteed to be available)
        if (_vsyncEnabled)
        {
            return PresentModeKHR.FifoKhr;
        }

        // If VSync is disabled, prefer Mailbox (Triple Buffering, uncapped)
        foreach (var mode in availablePresentModes)
        {
            if (mode == PresentModeKHR.MailboxKhr)
                return mode;
        }

        // Fallback to Immediate (Uncapped, tearing possible)
        foreach (var mode in availablePresentModes)
        {
            if (mode == PresentModeKHR.ImmediateKhr)
                return mode;
        }

        return PresentModeKHR.FifoKhr;
    }

    private Extent2D ChooseSwapExtent(SurfaceCapabilitiesKHR capabilities)
    {
        if (capabilities.CurrentExtent.Width != uint.MaxValue)
        {
            return capabilities.CurrentExtent;
        }

        var framebufferSize = _window.FramebufferSize;
        return new Extent2D
        {
            Width = Math.Clamp((uint)framebufferSize.X, capabilities.MinImageExtent.Width, capabilities.MaxImageExtent.Width),
            Height = Math.Clamp((uint)framebufferSize.Y, capabilities.MinImageExtent.Height, capabilities.MaxImageExtent.Height)
        };
    }

    private void CreateImageViews()
    {
        Console.WriteLine("[VulkanRenderer] Creating image views...");

        _swapchainImageViews = new ImageView[_swapchainImages.Length];

        for (int i = 0; i < _swapchainImages.Length; i++)
        {
            var createInfo = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = _swapchainImages[i],
                ViewType = ImageViewType.Type2D,
                Format = _swapchainImageFormat,
                Components = new ComponentMapping
                {
                    R = ComponentSwizzle.Identity,
                    G = ComponentSwizzle.Identity,
                    B = ComponentSwizzle.Identity,
                    A = ComponentSwizzle.Identity
                },
                SubresourceRange = new ImageSubresourceRange
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    BaseMipLevel = 0,
                    LevelCount = 1,
                    BaseArrayLayer = 0,
                    LayerCount = 1
                }
            };

            if (_vk!.CreateImageView(_device, &createInfo, null, out _swapchainImageViews[i]) != Result.Success)
            {
                throw new Exception($"Failed to create image view {i}");
            }
        }

        Console.WriteLine($"[VulkanRenderer] Created {_swapchainImageViews.Length} image views");
    }

    #endregion

    #region Render Pass and Framebuffers

    private void CreateRenderPass()
    {
        Console.WriteLine("[VulkanRenderer] Creating render pass...");

        var colorAttachment = new AttachmentDescription
        {
            Format = _swapchainImageFormat,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.Undefined,
            FinalLayout = ImageLayout.PresentSrcKhr
        };

        var colorAttachmentRef = new AttachmentReference
        {
            Attachment = 0,
            Layout = ImageLayout.ColorAttachmentOptimal
        };

        var subpass = new SubpassDescription
        {
            PipelineBindPoint = PipelineBindPoint.Graphics,
            ColorAttachmentCount = 1,
            PColorAttachments = &colorAttachmentRef
        };

        var dependency = new SubpassDependency
        {
            SrcSubpass = Vk.SubpassExternal,
            DstSubpass = 0,
            SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
            SrcAccessMask = 0,
            DstStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
            DstAccessMask = AccessFlags.ColorAttachmentWriteBit
        };

        var renderPassInfo = new RenderPassCreateInfo
        {
            SType = StructureType.RenderPassCreateInfo,
            AttachmentCount = 1,
            PAttachments = &colorAttachment,
            SubpassCount = 1,
            PSubpasses = &subpass,
            DependencyCount = 1,
            PDependencies = &dependency
        };

        if (_vk!.CreateRenderPass(_device, &renderPassInfo, null, out _renderPass) != Result.Success)
        {
            throw new Exception("Failed to create render pass");
        }

        Console.WriteLine("[VulkanRenderer] Render pass created");
    }

    private void CreateFramebuffers()
    {
        Console.WriteLine("[VulkanRenderer] Creating framebuffers...");

        _framebuffers = new Framebuffer[_swapchainImageViews.Length];

        for (int i = 0; i < _swapchainImageViews.Length; i++)
        {
            var attachment = _swapchainImageViews[i];

            var framebufferInfo = new FramebufferCreateInfo
            {
                SType = StructureType.FramebufferCreateInfo,
                RenderPass = _renderPass,
                AttachmentCount = 1,
                PAttachments = &attachment,
                Width = _swapchainExtent.Width,
                Height = _swapchainExtent.Height,
                Layers = 1
            };

            if (_vk!.CreateFramebuffer(_device, &framebufferInfo, null, out _framebuffers[i]) != Result.Success)
            {
                throw new Exception($"Failed to create framebuffer {i}");
            }
        }

        Console.WriteLine($"[VulkanRenderer] Created {_framebuffers.Length} framebuffers");
    }

    #endregion

    #region Command Buffers

    private void CreateCommandPool()
    {
        Console.WriteLine("[VulkanRenderer] Creating command pool...");

        var poolInfo = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo,
            QueueFamilyIndex = _graphicsQueueFamily,
            Flags = CommandPoolCreateFlags.ResetCommandBufferBit
        };

        if (_vk!.CreateCommandPool(_device, &poolInfo, null, out _commandPool) != Result.Success)
        {
            throw new Exception("Failed to create command pool");
        }

        Console.WriteLine("[VulkanRenderer] Command pool created");
    }

    private void CreateCommandBuffers()
    {
        Console.WriteLine("[VulkanRenderer] Creating command buffers...");

        _commandBuffers = new CommandBuffer[MaxFramesInFlight];

        var allocInfo = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = _commandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = (uint)_commandBuffers.Length
        };

        fixed (CommandBuffer* commandBuffersPtr = _commandBuffers)
        {
            if (_vk!.AllocateCommandBuffers(_device, &allocInfo, commandBuffersPtr) != Result.Success)
            {
                throw new Exception("Failed to allocate command buffers");
            }
        }

        Console.WriteLine($"[VulkanRenderer] Created {_commandBuffers.Length} command buffers");
    }

    #endregion

    #region Synchronization

    private void CreateSyncObjects()
    {
        Console.WriteLine("[VulkanRenderer] Creating synchronization objects...");

        _imageAvailableSemaphores = new Semaphore[MaxFramesInFlight];
        _renderFinishedSemaphores = new Semaphore[MaxFramesInFlight];
        _inFlightFences = new Fence[MaxFramesInFlight];
        _imagesInFlight = new Fence[_swapchainImages.Length];

        var semaphoreInfo = new SemaphoreCreateInfo
        {
            SType = StructureType.SemaphoreCreateInfo
        };

        var fenceInfo = new FenceCreateInfo
        {
            SType = StructureType.FenceCreateInfo,
            Flags = FenceCreateFlags.SignaledBit
        };

        for (int i = 0; i < MaxFramesInFlight; i++)
        {
            if (_vk!.CreateSemaphore(_device, &semaphoreInfo, null, out _imageAvailableSemaphores[i]) != Result.Success ||
                _vk.CreateSemaphore(_device, &semaphoreInfo, null, out _renderFinishedSemaphores[i]) != Result.Success ||
                _vk.CreateFence(_device, &fenceInfo, null, out _inFlightFences[i]) != Result.Success)
            {
                throw new Exception("Failed to create synchronization objects");
            }
        }

        Console.WriteLine("[VulkanRenderer] Synchronization objects created");
    }

    #endregion

    #region Frame Rendering

    public bool BeginFrame()
    {
        if (!_isInitialized) return false;

        // Wait for previous frame
        _vk!.WaitForFences(_device, 1, _inFlightFences[_currentFrame], true, ulong.MaxValue);

        // Acquire next image
        var result = _khrSwapchain!.AcquireNextImage(
            _device,
            _swapchain,
            ulong.MaxValue,
            _imageAvailableSemaphores[_currentFrame],
            default,
            ref _currentImageIndex);

        if (result == Result.ErrorOutOfDateKhr)
        {
            RecreateSwapChain();
            return false;
        }
        else if (result != Result.Success && result != Result.SuboptimalKhr)
        {
            throw new Exception("Failed to acquire swap chain image");
        }

        // Check if a previous frame is using this image
        if (_imagesInFlight[_currentImageIndex].Handle != 0)
        {
            _vk.WaitForFences(_device, 1, _imagesInFlight[_currentImageIndex], true, ulong.MaxValue);
        }
        _imagesInFlight[_currentImageIndex] = _inFlightFences[_currentFrame];

        // Reset fence
        _vk.ResetFences(_device, 1, _inFlightFences[_currentFrame]);

        // Begin command buffer
        var commandBuffer = _commandBuffers[_currentFrame];
        _vk.ResetCommandBuffer(commandBuffer, 0);

        var beginInfo = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit
        };

        if (_vk.BeginCommandBuffer(commandBuffer, &beginInfo) != Result.Success)
        {
            throw new Exception("Failed to begin recording command buffer");
        }

        // Begin render pass
        var clearColor = new ClearValue
        {
            Color = new ClearColorValue { Float32_0 = 0.05f, Float32_1 = 0.06f, Float32_2 = 0.08f, Float32_3 = 1.0f }
        };

        var renderPassInfo = new RenderPassBeginInfo
        {
            SType = StructureType.RenderPassBeginInfo,
            RenderPass = _renderPass,
            Framebuffer = _framebuffers[_currentImageIndex],
            RenderArea = new Rect2D { Offset = new Offset2D(0, 0), Extent = _swapchainExtent },
            ClearValueCount = 1,
            PClearValues = &clearColor
        };

        _vk.CmdBeginRenderPass(commandBuffer, &renderPassInfo, SubpassContents.Inline);

        return true;
    }

    public void EndFrame()
    {
        if (!_isInitialized) return;

        var commandBuffer = _commandBuffers[_currentFrame];

        // End render pass
        _vk!.CmdEndRenderPass(commandBuffer);

        // End command buffer
        if (_vk.EndCommandBuffer(commandBuffer) != Result.Success)
        {
            throw new Exception("Failed to record command buffer");
        }

        // Submit command buffer
        var waitSemaphore = _imageAvailableSemaphores[_currentFrame];
        var waitStage = PipelineStageFlags.ColorAttachmentOutputBit;
        var signalSemaphore = _renderFinishedSemaphores[_currentFrame];

        var submitInfo = new SubmitInfo
        {
            SType = StructureType.SubmitInfo,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = &waitSemaphore,
            PWaitDstStageMask = &waitStage,
            CommandBufferCount = 1,
            PCommandBuffers = &commandBuffer,
            SignalSemaphoreCount = 1,
            PSignalSemaphores = &signalSemaphore
        };

        if (_vk.QueueSubmit(_graphicsQueue, 1, &submitInfo, _inFlightFences[_currentFrame]) != Result.Success)
        {
            throw new Exception("Failed to submit draw command buffer");
        }

        // Present
        var swapchain = _swapchain;
        var imageIndex = _currentImageIndex;

        var presentInfo = new PresentInfoKHR
        {
            SType = StructureType.PresentInfoKhr,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = &signalSemaphore,
            SwapchainCount = 1,
            PSwapchains = &swapchain,
            PImageIndices = &imageIndex
        };

        var result = _khrSwapchain!.QueuePresent(_presentQueue, &presentInfo);

        if (result == Result.ErrorOutOfDateKhr || result == Result.SuboptimalKhr || _framebufferResized)
        {
            _framebufferResized = false;
            RecreateSwapChain();
        }
        else if (result != Result.Success)
        {
            throw new Exception("Failed to present swap chain image");
        }

        _currentFrame = (_currentFrame + 1) % MaxFramesInFlight;
    }

    public CommandBuffer GetCurrentCommandBuffer()
    {
        return _commandBuffers[_currentFrame];
    }

    #endregion

    #region Resize and Recreation

    public void OnResize(int width, int height)
    {
        _framebufferResized = true;
    }

    private void RecreateSwapChain()
    {
        // Handle minimization
        var size = _window.FramebufferSize;
        while (size.X == 0 || size.Y == 0)
        {
            size = _window.FramebufferSize;
            _window.DoEvents();
        }

        _vk!.DeviceWaitIdle(_device);

        CleanupSwapChain();

        CreateSwapChain();
        CreateImageViews();
        CreateFramebuffers();
    }

    private void CleanupSwapChain()
    {
        foreach (var framebuffer in _framebuffers)
        {
            _vk!.DestroyFramebuffer(_device, framebuffer, null);
        }

        foreach (var imageView in _swapchainImageViews)
        {
            _vk!.DestroyImageView(_device, imageView, null);
        }

        _khrSwapchain!.DestroySwapchain(_device, _swapchain, null);
    }

    #endregion

    #region Public Helpers - Texture & Buffer Support

    public void CreateBuffer(ulong size, BufferUsageFlags usage, MemoryPropertyFlags properties,
        out Buffer buffer, out DeviceMemory memory)
    {
        var bufferInfo = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = size,
            Usage = usage,
            SharingMode = SharingMode.Exclusive
        };

        fixed (Buffer* bufferPtr = &buffer)
        {
            if (_vk!.CreateBuffer(_device, &bufferInfo, null, bufferPtr) != Result.Success)
            {
                throw new Exception("Failed to create buffer");
            }
        }

        _vk.GetBufferMemoryRequirements(_device, buffer, out var memRequirements);

        var allocInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = memRequirements.Size,
            MemoryTypeIndex = FindMemoryType(memRequirements.MemoryTypeBits, properties)
        };

        fixed (DeviceMemory* memoryPtr = &memory)
        {
            if (_vk.AllocateMemory(_device, &allocInfo, null, memoryPtr) != Result.Success)
            {
                throw new Exception("Failed to allocate buffer memory");
            }
        }

        _vk.BindBufferMemory(_device, buffer, memory, 0);
    }

    public void CreateImage(uint width, uint height, Format format, ImageTiling tiling,
        ImageUsageFlags usage, MemoryPropertyFlags properties, out Image image, out DeviceMemory imageMemory)
    {
        var imageInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Extent = new Extent3D(width, height, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Format = format,
            Tiling = tiling,
            InitialLayout = ImageLayout.Undefined,
            Usage = usage,
            Samples = SampleCountFlags.Count1Bit,
            SharingMode = SharingMode.Exclusive
        };

        fixed (Image* imagePtr = &image)
        {
            if (_vk!.CreateImage(_device, &imageInfo, null, imagePtr) != Result.Success)
            {
                throw new Exception("Failed to create image");
            }
        }

        _vk.GetImageMemoryRequirements(_device, image, out var memRequirements);

        var allocInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = memRequirements.Size,
            MemoryTypeIndex = FindMemoryType(memRequirements.MemoryTypeBits, properties)
        };

        fixed (DeviceMemory* memoryPtr = &imageMemory)
        {
            if (_vk.AllocateMemory(_device, &allocInfo, null, memoryPtr) != Result.Success)
            {
                throw new Exception("Failed to allocate image memory");
            }
        }

        _vk.BindImageMemory(_device, image, imageMemory, 0);
    }

    public uint FindMemoryType(uint typeFilter, MemoryPropertyFlags properties)
    {
        _vk!.GetPhysicalDeviceMemoryProperties(_physicalDevice, out var memProperties);

        for (uint i = 0; i < memProperties.MemoryTypeCount; i++)
        {
            if ((typeFilter & (1 << (int)i)) != 0 &&
                (memProperties.MemoryTypes[(int)i].PropertyFlags & properties) == properties)
            {
                return i;
            }
        }

        throw new Exception("Failed to find suitable memory type");
    }

    public CommandBuffer BeginSingleTimeCommands()
    {
        var allocInfo = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            Level = CommandBufferLevel.Primary,
            CommandPool = _commandPool,
            CommandBufferCount = 1
        };

        CommandBuffer commandBuffer;
        _vk!.AllocateCommandBuffers(_device, &allocInfo, &commandBuffer);

        var beginInfo = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit
        };

        _vk.BeginCommandBuffer(commandBuffer, &beginInfo);
        return commandBuffer;
    }

    public void EndSingleTimeCommands(CommandBuffer commandBuffer)
    {
        _vk!.EndCommandBuffer(commandBuffer);

        var submitInfo = new SubmitInfo
        {
            SType = StructureType.SubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers = &commandBuffer
        };

        _vk.QueueSubmit(_graphicsQueue, 1, &submitInfo, default);
        _vk.QueueWaitIdle(_graphicsQueue);

        _vk.FreeCommandBuffers(_device, _commandPool, 1, &commandBuffer);
    }

    public void TransitionImageLayout(CommandBuffer commandBuffer, Image image, Format format,
        ImageLayout oldLayout, ImageLayout newLayout)
    {
        // Simple barrier helper similar to one in ImGuiController
        PipelineStageFlags srcStage = PipelineStageFlags.TopOfPipeBit;
        PipelineStageFlags dstStage = PipelineStageFlags.BottomOfPipeBit;

        var barrier = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = oldLayout,
            NewLayout = newLayout,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1
            },
            SrcAccessMask = 0,
            DstAccessMask = 0
        };

        // Standard transitions
        if (oldLayout == ImageLayout.Undefined && newLayout == ImageLayout.TransferDstOptimal)
        {
            barrier.SrcAccessMask = 0;
            barrier.DstAccessMask = AccessFlags.TransferWriteBit;
            srcStage = PipelineStageFlags.TopOfPipeBit;
            dstStage = PipelineStageFlags.TransferBit;
        }
        else if (oldLayout == ImageLayout.TransferDstOptimal && newLayout == ImageLayout.ShaderReadOnlyOptimal)
        {
            barrier.SrcAccessMask = AccessFlags.TransferWriteBit;
            barrier.DstAccessMask = AccessFlags.ShaderReadBit;
            srcStage = PipelineStageFlags.TransferBit;
            dstStage = PipelineStageFlags.FragmentShaderBit;
        }

        _vk!.CmdPipelineBarrier(commandBuffer, srcStage, dstStage, 0, 0, null, 0, null, 1, &barrier);
    }

    // Helper overload that handles the command buffer lifecycle internally if not provided
    public void TransitionImageLayout(Image image, Format format, ImageLayout oldLayout, ImageLayout newLayout)
    {
        var cmdbuf = BeginSingleTimeCommands();
        TransitionImageLayout(cmdbuf, image, format, oldLayout, newLayout);
        EndSingleTimeCommands(cmdbuf);
    }

    public void CopyBufferToImage(Buffer buffer, Image image, uint width, uint height)
    {
        var commandBuffer = BeginSingleTimeCommands();

        var region = new BufferImageCopy
        {
            BufferOffset = 0,
            BufferRowLength = 0,
            BufferImageHeight = 0,
            ImageSubresource = new ImageSubresourceLayers
            {
                AspectMask = ImageAspectFlags.ColorBit,
                MipLevel = 0,
                BaseArrayLayer = 0,
                LayerCount = 1
            },
            ImageOffset = new Offset3D(0, 0, 0),
            ImageExtent = new Extent3D(width, height, 1)
        };

        _vk!.CmdCopyBufferToImage(commandBuffer, buffer, image, ImageLayout.TransferDstOptimal, 1, &region);

        EndSingleTimeCommands(commandBuffer);
    }

    #endregion

    #region Helper Structures

    private struct QueueFamilyIndices
    {
        public uint? GraphicsFamily;
        public uint? PresentFamily;

        public bool IsComplete => GraphicsFamily.HasValue && PresentFamily.HasValue;
    }

    private struct SwapChainSupportDetails
    {
        public SurfaceCapabilitiesKHR Capabilities;
        public SurfaceFormatKHR[] Formats;
        public PresentModeKHR[] PresentModes;
    }

    #endregion

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        Console.WriteLine("[VulkanRenderer] Disposing Vulkan resources...");

        _vk?.DeviceWaitIdle(_device);

        // Cleanup sync objects
        for (int i = 0; i < MaxFramesInFlight; i++)
        {
            _vk?.DestroySemaphore(_device, _imageAvailableSemaphores[i], null);
            _vk?.DestroySemaphore(_device, _renderFinishedSemaphores[i], null);
            _vk?.DestroyFence(_device, _inFlightFences[i], null);
        }

        _vk?.DestroyCommandPool(_device, _commandPool, null);

        CleanupSwapChain();

        _vk?.DestroyRenderPass(_device, _renderPass, null);
        _vk?.DestroyDevice(_device, null);

        if (_enableValidation && _debugUtils != null)
        {
            _debugUtils.DestroyDebugUtilsMessenger(_instance, _debugMessenger, null);
        }

        _khrSurface?.DestroySurface(_instance, _surface, null);
        _vk?.DestroyInstance(_instance, null);

        Console.WriteLine("[VulkanRenderer] Vulkan resources disposed");
    }
}
