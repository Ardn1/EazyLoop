#include "denoise.c"

typedef struct {
    DenoiseState denoiser;
    float voice_activity[MAX_NEURONS];
    float noise[MAX_NEURONS];
    float speech[MAX_NEURONS];
} EazyLoopDenoiser;

__declspec(dllexport) int eazyloop_rnnoise_state_size(int frame_size)
{
    if (frame_size != FRAME_SIZE) return 0;
    check_init();
    return common.kfft ? (int)sizeof(EazyLoopDenoiser) : 0;
}

__declspec(dllexport) void eazyloop_rnnoise_process(EazyLoopDenoiser *state, float *output, const float *input)
{
    // The caller pins the state for each call. Rebind its internal pointers after a GC move.
    state->denoiser.rnn.model = &rnnoise_model_orig;
    state->denoiser.rnn.vad_gru_state = state->voice_activity;
    state->denoiser.rnn.noise_gru_state = state->noise;
    state->denoiser.rnn.denoise_gru_state = state->speech;
    rnnoise_process_frame(&state->denoiser, output, input);
}
