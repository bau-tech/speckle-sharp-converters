$ts = Get-Date -Format yyyyMMddHHmmss
$backup = "d:\speckle-sharp-connectors\artifacts\revit-addin-backups\SpeckleRoamingBackup.$ts"
New-Item -Path $backup -ItemType Directory -Force | Out-Null
$rPath = 'C:\Users\echla\AppData\Roaming\Autodesk\Revit\Addins\2025'
$moved = @()

if (Test-Path $rPath) {
    Get-ChildItem -Path $rPath -Filter 'Speckle*' -Force | ForEach-Object {
        $dst = Join-Path $backup $_.Name
        Move-Item -LiteralPath $_.FullName -Destination $dst -Force
        $moved += $_.FullName
        Write-Output "MOVED: $($_.FullName)"
    }

    Get-ChildItem -Path $rPath -Filter 'Speckle*.addin' -File -Force | ForEach-Object {
        $dst = Join-Path $backup ($_.Name + '.disabled')
        Move-Item -LiteralPath $_.FullName -Destination $dst -Force
        $moved += $_.FullName
        Write-Output "MOVED_MANIFEST: $($_.FullName)"
    }
} else {
    Write-Output 'ROAMING_ADDINS_FOLDER_MISSING'
}

if ($moved.Count -eq 0) { Write-Output 'NO_SPECKLE_ROAMING_FOUND' }

# Restart Revit
Get-Process -Name Revit -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Process -FilePath 'C:\Program Files\Autodesk\Revit 2025\Revit.exe' -ErrorAction SilentlyContinue

Start-Sleep -Seconds 10

# Scan journal
$journalFolder = 'C:\Users\echla\AppData\Local\Autodesk\Revit\Autodesk Revit 2025\Journals'
$latestJournal = Get-ChildItem -Path $journalFolder -Filter 'journal.*.txt' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($latestJournal) {
    Write-Output ('SCANNING_JOURNAL:' + $latestJournal.FullName)
    Select-String -Path $latestJournal.FullName -Pattern 'Speckle|AddInLoadFailureMessage|API_ERROR|TaskDialog|Exception|Assembly version' -SimpleMatch | Select-Object -First 500 | ForEach-Object { Write-Output $_.Line }
} else {
    Write-Output 'NO_JOURNAL_FOUND'
}