# Fetches Android (arm64-v8a / x86_64) git binaries + runtime libraries from the
# Termux package repository and repacks them as lib*.so native libraries that can
# be embedded into the APK via AndroidNativeLibrary.
#
# Why lib*.so: Android 10+ forbids exec() of binaries inside the app data dir,
# so the only way to ship an executable is the APK's lib/<abi>/ directory
# (extracted to applicationInfo.nativeLibraryDir, which stays executable).
#
# Versioned sonames (libfoo.so.1) are renamed to libfoo.so and every ELF string
# table (DT_NEEDED / DT_SONAME) is patched in place so all references match the
# new names. Length-preserving NUL padding keeps offsets intact.
#
# Outputs:
#   src/MultiGitCommit.Avalonia/native-git/android/<abi>/*.so
#   src/MultiGitCommit.Avalonia/Platforms/Android/Resources/raw/ca_bundle.crt
#   (raw resource names cannot contain '-', hence ca_bundle)
#
# Termux git is GPL-2.0-or-later; sources: https://github.com/termux/termux-packages

param(
    [string]$RepoRoot = (Join-Path $PSScriptRoot "..")
)

$ErrorActionPreference = "Stop"
$mirror = "https://packages.termux.dev/apt/termux-main"
$work = Join-Path $RepoRoot ".tmp-android-git"
$outRoot = Join-Path $RepoRoot "src/MultiGitCommit.Avalonia/native-git/android"
$rawRes = Join-Path $RepoRoot "src/MultiGitCommit.Avalonia/Platforms/Android/Resources/raw"
New-Item -ItemType Directory -Force $work, $outRoot, $rawRes | Out-Null

$archs = @{
    "aarch64" = "arm64-v8a"
    "x86_64"  = "x86_64"
}

# ---------- 1. download + parse package indexes ----------

function Get-PkgIndex([string]$arch) {
    $gz = Join-Path $work "Packages-$arch.gz"
    $txt = Join-Path $work "Packages-$arch"
    if (-not (Test-Path $txt)) {
        Invoke-WebRequest -Uri "$mirror/dists/stable/main/binary-$arch/Packages.gz" `
            -OutFile $gz -UseBasicParsing
        $in = [IO.File]::OpenRead($gz)
        $out = [IO.File]::Create($txt)
        $gz2 = New-Object IO.Compression.GzipStream($in, [IO.Compression.CompressionMode]::Decompress)
        $gz2.CopyTo($out); $gz2.Dispose(); $out.Dispose(); $in.Dispose()
    }
    $pkgs = @{}
    foreach ($block in ([IO.File]::ReadAllText($txt) -split "`n`n")) {
        $p = $null
        foreach ($line in ($block -split "`n")) {
            if ($line -match '^Package: (.+)$')      { $p = @{ Name = $Matches[1].Trim() } }
            elseif ($p -ne $null -and $line -match '^Filename: (.+)$')   { $p.Filename = $Matches[1].Trim() }
            elseif ($p -ne $null -and $line -match '^Depends: (.+)$')    { $p.Depends = $Matches[1].Trim() }
        }
        if ($p -and $p.Filename) { $pkgs[$p.Name] = $p }
    }
    return $pkgs
}

# ---------- 2. resolve dependency closure ----------

function Resolve-Closure([hashtable]$pkgs, [string[]]$roots) {
    $result = @{}
    $queue = [System.Collections.Generic.Queue[string]]::new()
    foreach ($r in $roots) { $queue.Enqueue($r) }
    while ($queue.Count -gt 0) {
        $name = $queue.Dequeue()
        if ($result.ContainsKey($name) -or -not $pkgs.ContainsKey($name)) { continue }
        $result[$name] = $pkgs[$name]
        if ($pkgs[$name].Depends) {
            foreach ($dep in ($pkgs[$name].Depends -split ',')) {
                $depName = ($dep -split '\(')[0].Trim()
                if ($depName -and -not $result.ContainsKey($depName) -and $pkgs.ContainsKey($depName)) {
                    $queue.Enqueue($depName)
                }
            }
        }
    }
    return $result
}

# ---------- 3. download deb + extract (ar container -> data.tar.* -> tar) ----------

