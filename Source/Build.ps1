param([string] $OutputPath)

$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    throw 'The .NET Framework C# compiler was not found.'
}

if (!$OutputPath) {
    $OutputPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'CrashVault.exe'
}
$executable = [IO.Path]::GetFullPath($OutputPath)
$icon = Join-Path $PSScriptRoot 'CrashVault.ico'
$manifest = Join-Path $PSScriptRoot 'Application.manifest'
$sources = @(Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.cs' -File | ForEach-Object FullName)

& $compiler /nologo /langversion:5 /target:winexe /platform:anycpu /optimize+ /warn:4 `
    /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Xml.Linq.dll `
    "/win32manifest:$manifest" "/win32icon:$icon" "/resource:$icon,CrashVault.WindowIcon" "/out:$executable" @sources
if ($LASTEXITCODE -ne 0) {
    throw "Build failed with exit code $LASTEXITCODE."
}

Get-Item -LiteralPath $executable
