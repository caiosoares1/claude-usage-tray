# Compiles ClaudeUsageTray.exe with the C# compiler bundled with .NET Framework 4.x (no SDK needed).
$ErrorActionPreference = 'Stop'
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$out = Join-Path $PSScriptRoot 'ClaudeUsageTray.exe'

Get-Process ClaudeUsageTray -ErrorAction SilentlyContinue | Stop-Process -Force

& $csc /nologo /target:winexe /optimize+ /out:$out `
    /r:System.Web.Extensions.dll /r:Microsoft.VisualBasic.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll `
    (Join-Path $PSScriptRoot 'ClaudeUsageTray.cs')
if ($LASTEXITCODE -ne 0) { throw "csc falhou ($LASTEXITCODE)" }
Write-Host "OK: $out"
