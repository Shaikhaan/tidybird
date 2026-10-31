# Tests for Tidybird. Runs against dist\Tidybird.exe in temporary folders only.
# Usage: powershell -NoProfile -STA -ExecutionPolicy Bypass -File tests\Test-Tidybird.ps1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
$exe = Join-Path $PSScriptRoot '..\dist\Tidybird.exe' | Resolve-Path
$asm = [Reflection.Assembly]::LoadFrom($exe)
$script = Join-Path $PSScriptRoot '..\Sort-Downloads.ps1'
$S = [Tidybird.Sorter]
$failures = 0

function Check([string]$name, [bool]$ok, [string]$detail = '') {
    if ($ok) { Write-Host "PASS  $name" -ForegroundColor Green }
    else { Write-Host "FAIL  $name  $detail" -ForegroundColor Red; $script:failures++ }
}

function New-TestDir {
    $d = Join-Path $env:TEMP ('tb-test-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory $d | Out-Null
    return $d
}

function New-File([string]$dir, [string]$name, [datetime]$modified = '2026-10-15') {
    $p = Join-Path $dir $name
    [IO.File]::WriteAllText($p, $name)
    (Get-Item -LiteralPath $p -Force).LastWriteTime = $modified
    return $p
}

function Rel([string]$root) {
    @(Get-ChildItem -LiteralPath $root -Recurse -File -Force | ForEach-Object { $_.FullName.Substring($root.Length + 1) } | Sort-Object)
}

function Sort-All([string]$dir, [string]$fmt = 'yyyy-MM') {
    $S::Apply($dir, $S::Plan($dir, $fmt, [TimeSpan]::Zero))
}

# --- Categories and odd names -------------------------------------------------
$t = New-TestDir
'Photo.JPG', 'README', '.gitignore', 'résumé été.pdf', 'a, b, c.txt', 'archive.tar.gz', 'x.' | ForEach-Object { New-File $t $_ | Out-Null }
$r = Sort-All $t
$files = Rel $t | Where-Object { $_ -notlike '.sort-logs*' }
Check 'uppercase extension -> Images' ($files -contains 'Images\2026-10\Photo.JPG')
Check 'no extension -> Other' ($files -contains 'Other\2026-10\README')
Check 'dotfile -> Other' ($files -contains 'Other\2026-10\.gitignore')
Check 'unicode name sorted' ($files -contains 'Documents\2026-10\résumé été.pdf')
Check 'comma in name sorted' ($files -contains 'Documents\2026-10\a, b, c.txt')
Check '.tar.gz -> Archives' ($files -contains 'Archives\2026-10\archive.tar.gz')
Check 'undo restores odd names' (($S::Undo($t) -eq $r.Moved) -and ((Rel $t | Where-Object { $_ -notlike '.sort-logs*' }).Count -eq 7))
Remove-Item -LiteralPath $t -Recurse -Force

# --- Things that must never move ------------------------------------------------
$t = New-TestDir
New-File $t 'movie.mp4.crdownload' | Out-Null
New-File $t 'big.iso.part' | Out-Null
New-File $t 'desktop.ini' | Out-Null
$h = New-File $t 'secret.jpg'; (Get-Item $h).Attributes = 'Hidden'
New-File $t 'fresh.jpg' (Get-Date) | Out-Null
Check 'partial/hidden/desktop.ini skipped' ($S::Plan($t, 'yyyy-MM', [TimeSpan]::Zero).Count -eq 1)
Check 'auto mode skips files under 1 minute old' ($S::Plan($t, 'yyyy-MM', [TimeSpan]::FromMinutes(1)).Count -eq 0)
$self = New-File $t 'Tidybird.exe'
$selfField = $S.GetField('SelfPath')
if ($selfField) { $selfField.SetValue($null, $self) }
Check 'never moves its own exe' (-not ($S::Plan($t, '', [TimeSpan]::Zero) | Where-Object { $_.Source -eq $self }))
if ($selfField) { $selfField.SetValue($null, $null) }
Remove-Item -LiteralPath $t -Recurse -Force

# --- Name clashes, read-only files, folder with trailing backslash ---------------
$t = New-TestDir
New-File $t 'dup.jpg' | Out-Null
Sort-All $t | Out-Null
New-File $t 'dup.jpg' | Out-Null
$ro = New-File $t 'locked-down.pdf'; (Get-Item $ro).IsReadOnly = $true
$r = $S::Apply("$t\", $S::Plan("$t\", 'yyyy-MM', [TimeSpan]::Zero))
$files = Rel $t
Check 'clash renamed to "dup (1).jpg"' (($files -contains 'Images\2026-10\dup.jpg') -and ($files -contains 'Images\2026-10\dup (1).jpg'))
Check 'read-only file moved' ($files -contains 'Documents\2026-10\locked-down.pdf')
Check 'trailing backslash folder works' ($r.Moved -eq 2)
Get-ChildItem $t -Recurse -File | ForEach-Object { $_.IsReadOnly = $false }
Remove-Item -LiteralPath $t -Recurse -Force

# --- Locked file is skipped and picked up later ---------------------------------
$t = New-TestDir
$p = New-File $t 'open.docx'
$fs = [IO.File]::Open($p, 'Open', 'Read', 'None')
$r = Sort-All $t
Check 'locked file counted as failed' ($r.Failed -eq 1 -and $r.Moved -eq 0)
$fs.Close()
Check 'locked file moved once released' ((Sort-All $t).Moved -eq 1)
Remove-Item -LiteralPath $t -Recurse -Force

# --- Rapid runs each keep their own undo log ------------------------------------
$t = New-TestDir
1..5 | ForEach-Object { New-File $t "f$_.txt" | Out-Null; Sort-All $t | Out-Null }
$logs = @(Get-ChildItem (Join-Path $t '.sort-logs') -Filter 'sort-*.csv')
Check 'five rapid runs -> five logs' ($logs.Count -eq 5) "got $($logs.Count)"
$restored = 0; while (($n = $S::Undo($t)) -ge 0) { $restored += $n }
Check 'undo all rapid runs restores all 5' ($restored -eq 5) "restored $restored"
Remove-Item -LiteralPath $t -Recurse -Force

# --- Undo with a file that cannot be moved back keeps it undoable ---------------
$t = New-TestDir
New-File $t 'keep.pdf' | Out-Null; New-File $t 'busy.pdf' | Out-Null
Sort-All $t | Out-Null
$fs = [IO.File]::Open((Join-Path $t 'Documents\2026-10\busy.pdf'), 'Open', 'Read', 'None')
$n1 = $S::Undo($t)
$fs.Close()
$n2 = $S::Undo($t)
Check 'partial undo: 1 restored, rest retried' ($n1 -eq 1 -and $n2 -eq 1) "first $n1, second $n2"
Check 'partial undo: both files back' ((Test-Path "$t\keep.pdf") -and (Test-Path "$t\busy.pdf"))
Remove-Item -LiteralPath $t -Recurse -Force

# --- User folders and non-empty category folders survive undo -------------------
$t = New-TestDir
New-Item -ItemType Directory "$t\My Stuff" | Out-Null
New-Item -ItemType Directory "$t\Images\Wallpapers" | Out-Null
New-File "$t\Images\Wallpapers" 'mine.png' | Out-Null
New-File $t 'cat.png' | Out-Null
Sort-All $t | Out-Null
$S::Undo($t) | Out-Null
Check 'undo keeps user folder' (Test-Path "$t\My Stuff")
Check 'undo keeps user files inside category folder' (Test-Path "$t\Images\Wallpapers\mine.png")
Check 'undo removes emptied date folder' (-not (Test-Path "$t\Images\2026-10"))
Remove-Item -LiteralPath $t -Recurse -Force

# --- Log is compatible with Sort-Downloads.ps1 ----------------------------------
if (Test-Path $script) {
    $t = New-TestDir
    New-File $t 'a, b.pdf' | Out-Null; New-File $t 'ü.jpg' | Out-Null
    Sort-All $t | Out-Null
    & powershell -NoProfile -ExecutionPolicy Bypass -File $script -Path $t -Undo | Out-Null
    Check 'script can undo app log' ((Test-Path -LiteralPath "$t\a, b.pdf") -and (Test-Path -LiteralPath "$t\ü.jpg"))
    Remove-Item -LiteralPath $t -Recurse -Force
}

# --- UI: preview is filled when the window first opens --------------------------
$t = New-TestDir
New-File $t 'one.jpg' | Out-Null; New-File $t 'two.pdf' | Out-Null
$type = $asm.GetType('Tidybird.MainForm')
$bf = [Reflection.BindingFlags]'NonPublic,Instance'
$ctorArgs = New-Object 'object[]' 2
$ctorArgs[0] = $false
$ctorArgs[1] = (New-Object Threading.EventWaitHandle($false, [Threading.EventResetMode]::AutoReset)).psobject.BaseObject
$f = $type.GetConstructors()[0].Invoke($ctorArgs)
$settings = $type.GetField('settings', $bf).GetValue($f)
$settings.GetType().GetField('Folder').SetValue($settings, $t)
$list = $type.GetField('list', $bf).GetValue($f)
$f.Show(); 1..5 | ForEach-Object { [Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 50 }
Check 'preview filled on first show' ($list.Items.Count -eq 2) "items: $($list.Items.Count)"
Check 'window icon loaded from embedded resource' ($f.Icon.Width -eq 32)

# Auto-sort and undo through the window. These save settings, so restore the user's file after.
$settingsFile = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'Tidybird\settings.ini'
$hadSettings = Test-Path $settingsFile
if ($hadSettings) { $backup = Get-Content -Raw $settingsFile }
$status = $type.GetField('status', $bf).GetValue($f)
$autoBox = $type.GetField('autoBox', $bf).GetValue($f)
$autoBox.Checked = $true
[Windows.Forms.Application]::DoEvents()
Check 'auto-sort moves old files' ((Test-Path "$t\Images\2026-10\one.jpg") -and (Test-Path "$t\Documents\2026-10\two.pdf"))
New-File $t 'brand-new.zip' (Get-Date) | Out-Null
$type.GetMethod('AutoSortTick', $bf).Invoke($f, $null)
Check 'auto-sort leaves just-downloaded file' (Test-Path "$t\brand-new.zip")
$type.GetMethod('UndoLast', $bf).Invoke($f, $null)
Check 'undo pauses auto-sort' (-not $autoBox.Checked) "status: $($status.Text)"
Check 'undo puts files back' ((Test-Path "$t\one.jpg") -and (Test-Path "$t\two.pdf"))
$type.GetField('tray', $bf).GetValue($f).Visible = $false
if ($hadSettings) { Set-Content -NoNewline $settingsFile $backup } else { Remove-Item (Split-Path $settingsFile) -Recurse -Force }
$f.Dispose()
Remove-Item -LiteralPath $t -Recurse -Force

Write-Host ''
if ($failures) { Write-Host "$failures test(s) failed" -ForegroundColor Red; exit 1 }
Write-Host 'All tests passed' -ForegroundColor Green
