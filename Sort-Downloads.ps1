<#
.SYNOPSIS
    Sorts files in a folder (default: your Downloads) into <Category>\<YYYY-MM>\ subfolders.

.DESCRIPTION
    Category comes from the file extension. Date comes from the file's last-modified time.
    Runs as a dry run unless -Apply is given. Every real run writes a CSV log of moves,
    which -Undo can replay in reverse.

.EXAMPLE
    .\Sort-Downloads.ps1                 # preview what would move
    .\Sort-Downloads.ps1 -Apply          # move files
    .\Sort-Downloads.ps1 -Undo           # put back files from the latest run
    .\Sort-Downloads.ps1 -Path D:\Inbox -DateFormat yyyy -Apply
#>
[CmdletBinding()]
param(
    [string]$Path = (Join-Path $env:USERPROFILE 'Downloads'),
    [switch]$Apply,
    [switch]$Undo,
    # .NET date format for the date subfolder: 'yyyy-MM' (monthly), 'yyyy' (yearly), 'yyyy-MM-dd' (daily)
    [string]$DateFormat = 'yyyy-MM',
    # Skip files modified more recently than this, so in-progress work is left alone
    [int]$MinAgeMinutes = 5
)

$ErrorActionPreference = 'Stop'

$Categories = [ordered]@{
    'Images'      = 'jpg','jpeg','png','gif','bmp','webp','svg','heic','tif','tiff','ico','raw'
    'Videos'      = 'mp4','mkv','mov','avi','wmv','webm','flv','m4v'
    'Audio'       = 'mp3','wav','flac','aac','ogg','m4a','wma','opus'
    'Documents'   = 'pdf','doc','docx','odt','rtf','txt','md','epub'
    'Spreadsheets'= 'xls','xlsx','ods','csv','tsv'
    'Slides'      = 'ppt','pptx','odp','key'
    'Archives'    = 'zip','rar','7z','tar','gz','bz2','xz','tgz'
    'Installers'  = 'exe','msi','msix','appx','dmg','pkg','deb','rpm','apk','iso'
    'Code'        = 'py','js','ts','json','html','css','java','c','cpp','cs','go','rs','sh','ps1','ipynb','xml','yml','yaml','sql'
    'Fonts'       = 'ttf','otf','woff','woff2'
    'Torrents'    = 'torrent'
}
$OtherCategory = 'Other'

# Partial downloads and temp files: never touch these
$SkipExtensions = 'crdownload','part','partial','tmp','download','opdownload','!ut'

$LogDir = Join-Path $Path '.sort-logs'

function Get-Category([string]$ext) {
    $ext = $ext.TrimStart('.').ToLowerInvariant()
    foreach ($name in $Categories.Keys) {
        if ($Categories[$name] -contains $ext) { return $name }
    }
    return $OtherCategory
}

# Returns a free path: "name.ext", else "name (1).ext", "name (2).ext", ...
function Get-UniquePath([string]$dir, [string]$fileName) {
    $target = Join-Path $dir $fileName
    $base = [IO.Path]::GetFileNameWithoutExtension($fileName)
    $ext  = [IO.Path]::GetExtension($fileName)
    $i = 1
    while (Test-Path -LiteralPath $target) {
        $target = Join-Path $dir "$base ($i)$ext"
        $i++
    }
    return $target
}

if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
    throw "Folder not found: $Path"
}

if ($Undo) {
    $log = Get-ChildItem -LiteralPath $LogDir -Filter 'sort-*.csv' -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -notlike '*.undone.csv' } |
        Sort-Object Name | Select-Object -Last 1
    if (-not $log) { Write-Host 'No sort log found; nothing to undo.'; return }

    $restored = 0
    # Reverse order so chains of moves unwind correctly
    $rows = @(Import-Csv -LiteralPath $log.FullName)
    [array]::Reverse($rows)
    foreach ($row in $rows) {
        if (-not (Test-Path -LiteralPath $row.To)) {
            Write-Warning "Missing, skipped: $($row.To)"
            continue
        }
        $back = Get-UniquePath (Split-Path $row.From) (Split-Path $row.From -Leaf)
        Move-Item -LiteralPath $row.To -Destination $back
        $restored++
    }
    # Remove category/date folders left empty by the undo (deepest first)
    $sortRoots = @($Categories.Keys) + $OtherCategory | ForEach-Object { Join-Path $Path $_ } |
        Where-Object { Test-Path -LiteralPath $_ }
    @($sortRoots | Get-ChildItem -Directory -Recurse) + @($sortRoots | Get-Item) |
        Sort-Object { $_.FullName.Length } -Descending |
        Where-Object { -not (Get-ChildItem -LiteralPath $_.FullName -Force) } |
        Remove-Item
    Rename-Item -LiteralPath $log.FullName -NewName ($log.Name -replace '\.csv$', '.undone.csv')
    Write-Host "Restored $restored file(s) from $($log.Name)."
    return
}

$cutoff = (Get-Date).AddMinutes(-$MinAgeMinutes)
# Top level only: files already inside category folders are not re-sorted
$files = Get-ChildItem -LiteralPath $Path -File |
    Where-Object {
        $ext = $_.Extension.TrimStart('.').ToLowerInvariant()
        -not ($SkipExtensions -contains $ext) -and
        -not $_.Attributes.HasFlag([IO.FileAttributes]::Hidden) -and
        $_.Name -ne 'desktop.ini' -and
        $_.LastWriteTime -lt $cutoff
    }

if (-not $files) { Write-Host 'Nothing to sort.'; return }

$moves = foreach ($f in $files) {
    $category = Get-Category $f.Extension
    $dateDir  = $f.LastWriteTime.ToString($DateFormat)
    [pscustomobject]@{
        File     = $f
        Category = $category
        DestDir  = Join-Path (Join-Path $Path $category) $dateDir
    }
}

if (-not $Apply) {
    Write-Host "DRY RUN - nothing moved. Re-run with -Apply to move files.`n"
    $moves | Sort-Object Category, DestDir |
        Format-Table @{n='File';e={$_.File.Name}}, @{n='Destination';e={$_.DestDir.Substring($Path.Length).TrimStart('\')}} -AutoSize
    $moves | Group-Object Category | Sort-Object Name |
        Format-Table @{n='Category';e={$_.Name}}, Count -AutoSize
    return
}

New-Item -ItemType Directory -Path $LogDir -Force | Out-Null
$logFile = Join-Path $LogDir ("sort-{0}.csv" -f (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
$log = New-Object System.Collections.Generic.List[object]
$failed = 0

foreach ($m in $moves) {
    try {
        New-Item -ItemType Directory -Path $m.DestDir -Force | Out-Null
        $dest = Get-UniquePath $m.DestDir $m.File.Name
        Move-Item -LiteralPath $m.File.FullName -Destination $dest
        $log.Add([pscustomobject]@{ From = $m.File.FullName; To = $dest })
    } catch {
        # Usually a file still open in another program
        Write-Warning "Skipped $($m.File.Name): $($_.Exception.Message)"
        $failed++
    }
}

if ($log.Count) { $log | Export-Csv -LiteralPath $logFile -NoTypeInformation -Encoding UTF8 }
Write-Host "Moved $($log.Count) file(s), skipped $failed. Log: $logFile"
Write-Host 'Undo with: .\Sort-Downloads.ps1 -Undo'
