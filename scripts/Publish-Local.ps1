param(
    [string] $DotNet,
    [switch] $UpdateDesktopShortcut
)

$ErrorActionPreference = 'Stop'
if (!$DotNet) {
    $localSdk = Join-Path $env:LOCALAPPDATA 'LoopDeckBuild\dotnet\dotnet.exe'
    $DotNet = if (Test-Path -LiteralPath $localSdk -PathType Leaf) { $localSdk } else { 'dotnet' }
}
$root = Split-Path -Parent $PSScriptRoot
$worktrees = @(git -C $root worktree list --porcelain)
if ($LASTEXITCODE -ne 0 -or !$worktrees -or $worktrees[0] -notlike 'worktree *') {
    throw 'Could not find the primary EazyLoop worktree.'
}
$primaryRoot = [IO.Path]::GetFullPath($worktrees[0].Substring('worktree '.Length))
if ($primaryRoot -ne $root) {
    throw "Publish from the primary EazyLoop worktree at $primaryRoot."
}

$dist = [IO.Directory]::CreateDirectory((Join-Path $root 'dist')).FullName
$lockPath = Join-Path $dist '.release.lock'
try {
    $releaseLock = [IO.File]::Open($lockPath, 'OpenOrCreate', 'ReadWrite', 'None')
}
catch [IO.IOException] {
    throw "Could not acquire the release lock at $lockPath. Check whether another release is running."
}

$staging = $null
$pendingProject = $null
try {
    $project = Join-Path $root 'EazyLoop.csproj'
    $projectText = [IO.File]::ReadAllText($project)
    $versionPattern = '<Version>(\d+\.\d+\.\d+)-beta</Version>'
    $versionMatch = [regex]::Match($projectText, $versionPattern)
    if (!$versionMatch.Success) { throw 'Expected a three-part beta version in EazyLoop.csproj.' }
    $latest = [version]$versionMatch.Groups[1].Value
    $tags = @(git -C $root tag --list 'v*')
    if ($LASTEXITCODE -ne 0) { throw 'Could not read local release tags.' }
    $buildNames = @(Get-ChildItem -LiteralPath $dist -Directory | Select-Object -ExpandProperty Name)
    foreach ($name in ($tags + $buildNames)) {
        if ($name -match '^(?:v|github-)?(\d+\.\d+\.\d+)(?:-.+)?$') {
            $existing = [version]$Matches[1]
            if ($existing -gt $latest) { $latest = $existing }
        }
    }

    $next = "$($latest.Major).$($latest.Minor).$($latest.Build + 1)"
    $beta = "$next-beta"
    $output = [IO.Path]::GetFullPath((Join-Path $dist $next))
    $staging = [IO.Path]::GetFullPath((Join-Path $dist ".pending-$next-$([guid]::NewGuid().ToString('N'))"))
    if ([IO.Path]::GetDirectoryName($output) -ne $dist -or [IO.Path]::GetDirectoryName($staging) -ne $dist) {
        throw 'Release paths must be direct children of dist.'
    }
    $publishArgs = @(
        'publish', $project, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
        '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:NuGetAudit=false',
        "-p:Version=$beta", '-o', $staging
    )
    Write-Host "Building EazyLoop $beta"
    Push-Location -LiteralPath $root
    try {
        & $DotNet @publishArgs
        if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE." }
    }
    finally { Pop-Location }

    $exe = Get-Item -LiteralPath (Join-Path $staging 'EazyLoop.exe')
    if (($exe.VersionInfo.ProductVersion -split '\+', 2)[0] -ne $beta) {
        throw "The executable does not report version $beta."
    }
    if ([IO.File]::ReadAllText($project) -ne $projectText) {
        throw 'EazyLoop.csproj changed during the release. Run the release again after merging.'
    }

    $pendingProject = "$staging.csproj.tmp"
    $nextProjectText = $projectText.Remove($versionMatch.Index, $versionMatch.Length).Insert(
        $versionMatch.Index, "<Version>$beta</Version>")
    [IO.File]::WriteAllText($pendingProject, $nextProjectText)
    [IO.Directory]::Move($staging, $output)
    try {
        [IO.File]::Replace($pendingProject, $project, [NullString]::Value)
    }
    catch {
        [IO.Directory]::Move($output, $staging)
        throw
    }

    $target = Join-Path $output 'EazyLoop.exe'
    if ($UpdateDesktopShortcut) {
        $desktop = [Environment]::GetFolderPath('DesktopDirectory')
        $shortcutPath = Join-Path $desktop 'EazyLoop.lnk'
        $shell = New-Object -ComObject WScript.Shell
        $shortcut = $shell.CreateShortcut($shortcutPath)
        if ($shortcut.TargetPath -and !$shortcut.TargetPath.StartsWith(
                "$dist$([IO.Path]::DirectorySeparatorChar)", [StringComparison]::OrdinalIgnoreCase)) {
            Write-Warning "The existing shortcut points outside this project's dist folder: $shortcutPath"
        }
        else {
            $shortcut.TargetPath = $target
            $shortcut.WorkingDirectory = $output
            $shortcut.IconLocation = "$target,0"
            $shortcut.Description = "EazyLoop $next"
            $shortcut.Save()
        }
    }
    Write-Host "EazyLoop $beta published to $output"
}
finally {
    try {
        if ($pendingProject -and (Test-Path -LiteralPath $pendingProject -PathType Leaf)) {
            Remove-Item -LiteralPath $pendingProject -Force
        }
        if ($staging -and (Test-Path -LiteralPath $staging -PathType Container)) {
            $resolvedStage = (Resolve-Path -LiteralPath $staging).ProviderPath
            if ([IO.Path]::GetDirectoryName($resolvedStage) -ne $dist) {
                throw "Refusing to remove a staging path outside dist: $resolvedStage"
            }
            Remove-Item -LiteralPath $resolvedStage -Recurse -Force
        }
    }
    finally { $releaseLock.Dispose() }
}
