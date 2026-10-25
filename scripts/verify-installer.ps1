param([string]$InstallerPath,[string]$PublishedDirectory)
$ErrorActionPreference = 'Stop'
$sfRoot = Split-Path -Parent $PSScriptRoot
if (-not $InstallerPath) { $InstallerPath = Join-Path $sfRoot 'artifacts\release\Flux-Setup-1.0.0-win-x64.exe' }
if (-not $PublishedDirectory) { $PublishedDirectory = Join-Path $sfRoot 'artifacts\publish' }
$sfQaRoot = Join-Path $sfRoot ('.local\installer-check-' + [Guid]::NewGuid().ToString('N'))
if (Test-Path -LiteralPath $sfQaRoot) { throw 'Refusing to overwrite an existing QA installation.' }
$sfRegistryPaths = @(
 'Software\Google\Chrome\NativeMessagingHosts\com.flux.bridge',
 'Software\Microsoft\Edge\NativeMessagingHosts\com.flux.bridge',
 'Software\Mozilla\NativeMessagingHosts\com.flux.bridge',
 'Software\Google\Chrome\NativeMessagingHosts\com.flux.downloads',
 'Software\Microsoft\Edge\NativeMessagingHosts\com.flux.downloads',
 'Software\Mozilla\NativeMessagingHosts\com.flux.downloads',
 'Software\Microsoft\Windows\CurrentVersion\Uninstall\Flux')
