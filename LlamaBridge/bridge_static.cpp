// Bridge (static) — llama.cpp built from source (b10063) and linked statically.
//
// Same LB_* API as the dynamic loader variant, but calls llama.cpp directly (no
// GetProcAddress) and links llama.lib + ggml*.lib. Because everything shares the
// same CRT / compiler (MSVC 19.51), exceptions thrown inside llama.cpp are caught
// here and reported instead of crashing the process, and the CPU backend is
// registered at build time (no dynamic backend loading needed).
//
// Exports:
//   int         LB_Init()
//   void*       LB_LoadModel(model_path, n_ctx, n_threads)
//   int         LB_AddAdapter(handle, lora_path)
//   int         LB_SelectAdapter(handle, index)
//   int         LB_Generate(handle, prompt, out, out_cap, n_predict, temperature, seed)
//   void        LB_Free(handle)
//   const char* LB_LastError(void)

#include "llama.h"

#include <cstdio>
#include <cstring>
#include <stdexcept>
#include <string>
#include <vector>

// ---------------------------------------------------------------------------
// Error reporting (thread-local, no crossing C ABI).
// ---------------------------------------------------------------------------
static thread_local std::string g_last_error;

static void set_error(const std::string & msg)
{
    g_last_error = msg;
}

// ---------------------------------------------------------------------------
// Dummy log callback — silences the massive LoRA-weight log spam that
// otherwise overflows the stack with deep vsnprintf / callback chains.
// ---------------------------------------------------------------------------
static void dummy_log_callback(ggml_log_level /*level*/, const char * /*text*/, void * /*user_data*/)
{
    // no-op
}

// ---------------------------------------------------------------------------
// State
// ---------------------------------------------------------------------------
struct LBHandle
{
    llama_model * model = nullptr;
    llama_context * ctx = nullptr;
    std::vector<llama_adapter_lora *> adapters;
};

