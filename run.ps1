param([ValidateSet('Debug','Release')][string]$Configuration = 'Debug')
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'DraftOptimizer\DraftOptimizer\DraftOptimizer.csproj'
dotnet build $project -c $Configuration -p:Platform=x64 -p:RuntimeIdentifier=win-x64
if ($LASTEXITCODE -ne 0) { throw 'La compilación falló.' }
$app = Join-Path $PSScriptRoot "DraftOptimizer\DraftOptimizer\bin\x64\$Configuration\net8.0-windows10.0.19041.0\win-x64\DraftOptimizer.exe"
Start-Process -FilePath $app