function Extract-Deb([string]$debPath, [string]$outDir) {
    New-Item -ItemType Directory -Force $outDir | Out-Null
    $bytes = [IO.File]::ReadAllBytes($debPath)
    if ([Text.Encoding]::ASCII.GetString($bytes, 0, 8) -ne "!<arch>`n") {
        throw "not an ar archive: $debPath"
    }
    $pos = 8
    while ($pos + 60 -le $bytes.Length) {
        $name = [Text.Encoding]::ASCII.GetString($bytes, $pos, 16).Trim().TrimEnd('/')
        $size = [int][Text.Encoding]::ASCII.GetString($bytes, $pos + 48, 10).Trim()
        $dataStart = $pos + 60
        if ($name -like 'data.tar.*') {
            [IO.File]::WriteAllBytes((Join-Path $outDir $name), $bytes[$dataStart..($dataStart + $size - 1)])
        }
        $pos = $dataStart + $size
        if ($size % 2 -eq 1) { $pos++ }
    }
    $tar = Get-ChildItem $outDir -Filter 'data.tar.*' | Select-Object -First 1
    if (-not $tar) { throw "no data.tar.* inside $debPath" }
    & tar -xf $tar.FullName -C $outDir 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "tar failed on $($tar.FullName) (exit $LASTEXITCODE)" }
}

# ---------- 4. per-arch copy + rename ----------

# Only git-remote-http is a distinct binary; upload-pack / receive-pack /
# upload-archive are argv0-dispatch copies of the main git binary (identical
# hashes), so no copy is bundled - the runtime symlinks them to libgit.so.
$helpers = @{
    "git-remote-http" = "libgitremotehttp.so"
}

# final ABI dir -> { origFileName -> newFileName } (for DT_NEEDED patching)
$renameMaps = @{}
$abiUsr = @{}

foreach ($arch in $archs.Keys) {
    $abi = $archs[$arch]
    Write-Host "== $arch -> $abi =="

    $pkgs = Get-PkgIndex $arch
    $closure = Resolve-Closure $pkgs @("git", "ca-certificates")
    Write-Host ("packages: " + ($closure.Keys -join ", "))

    $extractRoot = Join-Path $work "extract-$arch"
    foreach ($name in ($closure.Keys | Sort-Object)) {
        $deb = Join-Path $work ("{0}_{1}.deb" -f $name, $arch)
        $url = "$mirror/$($closure[$name].Filename)"
        if (-not (Test-Path $deb)) { Invoke-WebRequest -Uri $url -OutFile $deb -UseBasicParsing }
        Extract-Deb $deb (Join-Path $extractRoot $name)
    }

    $usrs = Get-ChildItem $extractRoot -Recurse -Directory -Filter "usr"
    if (-not $usrs) { throw "usr/ not found under $extractRoot" }
    $abiUsr[$abi] = $usrs

    $outDir = Join-Path $outRoot $abi
    New-Item -ItemType Directory -Force $outDir | Out-Null
    $renameMap = @{}

    # main git binary
    $gitBin = $usrs | ForEach-Object { Join-Path $_.FullName "bin/git" } | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $gitBin) { throw "git binary not found under $extractRoot" }
    Copy-Item $gitBin (Join-Path $outDir "libgit.so") -Force

    # transport helpers (real binaries; https/ftp variants are argv0 aliases of git-remote-http)
    foreach ($h in $helpers.Keys) {
        $src = $usrs | ForEach-Object { Join-Path $_.FullName "libexec/git-core/$h" } |
            Where-Object { Test-Path $_ } | Select-Object -First 1
        if ($src) { Copy-Item $src (Join-Path $outDir $helpers[$h]) -Force }
        else { Write-Warning "helper not found: $h" }
    }

    # runtime libraries (from every package); strip version suffix libfoo.so.N -> libfoo.so
    foreach ($lib in ($usrs | ForEach-Object { Join-Path $_.FullName "lib" } |
        Where-Object { Test-Path $_ } | ForEach-Object { Get-ChildItem $_ -File })) {
        if ($lib.Name -match '^(lib.+\.so)\.[0-9]') {
            $newName = $Matches[1]
            Copy-Item $lib.FullName (Join-Path $outDir $newName) -Force
            $renameMap[$lib.Name] = $newName
        }
        elseif ($lib.Name -like '*.so') {
            Copy-Item $lib.FullName (Join-Path $outDir $lib.Name) -Force
        }
    }

    # CA bundle (once, from the first arch)
    if ($arch -eq "aarch64") {
        $cert = $usrs | ForEach-Object { Join-Path $_.FullName "etc/tls/cert.pem" } |
            Where-Object { Test-Path $_ } | Select-Object -First 1
        if ($cert) { Copy-Item $cert (Join-Path $rawRes "ca_bundle.crt") -Force }
    }

    $renameMaps[$abi] = $renameMap
    Write-Host ("copied: " + ((Get-ChildItem $outDir).Count) + " files")
}

