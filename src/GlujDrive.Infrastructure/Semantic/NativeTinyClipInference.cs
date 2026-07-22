using System.Runtime.InteropServices;
using GlujDrive.Application.Semantic;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace GlujDrive.Infrastructure.Semantic;

internal sealed class NativeTinyClipInference : IDisposable
{
    private const int ExpectedApiVersion = 1;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemanticSearchOptions _options;
    private readonly SemanticModelPackage _modelPackage;
    private readonly System.Threading.Timer _idleTimer;
    private NativeApi? _api;
    private IntPtr _context;
    private DateTimeOffset _lastUseUtc;
    private string? _loadedFingerprint;
    private string? _loadedSelection;
    private ClipTokenizer? _tokenizer;

    public NativeTinyClipInference(
        SemanticSearchOptions options,
        SemanticModelPackage modelPackage)
    {
        _options = options;
        _modelPackage = modelPackage;
        _idleTimer = new System.Threading.Timer(
            _ => ReleaseIfIdle(),
            null,
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(1));
    }

    public string? ActiveDevice { get; private set; }

    public string? FallbackReason { get; private set; }

    public bool RuntimeAvailable
    {
        get
        {
            try
            {
                return GetApi().ApiVersion() == ExpectedApiVersion;
            }
            catch
            {
                return false;
            }
        }
    }

