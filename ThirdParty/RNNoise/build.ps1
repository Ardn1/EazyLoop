$ErrorActionPreference = 'Stop'
if ($env:VSCMD_ARG_TGT_ARCH -ne 'x64') {
    throw 'Run from an x64 Native Tools Command Prompt for Visual Studio.'
}

$compiler = Join-Path $env:VSINSTALLDIR 'VC\Tools\Llvm\x64\bin\clang-cl.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'Install the Visual Studio Clang tools for Windows.' }

$source = Join-Path $PSScriptRoot 'upstream\src'
$output = Join-Path $PSScriptRoot 'win-x64'
$intermediate = Join-Path $PSScriptRoot '..\..\obj\rnnoise-win-x64'
New-Item -ItemType Directory -Force -Path $output,$intermediate | Out-Null
$inputs = 'rnn.c','rnn_data.c','pitch.c','kiss_fft.c','celt_lpc.c' | ForEach-Object { Join-Path $source $_ }
$include = Join-Path $PSScriptRoot 'upstream\include'
$dll = Join-Path $output 'eazyloop-rnnoise.dll'
$wrapper = Join-Path $PSScriptRoot 'eazyloop_rnnoise.c'

Push-Location $intermediate
try {
    & $compiler /nologo /LD /TC /std:c11 /O2 /MT /D_USE_MATH_DEFINES /I $source /I $include $wrapper @inputs /link /Brepro /OUT:$dll
    if ($LASTEXITCODE -ne 0) { throw 'Could not build RNNoise.' }
}
finally { Pop-Location }
