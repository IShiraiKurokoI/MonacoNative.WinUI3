# Windows desktop end-to-end automation; uses UIA + real SendInput mouse + SendKeys.
# Prerequisites: interactive, unlocked Windows desktop, Windows PowerShell 5.1, built sample.
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [string]$OutputDirectory = '',
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root 'artifacts\ui-tests' }
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$OutputDirectory = (Resolve-Path $OutputDirectory).Path
$script:steps = New-Object System.Collections.Generic.List[object]
$script:app = $null
$script:success = $false
$script:failure = $null
$oldProfileDir = [Environment]::GetEnvironmentVariable('MONACONATIVE_PROFILE_DIR')

function Check([bool]$assertion, [string]$message) {
    if (-not $assertion) { throw "ASSERTION FAILED: $message" }
    Write-Host "PASS: $message"
    $script:steps.Add([pscustomobject]@{ name=$message; pass=$true })
}
function Until([scriptblock]$probe, [string]$description, [int]$timeoutMs=15000) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $timeoutMs) {
        if ($script:app -and $script:app.HasExited) {
            throw "SampleApp exited before '$description' (exit code $($script:app.ExitCode)). Check the runtime XAML exception."
        }
        try { $result = & $probe; if ($result) { return $result } }
        catch [System.Windows.Automation.ElementNotAvailableException] { }
        Start-Sleep -Milliseconds 100
    }
    throw "Timeout: $description"
}
function FindById($rootElement, [string]$automationId) {
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $automationId)
    return $rootElement.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}
function ClickElement($element) {
    if (-not $element) { throw 'UI element missing' }
    $pattern = $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $pattern.Invoke()
}
function GetEditorText($element) {
    $pattern = [System.Windows.Automation.TextPattern]$element.GetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern)
    return $pattern.DocumentRange.GetText(-1)
}
function CheckCursor($state, [string]$expected) {
    $name = Until { $state.Current.Name -match "Cursor=$expected(?:;|$)" } "cursor=$expected" 3500
    Check ($null -ne $name) "hover cursor is $expected"
}

