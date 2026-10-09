using System.Runtime.InteropServices;
using Kakitome.Application.Asr;

namespace Kakitome.Infrastructure.Asr;

/// <summary>A GPU as Vulkan reports it (the API whisper.cpp and llama.cpp use for GPU work).</summary>
/// <param name="Index">Position in Vulkan's device enumeration (what <c>GGML_VK_VISIBLE_DEVICES</c> refers to).</param>
/// <param name="MemoryBytes">Largest device-local memory heap (dedicated video memory).</param>
public sealed record GpuDevice(int Index, string Name, GpuDeviceType Type, long MemoryBytes);

/// <summary>Vulkan physical device types.</summary>
public enum GpuDeviceType
{
    Other = 0,
    Integrated = 1,
    Discrete = 2,
    Virtual = 3,
    Cpu = 4,
}

/// <summary>
/// Finds the GPU worth using for local models by asking Vulkan — the API the bundled whisper.cpp/llama.cpp runtimes
/// use — so the answer matches what they can actually run on: only adapters that are present and have a Vulkan driver
/// count. A discrete GPU with at least <see cref="MinimumGpuMemoryBytes"/> is "capable"; integrated graphics are not
/// assumed to be faster than the CPU (not benchmarked; ADR-022, ADR-035). The chosen GPU is pinned for the native
/// runtimes through <c>GGML_VK_VISIBLE_DEVICES</c>, so a laptop's integrated GPU is never picked by accident.
/// </summary>
public sealed partial class GpuAccelerationProbe : IAccelerationProbe
{
    /// <summary>Whisper large-v3-turbo needs about 1 GB on the GPU; 2 GB leaves room for the desktop.</summary>
    public const long MinimumGpuMemoryBytes = 2L * 1024 * 1024 * 1024;

    private const string VisibleDevicesVariable = "GGML_VK_VISIBLE_DEVICES";

    private readonly Lazy<IReadOnlyList<GpuDevice>> _devices = new(Enumerate);
    private readonly Lazy<GpuDevice?> _chosen;