$sfRegistryBackup = @()
foreach ($sfView in @([Microsoft.Win32.RegistryView]::Registry64,[Microsoft.Win32.RegistryView]::Registry32)) {
 $sfBase = [Microsoft.Win32.RegistryKey]::OpenBaseKey('CurrentUser',$sfView)
 foreach ($sfPath in $sfRegistryPaths) {
  $sfKey = $sfBase.OpenSubKey($sfPath)
  $sfValues = @()
  if ($sfKey) { foreach ($sfName in $sfKey.GetValueNames()) { $sfValues += @{Name=$sfName;Value=$sfKey.GetValue($sfName);Kind=$sfKey.GetValueKind($sfName)} } }
  $sfRegistryBackup += @{View=$sfView;Path=$sfPath;Exists=($null -ne $sfKey);Values=$sfValues}
  if ($sfKey) { $sfKey.Dispose() }
 }
 foreach ($sfEntry in @(@('Software\Flux','InstallDir'),@('Software\Microsoft\Windows\CurrentVersion\Run','Flux'))) {
  $sfKey = $sfBase.OpenSubKey($sfEntry[0]); $sfExists = $sfKey -and $sfKey.GetValueNames().Contains($sfEntry[1])
  $sfRegistryBackup += @{View=$sfView;Path=$sfEntry[0];SingleName=$sfEntry[1];Exists=$sfExists;Value=$(if($sfExists){$sfKey.GetValue($sfEntry[1])});Kind=$(if($sfExists){$sfKey.GetValueKind($sfEntry[1])})}
  if ($sfKey) { $sfKey.Dispose() }
 }
 $sfBase.Dispose()
}
$sfShortcutBackup = @()
foreach ($sfPath in @((Join-Path ([Environment]::GetFolderPath('Programs')) 'Flux\Flux.lnk'),(Join-Path ([Environment]::GetFolderPath('Programs')) 'Flux\Uninstall Flux.lnk'),(Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'Flux.lnk'))) {
 $sfShortcutBackup += @{Path=$sfPath;Bytes=$(if(Test-Path -LiteralPath $sfPath){[IO.File]::ReadAllBytes($sfPath)})}
}
try {
 $sfProcess = Start-Process -FilePath $InstallerPath -ArgumentList "/S /D=$sfQaRoot" -WindowStyle Hidden -PassThru -Wait
 if ($sfProcess.ExitCode -ne 0) { throw "Installer exit code $($sfProcess.ExitCode)." }
 $sfFiles = @(Get-ChildItem -LiteralPath $PublishedDirectory -File -Recurse)
 foreach ($sfFile in $sfFiles) {
  $sfRelative = [IO.Path]::GetRelativePath($PublishedDirectory,$sfFile.FullName)
  $sfInstalled = Join-Path $sfQaRoot $sfRelative
  if (-not (Test-Path -LiteralPath $sfInstalled) -or (Get-FileHash -LiteralPath $sfInstalled).Hash -ne (Get-FileHash -LiteralPath $sfFile.FullName).Hash) { throw "Installed file mismatch: $sfRelative" }
 }
 if ((Get-Item -LiteralPath (Join-Path $sfQaRoot 'Flux.exe')).VersionInfo.ProductVersion -notlike '1.0.0*') { throw 'Incorrect installed version.' }
 "PASS: current-user installation, version and all $($sfFiles.Count) published file hashes"
 foreach ($sfName in @('Flux.exe','Flux.NativeHost.exe')) {
  & node (Join-Path $sfRoot 'browser-extension\tests\native-host-smoke.mjs') (Join-Path $sfQaRoot $sfName)
  if ($LASTEXITCODE -ne 0) { throw "Installed $sfName protocol check failed." }
 }
 $sfLocked = [IO.File]::Open((Join-Path $sfQaRoot 'Flux.exe'),'Open','Read','Read')
 try {
  $sfProcess = Start-Process -FilePath $InstallerPath -ArgumentList "/S /D=$sfQaRoot" -WindowStyle Hidden -PassThru -Wait
  if ($sfProcess.ExitCode -ne 2) { throw "A locked installation should fail safely with exit code 2; got $($sfProcess.ExitCode)." }
 } finally { $sfLocked.Dispose() }
 'PASS: silent upgrade refuses locked application before replacing files'
 $sfCanary = Join-Path $sfQaRoot 'user-file-canary.txt'; [IO.File]::WriteAllText($sfCanary,'Owned verification file: preserve on uninstall.')
 $sfProcess = Start-Process -FilePath (Join-Path $sfQaRoot 'Uninstall.exe') -ArgumentList "/S _?=$sfQaRoot" -WindowStyle Hidden -PassThru -Wait
 if ($sfProcess.ExitCode -ne 0) { throw "Uninstaller exit code $($sfProcess.ExitCode)." }
 foreach ($sfFile in $sfFiles) { if (Test-Path -LiteralPath (Join-Path $sfQaRoot ([IO.Path]::GetRelativePath($PublishedDirectory,$sfFile.FullName)))) { throw "Uninstall retained program file: $($sfFile.Name)" } }
 if (-not (Test-Path -LiteralPath $sfCanary)) { throw 'Uninstall removed the user-file canary.' }
 'PASS: uninstall removes published files and retains unrelated user data'
 "QA location: $sfQaRoot"
} finally {
 foreach ($sfBackup in $sfRegistryBackup) {
  $sfBase = [Microsoft.Win32.RegistryKey]::OpenBaseKey('CurrentUser',$sfBackup.View)
  if ($sfBackup.ContainsKey('SingleName')) {
   $sfKey = $sfBase.CreateSubKey($sfBackup.Path)
   if ($sfBackup.Exists) { $sfKey.SetValue($sfBackup.SingleName,$sfBackup.Value,$sfBackup.Kind) } else { $sfKey.DeleteValue($sfBackup.SingleName,$false) }
   $sfKey.Dispose()
  } elseif ($sfBackup.Exists) {
   $sfKey = $sfBase.CreateSubKey($sfBackup.Path)
   foreach ($sfValue in $sfBackup.Values) { $sfKey.SetValue($sfValue.Name,$sfValue.Value,$sfValue.Kind) }
   $sfKey.Dispose()
  } else { $sfBase.DeleteSubKeyTree($sfBackup.Path,$false) }
  $sfBase.Dispose()
 }
 foreach ($sfShortcut in $sfShortcutBackup) {
  if ($sfShortcut.Bytes) { [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($sfShortcut.Path)) | Out-Null; [IO.File]::WriteAllBytes($sfShortcut.Path,$sfShortcut.Bytes) }
 }
}
