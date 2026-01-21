using System.Numerics;
using System.Runtime.InteropServices;
using ImGuiNET;
using Silk.NET.Input;
using Silk.NET.Vulkan;
using Silk.NET.Windowing;
using Buffer = Silk.NET.Vulkan.Buffer;
using StbImageSharp;

namespace PrometheusVulkan.Graphics;

public sealed unsafe class ImGuiController : IDisposable
{
    private readonly VulkanRenderer _renderer;
    private readonly IInputContext _input;
    private readonly IWindow _window;

    // Vulkan resources
    private DescriptorPool _descriptorPool;
    private DescriptorSetLayout _descriptorSetLayout;
    private DescriptorSet _fontDescriptorSet;
    private PipelineLayout _pipelineLayout;
    private Pipeline _pipeline;
    private ShaderModule _vertexShaderModule;
    private ShaderModule _fragmentShaderModule;

    // Font texture resources
    private Image _fontImage;
    private DeviceMemory _fontMemory;
    private ImageView _fontImageView;
    private Sampler _fontSampler;

    // Per-frame resources (double buffered)
    private Buffer[] _vertexBuffers = Array.Empty<Buffer>();
    private DeviceMemory[] _vertexBufferMemories = Array.Empty<DeviceMemory>();
    private ulong[] _vertexBufferSizes = Array.Empty<ulong>();
    private Buffer[] _indexBuffers = Array.Empty<Buffer>();
    private DeviceMemory[] _indexBufferMemories = Array.Empty<DeviceMemory>();
    private ulong[] _indexBufferSizes = Array.Empty<ulong>();

    // Input state
    private readonly Dictionary<Key, ImGuiKey> _keyMap = new();
    private readonly bool[] _mousePressed = new bool[5];
    private Vector2 _mousePosition;
    private float _mouseWheel;

    private bool _frameBegun;
    private bool _isInitialized;
    private bool _isDisposed;
    private int _windowWidth;
    private int _windowHeight;

    // Push constants structure matching shader
    [StructLayout(LayoutKind.Sequential)]
    private struct PushConstantBlock
    {
        public Vector2 Scale;
        public Vector2 Translate;
    }

    public ImGuiController(VulkanRenderer renderer, IInputContext input, IWindow window)
    {
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _window = window ?? throw new ArgumentNullException(nameof(window));
    }

    public void Initialize()
    {
        Console.WriteLine("[ImGuiController] Initializing...");

        _windowWidth = _window.FramebufferSize.X;
        _windowHeight = _window.FramebufferSize.Y;

        // Create ImGui context
        var context = ImGui.CreateContext();
        ImGui.SetCurrentContext(context);

        var io = ImGui.GetIO();
        io.BackendFlags |= ImGuiBackendFlags.HasMouseCursors;
        io.BackendFlags |= ImGuiBackendFlags.HasSetMousePos;
        io.BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset;

        // Configure display size
        io.DisplaySize = new Vector2(_windowWidth, _windowHeight);
        io.DisplayFramebufferScale = new Vector2(1.0f, 1.0f);

        // Set up key mapping
        SetupKeyMap();

        // Initialize Vulkan resources
        CreateDescriptorPool();
        CreateDescriptorSetLayout();
        CreatePipelineLayout();
        CreatePipeline();
        CreateFontTexture();
        CreatePerFrameResources();

        _isInitialized = true;
        Console.WriteLine("[ImGuiController] Initialized successfully with Vulkan backend");
    }

    private void SetupKeyMap()
    {
        _keyMap[Key.Tab] = ImGuiKey.Tab;
        _keyMap[Key.Left] = ImGuiKey.LeftArrow;
        _keyMap[Key.Right] = ImGuiKey.RightArrow;
        _keyMap[Key.Up] = ImGuiKey.UpArrow;
        _keyMap[Key.Down] = ImGuiKey.DownArrow;
        _keyMap[Key.PageUp] = ImGuiKey.PageUp;
        _keyMap[Key.PageDown] = ImGuiKey.PageDown;
        _keyMap[Key.Home] = ImGuiKey.Home;
        _keyMap[Key.End] = ImGuiKey.End;
        _keyMap[Key.Insert] = ImGuiKey.Insert;
        _keyMap[Key.Delete] = ImGuiKey.Delete;
        _keyMap[Key.Backspace] = ImGuiKey.Backspace;
        _keyMap[Key.Space] = ImGuiKey.Space;
        _keyMap[Key.Enter] = ImGuiKey.Enter;
        _keyMap[Key.Escape] = ImGuiKey.Escape;
        _keyMap[Key.A] = ImGuiKey.A;
        _keyMap[Key.C] = ImGuiKey.C;
        _keyMap[Key.V] = ImGuiKey.V;
        _keyMap[Key.X] = ImGuiKey.X;
        _keyMap[Key.Y] = ImGuiKey.Y;
        _keyMap[Key.Z] = ImGuiKey.Z;
        _keyMap[Key.ControlLeft] = ImGuiKey.LeftCtrl;
        _keyMap[Key.ControlRight] = ImGuiKey.RightCtrl;
        _keyMap[Key.ShiftLeft] = ImGuiKey.LeftShift;
        _keyMap[Key.ShiftRight] = ImGuiKey.RightShift;
        _keyMap[Key.AltLeft] = ImGuiKey.LeftAlt;
        _keyMap[Key.AltRight] = ImGuiKey.RightAlt;
    }

