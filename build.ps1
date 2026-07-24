# Builds VxeBatteryTray.exe from Program.cs using the in-box .NET Framework compiler.
# No SDK / no installs required. Run:  powershell -ExecutionPolicy Bypass -File .\build.ps1
$ErrorActionPreference = 'Stop'
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
$src = Join-Path $dir 'Program.cs'
$exe = Join-Path $dir 'VxeBatteryTray.exe'

$csc = Get-ChildItem 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe' -ErrorAction SilentlyContinue
if (-not $csc) { $csc = Get-ChildItem 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe' -ErrorAction SilentlyContinue }
if (-not $csc) { throw "csc.exe (.NET Framework 4) not found." }

if (Test-Path $exe) { Remove-Item $exe -Force }

# Generate the embedded app icon if missing
$ico = Join-Path $dir 'app.ico'
if (-not (Test-Path $ico)) { & powershell -ExecutionPolicy Bypass -File (Join-Path $dir 'make-icon.ps1') }
$iconArg = if (Test-Path $ico) { "/win32icon:$ico" } else { $null }

& $csc.FullName /nologo /target:winexe /optimize+ /out:$exe `
    /reference:System.dll `
    /reference:System.Drawing.dll `
    /reference:System.Windows.Forms.dll `
    $iconArg `
    $src

if (Test-Path $exe) {
    Write-Host "Built: $exe" -ForegroundColor Green
} else {
    throw "Build failed."
}
