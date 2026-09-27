using System;
using System.Runtime.InteropServices;
using System.Text;

namespace WpfApp1;

/// <summary>
/// P/Invoke wrapper for LlamaBridge.dll — direct C# binding to the native C++ bridge.
/// No Python, no ctypes, no HTTP. Just C# → C++ → llama.cpp.
/// </summary>
public static class LlamaBridge
{
    // -----------------------------------------------------------------------
    // DLL path — resolved from the llama/ subdirectory relative to the
    // orchestrator directory (ai_orchestrator/llama/LlamaBridge.dll).
    // Set by the orchestrator before any calls.
    // -----------------------------------------------------------------------
    private static string? _dllPath;

    public static void SetDllPath(string dllPath)
    {
        _dllPath = dllPath;
    }

    private static string DllPath =>
        _dllPath ?? throw new InvalidOperationException("LlamaBridge.SetDllPath() not called");

    // -----------------------------------------------------------------------
    // Function signatures (C ABI, __cdecl)
    // -----------------------------------------------------------------------

    [DllImport("__dummy__", EntryPoint = "LB_Init", CallingConvention = CallingConvention.Cdecl)]
    private static extern int _LB_Init();

    [DllImport("__dummy__", EntryPoint = "LB_LoadModel", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr _LB_LoadModel(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string modelPath,
        int nCtx,
        int nThreads);

    [DllImport("__dummy__", EntryPoint = "LB_AddAdapter", CallingConvention = CallingConvention.Cdecl)]
    private static extern int _LB_AddAdapter(
        IntPtr handle,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string loraPath);

    [DllImport("__dummy__", EntryPoint = "LB_SelectAdapter", CallingConvention = CallingConvention.Cdecl)]
    private static extern int _LB_SelectAdapter(IntPtr handle, int index);

    [DllImport("__dummy__", EntryPoint = "LB_Generate", CallingConvention = CallingConvention.Cdecl)]
    private static extern int _LB_Generate(
        IntPtr handle,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string prompt,
        byte[] outBuf,
        int outCap,
        int nPredict,
        float temperature,
        int seed);

    [DllImport("__dummy__", EntryPoint = "LB_Free", CallingConvention = CallingConvention.Cdecl)]
    private static extern void _LB_Free(IntPtr handle);

    [DllImport("__dummy__", EntryPoint = "LB_LastError", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr _LB_LastError();

    // -----------------------------------------------------------------------
    // NativeLibrary loading — we resolve symbols manually to avoid DLL hell
    // -----------------------------------------------------------------------
    private static IntPtr _nativeLib;
    private static bool _loaded;

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;

        if (!NativeLibrary.TryLoad(DllPath, out _nativeLib))
            throw new DllNotFoundException($"Failed to load {DllPath}");

        // Rebind all DllImport stubs to the actual library
        NativeLibrary.SetDllImportResolver(typeof(LlamaBridge).Assembly, (name, assembly, path) =>
        {
            if (name == "__dummy__") return _nativeLib;
            return IntPtr.Zero;
        });
    }

    // -----------------------------------------------------------------------
    // Public API
    // -----------------------------------------------------------------------

    /// <summary>Initialise llama.cpp backends. Call once at startup.</summary>
    public static void Init()
    {
        EnsureLoaded();
        var rc = _LB_Init();
        if (rc != 0)
            throw new InvalidOperationException($"LB_Init failed: {LastError()}");
    }

    /// <summary>Load model and create context. Returns an opaque handle.</summary>
    public static IntPtr LoadModel(string modelPath, int nCtx = 8192, int nThreads = 4)
    {
        EnsureLoaded();
        var handle = _LB_LoadModel(modelPath, nCtx, nThreads);
        if (handle == IntPtr.Zero)
            throw new InvalidOperationException($"LB_LoadModel failed: {LastError()}");
        return handle;
    }

    /// <summary>Load a LoRA adapter. Returns its index for SelectAdapter.</summary>
    public static int AddAdapter(IntPtr handle, string loraPath)
    {
        var idx = _LB_AddAdapter(handle, loraPath);
        if (idx < 0)
            throw new InvalidOperationException($"LB_AddAdapter failed: {LastError()}");
        return idx;
    }

    /// <summary>Select a loaded LoRA adapter by index.</summary>
    public static void SelectAdapter(IntPtr handle, int index)
    {
        var rc = _LB_SelectAdapter(handle, index);
        if (rc != 0)
            throw new InvalidOperationException($"LB_SelectAdapter failed: {LastError()}");
    }

    /// <summary>Generate text using the loaded model and optional LoRA adapter.</summary>
    public static string Generate(
        IntPtr handle,
        string prompt,
        int nPredict = 512,
        float temperature = 0.2f,
        int seed = 0)
    {
        var outBuf = new byte[1 << 20]; // 1 MB output buffer
        var n = _LB_Generate(handle, prompt, outBuf, outBuf.Length, nPredict, temperature, seed);
        if (n < 0)
            throw new InvalidOperationException($"LB_Generate failed: {LastError()}");
        return Encoding.UTF8.GetString(outBuf, 0, n);
    }

    /// <summary>Free the model and context.</summary>
    public static void Free(IntPtr handle)
    {
        if (handle != IntPtr.Zero)
            _LB_Free(handle);
    }

    /// <summary>Get the last error message from the bridge.</summary>
    public static string LastError()
    {
        var ptr = _LB_LastError();
        return ptr != IntPtr.Zero ? Marshal.PtrToStringAnsi(ptr) ?? "unknown" : "unknown";
    }
}