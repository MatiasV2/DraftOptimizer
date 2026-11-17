param([ValidateSet('Debug','Release')][string]$Configuration = 'Debug')
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'DraftOptimizer.Wpf\DraftOptimizer.Wpf.csproj'
dotnet run --project $project --configuration $Configuration
if ($LASTEXITCODE -ne 0) { throw 'DraftOptimizer terminó con un error.' }
