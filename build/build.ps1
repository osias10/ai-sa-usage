param([switch]$SkipTests, [switch]$Installer, [string]$Dotnet, [string]$Version)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $Dotnet) {
    $localSdk = Join-Path $repoRoot '.tools\dotnet\dotnet.exe'
    if (Test-Path -LiteralPath $localSdk) { $Dotnet = $localSdk } else { $Dotnet = 'dotnet' }
}
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$versionPattern = '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-(beta|rc)\.(0|[1-9][0-9]*))?\z'
$versionArguments = @()
if ($Version) {
    if ($Version -cnotmatch $versionPattern) { throw 'Invalid release version; use major.minor.patch with an optional -beta.N or -rc.N suffix.' }
    $versionArguments = @("-p:Version=$Version")
}
Push-Location $repoRoot
try {
    & $Dotnet build 'src\AIUsage.App\AIUsage.App.csproj' -c Release --nologo @versionArguments
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    if (-not $SkipTests) {
        & $Dotnet test 'tests\AIUsage.Tests\AIUsage.Tests.csproj' -c Release --nologo @versionArguments
        if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    }
    & $Dotnet publish 'src\AIUsage.App\AIUsage.App.csproj' -c Release -r win-x64 --self-contained true -o '.artifacts\publish' --nologo @versionArguments
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    if ($Installer) {
        $releaseVersion = (& $Dotnet msbuild 'src\AIUsage.App\AIUsage.App.csproj' -getProperty:Version -nologo @versionArguments).Trim()
        if ($LASTEXITCODE -ne 0 -or $releaseVersion -cnotmatch $versionPattern) { throw 'Invalid release version.' }
        $publishedVersion = (Get-Item -LiteralPath '.artifacts\publish\AIUsageWidget.exe').VersionInfo.ProductVersion.Split('+')[0]
        if ($publishedVersion -ne $releaseVersion) { throw 'Published app and installer versions differ.' }
        & $Dotnet restore 'build\Packaging.csproj' --nologo @versionArguments
        if ($LASTEXITCODE -ne 0) { throw 'Installer tools restore failed.' }
        & $Dotnet msbuild 'build\Packaging.csproj' -t:BuildInstaller -nologo @versionArguments
        if ($LASTEXITCODE -ne 0) { throw 'Installer build failed.' }
        $installerPath = Join-Path '.artifacts\installer' "AISA-$releaseVersion-win-x64-setup.exe"
        if (-not (Test-Path -LiteralPath $installerPath)) { throw 'Expected versioned installer was not produced.' }
        Write-Output "Release tag: v$releaseVersion"
        Write-Output "Upload installer: $installerPath"
    }
} finally { Pop-Location }
