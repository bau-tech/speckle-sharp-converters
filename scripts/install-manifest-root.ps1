# Install the addin manifest from artifacts Plugin folder into ProgramData addins root
$src = 'd:\speckle-sharp-connectors\artifacts\Revit2025-Release\Plugin\Speckle.Connectors.Revit2025.addin'
$dst = 'C:\ProgramData\Autodesk\Revit\Addins\2025\Speckle.Connectors.Revit2025.addin'
$backupRoot = 'D:\speckle-sharp-connectors\artifacts\revit-addin-backups'
New-Item -Path $backupRoot -ItemType Directory -Force | Out-Null

if (-not (Test-Path $src)) {
    Write-Output "MANIFEST_SOURCE_NOT_FOUND: $src"
    exit 2
}

if (Test-Path $dst) {
    $ts = Get-Date -Format yyyyMMddHHmmss
    $backup = Join-Path $backupRoot ("Speckle.Connectors.Revit2025.addin.$ts")
    Move-Item -LiteralPath $dst -Destination $backup -Force
    Write-Output "BACKED_UP_OLD_MANIFEST: $backup"
}

Copy-Item -LiteralPath $src -Destination $dst -Force
if (Test-Path $dst) { Write-Output "COPIED_MANIFEST:$dst" } else { Write-Output 'COPY_FAILED' ; exit 3 }

# Restart Revit to trigger addin load
Get-Process -Name Revit -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Process -FilePath 'C:\Program Files\Autodesk\Revit 2025\Revit.exe' -ArgumentList '/nosplash' -ErrorAction SilentlyContinue
Start-Sleep -Seconds 12

# Scan latest journal
$journalFolder = 'C:\Users\echla\AppData\Local\Autodesk\Revit\Autodesk Revit 2025\Journals'
$latestJournal = Get-ChildItem -Path $journalFolder -Filter 'journal.*.txt' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($latestJournal) {
    Write-Output ('SCANNING_JOURNAL:' + $latestJournal.FullName)
    Select-String -Path $latestJournal.FullName -Pattern 'Speckle|AddInLoadFailureMessage|API_ERROR|TaskDialog|Exception|Assembly version' -SimpleMatch -AllMatches | Select-Object -First 500 | ForEach-Object { Write-Output $_.Line }
} else {
    Write-Output 'NO_JOURNAL_FOUND'
}
