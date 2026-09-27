using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace WpfApp1;

/// <summary>
/// AI orchestrator — direct C# binding to LlamaBridge.dll.
/// No Python, no pythonnet, no HTTP. Just C# → C++ → llama.cpp.
/// Python is kept only for RAG (ChromaDB) — see PythonRag.cs.
/// </summary>
public static class PythonOrchestrator
{
    private static bool _initialized;
    private static IntPtr _bridgeHandle;
    private static readonly Dictionary<string, int> _adapters = new();
    private static string _defaultAdapter = "planner";

    private static string _orchestratorDir = "";

    // -----------------------------------------------------------------------
    // Initialization
    // -----------------------------------------------------------------------
    public static string Initialize(string orchestratorDir)
    {
        if (_initialized)
            return "Already initialised";
        _initialized = true;
        _orchestratorDir = orchestratorDir;

        // Resolve exe directory (for portable binary deployment)
        var exeDir = AppDomain.CurrentDomain.BaseDirectory;

        // 1. Locate LlamaBridge.dll and llama.dll
        var bridgeDll = FindFirst("LlamaBridge.dll", exeDir, Path.Combine(orchestratorDir, "llama"));
        var llamaDll = FindFirst("llama.dll", exeDir, Path.Combine(orchestratorDir, "llama"));
        if (bridgeDll == null) return "LlamaBridge.dll not found";
        if (llamaDll == null) return "llama.dll not found";

        // 2. Locate model GGUF (check exe dir, models/ subdir, orchestrator fallback)
        const string modelFileName = "gemma-4-E2B-it-Q5_K_M.gguf";
        var modelPath = FindFirst(modelFileName,
            exeDir,
            Path.Combine(exeDir, "models"),
            Path.Combine(orchestratorDir, "models"));
        if (modelPath == null) return $"Model {modelFileName} not found";

        // 3. Adapters (optional) — look in exeDir/adapters and orchestratorDir/adapters
        var adaptersDir = Directory.Exists(Path.Combine(exeDir, "adapters"))
            ? Path.Combine(exeDir, "adapters")
            : (Directory.Exists(Path.Combine(orchestratorDir, "adapters"))
                ? Path.Combine(orchestratorDir, "adapters")
                : null);

        // Add .dll directory to PATH so ggml_backend_load_all() finds CPU DLLs
        var currentPath = Environment.GetEnvironmentVariable("PATH") ?? "";
        var dllDir = Path.GetDirectoryName(llamaDll)!;
        if (!currentPath.Contains(dllDir))
            Environment.SetEnvironmentVariable("PATH", $"{dllDir};{currentPath}");

        try
        {
            // Init native bridge
            LlamaBridge.SetDllPath(bridgeDll);
            LlamaBridge.Init();

            // Load model
            _bridgeHandle = LlamaBridge.LoadModel(modelPath);

            // Load adapters
            if (Directory.Exists(adaptersDir))
            {
                foreach (var loraFile in Directory.GetFiles(adaptersDir, "*_lora.gguf").OrderBy(f => f))
                {
                    var name = Path.GetFileNameWithoutExtension(loraFile).Replace("_lora", "");
                    var idx = LlamaBridge.AddAdapter(_bridgeHandle, loraFile);
                    _adapters[name] = idx;
                }
            }

            // Select default adapter (planner) if available, else base model
            if (_adapters.ContainsKey(_defaultAdapter))
                LlamaBridge.SelectAdapter(_bridgeHandle, _adapters[_defaultAdapter]);

            return "OK";
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    // -----------------------------------------------------------------------
    // Plan — full pipeline: parse → prompt → LLM → parse [CMD:] → return
    // -----------------------------------------------------------------------
    public static async Task<string> PlanAsync(
        string stateString,
        float minX, float minY, float maxX, float maxY,
        string mode = "auto",
        int maxTokens = 512,
        string userMessage = "")
    {
        if (!_initialized)
            throw new InvalidOperationException("Not initialized. Call Initialize() first.");

        // Inference is CPU-bound and can take tens of seconds — run on threadpool.
        return await Task.Run(() =>
        {
            // 1. RAG context: search SNiP norms for relevant rules
            string snipContext = "";
            if (!string.IsNullOrWhiteSpace(userMessage))
                snipContext = SnipRagIndex.Search(userMessage, k: 3);
            else if (!string.IsNullOrWhiteSpace(stateString))
                snipContext = SnipRagIndex.Search(stateString, k: 2);

            // 2. Build prompt — with RAG context if available
            string fullPrompt;
            if (!string.IsNullOrWhiteSpace(userMessage))
            {
                // User asked a question — prepend RAG context so LLM can cite norms
                var snippet = !string.IsNullOrWhiteSpace(snipContext)
                    ? $"\nРелевантные строительные нормы (СНиП):\n{snipContext}\n"
                    : "";
                fullPrompt = $"{snippet}Запрос пользователя:\n{userMessage}";
            }
            else
            {
                // No user message → plan from state
                fullPrompt = $"Ты — ассистент по планировке квартир. " +
                    $"Ответь на русском языке.\n" +
                    $"\nСостояние квартиры:\n" +
                    $"{PromptBuilder.BuildUserPrompt(stateString, minX, minY, maxX, maxY, snipContext: snipContext)}";
            }

            // 3. Generate
            var rawOutput = LlamaBridge.Generate(_bridgeHandle, fullPrompt,
                nPredict: maxTokens, temperature: 0.7f);

            // 4. Return raw output — no adapter, no CMD parsing
            return rawOutput;
        });
    }

    // -----------------------------------------------------------------------
    // Cleanup
    // -----------------------------------------------------------------------
    public static void Shutdown()
    {
        if (_bridgeHandle != IntPtr.Zero)
        {
            LlamaBridge.Free(_bridgeHandle);
            _bridgeHandle = IntPtr.Zero;
        }
        _initialized = false;
    }

    // -------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------

    /// <summary>
    /// Searches candidates directories in order for the named file.
    /// Returns full path or null on miss.
    /// </summary>
    private static string? FindFirst(string fileName, params string?[] dirs)
    {
        foreach (var dir in dirs)
        {
            if (string.IsNullOrEmpty(dir)) continue;
            var full = Path.Combine(dir, fileName);
            if (File.Exists(full)) return full;
        }
        return null;
    }
}