param([switch]$RunChecks)
$ErrorActionPreference = 'Stop'
$env:MSBuildEnableWorkloadResolver = 'false'
$env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$projectFile = Join-Path $PSScriptRoot 'Source\HonestlyTUN.csproj'
$testProject = Join-Path $PSScriptRoot 'Source\Tests\SwitchTests.csproj'
$appFolder = Join-Path $PSScriptRoot 'App'
$buildFolder = Join-Path $PSScriptRoot '.build'
dotnet publish $projectFile -c Release --self-contained false --artifacts-path $buildFolder -o $appFolder --nologo
if ($LASTEXITCODE -ne 0) { throw 'Application build failed.' }
$thatPetSource = Join-Path $PSScriptRoot 'Source\Assets\ThatPet'
$thatPetApp = Join-Path $appFolder 'ThatPet'
if ((Test-Path $thatPetSource) -and !(Test-Path (Join-Path $thatPetApp 'thatpet.exe'))) {
    Copy-Item $thatPetSource $thatPetApp -Recurse -Force
}
if ($RunChecks) {
    dotnet run --project $testProject -c Release --artifacts-path (Join-Path $buildFolder 'tests')
    if ($LASTEXITCODE -ne 0) { throw 'Logic checks failed.' }
    dotnet run --project (Join-Path $PSScriptRoot 'Source\ClientTests\ClientTests.csproj') -c Release --artifacts-path (Join-Path $buildFolder 'client-tests')
    if ($LASTEXITCODE -ne 0) { throw 'Subscription parser checks failed.' }
    dotnet run --project (Join-Path $PSScriptRoot 'Source\IntroTests\IntroTests.csproj') -c Release --artifacts-path (Join-Path $buildFolder 'intro-tests') -- (Join-Path $PSScriptRoot 'Source\Assets\intro.frames.zip')
    if ($LASTEXITCODE -ne 0) { throw 'Intro frame decoding checks failed.' }
    dotnet run --project (Join-Path $PSScriptRoot 'Source\MediaTests\MediaTests.csproj') -c Release --artifacts-path (Join-Path $buildFolder 'media-tests')
    if ($LASTEXITCODE -ne 0) { throw 'Audio spectrum checks failed.' }
    dotnet run --project (Join-Path $PSScriptRoot 'Source\EpicTests\EpicTests.csproj') -c Release --artifacts-path (Join-Path $buildFolder 'epic-tests')
    if ($LASTEXITCODE -ne 0) { throw 'Epic profile transaction checks failed.' }
}
Write-Output "Ready: $appFolder\HonestlyTUN.exe"

