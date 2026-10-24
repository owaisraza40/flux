param([switch]$SkipTests, [switch]$SkipInstaller, [switch]$MediaIntegration, [string]$DotnetPath, [string]$NsisCompiler)
$ErrorActionPreference = 'Stop'
$sfRoot = Split-Path -Parent $PSScriptRoot
$env:DOTNET_CLI_HOME = Join-Path $sfRoot '.local\dotnet-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
if (-not $DotnetPath) {
  $sfLocalSdk = Join-Path (Split-Path -Parent $sfRoot) 'tools\dotnet\dotnet.exe'
  $DotnetPath = if (Test-Path -LiteralPath $sfLocalSdk) { $sfLocalSdk } else { 'dotnet' }
}
function Invoke-SfDotnet { & $DotnetPath @args; if ($LASTEXITCODE -ne 0) { throw "dotnet failed: $args" } }
$env:DOTNET_ROOT = Split-Path -Parent (Get-Command $DotnetPath).Source
Push-Location $sfRoot
try {
  & (Join-Path $PSScriptRoot 'generate-icon.ps1')
  & node browser-extension/scripts/package.mjs
  if ($LASTEXITCODE -ne 0) { throw 'Extension packaging failed' }
  Invoke-SfDotnet restore src/Flux.App/Flux.App.csproj -r win-x64 --configfile NuGet.Config
  if (-not $SkipTests) {
    Invoke-SfDotnet restore tests/Flux.Tests/Flux.Tests.csproj --configfile NuGet.Config
    Invoke-SfDotnet restore browser-extension/tests/NativeBridge.Tests/NativeBridge.Tests.csproj --configfile NuGet.Config
    Invoke-SfDotnet restore tests/Flux.Services.Tests/Flux.Services.Tests.csproj --configfile NuGet.Config
    Invoke-SfDotnet run --project tests/Flux.Tests/Flux.Tests.csproj -c Release --no-restore
    Invoke-SfDotnet run --project browser-extension/tests/NativeBridge.Tests/NativeBridge.Tests.csproj -c Release --no-restore
    if ($MediaIntegration) { Invoke-SfDotnet run --project tests/Flux.Services.Tests/Flux.Services.Tests.csproj -c Release --no-restore -- --media-integration }
    else { Invoke-SfDotnet run --project tests/Flux.Services.Tests/Flux.Services.Tests.csproj -c Release --no-restore }
    $sfNodeTests = @(Get-ChildItem -LiteralPath browser-extension/tests -Filter '*.test.mjs' -File | ForEach-Object { $_.FullName })
    & node --test @sfNodeTests
    if ($LASTEXITCODE -ne 0) { throw 'Browser extension tests failed' }
  }
  Invoke-SfDotnet restore src/Flux.App/Flux.App.csproj -r win-x64 --configfile NuGet.Config
  Invoke-SfDotnet publish src/Flux.App/Flux.App.csproj -c Release -r win-x64 --self-contained true --no-restore -o artifacts/publish
  & node browser-extension/tests/native-host-smoke.mjs (Join-Path $sfRoot 'artifacts\publish\Flux.exe')
  if ($LASTEXITCODE -ne 0) { throw 'Published native host smoke test failed' }
  New-Item -ItemType Directory -Path artifacts/release -Force | Out-Null
  Copy-Item -LiteralPath LICENSE,README.md,THIRD-PARTY-NOTICES.md -Destination artifacts/publish -Force
  New-Item -ItemType Directory -Path artifacts/publish/docs -Force | Out-Null
  $sfPublicDocs = @('ARCHITECTURE.md','BROWSER-INTEGRATION.md','CORE-TESTING.md','RELEASE-NOTES.md','RELEASING.md','USER-GUIDE.md','VALIDATION.md')
  foreach ($sfDoc in $sfPublicDocs) { Copy-Item -LiteralPath (Join-Path 'docs' $sfDoc) -Destination artifacts/publish/docs -Force }
  New-Item -ItemType Directory -Path artifacts/publish/docs/images -Force | Out-Null
  Copy-Item -LiteralPath docs/images/flux.png -Destination artifacts/publish/docs/images -Force
  $sfPortableZip = Join-Path $sfRoot 'artifacts\release\Flux-1.0.0-win-x64-portable.zip'
  Compress-Archive -Path artifacts/publish/* -DestinationPath $sfPortableZip -Force
  $sfBrowserZip = Join-Path $sfRoot 'artifacts\release\Flux-Browser-Extension-1.0.0.zip'
  Compress-Archive -Path browser-extension/chromium,browser-extension/firefox,docs/BROWSER-INTEGRATION.md -DestinationPath $sfBrowserZip -Force
  if (-not $SkipInstaller) {
    if (-not $NsisCompiler) {
      $sfNsisCandidates = @((Join-Path (Split-Path -Parent $sfRoot) 'tools\NSIS\nsis-3.13\makensis.exe'), "${env:ProgramFiles(x86)}\NSIS\makensis.exe", "$env:ProgramFiles\NSIS\makensis.exe")
      $NsisCompiler = $sfNsisCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
      if (-not $NsisCompiler) { $NsisCompiler = (Get-Command makensis.exe -ErrorAction SilentlyContinue).Source }
    }
    if (-not $NsisCompiler) { throw 'NSIS makensis.exe not found. Use -NsisCompiler or -SkipInstaller.' }
    & $NsisCompiler /V2 installer/Flux.nsi
    if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed' }
  }
  $sfChecksums = Get-ChildItem -LiteralPath (Join-Path $sfRoot 'artifacts\release') -File | Where-Object { $_.Name -ne 'SHA256SUMS.txt' } | Sort-Object Name | ForEach-Object { '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(),$_.Name }
  [System.IO.File]::WriteAllLines((Join-Path $sfRoot 'artifacts\release\SHA256SUMS.txt'),$sfChecksums,[System.Text.UTF8Encoding]::new($false))
  Get-ChildItem -LiteralPath (Join-Path $sfRoot 'artifacts\release') | Select-Object Name,Length
} finally { Pop-Location }