// ---------------------------------------------------------------------------
// Public API
// ---------------------------------------------------------------------------
extern "C" {

__declspec(dllexport) int LB_Init()
{
    try
    {
        ggml_backend_load_all(); // load CPU backend dynamically
        llama_backend_init();
        return 0;
    }
    catch (const std::exception & e)
    {
        set_error(std::string("LB_Init exception: ") + e.what());
        return -1;
    }
    catch (...)
    {
        set_error("LB_Init unknown exception");
        return -1;
    }
}

__declspec(dllexport) void * LB_LoadModel(const char * model_path, int n_ctx, int n_threads)
{
    try
    {
        if (!model_path || !*model_path)
        {
            set_error("LB_LoadModel: empty model path");
            return nullptr;
        }

        llama_model_params mparams = llama_model_default_params();
        llama_model * model = llama_model_load_from_file(model_path, mparams);
        if (!model)
        {
            set_error("LB_LoadModel: llama_model_load_from_file returned null");
            return nullptr;
        }

        llama_context_params cparams = llama_context_default_params();
        if (n_ctx > 0)
            cparams.n_ctx = (uint32_t)n_ctx;
        if (n_threads > 0)
        {
            cparams.n_threads = n_threads;
            cparams.n_threads_batch = n_threads;
        }

        llama_context * ctx = llama_init_from_model(model, cparams);
        if (!ctx)
        {
            llama_model_free(model);
            set_error("LB_LoadModel: llama_init_from_model returned null");
            return nullptr;
        }

        if (n_threads > 0)
            llama_set_n_threads(ctx, n_threads, n_threads);

        LBHandle * h = new LBHandle();
        h->model = model;
        h->ctx = ctx;
        return h;
    }
    catch (const std::exception & e)
    {
        set_error(std::string("LB_LoadModel exception: ") + e.what());
        return nullptr;
    }
    catch (...)
    {
        set_error("LB_LoadModel unknown exception");
        return nullptr;
    }
}

__declspec(dllexport) int LB_AddAdapter(void * handle, const char * lora_path)
{
    try
    {
        LBHandle * h = static_cast<LBHandle *>(handle);
        if (!h || !h->model || !lora_path || !*lora_path)
        {
            set_error("LB_AddAdapter: invalid handle or empty path");
            return -1;
        }
        llama_adapter_lora * adapter = llama_adapter_lora_init(h->model, lora_path);
        if (!adapter)
        {
            set_error("LB_AddAdapter: llama_adapter_lora_init failed for: " + std::string(lora_path));
            return -1;
        }
        h->adapters.push_back(adapter);
        return (int)(h->adapters.size() - 1);
    }
    catch (const std::exception & e)
    {
        set_error(std::string("LB_AddAdapter exception: ") + e.what());
        return -1;
    }
    catch (...)
    {
        set_error("LB_AddAdapter unknown exception");
        return -1;
    }
}

__declspec(dllexport) int LB_SelectAdapter(void * handle, int index)
{
    try
    {
        LBHandle * h = static_cast<LBHandle *>(handle);
        if (!h || !h->ctx)
        {
            set_error("LB_SelectAdapter: invalid handle");
            return -1;
        }
        if (index < 0 || index >= (int)h->adapters.size())
        {
            set_error("LB_SelectAdapter: adapter index out of range");
            return -1;
        }
        llama_adapter_lora * arr[1] = { h->adapters[(size_t)index] };
        float scales[1] = { 1.0f };
        if (llama_set_adapters_lora(h->ctx, arr, 1, scales) != 0)
        {
            set_error("LB_SelectAdapter: llama_set_adapters_lora failed");
            return -1;
        }
        return 0;
    }
    catch (const std::exception & e)
    {
        set_error(std::string("LB_SelectAdapter exception: ") + e.what());
        return -1;
    }
    catch (...)
    {
        set_error("LB_SelectAdapter unknown exception");
        return -1;
    }
}

__declspec(dllexport) int LB_Generate(void * handle, const char * prompt,
                                      char * out, int out_cap,
                                      int n_predict, float temperature, int seed)
{
    try
    {
        LBHandle * h = static_cast<LBHandle *>(handle);
        if (!h || !h->model || !prompt || !out || out_cap <= 0)
        {
            set_error("LB_Generate: invalid arguments");
            return -1;
        }

        const llama_vocab * vocab = llama_model_get_vocab(h->model);
        if (!vocab)
        {
            set_error("LB_Generate: cannot get vocab");
            llama_log_set(nullptr, nullptr);
            return -1;
        }

        // --- Tokenize prompt (add_special=true for Gemma) --------------------
        const int32_t prompt_len = (int32_t)strlen(prompt);
        int n_prompt = -llama_tokenize(vocab, prompt, prompt_len, nullptr, 0, true, true);
        if (n_prompt <= 0)
        {
            set_error("LB_Generate: failed to tokenize prompt (size check)");
            llama_log_set(nullptr, nullptr);
            return -1;
        }
        std::vector<llama_token> prompt_tokens(n_prompt);
        if (llama_tokenize(vocab, prompt, prompt_len, prompt_tokens.data(), n_prompt, true, true) < 0)
        {
            set_error("LB_Generate: failed to tokenize prompt");
            llama_log_set(nullptr, nullptr);
            return -1;
        }

        if (n_prompt > 0 && prompt_tokens[0] < 0) {
            set_error("LB_Generate: tokenization returned invalid token id");
            llama_log_set(nullptr, nullptr);
            return -1;
        }

        // --- Use the handle's context (created in LB_LoadModel) ---------------
        llama_context * ctx = h->ctx;
        if (!ctx)
        {
            set_error("LB_Generate: no context (handle not initialized)");
            llama_log_set(nullptr, nullptr);
            return -1;
        }

        // --- Build sampler chain (greedy) -------------------------------------
        auto sparams = llama_sampler_chain_default_params();
        llama_sampler * smpl = llama_sampler_chain_init(sparams);
        llama_sampler_chain_add(smpl, llama_sampler_init_greedy());

        // --- Token piece buffer (heap, safe) ----------------------------------
        std::vector<char> piece_buf(256);

        // --- Prepare prompt batch (same as simple.cpp) ------------------------
        llama_batch batch = llama_batch_get_one(prompt_tokens.data(), (int32_t)n_prompt);

        // --- Main loop (same as simple.cpp) -----------------------------------
        int written = 0;
        int n_decode = 0;

        for (int n_pos = 0; n_pos + batch.n_tokens < n_prompt + n_predict; )
        {
            if (llama_decode(ctx, batch) != 0) {
                set_error("LB_Generate: llama_decode failed");
                llama_sampler_free(smpl);
                llama_log_set(nullptr, nullptr);
                return -1;
            }

            n_pos += batch.n_tokens;

            // Sample the next token
            llama_token new_token_id = llama_sampler_sample(smpl, ctx, -1);

            if (llama_vocab_is_eog(vocab, new_token_id)) {
                break;
            }

            int n_piece = llama_token_to_piece(vocab, new_token_id, piece_buf.data(), (int)piece_buf.size() - 1, 0, true);
            if (n_piece > (int)piece_buf.size() - 1) {
                piece_buf.resize(n_piece + 1);
                n_piece = llama_token_to_piece(vocab, new_token_id, piece_buf.data(), (int)piece_buf.size() - 1, 0, true);
            }
            if (n_piece < 0) n_piece = 0;

            if (n_piece > 0 && written + n_piece < out_cap) {
                memcpy(out + written, piece_buf.data(), (size_t)n_piece);
                written += n_piece;
            }

            llama_sampler_accept(smpl, new_token_id);

            // Prepare the next batch with the sampled token
            batch = llama_batch_get_one(&new_token_id, 1);

            n_decode += 1;
        }

        out[written] = '\0';
        llama_sampler_free(smpl);
        // NOTE: ctx belongs to the handle, do NOT free it here

        llama_log_set(nullptr, nullptr); // restore default logging
        return written;
    }
    catch (const std::exception & e)
    {
        llama_log_set(nullptr, nullptr);
        set_error(std::string("LB_Generate exception: ") + e.what());
        return -1;
    }
    catch (...)
    {
        llama_log_set(nullptr, nullptr);
        set_error("LB_Generate: unknown critical exception");
        return -1;
    }
}

__declspec(dllexport) void LB_Free(void * handle)
{
    LBHandle * h = static_cast<LBHandle *>(handle);
    if (!h)
        return;
    for (size_t i = 0; i < h->adapters.size(); ++i)
        if (h->adapters[i])
            llama_adapter_lora_free(h->adapters[i]);
    h->adapters.clear();
    if (h->ctx)
        llama_free(h->ctx);
    if (h->model)
        llama_model_free(h->model);
    delete h;
}

__declspec(dllexport) const char * LB_LastError(void)
{
    return g_last_error.c_str();
}

} // extern "C"