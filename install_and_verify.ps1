param (
    [switch]$SkipIndex
)

$ErrorActionPreference = "Continue"

function Run-Command {
    param (
        [string]$Message,
        [scriptblock]$Command,
        [bool]$ContinueOnError = $false
    )

    Write-Host "`n$Message" -ForegroundColor Cyan
    & $Command

    if ($LASTEXITCODE -ne 0) {
        Write-Host "[ERROR] Command failed with exit code $LASTEXITCODE." -ForegroundColor Red
        if (-not $ContinueOnError) {
            Write-Host "Aborting installation." -ForegroundColor Red
            exit 1
        }
    }
}

Run-Command -Message "1. Building Plugin (Release | x64)..." -Command {
    dotnet build "Community.PowerToys.Run.Plugin.SemanticSearcher\Community.PowerToys.Run.Plugin.SemanticSearcher.csproj" -c Release -p:Platform=x64
}

Run-Command -Message "2. Building IndexerTool..." -Command {
    dotnet build "IndexerTool\IndexerTool.csproj" -c Release
}

Run-Command -Message "2.5. Deploying updated config.json..." -Command {
    $pluginDest = "$env:LOCALAPPDATA\PowerToys\RunPlugins\SemanticSearcher"
    if (-not (Test-Path $pluginDest)) {
        New-Item -ItemType Directory -Force -Path $pluginDest | Out-Null
    }
    Copy-Item -Path "Community.PowerToys.Run.Plugin.SemanticSearcher\config.json" -Destination $pluginDest -Force
}

if (-not $SkipIndex) {
    Run-Command -Message "2.6. Clearing old index database..." -Command {
        dotnet run --project IndexerTool\IndexerTool.csproj -- clear
    } -ContinueOnError $true
}

if (-not $SkipIndex) {
    Run-Command -Message "3. Generating Semantic Index (Foreground)..." -Command {
        Write-Host "This will scan your folders and generate embeddings. You can watch the progress here:" -ForegroundColor Yellow
        dotnet run --project IndexerTool\IndexerTool.csproj -- index
    } -ContinueOnError $true
} else {
    Write-Host "`n3. Skipping Index Generation (-SkipIndex used)..." -ForegroundColor Yellow
}

Run-Command -Message "4. Verifying SQLite Database..." -Command {
    dotnet run --project IndexerTool\IndexerTool.csproj -- inspect
} -ContinueOnError $true

Run-Command -Message "5. Stopping PowerToys Suite..." -Command {
    Get-Process | Where-Object {$_.Name -match "PowerToys"} | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
} -ContinueOnError $true

Write-Host "`n6. Deploying Plugin to PowerToys..." -ForegroundColor Cyan
$pluginDest = "$env:LOCALAPPDATA\PowerToys\RunPlugins\SemanticSearcher"
if (-not (Test-Path $pluginDest)) {
    New-Item -ItemType Directory -Force -Path $pluginDest | Out-Null
}
Copy-Item -Path "Community.PowerToys.Run.Plugin.SemanticSearcher\bin\x64\Release\net9.0-windows10.0.26100.0\*" -Destination $pluginDest -Recurse -Force
if ($?) {
    Write-Host "Plugin deployed successfully to: $pluginDest" -ForegroundColor Green
} else {
    Write-Host "[ERROR] Failed to copy plugin files." -ForegroundColor Red
    exit 1
}

Write-Host "`n7. Restarting PowerToys Suite..." -ForegroundColor Cyan
Start-Process "$env:LOCALAPPDATA\PowerToys\PowerToys.exe" -ErrorAction SilentlyContinue
if ($?) {
    Write-Host "Done! PowerToys is ready." -ForegroundColor Green
} else {
    Write-Host "[ERROR] Failed to start PowerToys." -ForegroundColor Red
}
