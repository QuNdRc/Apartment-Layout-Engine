// Minimal test: dynamic llama.cpp with Gemma 4
#include "llama.h"
#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

int main() {
    ggml_backend_load_all();
    llama_backend_init();

    const char * model_path = "ai_orchestrator/models/gemma-4-E2B-it-Q5_K_M.gguf";

    llama_model_params mparams = llama_model_default_params();
    llama_model * model = llama_model_load_from_file(model_path, mparams);
    if (!model) {
        fprintf(stderr, "FAIL: model load\n");
        return 1;
    }
    fprintf(stderr, "model loaded\n");

    llama_context_params cparams = llama_context_default_params();
    cparams.n_ctx = 8192;
    cparams.n_threads = 4;
    cparams.n_threads_batch = 4;

    llama_context * ctx = llama_init_from_model(model, cparams);
    if (!ctx) {
        fprintf(stderr, "FAIL: context init\n");
        return 1;
    }
    fprintf(stderr, "context created\n");

    const llama_vocab * vocab = llama_model_get_vocab(model);
    fprintf(stderr, "vocab OK\n"); fflush(stderr);

    std::string prompt_str = "Hello, how are you?";
    int n_prompt = -llama_tokenize(vocab, prompt_str.c_str(), (int32_t)prompt_str.size(), nullptr, 0, true, true);
    fprintf(stderr, "n_prompt = %d\n", n_prompt); fflush(stderr);

    std::vector<llama_token> prompt_tokens(n_prompt);
    llama_tokenize(vocab, prompt_str.c_str(), (int32_t)prompt_str.size(), prompt_tokens.data(), n_prompt, true, true);
    fprintf(stderr, "tokenized OK\n"); fflush(stderr);

    // Simple batch using llama_batch_get_one
    llama_batch batch = llama_batch_get_one(prompt_tokens.data(), (int32_t)n_prompt);

    fprintf(stderr, "calling llama_decode for prompt...\n"); fflush(stderr);
    if (llama_decode(ctx, batch) != 0) {
        fprintf(stderr, "FAIL: prompt decode\n");
        return 1;
    }
    fprintf(stderr, "prompt decoded OK\n"); fflush(stderr);

    // Sampler
    auto sparams = llama_sampler_chain_default_params();
    llama_sampler * smpl = llama_sampler_chain_init(sparams);
    llama_sampler_chain_add(smpl, llama_sampler_init_greedy());

    // Generate 8 tokens
    for (int i = 0; i < 8; i++) {
        llama_token tok = llama_sampler_sample(smpl, ctx, -1);
        if (llama_vocab_is_eog(vocab, tok)) break;

        char buf[128];
        int n = llama_token_to_piece(vocab, tok, buf, sizeof(buf), 0, true);
        fprintf(stderr, "token %d: '%.*s'\n", i, n, buf);

        llama_sampler_accept(smpl, tok);
        batch = llama_batch_get_one(&tok, 1);
        if (llama_decode(ctx, batch) != 0) {
            fprintf(stderr, "FAIL: gen decode at step %d\n", i);
            return 1;
        }
    }

    fprintf(stderr, "SUCCESS\n");
    llama_sampler_free(smpl);
    llama_free(ctx);
    llama_model_free(model);
    return 0;
}