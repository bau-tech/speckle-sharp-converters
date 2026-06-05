$backupRoot = 'D:\speckle-sharp-connectors\artifacts\revit-addin-backups'
New-Item -Path $backupRoot -ItemType Directory -Force | Out-Null

Write-Output '=== PROGRAMDATA SPECKLE FILES ==='
Get-ChildItem -Path 'C:\ProgramData\Autodesk\Revit\Addins\2025' -Filter 'Speckle*' -Recurse -Force | ForEach-Object { Write-Output $_.FullName }

Write-Output '=== ROAMING SPECKLE FILES ==='
if (Test-Path "$env:APPDATA\Autodesk\Revit\Addins\2025") {
  $r = Get-ChildItem -Path "$env:APPDATA\Autodesk\Revit\Addins\2025" -Filter 'Speckle*' -Recurse -Force -ErrorAction SilentlyContinue
  if ($r.Count -eq 0) { Write-Output 'NO_ROAMING_SPECKLE_FOUND' } else { $r | ForEach-Object { Write-Output $_.FullName } }
} else {
  Write-Output 'ROAMING_FOLDER_MISSING'
}

Write-Output '=== DEPLOYED FOLDER CONTENTS ==='
$dstAddin = 'C:\ProgramData\Autodesk\Revit\Addins\2025\Speckle.Connectors.Revit2025'
if (Test-Path $dstAddin) { Get-ChildItem $dstAddin -File | ForEach-Object { Write-Output ("FILE: " + $_.Name + "  " + $_.Length + " bytes  " + $_.LastWriteTime) } } else { Write-Output 'NO_DEPLOYED_FOLDER' }

Write-Output '=== MANIFEST CONTENT ==='
$dstManifest = 'C:\ProgramData\Autodesk\Revit\Addins\2025\Speckle.Connectors.Revit2025.addin'
if (Test-Path $dstManifest) {
  Get-Content $dstManifest -ErrorAction SilentlyContinue | ForEach-Object { Write-Output $_ }
} else {
  Write-Output 'MANIFEST_MISSING'
}

Write-Output '=== ARTIFACT MANIFEST CHECK (COPY IF MISSING) ==='
$srcManifest = 'D:\speckle-sharp-connectors\artifacts\Revit2025-Release\Plugin\Speckle.Connectors.Revit2025.addin'
if (-not (Test-Path $srcManifest)) { Write-Output ("ARTIFACT_MANIFEST_MISSING: $srcManifest") } else {
  if (Test-Path $dstManifest) { $ts=Get-Date -Format yyyyMMddHHmmss; $backup=Join-Path $backupRoot ("Speckle.Connectors.Revit2025.addin.$ts"); Move-Item -LiteralPath $dstManifest -Destination $backup -Force; Write-Output ("BACKED_UP_OLD_MANIFEST: $backup") }
  Copy-Item -LiteralPath $srcManifest -Destination 'C:\ProgramData\Autodesk\Revit\Addins\2025\' -Force
  if (Test-Path $dstManifest) { Write-Output ("COPIED_MANIFEST:$dstManifest") } else { Write-Output 'COPY_FAILED' }
}

Write-Output '=== DEPLOY ARTIFACTS FOLDER COPY ==='
$srcFolder = 'D:\speckle-sharp-connectors\artifacts\Revit2025-Release\*'
if (Test-Path 'D:\speckle-sharp-connectors\artifacts\Revit2025-Release') {
  Copy-Item -Path $srcFolder -Destination $dstAddin -Recurse -Force
  Write-Output 'COPIED_ARTIFACTS_TO_PROGRAMDATA'
} else {
  Write-Output 'ARTIFACTS_FOLDER_MISSING'
}

Write-Output '=== RESTART REVIT ==='
Get-Process -Name Revit -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Process -FilePath 'C:\Program Files\Autodesk\Revit 2025\Revit.exe' -ArgumentList '/nosplash' -ErrorAction SilentlyContinue
Start-Sleep -Seconds 12

Write-Output '=== SCAN LATEST JOURNAL ==='
$journalFolder = 'C:\Users\echla\AppData\Local\Autodesk\Revit\Autodesk Revit 2025\Journals'
$latest = Get-ChildItem -Path $journalFolder -Filter 'journal.*.txt' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($latest) {
  Write-Output ("SCANNING_JOURNAL: $($latest.FullName)")
  Select-String -Path $latest.FullName -Pattern 'Speckle|AddInLoadFailureMessage|API_ERROR|TaskDialog|Exception|Assembly version|Added pushbutton|AddInManifest' -AllMatches -Context 3,2 | Select-Object -First 500 | ForEach-Object { Write-Output $_.Line }
  Write-Output '=== LAST 200 LINES OF JOURNAL ==='
  Get-Content $latest.FullName -Tail 200 | ForEach-Object { Write-Output $_ }
} else {
  Write-Output 'NO_JOURNAL_FOUND'
}

Write-Output '=== DONE ==='