# ---------- 5. prune unreferenced libraries ----------

$latin1 = [Text.Encoding]::GetEncoding(28591)

foreach ($abi in $archs.Values) {
    $outDir = Join-Path $outRoot $abi
    $bins = @("libgit.so", "libgitremotehttp.so")

    $libNames = @(Get-ChildItem $outDir -Filter '*.so' | Select-Object -ExpandProperty Name)
    $contents = @{}
    foreach ($n in $libNames) {
        $contents[$n] = $latin1.GetString([IO.File]::ReadAllBytes((Join-Path $outDir $n)))
    }

    $needed = [System.Collections.Generic.HashSet[string]]::new()
    $queue = [System.Collections.Generic.Queue[string]]::new()
    foreach ($b in $bins) { $queue.Enqueue($b); [void]$needed.Add($b) }
    while ($queue.Count -gt 0) {
        $cur = $queue.Dequeue()
        if (-not $contents.ContainsKey($cur)) { continue }
        foreach ($m in [regex]::Matches($contents[$cur], 'lib[A-Za-z0-9_+.-]+\.so(?:\.[0-9][A-Za-z0-9_.]*)?(?=\0)')) {
            $ref = ($m.Value -replace '\.so.*$', '.so')   # versioned soname -> bare file name
            if (-not $needed.Contains($ref) -and $contents.ContainsKey($ref)) {
                [void]$needed.Add($ref)
                $queue.Enqueue($ref)
            }
        }
    }

    foreach ($n in $libNames) {
        if (-not $needed.Contains($n)) {
            Remove-Item (Join-Path $outDir $n) -Force
            Write-Host "$abi : pruned unreferenced $n"
        }
    }
}

# ---------- 6. patch ELF string tables (versioned sonames -> bare sonames) ----------

foreach ($abi in $renameMaps.Keys) {
    $outDir = Join-Path $outRoot $abi
    # longest-first so overlapping names (libssl.so.3 vs libssl.so.3x) are safe
    $patterns = @($renameMaps[$abi].GetEnumerator() |
        Where-Object { $_.Key -ne "git" } |
        Sort-Object { $_.Key.Length } -Descending)

    foreach ($f in (Get-ChildItem $outDir -Filter '*.so')) {
        $s = $latin1.GetString([IO.File]::ReadAllBytes($f.FullName))
        foreach ($pat in $patterns) {
            $old = $pat.Key; $new = $pat.Value
            if ($old -eq $new) { continue }
            $pad = [string]::new("`0", $old.Length - $new.Length)
            $s = $s.Replace("$old`0", "$new$pad`0")
        }
        [IO.File]::WriteAllBytes($f.FullName, $latin1.GetBytes($s))
    }

    # verify: no versioned lib sonames left
    $left = @()
    foreach ($f in (Get-ChildItem $outDir -Filter '*.so')) {
        $s = $latin1.GetString([IO.File]::ReadAllBytes($f.FullName))
        foreach ($m in [regex]::Matches($s, '[A-Za-z0-9_+.-]+\.so\.[0-9][A-Za-z0-9_.]*')) {
            $left += "$($f.Name): $($m.Value)"
        }
    }
    $left = $left | Sort-Object -Unique
    if ($left.Count -gt 0) {
        Write-Warning "unresolved versioned sonames remain in $abi :"
        $left | ForEach-Object { Write-Warning "  $_" }
    } else {
        Write-Host "$abi : ELF string tables clean"
    }
}

Write-Host "done."
