$ErrorActionPreference = 'Stop'

$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$solutionPath = Join-Path $projectRoot 'BilibiliDownloader.sln'
$applicationProject = Join-Path $projectRoot 'src\BilibiliDownloader.WinForms\BilibiliDownloader.WinForms.csproj'
$publishDirectory = Join-Path $projectRoot 'artifacts\publish\win-x64'

function Assert-NativeCommandSucceeded([string] $step) {
    if ($LASTEXITCODE -ne 0) {
        throw "$step failed with exit code $LASTEXITCODE."
    }
}

dotnet restore $solutionPath
Assert-NativeCommandSucceeded 'Solution restore'
dotnet build $solutionPath -c Release --no-restore
Assert-NativeCommandSucceeded 'Solution build'
dotnet test $solutionPath -c Release --no-build
Assert-NativeCommandSucceeded 'Test run'
dotnet restore $applicationProject `
    -r win-x64 `
    -p:RuntimeFrameworkVersion=9.0.19
Assert-NativeCommandSucceeded 'Windows runtime restore'
dotnet publish $applicationProject `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:RuntimeFrameworkVersion=9.0.19 `
    --no-restore `
    -o $publishDirectory
Assert-NativeCommandSucceeded 'Windows publish'

Write-Host "Published to: $publishDirectory"