    public GpuAccelerationProbe() =>
        _chosen = new Lazy<GpuDevice?>(() =>
        {
            var chosen = Choose(Devices);
            if (chosen is not null && Environment.GetEnvironmentVariable(VisibleDevicesVariable) is null)
            {
                // Read by ggml-vulkan when whisper.cpp/llama.cpp first initialize the GPU (later in this process).
                Environment.SetEnvironmentVariable(VisibleDevicesVariable, chosen.Index.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            return chosen;
        });

    public IReadOnlyList<GpuDevice> Devices => _devices.Value;

    /// <summary>The GPU the local models use, or null when there is none worth using.</summary>
    public GpuDevice? Gpu => _chosen.Value;

    public bool HasCapableGpu => Gpu is not null;

    public long GpuMemoryBytes => Gpu?.MemoryBytes ?? 0;

    /// <summary>Physical memory as reported to the .NET runtime (no job-object limit applies to the app).</summary>
    public long TotalMemoryBytes => GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;

    /// <summary>The discrete GPU with the most memory, if it has enough.</summary>
    public static GpuDevice? Choose(IEnumerable<GpuDevice> devices) =>
        devices
            .Where(d => d.Type == GpuDeviceType.Discrete && d.MemoryBytes >= MinimumGpuMemoryBytes)
            .OrderByDescending(d => d.MemoryBytes)
            .ThenBy(d => d.Index)
            .FirstOrDefault();

    private static List<GpuDevice> Enumerate()
    {
        var result = new List<GpuDevice>();
        try
        {
            var appName = Marshal.StringToHGlobalAnsi("Kakitome");
            var appInfo = Marshal.AllocHGlobal(Marshal.SizeOf<VkApplicationInfo>());
            try
            {
                Marshal.StructureToPtr(new VkApplicationInfo { SType = 0, PApplicationName = appName, ApiVersion = 1u << 22 }, appInfo, false);
                var create = new VkInstanceCreateInfo { SType = 1, PApplicationInfo = appInfo };
                if (vkCreateInstance(ref create, IntPtr.Zero, out var instance) != 0 || instance == IntPtr.Zero)
                {
                    return result;
                }

                try
                {
                    uint count = 0;
                    vkEnumeratePhysicalDevices(instance, ref count, null);
                    var handles = new IntPtr[count];
                    vkEnumeratePhysicalDevices(instance, ref count, handles);
                    var buffer = Marshal.AllocHGlobal(4096);
                    try
                    {
                        for (var i = 0; i < count; i++)
                        {
                            Clear(buffer, 4096);
                            vkGetPhysicalDeviceProperties(handles[i], buffer);
                            var type = (GpuDeviceType)Marshal.ReadInt32(buffer, 16);
                            var name = Marshal.PtrToStringUTF8(buffer + 20) ?? string.Empty;

                            Clear(buffer, 4096);
                            vkGetPhysicalDeviceMemoryProperties(handles[i], buffer);
                            result.Add(new GpuDevice(i, name, type, DeviceLocalMemory(buffer)));
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(buffer);
                    }
                }
                finally
                {
                    vkDestroyInstance(instance, IntPtr.Zero);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(appInfo);
                Marshal.FreeHGlobal(appName);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or SEHException)
        {
            // No Vulkan loader/driver: the native runtimes cannot use a GPU either.
        }

        return result;
    }

    /// <summary>
    /// Largest heap with VK_MEMORY_HEAP_DEVICE_LOCAL_BIT in a VkPhysicalDeviceMemoryProperties: memoryTypeCount (4 bytes),
    /// 32 memory types (8 bytes each), memoryHeapCount at 260, then 16 heaps of {VkDeviceSize size; flags} from 264.
    /// </summary>
    internal static long DeviceLocalMemory(IntPtr properties)
    {
        var heaps = Math.Min(16, Marshal.ReadInt32(properties, 260));
        long largest = 0;
        for (var h = 0; h < heaps; h++)
        {
            var size = Marshal.ReadInt64(properties, 264 + (h * 16));
            var flags = Marshal.ReadInt32(properties, 264 + (h * 16) + 8);
            if ((flags & 1) != 0 && size > largest)
            {
                largest = size;
            }
        }

        return largest;
    }

    private static void Clear(IntPtr buffer, int length)
    {
        for (var i = 0; i < length; i += 8)
        {
            Marshal.WriteInt64(buffer, i, 0);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VkApplicationInfo
    {
        public int SType;
        public IntPtr PNext;
        public IntPtr PApplicationName;
        public uint ApplicationVersion;
        public IntPtr PEngineName;
        public uint EngineVersion;
        public uint ApiVersion;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VkInstanceCreateInfo
    {
        public int SType;
        public IntPtr PNext;
        public uint Flags;
        public IntPtr PApplicationInfo;
        public uint EnabledLayerCount;
        public IntPtr PpEnabledLayerNames;
        public uint EnabledExtensionCount;
        public IntPtr PpEnabledExtensionNames;
    }

    [LibraryImport("vulkan-1.dll")]
    private static partial int vkCreateInstance(ref VkInstanceCreateInfo createInfo, IntPtr allocator, out IntPtr instance);

    [LibraryImport("vulkan-1.dll")]
    private static partial void vkDestroyInstance(IntPtr instance, IntPtr allocator);

    [LibraryImport("vulkan-1.dll")]
    private static partial int vkEnumeratePhysicalDevices(IntPtr instance, ref uint count, [Out] IntPtr[]? devices);

    [LibraryImport("vulkan-1.dll")]
    private static partial void vkGetPhysicalDeviceProperties(IntPtr device, IntPtr properties);

    [LibraryImport("vulkan-1.dll")]
    private static partial void vkGetPhysicalDeviceMemoryProperties(IntPtr device, IntPtr properties);
}
