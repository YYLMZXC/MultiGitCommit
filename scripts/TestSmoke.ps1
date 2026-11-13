<#
Smoke regression for MultiGitCommit desktop CLI / MCP surface.
No third-party dependencies and no changes to repository data.

    pwsh scripts/TestSmoke.ps1
    pwsh scripts/TestSmoke.ps1 -IncludeAndroid
    pwsh scripts/TestSmoke.ps1 -Configuration Release

Prerequisites: .NET 10 SDK and a usable git executable (git status / branch are exercised).

Note: CLI / MCP human-readable modes call ConsoleAttach, which may write to the parent
console instead of an inherited pipe. Always capture via Start-Process redirection
(stdout redirected => ConsoleAttach keeps the pipe) or a real stdio pipe.
#>
param(
    [string] $Configuration = 'Debug',
    [switch] $IncludeAndroid
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src/MultiGitCommit.Avalonia/MultiGitCommit.csproj'
$exe = Join-Path $root "src/MultiGitCommit.Avalonia/bin/$Configuration/net10.0/MultiGitCommit.exe"
$temp = Join-Path ([System.IO.Path]::GetTempPath()) ('mgc-smoke-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp | Out-Null
$pass = 0
$fail = 0

function Assert-True([bool] $condition, [string] $name) {
    if ($condition) {
        Write-Host "PASS  $name"
        $script:pass++
    } else {
        Write-Host "FAIL  $name"
        $script:fail++
    }
}

function Invoke-Cli([string[]] $arguments) {
    $out = Join-Path $script:temp 'cli.out'
    $err = Join-Path $script:temp 'cli.err'
    $quoted = $arguments | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }
    $p = Start-Process -FilePath $script:exe -ArgumentList $quoted -Wait -PassThru `
        -RedirectStandardOutput $out -RedirectStandardError $err
    $text = ''
    if (Test-Path $out) { $text += Get-Content $out -Raw }
    if (Test-Path $err) { $text += Get-Content $err -Raw }
    return [pscustomobject]@{ Code = $p.ExitCode; Text = $text }
}

function Invoke-McpStdio([string[]] $requests) {
    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $script:exe
    $psi.Arguments = '--stdio'
    $psi.UseShellExecute = $false
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $proc = [System.Diagnostics.Process]::Start($psi)
    foreach ($request in $requests) { $proc.StandardInput.WriteLine($request) }
    $proc.StandardInput.Close()
    $text = $proc.StandardOutput.ReadToEnd()
    $proc.WaitForExit()
    $proc.Dispose()
    return $text
}

try {
    Write-Host '== Build =='
    dotnet build $project -f net10.0 -v minimal | Out-Null
    Assert-True ($LASTEXITCODE -eq 0) 'build net10.0'
    Assert-True (Test-Path $exe) 'desktop exe exists'

    if ($IncludeAndroid) {
        dotnet build $project -f net10.0-android -v minimal | Out-Null
        Assert-True ($LASTEXITCODE -eq 0) 'build net10.0-android'
    }

    Write-Host '== CLI / MCP =='
    $r = Invoke-Cli @('--self-test')
    Assert-True ($r.Code -eq 0) 'MCP --self-test exits 0'
    Assert-True ($r.Text -match '\b14\b') 'MCP tool list still has 14 tools'

    $r = Invoke-Cli @('env')
    Assert-True ($r.Code -eq 0) 'CLI env exits 0'
    Assert-True ($r.Text -match '"ok"\s*:\s*true') 'CLI env returns ok=true'
    Assert-True ($r.Text -match '"git"') 'CLI env includes git snapshot'

    $r = Invoke-Cli @('status', $root)
    Assert-True ($r.Code -eq 0) 'CLI status exits 0'
    Assert-True ($r.Text -match '"repoRoot"') 'CLI status resolves repo root'

    $r = Invoke-Cli @('help')
    Assert-True ($r.Code -eq 0 -and $r.Text -match 'CLI') 'CLI help prints usage'

    $r = Invoke-Cli @('definitely-not-a-command')
    Assert-True ($r.Code -eq 2) 'unknown CLI command exits 2'

    $r = Invoke-Cli @('run', $root, 'reset', '--hard')
    Assert-True ($r.Code -ne 0 -or $r.Text -match '"ok"\s*:\s*false') 'CLI run rejects non-whitelisted git command'

    $repoPath = $root -replace '\\', '/'
    $init = '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"smoke","version":"1"}}}'
    $badUrl = '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"set_config","arguments":{"path":"' + $repoPath + '","remote_urls":["https://user:***@example.com/x.git"],"confirm":true}}}'
    $mcpOut = Invoke-McpStdio @($init, $badUrl)
    Assert-True ($mcpOut -match '"isError":true') 'MCP set_config rejects masked URL (no write performed)'
} finally {
    if (Test-Path $temp) { Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue }
}

Write-Host ''
Write-Host "$pass passed, $fail failed"
if ($fail -gt 0) { exit 1 }
exit 0
