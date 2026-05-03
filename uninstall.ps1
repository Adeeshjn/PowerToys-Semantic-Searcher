param (
    [switch]$WipeDatabase
)

$pluginDir = "$env:LOCALAPPDATA\PowerToys\RunPlugins\SemanticSearcher"
$dbDir     = "$env:LOCALAPPDATA\SemanticSearcher"

Write-Host "`n1. Stopping PowerToys Suite..." -ForegroundColor Cyan
Get-Process | Where-Object {$_.Name -match "PowerToys"} | Stop-Process -Force -ErrorAction SilentlyContinue
Get-Process -Name "dotnet" -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2

Write-Host "`n2. Removing plugin from PowerToys..." -ForegroundColor Cyan
if (Test-Path $pluginDir) {
    Remove-Item $pluginDir -Recurse -Force
    Write-Host "   Removed: $pluginDir" -ForegroundColor Green
} else {
    Write-Host "   Plugin not installed at: $pluginDir" -ForegroundColor Yellow
}

if ($WipeDatabase) {
    Write-Host "`n3. Wiping database and logs..." -ForegroundColor Cyan
    if (Test-Path $dbDir) {
        Remove-Item $dbDir -Recurse -Force
        Write-Host "   Wiped: $dbDir" -ForegroundColor Green
    } else {
        Write-Host "   Nothing to wipe at: $dbDir" -ForegroundColor Yellow
    }
}

Write-Host "`n4. Restarting PowerToys Suite..." -ForegroundColor Cyan
$launcher = "$env:LOCALAPPDATA\PowerToys\PowerToys.exe"
if (Test-Path $launcher) {
    Start-Process $launcher
    Write-Host "   PowerToys restarted." -ForegroundColor Green
} else {
    Write-Host "   PowerToys.exe not found. Start PowerToys manually." -ForegroundColor Yellow
}

Write-Host "`nDone. Plugin uninstalled." -ForegroundColor Green
