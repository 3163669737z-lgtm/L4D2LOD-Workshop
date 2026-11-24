param(
    [string]$Compiler = 'g++',
    [string]$SourceArchive,
    [string]$OutputPath = (Join-Path $PSScriptRoot '../src/Assets/gltfpack.exe')
)

$ErrorActionPreference = 'Stop'
$version = '1.3'
$expectedSha256 = 'CA4F47D451201593904B9E3067FA54B65DB94BC34AB5173864950862B748A145'
$scratch = Join-Path ([System.IO.Path]::GetTempPath()) ('lodstudio-gltfpack-' + [Guid]::NewGuid().ToString('N'))
[void](New-Item -ItemType Directory -Path $scratch)

$archive = $SourceArchive
if (-not $archive) {
    $archive = Join-Path $scratch 'meshoptimizer.tar.gz'
    $url = "https://codeload.github.com/zeux/meshoptimizer/tar.gz/refs/tags/v$version"
    curl.exe --fail --location --silent --show-error --retry 2 --max-time 120 $url --output $archive
    if ($LASTEXITCODE -ne 0) { throw 'meshoptimizer download failed.' }
}
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $expectedSha256) {
    throw 'The meshoptimizer source archive failed SHA-256 verification.'
}

tar.exe -xzf $archive -C $scratch
if ($LASTEXITCODE -ne 0) { throw 'Could not unpack meshoptimizer.' }
$src = Join-Path $scratch "meshoptimizer-$version"
$build = Join-Path $scratch 'build'
$patch = Join-Path $PSScriptRoot 'gltfpack-lod.patch'
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'lod-appearance.h') -Destination (Join-Path $src 'gltf')
Push-Location -LiteralPath $src
try {
    git apply --whitespace=nowarn $patch
    if ($LASTEXITCODE -ne 0) { throw 'The LOD patch did not apply.' }
}
finally { Pop-Location }

$configure = @('-S', $src, '-B', $build, '-G', 'Ninja', '-DCMAKE_BUILD_TYPE=Release',
    '-DMESHOPT_BUILD_GLTFPACK=ON', '-DMESHOPT_INSTALL=OFF', "-DCMAKE_CXX_COMPILER=$Compiler",
    '-DCMAKE_EXE_LINKER_FLAGS=-static -static-libgcc -static-libstdc++ -Wl,--strip-all,--no-insert-timestamp')
cmake @configure
if ($LASTEXITCODE -ne 0) { throw 'Could not configure gltfpack.' }
cmake --build $build --target gltfpack --parallel 6
if ($LASTEXITCODE -ne 0) { throw 'Could not build gltfpack.' }

$engine = Join-Path $build 'gltfpack.exe'
Copy-Item -LiteralPath $engine -Destination $OutputPath -Force
Get-FileHash -LiteralPath $OutputPath -Algorithm SHA256