    public async Task<IReadOnlyList<SemanticDevice>> GetDevicesAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);

        try
        {
            NativeApi api;
            try
            {
                api = GetApi();
            }
            catch
            {
                return [new SemanticDevice("cpu", "CPU (native runtime unavailable)", "cpu", false)];
            }

            var devices = new List<SemanticDevice>
            {
                new("cpu", "CPU", "cpu", true)
            };
            var count = Math.Max(0, api.VulkanDeviceCount());

            for (var index = 0; index < count; index++)
            {
                var name = GetVulkanDeviceName(api, index);
                devices.Add(new SemanticDevice(
                    $"vulkan:{index}",
                    string.IsNullOrWhiteSpace(name) ? $"Vulkan GPU {index + 1}" : name,
                    "vulkan",
                    true));
            }

            return devices;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<float[]> EmbedImageAsync(
        Stream imageStream,
        SemanticModelManifest manifest,
        string computeSelection,
        CancellationToken cancellationToken = default)
    {
        using var image = await SixLabors.ImageSharp.Image.LoadAsync<Rgb24>(imageStream, cancellationToken);
        image.Mutate(context => context.Resize(new ResizeOptions
        {
            Size = new SixLabors.ImageSharp.Size(manifest.ImageWidth, manifest.ImageHeight),
            Mode = ResizeMode.Crop,
            Position = AnchorPositionMode.Center,
            Sampler = KnownResamplers.Bicubic
        }));
        var pixels = new byte[checked(manifest.ImageWidth * manifest.ImageHeight * 3)];
        image.CopyPixelDataTo(pixels);

        return await InvokeAsync(
            manifest,
            computeSelection,
            (api, context, output) => api.EmbedImageRgb(
                context,
                pixels,
                manifest.ImageWidth,
                manifest.ImageHeight,
                manifest.ImageWidth * 3,
                output,
                output.Length),
            cancellationToken);
    }

    public Task<float[]> EmbedTextAsync(
        string text,
        SemanticModelManifest manifest,
        string computeSelection,
        CancellationToken cancellationToken = default) =>
        InvokeAsync(
            manifest,
            computeSelection,
            (api, context, output) =>
            {
                _tokenizer ??= new ClipTokenizer(_modelPackage.ModelPath, manifest);
                var tokens = _tokenizer.Encode(text);
                return api.EmbedTextTokens(context, tokens, tokens.Length, output, output.Length);
            },
            cancellationToken);

    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ReleaseContext();
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _idleTimer.Dispose();
        ReleaseContext();
        _api?.Dispose();
        _gate.Dispose();
    }

    private async Task<float[]> InvokeAsync(
        SemanticModelManifest manifest,
        string computeSelection,
        Func<NativeApi, IntPtr, float[], int> operation,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);

        try
        {
            var api = GetApi();
            EnsureContext(api, manifest, computeSelection);
            cancellationToken.ThrowIfCancellationRequested();
            var output = new float[manifest.EmbeddingDimensions];
            var result = operation(api, _context, output);
            _lastUseUtc = DateTimeOffset.UtcNow;

            if (result != 0)
            {
                throw new InvalidOperationException(GetLastError(api));
            }

            Normalize(output);
            return output;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void EnsureContext(
        NativeApi api,
        SemanticModelManifest manifest,
        string computeSelection)
    {
        if (_context != IntPtr.Zero &&
            _loadedFingerprint == manifest.Fingerprint &&
            _loadedSelection == computeSelection)
        {
            return;
        }

        ReleaseContext();
        FallbackReason = null;
        var requestedDevice = ParseDevice(computeSelection);
        var result = api.Create(
            _modelPackage.ModelPath,
            requestedDevice,
            out _context);

        if (result != 0 && computeSelection.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            FallbackReason = $"Vulkan initialization failed: {GetLastError(api)}";
            result = api.Create(_modelPackage.ModelPath, -1, out _context);
            requestedDevice = -1;
        }

        if (result != 0 || _context == IntPtr.Zero)
        {
            throw new InvalidOperationException(GetLastError(api));
        }

        if (api.EmbeddingDimensions(_context) != manifest.EmbeddingDimensions)
        {
            ReleaseContext();
            throw new InvalidDataException("The native runtime and model manifest disagree on vector dimensions.");
        }

        _loadedFingerprint = manifest.Fingerprint;
        _loadedSelection = computeSelection;
        ActiveDevice = requestedDevice < 0 ? "CPU" : GetVulkanDeviceName(api, requestedDevice);
        _lastUseUtc = DateTimeOffset.UtcNow;
    }

    private NativeApi GetApi()
    {
        if (_api is not null)
        {
            return _api;
        }

        var runtimePath = Path.IsPathRooted(_options.RuntimeLibraryPath)
            ? _options.RuntimeLibraryPath
            : Path.Combine(_modelPackage.ModelPath, _options.RuntimeLibraryPath);

        if (!File.Exists(runtimePath) && !Path.IsPathRooted(_options.RuntimeLibraryPath))
        {
            runtimePath = Path.Combine(AppContext.BaseDirectory, _options.RuntimeLibraryPath);
        }
        _api = new NativeApi(runtimePath);

        if (_api.ApiVersion() != ExpectedApiVersion)
        {
            _api.Dispose();
            _api = null;
            throw new InvalidOperationException("The native semantic runtime API version is incompatible.");
        }

        return _api;
    }

    private static int ParseDevice(string selection)
    {
        if (selection.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (selection.Equals("cpu", StringComparison.OrdinalIgnoreCase))
        {
            return -1;
        }

        if (selection.StartsWith("vulkan:", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(selection.AsSpan("vulkan:".Length), out var index) &&
            index >= 0)
        {
            return index;
        }

        throw new ArgumentException("Select Auto, CPU, or a detected Vulkan device.", nameof(selection));
    }

    private void ReleaseIfIdle()
    {
        if (_context == IntPtr.Zero ||
            DateTimeOffset.UtcNow - _lastUseUtc < TimeSpan.FromMinutes(Math.Max(1, _options.IdleUnloadMinutes)) ||
            !_gate.Wait(0))
        {
            return;
        }

        try
        {
            ReleaseContext();
        }
        finally
        {
            _gate.Release();
        }
    }

    private void ReleaseContext()
    {
        if (_context != IntPtr.Zero && _api is not null)
        {
            _api.Destroy(_context);
        }

        _context = IntPtr.Zero;
        _loadedFingerprint = null;
        _loadedSelection = null;
        _tokenizer = null;
        ActiveDevice = null;
        FallbackReason = null;
    }

    private static string GetVulkanDeviceName(NativeApi api, int index)
    {
        var buffer = new byte[256];
        var result = api.VulkanDeviceName(index, buffer, buffer.Length);
        return result == 0
            ? System.Text.Encoding.UTF8.GetString(buffer).TrimEnd('\0')
            : $"Vulkan GPU {index + 1}";
    }

    private static string GetLastError(NativeApi api)
    {
        var pointer = api.LastError();
        return pointer == IntPtr.Zero
            ? "The native semantic runtime failed without an error message."
            : Marshal.PtrToStringUTF8(pointer) ?? "The native semantic runtime failed.";
    }

    private static void Normalize(float[] vector)
    {
        var lengthSquared = 0d;
        foreach (var value in vector)
        {
            lengthSquared += value * value;
        }

        var length = Math.Sqrt(lengthSquared);
        if (length <= double.Epsilon)
        {
            throw new InvalidDataException("The model returned an empty semantic vector.");
        }

        for (var index = 0; index < vector.Length; index++)
        {
            vector[index] = (float)(vector[index] / length);
        }
    }

    private sealed class NativeApi : IDisposable
    {
        private readonly IntPtr _library;

        public NativeApi(string path)
        {
            _library = NativeLibrary.Load(path);
            ApiVersion = Load<ApiVersionDelegate>("gd_api_version");
            VulkanDeviceCount = Load<VulkanDeviceCountDelegate>("gd_vulkan_device_count");
            VulkanDeviceName = Load<VulkanDeviceNameDelegate>("gd_vulkan_device_name");
            Create = Load<CreateDelegate>("gd_create");
            Destroy = Load<DestroyDelegate>("gd_destroy");
            EmbeddingDimensions = Load<EmbeddingDimensionsDelegate>("gd_embedding_dimensions");
            EmbedImageRgb = Load<EmbedImageRgbDelegate>("gd_embed_image_rgb");
            EmbedTextTokens = Load<EmbedTextTokensDelegate>("gd_embed_text_tokens");
            LastError = Load<LastErrorDelegate>("gd_last_error");
        }

        public ApiVersionDelegate ApiVersion { get; }
        public VulkanDeviceCountDelegate VulkanDeviceCount { get; }
        public VulkanDeviceNameDelegate VulkanDeviceName { get; }
        public CreateDelegate Create { get; }
        public DestroyDelegate Destroy { get; }
        public EmbeddingDimensionsDelegate EmbeddingDimensions { get; }
        public EmbedImageRgbDelegate EmbedImageRgb { get; }
        public EmbedTextTokensDelegate EmbedTextTokens { get; }
        public LastErrorDelegate LastError { get; }

        public void Dispose() => NativeLibrary.Free(_library);

        private T Load<T>(string name) where T : Delegate =>
            Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_library, name));

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int ApiVersionDelegate();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int VulkanDeviceCountDelegate();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int VulkanDeviceNameDelegate(int index, [Out] byte[] buffer, int capacity);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int CreateDelegate(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string modelPath,
            int deviceIndex,
            out IntPtr context);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void DestroyDelegate(IntPtr context);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int EmbeddingDimensionsDelegate(IntPtr context);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int EmbedImageRgbDelegate(
            IntPtr context,
            byte[] pixels,
            int width,
            int height,
            int stride,
            [Out] float[] output,
            int dimensions);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int EmbedTextTokensDelegate(
            IntPtr context,
            int[] tokens,
            int tokenCount,
            [Out] float[] output,
            int dimensions);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate IntPtr LastErrorDelegate();
    }
}
