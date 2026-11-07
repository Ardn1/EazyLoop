# RNNoise for EazyLoop

The files in upstream/ are from Xiph.Org RNNoise v0.1.1 at commit
6cbfd53eb348a8d394e0757b4025c6ded34eb2b6, including the original model in
upstream/src/rnn_data.c. The upstream sources are unchanged. See COPYING
for their BSD 3-Clause terms.

Source: https://github.com/xiph/rnnoise/tree/v0.1.1

The small C wrapper exposes the built-in model to EazyLoop. It keeps all
per-channel state in caller-owned memory so resetting a playback stream
does not allocate on the audio thread.

The portable build bundles win-x64/eazyloop-rnnoise.dll. To rebuild it,
install the Visual Studio Clang tools for Windows, open the x64 Native Tools Command Prompt and run
powershell -File ThirdParty/RNNoise/build.ps1 from the EazyLoop root.
The DLL links the C runtime statically.