    #region Vulkan Resource Creation

    private void CreateDescriptorPool()
    {
        var poolSizes = stackalloc DescriptorPoolSize[]
        {
            new DescriptorPoolSize
            {
                Type = DescriptorType.CombinedImageSampler,
                DescriptorCount = 1000
            }
        };

        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            Flags = DescriptorPoolCreateFlags.FreeDescriptorSetBit,
            MaxSets = 1000,
            PoolSizeCount = 1,
            PPoolSizes = poolSizes
        };

        fixed (DescriptorPool* poolPtr = &_descriptorPool)
        {
            if (_renderer.Vk.CreateDescriptorPool(_renderer.Device, &poolInfo, null, poolPtr) != Result.Success)
            {
                throw new Exception("Failed to create descriptor pool");
            }
        }
    }

    private void CreateDescriptorSetLayout()
    {
        var binding = new DescriptorSetLayoutBinding
        {
            Binding = 0,
            DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.FragmentBit
        };

        var layoutInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 1,
            PBindings = &binding
        };

        fixed (DescriptorSetLayout* layoutPtr = &_descriptorSetLayout)
        {
            if (_renderer.Vk.CreateDescriptorSetLayout(_renderer.Device, &layoutInfo, null, layoutPtr) != Result.Success)
            {
                throw new Exception("Failed to create descriptor set layout");
            }
        }
    }

    private void CreatePipelineLayout()
    {
        var pushConstantRange = new PushConstantRange
        {
            StageFlags = ShaderStageFlags.VertexBit,
            Offset = 0,
            Size = (uint)sizeof(PushConstantBlock)
        };

        var setLayout = _descriptorSetLayout;
        var layoutInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1,
            PSetLayouts = &setLayout,
            PushConstantRangeCount = 1,
            PPushConstantRanges = &pushConstantRange
        };

        fixed (PipelineLayout* layoutPtr = &_pipelineLayout)
        {
            if (_renderer.Vk.CreatePipelineLayout(_renderer.Device, &layoutInfo, null, layoutPtr) != Result.Success)
            {
                throw new Exception("Failed to create pipeline layout");
            }
        }
    }

    private void CreatePipeline()
    {
        // Create shader modules
        var vertexShaderCode = ImGuiShaders.GetVertexShaderSpirV();
        var fragmentShaderCode = ImGuiShaders.GetFragmentShaderSpirV();

        _vertexShaderModule = CreateShaderModule(vertexShaderCode);
        _fragmentShaderModule = CreateShaderModule(fragmentShaderCode);

        var mainName = stackalloc byte[] { 0x6D, 0x61, 0x69, 0x6E, 0x00 }; // "main\0"

        var shaderStages = stackalloc PipelineShaderStageCreateInfo[]
        {
            new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.VertexBit,
                Module = _vertexShaderModule,
                PName = mainName
            },
            new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.FragmentBit,
                Module = _fragmentShaderModule,
                PName = mainName
            }
        };

        // Vertex input - ImDrawVert layout
        var bindingDescription = new VertexInputBindingDescription
        {
            Binding = 0,
            Stride = (uint)sizeof(ImDrawVert),
            InputRate = VertexInputRate.Vertex
        };

        var attributeDescriptions = stackalloc VertexInputAttributeDescription[]
        {
            // Position (vec2)
            new VertexInputAttributeDescription
            {
                Binding = 0,
                Location = 0,
                Format = Format.R32G32Sfloat,
                Offset = 0
            },
            // UV (vec2)
            new VertexInputAttributeDescription
            {
                Binding = 0,
                Location = 1,
                Format = Format.R32G32Sfloat,
                Offset = 8
            },
            // Color (vec4 packed as uint32)
            new VertexInputAttributeDescription
            {
                Binding = 0,
                Location = 2,
                Format = Format.R8G8B8A8Unorm,
                Offset = 16
            }
        };

        var vertexInputInfo = new PipelineVertexInputStateCreateInfo
        {
            SType = StructureType.PipelineVertexInputStateCreateInfo,
            VertexBindingDescriptionCount = 1,
            PVertexBindingDescriptions = &bindingDescription,
            VertexAttributeDescriptionCount = 3,
            PVertexAttributeDescriptions = attributeDescriptions
        };

        var inputAssembly = new PipelineInputAssemblyStateCreateInfo
        {
            SType = StructureType.PipelineInputAssemblyStateCreateInfo,
            Topology = PrimitiveTopology.TriangleList,
            PrimitiveRestartEnable = false
        };

        // Dynamic viewport and scissor
        var dynamicStates = stackalloc DynamicState[]
        {
            DynamicState.Viewport,
            DynamicState.Scissor
        };

        var dynamicState = new PipelineDynamicStateCreateInfo
        {
            SType = StructureType.PipelineDynamicStateCreateInfo,
            DynamicStateCount = 2,
            PDynamicStates = dynamicStates
        };

        var viewportState = new PipelineViewportStateCreateInfo
        {
            SType = StructureType.PipelineViewportStateCreateInfo,
            ViewportCount = 1,
            ScissorCount = 1
        };

        var rasterizer = new PipelineRasterizationStateCreateInfo
        {
            SType = StructureType.PipelineRasterizationStateCreateInfo,
            DepthClampEnable = false,
            RasterizerDiscardEnable = false,
            PolygonMode = PolygonMode.Fill,
            LineWidth = 1.0f,
            CullMode = CullModeFlags.None,
            FrontFace = FrontFace.CounterClockwise,
            DepthBiasEnable = false
        };

        var multisampling = new PipelineMultisampleStateCreateInfo
        {
            SType = StructureType.PipelineMultisampleStateCreateInfo,
            SampleShadingEnable = false,
            RasterizationSamples = SampleCountFlags.Count1Bit
        };

        // Alpha blending for ImGui
        var colorBlendAttachment = new PipelineColorBlendAttachmentState
        {
            ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit |
                             ColorComponentFlags.BBit | ColorComponentFlags.ABit,
            BlendEnable = true,
            SrcColorBlendFactor = BlendFactor.SrcAlpha,
            DstColorBlendFactor = BlendFactor.OneMinusSrcAlpha,
            ColorBlendOp = BlendOp.Add,
            SrcAlphaBlendFactor = BlendFactor.One,
            DstAlphaBlendFactor = BlendFactor.OneMinusSrcAlpha,
            AlphaBlendOp = BlendOp.Add
        };

        var colorBlending = new PipelineColorBlendStateCreateInfo
        {
            SType = StructureType.PipelineColorBlendStateCreateInfo,
            LogicOpEnable = false,
            AttachmentCount = 1,
            PAttachments = &colorBlendAttachment
        };

        var depthStencil = new PipelineDepthStencilStateCreateInfo
        {
            SType = StructureType.PipelineDepthStencilStateCreateInfo,
            DepthTestEnable = false,
            DepthWriteEnable = false,
            DepthCompareOp = CompareOp.Always,
            DepthBoundsTestEnable = false,
            StencilTestEnable = false
        };

        var pipelineInfo = new GraphicsPipelineCreateInfo
        {
            SType = StructureType.GraphicsPipelineCreateInfo,
            StageCount = 2,
            PStages = shaderStages,
            PVertexInputState = &vertexInputInfo,
            PInputAssemblyState = &inputAssembly,
            PViewportState = &viewportState,
            PRasterizationState = &rasterizer,
            PMultisampleState = &multisampling,
            PColorBlendState = &colorBlending,
            PDepthStencilState = &depthStencil,
            PDynamicState = &dynamicState,
            Layout = _pipelineLayout,
            RenderPass = _renderer.RenderPass,
            Subpass = 0
        };

        fixed (Pipeline* pipelinePtr = &_pipeline)
        {
            if (_renderer.Vk.CreateGraphicsPipelines(_renderer.Device, default, 1, &pipelineInfo, null, pipelinePtr) != Result.Success)
            {
                throw new Exception("Failed to create ImGui pipeline");
            }
        }

        Console.WriteLine("[ImGuiController] Graphics pipeline created");
    }

    private ShaderModule CreateShaderModule(uint[] code)
    {
        fixed (uint* codePtr = code)
        {
            var createInfo = new ShaderModuleCreateInfo
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)(code.Length * sizeof(uint)),
                PCode = codePtr
            };

            ShaderModule shaderModule;
            if (_renderer.Vk.CreateShaderModule(_renderer.Device, &createInfo, null, &shaderModule) != Result.Success)
            {
                throw new Exception("Failed to create shader module");
            }

            return shaderModule;
        }
    }

    public IntPtr LoadTexture(string path)
    {
        using var stream = File.OpenRead(path);
        var image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
        
        ulong size = (ulong)(image.Width * image.Height * 4);
        
        // 1. Create Staging Buffer
        _renderer.CreateBuffer(size, BufferUsageFlags.TransferSrcBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            out var stagingBuffer, out var stagingMemory);

        // 2. Copy Data to Staging Buffer
        void* data;
        _renderer.Vk.MapMemory(_renderer.Device, stagingMemory, 0, size, 0, &data);
        fixed (byte* pixelPtr = image.Data)
        {
            System.Buffer.MemoryCopy(pixelPtr, data, size, size);
        }
        _renderer.Vk.UnmapMemory(_renderer.Device, stagingMemory);
        
        // 3. Create Image
        _renderer.CreateImage((uint)image.Width, (uint)image.Height, Format.R8G8B8A8Unorm, ImageTiling.Optimal,
            ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit,
            MemoryPropertyFlags.DeviceLocalBit,
            out var vkImage, out var vkImageMemory);
            
        // 4. Transition & Copy
        _renderer.TransitionImageLayout(vkImage, Format.R8G8B8A8Unorm, ImageLayout.Undefined, ImageLayout.TransferDstOptimal);
        _renderer.CopyBufferToImage(stagingBuffer, vkImage, (uint)image.Width, (uint)image.Height);
        _renderer.TransitionImageLayout(vkImage, Format.R8G8B8A8Unorm, ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal);
        
        // Cleanup Staging
        _renderer.Vk.DestroyBuffer(_renderer.Device, stagingBuffer, null);
        _renderer.Vk.FreeMemory(_renderer.Device, stagingMemory, null);
        
        // 5. Create ImageView
        var viewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = vkImage,
            ViewType = ImageViewType.Type2D,
            Format = Format.R8G8B8A8Unorm,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1
            }
        };
        
        ImageView imageView;
        if (_renderer.Vk.CreateImageView(_renderer.Device, &viewInfo, null, &imageView) != Result.Success)
            throw new Exception("Failed to create texture image view");
            
        // 6. Create Sampler
        var samplerInfo = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Linear,
            MinFilter = Filter.Linear,
            AddressModeU = SamplerAddressMode.ClampToEdge,
            AddressModeV = SamplerAddressMode.ClampToEdge,
            AddressModeW = SamplerAddressMode.ClampToEdge,
            AnisotropyEnable = false,
            MaxAnisotropy = 1,
            BorderColor = BorderColor.IntOpaqueBlack,
            UnnormalizedCoordinates = false,
            CompareEnable = false,
            CompareOp = CompareOp.Always,
            MipmapMode = SamplerMipmapMode.Linear
        };
        
        Sampler sampler;
        if (_renderer.Vk.CreateSampler(_renderer.Device, &samplerInfo, null, &sampler) != Result.Success)
            throw new Exception("Failed to create texture sampler");
            
        // 7. Descriptor Set
        var layout = _descriptorSetLayout;
        var allocInfo = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _descriptorPool,
            DescriptorSetCount = 1,
            PSetLayouts = &layout
        };
        
        DescriptorSet descriptorSet;
        if (_renderer.Vk.AllocateDescriptorSets(_renderer.Device, &allocInfo, &descriptorSet) != Result.Success)
            throw new Exception("Failed to allocate texture descriptor set");
            
        var imageInfoDesc = new DescriptorImageInfo
        {
            Sampler = sampler,
            ImageView = imageView,
            ImageLayout = ImageLayout.ShaderReadOnlyOptimal
        };
        
        var writeDesc = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = descriptorSet,
            DstBinding = 0,
            DstArrayElement = 0,
            DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
            PImageInfo = &imageInfoDesc
        };
        
        _renderer.Vk.UpdateDescriptorSets(_renderer.Device, 1, &writeDesc, 0, null);
        
        return (IntPtr)descriptorSet.Handle;
    }

    private void CreateFontTexture()
    {
        var io = ImGui.GetIO();
        
        string basePath = AppDomain.CurrentDomain.BaseDirectory;
        string fontPath = Path.Combine(basePath, "Assets", "Fonts", "font.ttf");

        if (File.Exists(fontPath))
        {
            Console.WriteLine($"[ImGuiController] Loading custom font from {fontPath}");
            // Load at 24px for better readability (default is usually 13px)
            io.Fonts.AddFontFromFileTTF(fontPath, 24.0f);
        }
        else
        {
            Console.WriteLine("[ImGuiController] Custom font not found, using default.");
            io.Fonts.AddFontDefault();
        }

        io.Fonts.GetTexDataAsRGBA32(out IntPtr pixels, out int width, out int height, out int bytesPerPixel);
        
        ulong size = (ulong)(width * height * 4);
        
        // Staging
        _renderer.CreateBuffer(size, BufferUsageFlags.TransferSrcBit, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit, 
            out var stagingBuffer, out var stagingMemory);
            
        void* data;
        _renderer.Vk.MapMemory(_renderer.Device, stagingMemory, 0, size, 0, &data);
        System.Buffer.MemoryCopy((void*)pixels, data, size, size);
        _renderer.Vk.UnmapMemory(_renderer.Device, stagingMemory);
        
        // Image
        _renderer.CreateImage((uint)width, (uint)height, Format.R8G8B8A8Unorm, ImageTiling.Optimal,
            ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit, MemoryPropertyFlags.DeviceLocalBit,
            out _fontImage, out _fontMemory);
            
        // Transfer
        _renderer.TransitionImageLayout(_fontImage, Format.R8G8B8A8Unorm, ImageLayout.Undefined, ImageLayout.TransferDstOptimal);
        _renderer.CopyBufferToImage(stagingBuffer, _fontImage, (uint)width, (uint)height);
        _renderer.TransitionImageLayout(_fontImage, Format.R8G8B8A8Unorm, ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal);
        
        // Cleanup Buffer
        _renderer.Vk.DestroyBuffer(_renderer.Device, stagingBuffer, null);
        _renderer.Vk.FreeMemory(_renderer.Device, stagingMemory, null);

        // View
        var viewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = _fontImage,
            ViewType = ImageViewType.Type2D,
            Format = Format.R8G8B8A8Unorm,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1
            }
        };
        
        fixed (ImageView* viewPtr = &_fontImageView)
            _renderer.Vk.CreateImageView(_renderer.Device, &viewInfo, null, viewPtr);
            
        // Sampler
        var samplerInfo = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Linear,
            MinFilter = Filter.Linear,
            MipmapMode = SamplerMipmapMode.Linear,
            AddressModeU = SamplerAddressMode.Repeat,
            AddressModeV = SamplerAddressMode.Repeat,
            AddressModeW = SamplerAddressMode.Repeat,
            MinLod = -1000,
            MaxLod = 1000,
            MaxAnisotropy = 1.0f
        };
        
        fixed (Sampler* samplerPtr = &_fontSampler)
            _renderer.Vk.CreateSampler(_renderer.Device, &samplerInfo, null, samplerPtr);
            
        // Descriptor
        var layout = _descriptorSetLayout;
        var allocInfo = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _descriptorPool,
            DescriptorSetCount = 1,
            PSetLayouts = &layout
        };
        
        fixed (DescriptorSet* setPtr = &_fontDescriptorSet)
            _renderer.Vk.AllocateDescriptorSets(_renderer.Device, &allocInfo, setPtr);
            
        var imageInfo = new DescriptorImageInfo
        {
            Sampler = _fontSampler,
            ImageView = _fontImageView,
            ImageLayout = ImageLayout.ShaderReadOnlyOptimal
        };
        
        var writeDesc = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = _fontDescriptorSet,
            DstBinding = 0,
            DstArrayElement = 0,
            DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
            PImageInfo = &imageInfo
        };
        
        _renderer.Vk.UpdateDescriptorSets(_renderer.Device, 1, &writeDesc, 0, null);
        
        io.Fonts.SetTexID((IntPtr)_fontDescriptorSet.Handle);
    }
    
    private void CreatePerFrameResources()
    {
        int frameCount = _renderer.SwapchainImageCount;

        _vertexBuffers = new Buffer[frameCount];
        _vertexBufferMemories = new DeviceMemory[frameCount];
        _vertexBufferSizes = new ulong[frameCount];
        _indexBuffers = new Buffer[frameCount];
        _indexBufferMemories = new DeviceMemory[frameCount];
        _indexBufferSizes = new ulong[frameCount];

        for (int i = 0; i < frameCount; i++)
        {
             _vertexBufferSizes[i] = 0;
             _indexBufferSizes[i] = 0;
        }
    }

    #endregion

    public void Update(float deltaTime)
    {
        var io = ImGui.GetIO();
        io.DeltaTime = deltaTime > 0 ? deltaTime : 1.0f / 60.0f;

        io.DisplaySize = new Vector2(_windowWidth, _windowHeight);
        io.MousePos = _mousePosition;

        for (int i = 0; i < _mousePressed.Length; i++)
        {
            io.MouseDown[i] = _mousePressed[i];
        }

        io.MouseWheel = _mouseWheel;
        _mouseWheel = 0;
    }

    public void BeginFrame()
    {
        if (_frameBegun)
            return;

        ImGui.NewFrame();
        _frameBegun = true;
    }

    public void EndFrame()
    {
        if (!_frameBegun)
            return;

        _frameBegun = false;
        ImGui.Render();

        var drawData = ImGui.GetDrawData();
        RenderDrawData(drawData);
    }

    private void RenderDrawData(ImDrawDataPtr drawData)
    {
        // Avoid rendering when minimized
        int fbWidth = (int)(drawData.DisplaySize.X * drawData.FramebufferScale.X);
        int fbHeight = (int)(drawData.DisplaySize.Y * drawData.FramebufferScale.Y);
        if (fbWidth <= 0 || fbHeight <= 0)
            return;

        var commandBuffer = _renderer.GetCurrentCommandBuffer();
        int frameIndex = _renderer.CurrentFrame;

        // Create/resize vertex and index buffers if needed
        if (drawData.TotalVtxCount > 0)
        {
            ulong vertexSize = (ulong)(drawData.TotalVtxCount * sizeof(ImDrawVert));
            ulong indexSize = (ulong)(drawData.TotalIdxCount * sizeof(ushort));

            // Recreate vertex buffer if needed
            if (_vertexBufferSizes[frameIndex] < vertexSize)
            {
                if (_vertexBuffers[frameIndex].Handle != 0)
                {
                    _renderer.Vk.DestroyBuffer(_renderer.Device, _vertexBuffers[frameIndex], null);
                    _renderer.Vk.FreeMemory(_renderer.Device, _vertexBufferMemories[frameIndex], null);
                }

                _renderer.CreateBuffer(vertexSize, BufferUsageFlags.VertexBufferBit,
                    MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                    out _vertexBuffers[frameIndex], out _vertexBufferMemories[frameIndex]);
                _vertexBufferSizes[frameIndex] = vertexSize;
            }

            // Recreate index buffer if needed
            if (_indexBufferSizes[frameIndex] < indexSize)
            {
                if (_indexBuffers[frameIndex].Handle != 0)
                {
                    _renderer.Vk.DestroyBuffer(_renderer.Device, _indexBuffers[frameIndex], null);
                    _renderer.Vk.FreeMemory(_renderer.Device, _indexBufferMemories[frameIndex], null);
                }

                _renderer.CreateBuffer(indexSize, BufferUsageFlags.IndexBufferBit,
                    MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                    out _indexBuffers[frameIndex], out _indexBufferMemories[frameIndex]);
                _indexBufferSizes[frameIndex] = indexSize;
            }

            // Upload vertex/index data
            void* vtxDst;
            void* idxDst;
            _renderer.Vk.MapMemory(_renderer.Device, _vertexBufferMemories[frameIndex], 0, vertexSize, 0, &vtxDst);
            _renderer.Vk.MapMemory(_renderer.Device, _indexBufferMemories[frameIndex], 0, indexSize, 0, &idxDst);

            for (int n = 0; n < drawData.CmdListsCount; n++)
            {
                var cmdList = drawData.CmdLists[n];

                ulong vtxSize = (ulong)(cmdList.VtxBuffer.Size * sizeof(ImDrawVert));
                ulong idxSize = (ulong)(cmdList.IdxBuffer.Size * sizeof(ushort));

                System.Buffer.MemoryCopy((void*)cmdList.VtxBuffer.Data, vtxDst, vtxSize, vtxSize);
                System.Buffer.MemoryCopy((void*)cmdList.IdxBuffer.Data, idxDst, idxSize, idxSize);

                vtxDst = (byte*)vtxDst + vtxSize;
                idxDst = (byte*)idxDst + idxSize;
            }

            _renderer.Vk.UnmapMemory(_renderer.Device, _vertexBufferMemories[frameIndex]);
            _renderer.Vk.UnmapMemory(_renderer.Device, _indexBufferMemories[frameIndex]);
        }

        // Bind pipeline
        _renderer.Vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, _pipeline);

        // Bind vertex and index buffers
        if (drawData.TotalVtxCount > 0)
        {
            var vertexBuffer = _vertexBuffers[frameIndex];
            ulong offset = 0;
            _renderer.Vk.CmdBindVertexBuffers(commandBuffer, 0, 1, &vertexBuffer, &offset);
            _renderer.Vk.CmdBindIndexBuffer(commandBuffer, _indexBuffers[frameIndex], 0, IndexType.Uint16);
        }

        // Setup viewport
        var viewport = new Viewport
        {
            X = 0,
            Y = 0,
            Width = fbWidth,
            Height = fbHeight,
            MinDepth = 0.0f,
            MaxDepth = 1.0f
        };
        _renderer.Vk.CmdSetViewport(commandBuffer, 0, 1, &viewport);

        // Setup scale and translation via push constants
        var pushConstants = new PushConstantBlock
        {
            Scale = new Vector2(2.0f / drawData.DisplaySize.X, 2.0f / drawData.DisplaySize.Y),
            Translate = new Vector2(-1.0f - drawData.DisplayPos.X * (2.0f / drawData.DisplaySize.X),
                                    -1.0f - drawData.DisplayPos.Y * (2.0f / drawData.DisplaySize.Y))
        };

        _renderer.Vk.CmdPushConstants(commandBuffer, _pipelineLayout, ShaderStageFlags.VertexBit,
            0, (uint)sizeof(PushConstantBlock), &pushConstants);

        // Render command lists
        Vector2 clipOff = drawData.DisplayPos;
        Vector2 clipScale = drawData.FramebufferScale;

        int globalVtxOffset = 0;
        int globalIdxOffset = 0;

        for (int n = 0; n < drawData.CmdListsCount; n++)
        {
            var cmdList = drawData.CmdLists[n];

            for (int cmd = 0; cmd < cmdList.CmdBuffer.Size; cmd++)
            {
                var pcmd = cmdList.CmdBuffer[cmd];

                if (pcmd.UserCallback != IntPtr.Zero)
                {
                    // User callback (not implemented)
                    continue;
                }

                // Clip rectangle
                Vector2 clipMin = new((pcmd.ClipRect.X - clipOff.X) * clipScale.X,
                                      (pcmd.ClipRect.Y - clipOff.Y) * clipScale.Y);
                Vector2 clipMax = new((pcmd.ClipRect.Z - clipOff.X) * clipScale.X,
                                      (pcmd.ClipRect.W - clipOff.Y) * clipScale.Y);

                if (clipMin.X < 0) clipMin.X = 0;
                if (clipMin.Y < 0) clipMin.Y = 0;
                if (clipMax.X > fbWidth) clipMax.X = fbWidth;
                if (clipMax.Y > fbHeight) clipMax.Y = fbHeight;
                if (clipMax.X <= clipMin.X || clipMax.Y <= clipMin.Y)
                    continue;

                var scissor = new Rect2D
                {
                    Offset = new Offset2D((int)clipMin.X, (int)clipMin.Y),
                    Extent = new Extent2D((uint)(clipMax.X - clipMin.X), (uint)(clipMax.Y - clipMin.Y))
                };
                _renderer.Vk.CmdSetScissor(commandBuffer, 0, 1, &scissor);

                // Bind texture
                var descSet = new DescriptorSet { Handle = (ulong)pcmd.TextureId };
                if (descSet.Handle == 0)
                    descSet = _fontDescriptorSet;

                _renderer.Vk.CmdBindDescriptorSets(commandBuffer, PipelineBindPoint.Graphics, _pipelineLayout,
                    0, 1, &descSet, 0, null);

                // Draw
                _renderer.Vk.CmdDrawIndexed(commandBuffer, pcmd.ElemCount, 1,
                    pcmd.IdxOffset + (uint)globalIdxOffset,
                    (int)(pcmd.VtxOffset + (uint)globalVtxOffset), 0);
            }

            globalVtxOffset += cmdList.VtxBuffer.Size;
            globalIdxOffset += cmdList.IdxBuffer.Size;
        }
    }

    #region Input Handling

    public void OnKeyDown(Key key)
    {
        var io = ImGui.GetIO();
        if (_keyMap.TryGetValue(key, out var imguiKey))
        {
            io.AddKeyEvent(imguiKey, true);
        }

        UpdateModifiers(io);
    }

    public void OnKeyUp(Key key)
    {
        var io = ImGui.GetIO();
        if (_keyMap.TryGetValue(key, out var imguiKey))
        {
            io.AddKeyEvent(imguiKey, false);
        }

        UpdateModifiers(io);
    }

    public void OnKeyChar(char character)
    {
        var io = ImGui.GetIO();
        io.AddInputCharacter(character);
    }

    public void OnMouseDown(MouseButton button)
    {
        int index = button switch
        {
            MouseButton.Left => 0,
            MouseButton.Right => 1,
            MouseButton.Middle => 2,
            _ => -1
        };

        if (index >= 0 && index < _mousePressed.Length)
        {
            _mousePressed[index] = true;
        }
    }

    public void OnMouseUp(MouseButton button)
    {
        int index = button switch
        {
            MouseButton.Left => 0,
            MouseButton.Right => 1,
            MouseButton.Middle => 2,
            _ => -1
        };

        if (index >= 0 && index < _mousePressed.Length)
        {
            _mousePressed[index] = false;
        }
    }

    public void OnMouseMove(Vector2 position)
    {
        _mousePosition = position;
    }

    public void OnScroll(float offset)
    {
        _mouseWheel += offset;
    }

    public void OnResize(int width, int height)
    {
        _windowWidth = width;
        _windowHeight = height;
    }

    private void UpdateModifiers(ImGuiIOPtr io)
    {
        foreach (var keyboard in _input.Keyboards)
        {
            io.KeyCtrl = keyboard.IsKeyPressed(Key.ControlLeft) || keyboard.IsKeyPressed(Key.ControlRight);
            io.KeyShift = keyboard.IsKeyPressed(Key.ShiftLeft) || keyboard.IsKeyPressed(Key.ShiftRight);
            io.KeyAlt = keyboard.IsKeyPressed(Key.AltLeft) || keyboard.IsKeyPressed(Key.AltRight);
            io.KeySuper = keyboard.IsKeyPressed(Key.SuperLeft) || keyboard.IsKeyPressed(Key.SuperRight);
        }
    }

    #endregion

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        Console.WriteLine("[ImGuiController] Disposing...");

        _renderer.Vk.DeviceWaitIdle(_renderer.Device);

        // Destroy per-frame resources
        for (int i = 0; i < _vertexBuffers.Length; i++)
        {
            if (_vertexBuffers[i].Handle != 0)
            {
                _renderer.Vk.DestroyBuffer(_renderer.Device, _vertexBuffers[i], null);
                _renderer.Vk.FreeMemory(_renderer.Device, _vertexBufferMemories[i], null);
            }
            if (_indexBuffers[i].Handle != 0)
            {
                _renderer.Vk.DestroyBuffer(_renderer.Device, _indexBuffers[i], null);
                _renderer.Vk.FreeMemory(_renderer.Device, _indexBufferMemories[i], null);
            }
        }

        // Destroy font resources
        _renderer.Vk.DestroySampler(_renderer.Device, _fontSampler, null);
        _renderer.Vk.DestroyImageView(_renderer.Device, _fontImageView, null);
        _renderer.Vk.DestroyImage(_renderer.Device, _fontImage, null);
        _renderer.Vk.FreeMemory(_renderer.Device, _fontMemory, null);

        // Destroy pipeline resources
        _renderer.Vk.DestroyPipeline(_renderer.Device, _pipeline, null);
        _renderer.Vk.DestroyPipelineLayout(_renderer.Device, _pipelineLayout, null);
        _renderer.Vk.DestroyShaderModule(_renderer.Device, _vertexShaderModule, null);
        _renderer.Vk.DestroyShaderModule(_renderer.Device, _fragmentShaderModule, null);

        // Destroy descriptor resources
        _renderer.Vk.DestroyDescriptorSetLayout(_renderer.Device, _descriptorSetLayout, null);
        _renderer.Vk.DestroyDescriptorPool(_renderer.Device, _descriptorPool, null);

        ImGui.DestroyContext();

        Console.WriteLine("[ImGuiController] Disposed");
    }
}