try {
    if (-not [Environment]::UserInteractive) { throw 'UI automation requires an interactive desktop session.' }
    if (-not $SkipBuild) {
        & (Join-Path $root 'scripts\build-windows.ps1') -Configuration $Configuration -Platform x64
        if ($LASTEXITCODE -ne 0) { throw "Windows build failed ($LASTEXITCODE)" }
    }

    Add-Type -AssemblyName UIAutomationClient
    Add-Type -AssemblyName UIAutomationTypes
    Add-Type -AssemblyName System.Windows.Forms
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class WinUiTestMouse {
    [DllImport("user32.dll", SetLastError=true)] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, int data, UIntPtr extraInfo);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);
    public static void Move(int x, int y) { if (!SetCursorPos(x,y)) throw new InvalidOperationException("SetCursorPos failed"); }
    public static void Down() { mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero); }
    public static void Up() { mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero); }
    public static void RightClick(int x, int y) {
        Move(x,y); System.Threading.Thread.Sleep(80);
        mouse_event(0x0008, 0, 0, 0, UIntPtr.Zero);
        mouse_event(0x0010, 0, 0, 0, UIntPtr.Zero);
    }
    public static void Click(int x, int y) { Move(x,y); System.Threading.Thread.Sleep(80); Down(); Up(); }
}
'@

    $exe = Join-Path $root "src\MonacoNative.SampleApp\bin\x64\$Configuration\net8.0-windows10.0.26100.0\MonacoNative.SampleApp.exe"
    if (-not (Test-Path $exe)) { throw "Sample executable missing: $exe" }
    $profileDir = Join-Path $OutputDirectory 'method-profiles'
    New-Item -ItemType Directory -Force -Path $profileDir | Out-Null
    [Environment]::SetEnvironmentVariable('MONACONATIVE_PROFILE_DIR', $profileDir)
    $script:app = Start-Process -FilePath $exe -PassThru
    $pidToFind = $script:app.Id
    $windowCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $pidToFind)
    $window = Until {
        [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
            [System.Windows.Automation.TreeScope]::Children, $windowCondition)
    } 'WinUI sample window'
    $editor = Until { FindById $window 'MonacoEditorSurface' } 'Monaco Editor UIA document'
    $state = Until { FindById $window 'EditorStatus' } 'Status telemetry'
    $load = Until { FindById $window 'LoadSampleButton' } 'Load Sample button'
    $toggle = Until { FindById $window 'ProfilerToggle' } 'Profile toggle'
    $export = Until { FindById $window 'ExportProfileButton' } 'Profile export button'
    Check ($editor.Current.ControlType -eq [System.Windows.Automation.ControlType]::Document) 'editor exposes UIA Document peer'

    [WinUiTestMouse]::SetForegroundWindow($script:app.MainWindowHandle) | Out-Null
    ClickElement $load
    $source = Until { $text = GetEditorText $editor; if ($text -like '*#include*') { return $text } } 'sample loading'
    Check ($source.Contains('int main()')) 'sample load updates real editor text model'

    $bounds = $editor.Current.BoundingRectangle
    Check ($bounds.Width -ge 500 -and $bounds.Height -ge 200) 'editor UIA bounds valid'
    $dpi = [WinUiTestMouse]::GetDpiForWindow($script:app.MainWindowHandle)
    $scale = if ($dpi -gt 0) { $dpi / 96.0 } else { 1.0 }
    $cx = [int]($bounds.Left + [Math]::Min($bounds.Width / 2.0, 200 * $scale))
    $cy = [int]($bounds.Top + [Math]::Min($bounds.Height / 2.0, 70 * $scale))
    $gx = [int]($bounds.Left + 25 * $scale)
    $mx = [int]($bounds.Right - 45 * $scale)

    [WinUiTestMouse]::Move($cx, $cy)
    CheckCursor $state 'Text'
    [WinUiTestMouse]::Move($gx, $cy)
    CheckCursor $state 'Arrow'
    [WinUiTestMouse]::Move($mx, $cy)
    CheckCursor $state 'Minimap'
    [WinUiTestMouse]::Move($cx, $cy)
    CheckCursor $state 'Text'

    # Hit-test, real pointer, selection and editing. No synthetic editor-only model API calls.
    $togglePattern = [System.Windows.Automation.TogglePattern]$toggle.GetCurrentPattern(
        [System.Windows.Automation.TogglePattern]::Pattern)
    if ($togglePattern.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::On) { $togglePattern.Toggle() }
    Until { $state.Current.Name -like '*Profile=on*' } 'profiler enabled' | Out-Null
    [WinUiTestMouse]::Click($cx, $cy)
    [System.Windows.Forms.SendKeys]::SendWait('abc')
    $edited = Until { $text = GetEditorText $editor; if ($text.Length -ge $source.Length + 3) { return $text } } 'keyboard insert'
    Check ($edited.Length -ge $source.Length + 3) 'keyboard typing changes document through native input path'
    [System.Windows.Forms.SendKeys]::SendWait('^z')
    $undone = Until { $text = GetEditorText $editor; if ($text.Length -lt $edited.Length) { return $text } } 'Ctrl+Z undo'
    Check ($undone.Length -lt $edited.Length) 'undo reduces inserted text'
    [System.Windows.Forms.SendKeys]::SendWait('^y')
    $redone = Until { $text = GetEditorText $editor; if ($text.Length -gt $undone.Length) { return $text } } 'Ctrl+Y redo'
    Check ($redone.Length -gt $undone.Length) 'redo restores inserted text'

    [WinUiTestMouse]::Move($cx,$cy)
    [WinUiTestMouse]::Down()
    [WinUiTestMouse]::Move($cx + [int](90 * $scale), $cy)
    Start-Sleep -Milliseconds 130
    [WinUiTestMouse]::Up()
    $selection = Until {
        $pattern = [System.Windows.Automation.TextPattern]$editor.GetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern)
        $ranges = $pattern.GetSelection()
        if ($ranges.Count -gt 0 -and $ranges[0].GetText(-1).Length -gt 0) { return $ranges[0].GetText(-1) }
    } 'mouse drag selects text'
    Check ($selection.Length -gt 0) 'mouse drag selection is exposed through UIA TextPattern'

    # Gutter fold marker on the namespace opening line in Assets/Samples/demo.cpp (line 6).
    $foldx = [int]($bounds.Left + 48 * $scale)
    $foldy = [int]($bounds.Top + (8 + 5*22 + 11) * $scale)
    Until { $state.Current.Name -match 'Folds=([1-9][0-9]*)' } 'fold candidates built' | Out-Null
    [WinUiTestMouse]::Click($foldx, $foldy)
    Until { $state.Current.Name -match 'Collapsed=1(?:;|$)' } 'gutter fold collapse' | Out-Null
    Check ($state.Current.Name -match 'Collapsed=1(?:;|$)') 'gutter folding collapses block'
    [WinUiTestMouse]::Click($foldx, $foldy)
    Until { $state.Current.Name -match 'Collapsed=0(?:;|$)' } 'gutter fold expand' | Out-Null
    Check ($state.Current.Name -match 'Collapsed=0(?:;|$)') 'gutter folding expands block'

    # Right-click menu must execute editor commands even after the popup steals focus.
    [WinUiTestMouse]::RightClick($cx, $cy)
    $selectAll = Until {
        $result = FindById $window 'EditorContextMenu_editor.action.selectAll'
        if (-not $result) { $result = FindById ([System.Windows.Automation.AutomationElement]::RootElement) 'EditorContextMenu_editor.action.selectAll' }
        return $result
    } 'context menu select all'
    ClickElement $selectAll
    $selectedAll = Until {
        $pattern = [System.Windows.Automation.TextPattern]$editor.GetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern)
        $ranges = $pattern.GetSelection()
        if ($ranges.Count -gt 0 -and $ranges[0].GetText(-1).Length -ge $redone.Length) { return $ranges[0].GetText(-1) }
    } 'context menu select all execution'
    Check ($selectedAll.Length -ge $redone.Length) 'context menu dispatch selects all text'

    # Theme changes are synchronous with the requested theme; UIA state exposes it.
    $themeBox = Until { FindById $window 'ThemeBox' } 'theme selector'
    $themeBox.SetFocus()
    [System.Windows.Forms.SendKeys]::SendWait('{HOME}{DOWN}{ENTER}')
    Until { $state.Current.Name -match 'Theme=Light(?:;|$)' } 'switch editor to light' | Out-Null
    Check ($state.Current.Name -match 'Theme=Light(?:;|$)') 'light theme applied without delayed state'
    $themeBox.SetFocus()
    [System.Windows.Forms.SendKeys]::SendWait('{END}{ENTER}')
    Until { $state.Current.Name -match 'Theme=Dark(?:;|$)' } 'switch editor back to dark' | Out-Null
    Check ($state.Current.Name -match 'Theme=Dark(?:;|$)') 'dark theme applied after light'

    # The example selector is asset-backed; changing the language must also change lexical mode.
    $sampleBox = Until { FindById $window 'SampleBox' } 'example selector'
    $sampleBox.SetFocus()
    [System.Windows.Forms.SendKeys]::SendWait('{HOME}')
    Until { $state.Current.Name -match 'Language=html(?:;|$)' } 'HTML asset selected' | Out-Null
    Check ((GetEditorText $editor).Contains('<!DOCTYPE html>')) 'HTML source loaded from Assets'
    [System.Windows.Forms.SendKeys]::SendWait('{DOWN}')
    Until { $state.Current.Name -match 'Language=javascript(?:;|$)' } 'JavaScript asset selected' | Out-Null
    Check ((GetEditorText $editor).Contains('class Counter')) 'JavaScript source loaded from Assets'
    [System.Windows.Forms.SendKeys]::SendWait('{DOWN}')
    Until { $state.Current.Name -match 'Language=csharp(?:;|$)' } 'C# asset selected' | Out-Null
    Check ((GetEditorText $editor).Contains('public static class Program')) 'C# source loaded from Assets'
    [System.Windows.Forms.SendKeys]::SendWait('{END}')
    Until { $state.Current.Name -match 'Language=cpp(?:;|$)' } 'C++ asset selected' | Out-Null
    Check ((GetEditorText $editor).Contains('#include <iostream>')) 'C++ source loaded from Assets'

    # View switches live in a toolbar flyout (not in the sidebar).
    $viewOptions = Until { FindById $window 'ViewOptionsButton' } 'toolbar View Options dropdown'
    ClickElement $viewOptions
    $foldSwitch = Until {
        FindById ([System.Windows.Automation.AutomationElement]::RootElement) 'Option_folding'
    } 'toolbar folding toggle'
    $foldSwitchPattern = [System.Windows.Automation.TogglePattern]$foldSwitch.GetCurrentPattern(
        [System.Windows.Automation.TogglePattern]::Pattern)
    Check ($foldSwitchPattern.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On) 'toolbar folding checkmark reflects model'
    [System.Windows.Forms.SendKeys]::SendWait('{ESC}')

    # Non-basic actions default to ShortcutOnly; one policy controls right click and
    # keybinding execution. Changing it in the sample updates the XAML DP directly.
    [WinUiTestMouse]::RightClick($cx, $cy)
    Start-Sleep -Milliseconds 180
    $defaultAdvanced = FindById ([System.Windows.Automation.AutomationElement]::RootElement) 'EditorContextMenu_editor.action.addCommentLine'
    Check ($null -eq $defaultAdvanced) 'advanced commands are hidden from context menu by default'
    $defaultToggle = FindById ([System.Windows.Automation.AutomationElement]::RootElement) 'EditorContextMenu_editor.action.toggleMinimap'
    Check ($null -eq $defaultToggle) 'context-menu toggle commands are hidden by default'

    [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
    $commandMode = Until { FindById $window 'CommandMode_editor.action.addCommentLine' } 'comment command policy'
    $commandMode.SetFocus()
    [System.Windows.Forms.SendKeys]::SendWait('{HOME}{ENTER}') # ContextMenuAndShortcut
    Start-Sleep -Milliseconds 100
    [WinUiTestMouse]::RightClick($cx, $cy)
    $advancedMenuItem = Until {
        $root = [System.Windows.Automation.AutomationElement]::RootElement
        FindById $root 'EditorContextMenu_editor.action.addCommentLine'
    } 'explicitly enabled advanced context menu entry'
    Check ($null -ne $advancedMenuItem) 'Menu + Shortcut policy shows advanced command'
    [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
    $commandMode.SetFocus()
    [System.Windows.Forms.SendKeys]::SendWait('{END}{ENTER}') # Disabled
    Start-Sleep -Milliseconds 100
    [WinUiTestMouse]::RightClick($cx, $cy)
    Start-Sleep -Milliseconds 180
    $hidden = FindById ([System.Windows.Automation.AutomationElement]::RootElement) 'EditorContextMenu_editor.action.addCommentLine'
    Check ($null -eq $hidden) 'Disabled policy hides advanced right-click command'
    [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
    [WinUiTestMouse]::Click($cx, $cy)
    $beforeDisabledChord = GetEditorText $editor
    [System.Windows.Forms.SendKeys]::SendWait('^k^c')
    Start-Sleep -Milliseconds 250
    Check ((GetEditorText $editor) -eq $beforeDisabledChord) 'Disabled policy prevents registered Ctrl+K Ctrl+C command'
    $commandMode.SetFocus()
    [System.Windows.Forms.SendKeys]::SendWait('{HOME}{DOWN}{ENTER}') # ShortcutOnly

    # Find and Replace share the same in-editor widget, with live mode switching.
    $findOpen = Until { FindById $window 'OpenFindButton' } 'sidebar Find button'
    ClickElement $findOpen
    $findBox = Until { FindById $window 'FindTextBox' } 'live Find box'
    Check ($null -ne $findBox) 'Ctrl+F style Find widget appears'
    $findValue = [System.Windows.Automation.ValuePattern]$findBox.GetCurrentPattern(
        [System.Windows.Automation.ValuePattern]::Pattern)
    $findValue.SetValue('demo')
    $replaceOpen = Until { FindById $window 'OpenReplaceButton' } 'sidebar Replace button'
    ClickElement $replaceOpen
    $replaceText = Until { FindById $window 'ReplaceTextBox' } 'expanded Replace box'
    Check ($null -ne $replaceText) 'Find-to-Replace switches live without closing widget'
    Check ($findValue.Current.Value -eq 'demo') 'switching Find to Replace retains the current query'
    $regexToggle = Until { FindById $window 'FindRegex' } 'regex option'
    Check ($null -ne $regexToggle) 'regex option exposed through UI Automation'
    $replaceChevron = Until { FindById $window 'FindReplaceExpander' } 'replace expander'
    Check ($null -ne $replaceChevron) 'one compact expand/collapse control'
    $findClose = Until { FindById $window 'FindCloseButton' } 'find close button'
    $editorBounds = $editor.Current.BoundingRectangle
    $closeBounds = $findClose.Current.BoundingRectangle
    Check ($closeBounds.Right -lt ($editorBounds.Right - 86 * $scale)) 'find controls do not overlap minimap region'
    $formatButton = Until { FindById $window 'FormatSampleButton' } 'sample format button'
    Check ($null -ne $formatButton) 'explicit format sample command is available'


    Start-Sleep -Milliseconds 450 # Allow queued Win2D render to finish before exporting.
    ClickElement $export
    $profile = Until { Get-ChildItem -Path $profileDir -Filter 'method-profile-*.json' -File |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1 } 'JSON profile export'
    $csv = [IO.Path]::ChangeExtension($profile.FullName, '.csv')
    Check (Test-Path $csv) 'CSV method profile exported'
    $data = Get-Content -Raw -Path $profile.FullName | ConvertFrom-Json
    Check ($data.Methods.Count -ge 3) 'method profile has real instrumentation data'
    Check (@($data.Methods | Where-Object Name -eq 'HitTest').Count -gt 0) 'method profile includes mouse hit testing'
    Check (@($data.Methods | Where-Object { $_.Calls -gt 0 -and $_.TotalInclusiveMs -ge 0 }).Count -gt 0) 'method timings and call counts emitted'

    $script:success = $true
} catch {
    $script:failure = $_.Exception.Message
    Write-Error -Message $script:failure -ErrorAction Continue
} finally {
    $report = [ordered]@{
        schemaVersion = 1
        timestampUtc = [DateTime]::UtcNow.ToString('o')
        pass = $script:success
        error = $script:failure
        steps = @($script:steps.ToArray())
        profiles = @((Get-ChildItem -Path (Join-Path $OutputDirectory 'method-profiles') -Filter 'method-profile-*.json' -File -ErrorAction SilentlyContinue | ForEach-Object FullName))
        process = if ($script:app) { [ordered]@{ id=$script:app.Id; peakWorkingSetBytes=$script:app.PeakWorkingSet64; cpuMilliseconds=$script:app.TotalProcessorTime.TotalMilliseconds } } else { $null }
    }
    $reportPath = Join-Path $OutputDirectory 'ui-test-report.json'
    $report | ConvertTo-Json -Depth 8 | Set-Content -Encoding UTF8 $reportPath
    Write-Host "UI test report: $reportPath"
    if ($script:app) { try { if (-not $script:app.HasExited) { $script:app.Kill() }; $script:app.Dispose() } catch {} }
    [Environment]::SetEnvironmentVariable('MONACONATIVE_PROFILE_DIR', $oldProfileDir)
}
if (-not $script:success) { exit 1 }
Write-Host 'Interactive WinUI UI automation PASS'
