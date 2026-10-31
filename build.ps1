# Builds dist\Tidybird.exe with the C# compiler that ships with Windows (.NET Framework 4).
# Run in a fresh PowerShell each time: Add-Type cannot reload a changed Logo.cs in one session.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$out  = Join-Path $root 'dist'
New-Item -ItemType Directory -Path $out -Force | Out-Null

Add-Type -Path (Join-Path $root 'src\Logo.cs') -ReferencedAssemblies System.Drawing
$ico = Join-Path $out 'Tidybird.ico'
[Tidybird.Logo]::SaveIco($ico, [int[]](16, 20, 24, 32, 40, 48, 64, 128, 256))
$png = [Tidybird.Logo]::Render(512)
$png.Save((Join-Path $root 'logo.png'))
$png.Dispose()

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $csc /nologo /target:winexe /optimize+ /out:"$out\Tidybird.exe" /win32icon:"$ico" /resource:"$ico",Tidybird.ico `
    /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll "$root\src\*.cs"
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
Write-Host "Built $out\Tidybird.exe"
