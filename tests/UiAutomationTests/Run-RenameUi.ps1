# Real Windows input regression for inline rename. Requires an unlocked Windows desktop.
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$app = $null

function Assert([bool]$condition, [string]$message) {
    if (-not $condition) { throw "ASSERTION FAILED: $message" }
    Write-Host "PASS: $message"
}
function Wait-For([scriptblock]$probe, [string]$description) {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    while ($clock.ElapsedMilliseconds -lt 15000) {
        if ($script:app -and $script:app.HasExited) { throw 'The sample app exited.' }
        try { $value = & $probe; if ($value) { return $value } }
        catch [System.Windows.Automation.ElementNotAvailableException] { }
        Start-Sleep -Milliseconds 80
    }
    throw "Timeout: $description"
}
function Find-Id([string]$id) {
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}
function Text {
    $pattern = [System.Windows.Automation.TextPattern]$editor.GetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern)
    $pattern.DocumentRange.GetText(-1)
}
function Open-Rename {
    $editor.SetFocus()
    $pattern = [System.Windows.Automation.TextPattern]$editor.GetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern)
    $word = $pattern.DocumentRange.FindText('numbers', $false, $false)
    Assert ($null -ne $word) 'C# local variable is present'
    $word.Select()
    [System.Windows.Forms.SendKeys]::SendWait('{F2}')
    Wait-For { $input = Find-Id 'RenameInput'; if ($input -and $input.Current.HasKeyboardFocus) { $input } } 'inline rename keyboard focus'
}
function Invoke-Id([string]$id) {
    $button = Wait-For { Find-Id $id } $id
    ([System.Windows.Automation.InvokePattern]$button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
}

try {
    if (-not [Environment]::UserInteractive) { throw 'Run on an interactive Windows desktop.' }
    if (-not $SkipBuild) {
        & (Join-Path $root 'scripts\build-windows.ps1') -Configuration $Configuration -Platform x64
        if ($LASTEXITCODE -ne 0) { throw 'Windows build failed.' }
    }
    Add-Type -AssemblyName UIAutomationClient
    Add-Type -AssemblyName UIAutomationTypes
    Add-Type -AssemblyName System.Windows.Forms
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class RenameUiWindow {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
}
'@
    $exe = Join-Path $root "src\MonacoNative.SampleApp\bin\x64\$Configuration\net10.0-windows10.0.26100.0\MonacoNative.SampleApp.exe"
    $app = Start-Process -FilePath $exe -PassThru
    $processCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $app.Id)
    $window = Wait-For {
        [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
            [System.Windows.Automation.TreeScope]::Children, $processCondition)
    } 'sample window'
    [RenameUiWindow]::SetForegroundWindow($app.MainWindowHandle) | Out-Null
    $editor = Wait-For { Find-Id 'MonacoEditorSurface' } 'editor'
    $sample = Wait-For { Find-Id 'SampleBox' } 'sample selector'
    $sample.SetFocus()
    [System.Windows.Forms.SendKeys]::SendWait('{HOME}{DOWN}{DOWN}{ENTER}')
    $original = Wait-For { $value = Text; if ($value.Contains('var numbers = new List<int>')) { $value } } 'C# sample'

    $input = Open-Rename
    [System.Windows.Forms.SendKeys]::SendWait('cancelledNumbers')
    $value = [System.Windows.Automation.ValuePattern]$input.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    Assert ($value.Current.Value -eq 'cancelledNumbers') 'typing replaces the selected name in RenameInput'
    Assert ((Text) -eq $original) 'typing in rename never edits the document or its selection'
    [System.Windows.Forms.SendKeys]::SendWait('^aotherName')
    Assert ((Text) -eq $original) 'Ctrl+A remains local to the rename TextBox'
    [System.Windows.Forms.SendKeys]::SendWait('^z')
    Assert ((Text) -eq $original) 'Ctrl+Z remains local to the rename TextBox'
    [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
    Assert ((Text) -eq $original) 'Escape cancels without changing the document'

    $null = Open-Rename
    [System.Windows.Forms.SendKeys]::SendWait('previewNumbers^{ENTER}')
    $null = Wait-For { Find-Id 'RenamePreviewApply' } 'refactor preview'
    Assert ((Text) -eq $original) 'Ctrl+Enter opens a real preview before changing text'
    $previewTree = Wait-For { Find-Id 'RefactorPreviewTree' } 'docked change tree'
    $previewTab = Wait-For { Find-Id 'RefactorPreviewEditorTab' } 'multi-diff editor tab'
    $previewPanel = Wait-For { Find-Id 'RenamePreview' } 'docked preview panel'
    Assert (-not $previewTree.Current.IsOffscreen) 'change tree is visible in the docked panel'
    Assert (-not $previewTab.Current.IsOffscreen) 'preview opens a separate editor tab'
    Assert ($previewPanel.Current.BoundingRectangle.Top -gt $previewTab.Current.BoundingRectangle.Bottom) 'panel is below the editor tab rather than a floating input popup'

    Invoke-Id 'RenamePreviewDiscard'
    Assert ((Text) -eq $original) 'discarding preview leaves every source occurrence unchanged'

    $null = Open-Rename
    [System.Windows.Forms.SendKeys]::SendWait('renamedNumbers{ENTER}')
    $expected = $original.Replace('numbers', 'renamedNumbers')
    $null = Wait-For { (Text) -eq $expected } 'semantic rename applies all occurrences'
    Assert ((Text) -eq $expected) 'Enter applies the provider workspace edit'
    $editor.SetFocus()
    [System.Windows.Forms.SendKeys]::SendWait('^z')
    $null = Wait-For { (Text) -eq $original } 'single-step rename undo'
    Assert ((Text) -eq $original) 'one Ctrl+Z restores the entire rename'

    $null = Open-Rename
    [System.Windows.Forms.SendKeys]::SendWait('appliedNumbers^{ENTER}')
    Invoke-Id 'RenamePreviewApply'
    $expected = $original.Replace('numbers', 'appliedNumbers')
    $null = Wait-For { (Text) -eq $expected } 'preview apply'
    Assert ((Text) -eq $expected) 'preview Apply commits selected provider edits'
    $editor.SetFocus()
    [System.Windows.Forms.SendKeys]::SendWait('^z')
    $null = Wait-For { (Text) -eq $original } 'preview undo'

    $null = Open-Rename
    [System.Windows.Forms.SendKeys]::SendWait('blurredNumbers')
    $sample.SetFocus()
    $null = Wait-For { $input = Find-Id 'RenameInput'; (-not $input) -or $input.Current.IsOffscreen } 'rename closes on blur'
    Assert ((Text) -eq $original) 'focus leaving rename cancels pending text'

    # Menu dispatch has its own finally/focus path; this must preserve the new TextBox.
    $mode = Wait-For { Find-Id 'CommandMode_editor.action.rename' } 'rename command menu policy'
    $mode.SetFocus()
    [System.Windows.Forms.SendKeys]::SendWait('{HOME}{ENTER}')
    $editor.SetFocus()
    $pattern = [System.Windows.Automation.TextPattern]$editor.GetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern)
    $pattern.DocumentRange.FindText('numbers', $false, $false).Select()
    [System.Windows.Forms.SendKeys]::SendWait('+{F10}')
    $menuId = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'EditorContextMenu_editor.action.rename')
    $menuCondition = New-Object System.Windows.Automation.AndCondition($menuId, $processCondition)
    $menuItem = Wait-For {
        [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
            [System.Windows.Automation.TreeScope]::Descendants, $menuCondition)
    } 'Rename Symbol context-menu entry'
    ([System.Windows.Automation.InvokePattern]$menuItem.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
    $null = Wait-For { $input = Find-Id 'RenameInput'; if ($input -and $input.Current.HasKeyboardFocus) { $input } } 'context-menu rename owns focus'
    [System.Windows.Forms.SendKeys]::SendWait('menuNumbers')
    Assert ((Text) -eq $original) 'context-menu completion does not steal focus back to the editor'
    [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
    Write-Host 'Rename UI regressions passed.'
}
finally {
    if ($app -and -not $app.HasExited) { $app.Kill(); $app.WaitForExit() }
}
