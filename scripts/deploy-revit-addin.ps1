# Deploy Speckle Revit addin: backup existing, copy new artifacts, restart Revit, scan latest journal
$dstAddin = 'C:\ProgramData\Autodesk\Revit\Addins\2025\Speckle.Connectors.Revit2025'
$dstManifest = 'C:\ProgramData\Autodesk\Revit\Addins\2025\Speckle.Connectors.Revit2025.addin'
$backupRoot = 'D:\speckle-sharp-connectors\artifacts\revit-addin-backups'
New-Item -Path $backupRoot -ItemType Directory -Force | Out-Null
$ts = Get-Date -Format yyyyMMddHHmmss
$backupFolder = Join-Path $backupRoot ("Speckle.Connectors.Revit2025." + $ts)

if (Test-Path $dstAddin) {
  Move-Item -LiteralPath $dstAddin -Destination $backupFolder -Force
  Write-Output "MOVED_ADDIN_FOLDER:$backupFolder"
} else {
  Write-Output 'NO_EXISTING_ADDIN_FOLDER'
}

if (Test-Path $dstManifest) {
  Move-Item -LiteralPath $dstManifest -Destination ($backupFolder + '.addin.disabled') -Force
  Write-Output 'MOVED_ADDIN_MANIFEST'
} else {
  Write-Output 'NO_EXISTING_MANIFEST'
}

New-Item -Path $dstAddin -ItemType Directory -Force | Out-Null
Copy-Item -Path 'artifacts\Revit2025-Release\*' -Destination $dstAddin -Recurse -Force

if (Test-Path 'artifacts\Revit2025-Release\Speckle.Connectors.Revit2025.addin') {
  Copy-Item -Path 'artifacts\Revit2025-Release\Speckle.Connectors.Revit2025.addin' -Destination 'C:\ProgramData\Autodesk\Revit\Addins\2025\' -Force
  Write-Output 'COPIED_ADDIN_MANIFEST'
} else {
  Write-Output 'NO_MANIFEST_FOUND'
}

Write-Output 'DEPLOY_DONE'

Stop-Process -Name Revit -ErrorAction SilentlyContinue
Start-Process -FilePath 'C:\Program Files\Autodesk\Revit 2025\Revit.exe' -ArgumentList '/nosplash'
Start-Sleep -Seconds 10

$j = (Get-ChildItem 'C:\Users\echla\AppData\Local\Autodesk\Revit\Autodesk Revit 2025\Journals' -Filter 'journal.*.txt' | Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
Write-Output "SCANNING_JOURNAL:$j"
Select-String -Path $j -Pattern 'Speckle|Microsoft.Extensions|Serilog|ERROR|Exception|Fail|Binding|TypeLoad|FileLoad|FileNotFound|LoaderExceptions' -AllMatches -Context 3,3 | Select-Object -First 300
