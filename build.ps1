$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $scriptDir
[System.Environment]::CurrentDirectory = $scriptDir

$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$wpfDir = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF"

if (Test-Path "$scriptDir\LidGuard.exe") {
    Move-Item -Force "$scriptDir\LidGuard.exe" "$scriptDir\LidGuard.old.exe" -ErrorAction SilentlyContinue
}
if (Test-Path "$scriptDir\LidGuardInstaller.exe") {
    Move-Item -Force "$scriptDir\LidGuardInstaller.exe" "$scriptDir\LidGuardInstaller.old.exe" -ErrorAction SilentlyContinue
}

Write-Host "Compiling 64-bit LidGuard.exe..." -ForegroundColor Cyan
& $csc /nologo /target:winexe /platform:x64 /optimize+ `
    /out:LidGuard.exe `
    /win32icon:LidGuard.ico `
    "/resource:LidGuardWindow.xaml,LidGuardWindow.xaml" `
    "/resource:LidGuard.ico,LidGuard.ico" `
    /r:System.dll `
    /r:System.Core.dll `
    /r:System.Drawing.dll `
    /r:System.Windows.Forms.dll `
    /r:System.Xaml.dll `
    "/r:$wpfDir\PresentationFramework.dll" `
    "/r:$wpfDir\PresentationCore.dll" `
    "/r:$wpfDir\WindowsBase.dll" `
    LidGuard.cs

if ($LASTEXITCODE -ne 0) {
    throw "Compilation of LidGuard.exe failed with exit code $LASTEXITCODE"
}

Write-Host "Compiling 64-bit LidGuardInstaller.exe..." -ForegroundColor Cyan
& $csc /nologo /target:winexe /platform:x64 /optimize+ `
    /out:LidGuardInstaller.exe `
    /win32icon:LidGuard.ico `
    "/resource:LidGuard.ico,LidGuard.ico" `
    /r:System.dll `
    /r:System.Core.dll `
    /r:System.Xaml.dll `
    "/r:$wpfDir\PresentationFramework.dll" `
    "/r:$wpfDir\PresentationCore.dll" `
    "/r:$wpfDir\WindowsBase.dll" `
    LidGuardInstaller.cs

if ($LASTEXITCODE -ne 0) {
    throw "Compilation of LidGuardInstaller.exe failed with exit code $LASTEXITCODE"
}

Remove-Item -Force "$scriptDir\LidGuard.old.exe" -ErrorAction SilentlyContinue
Remove-Item -Force "$scriptDir\LidGuardInstaller.old.exe" -ErrorAction SilentlyContinue

Write-Host "Successfully built LidGuard.exe and LidGuardInstaller.exe (x64)" -ForegroundColor Green
