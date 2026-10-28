# Native tab-strip/document lifecycle regression tests using UIA and real keyboard/mouse input.
# Requires an interactive, unlocked Windows desktop and the built WinUI sample.
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [string]$OutputDirectory = '',
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repoRoot 'artifacts\ui-document-tests' }
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$script:steps = New-Object System.Collections.Generic.List[object]
$script:app = $null
$script:window = $null
$script:editor = $null
$script:state = $null
$script:success = $false
$script:failure = $null

function Check([bool]$assertion, [string]$message) {
    if (-not $assertion) { throw "ASSERTION FAILED: $message" }
    Write-Host "PASS: $message"
    $script:steps.Add([pscustomobject]@{ name=$message; pass=$true })
}

function Until([scriptblock]$probe, [string]$description, [int]$timeoutMs=15000) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    while ($timer.ElapsedMilliseconds -lt $timeoutMs) {
        if ($script:app -and $script:app.HasExited) {
            throw "Sample exited while waiting for '$description' (exit $($script:app.ExitCode))."
        }
        try { $value = & $probe; if ($value) { return $value } }
        catch [System.Windows.Automation.ElementNotAvailableException] { }
        Start-Sleep -Milliseconds 80
    }
    throw "Timeout: $description"
}

function FindById($rootElement, [string]$automationId) {
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $automationId)
    return $rootElement.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function InvokeElement($element) {
    $invoke = [System.Windows.Automation.InvokePattern]$element.GetCurrentPattern(
        [System.Windows.Automation.InvokePattern]::Pattern)
    $invoke.Invoke()
}

function Text {
    $pattern = [System.Windows.Automation.TextPattern]$script:editor.GetCurrentPattern(
        [System.Windows.Automation.TextPattern]::Pattern)
    return $pattern.DocumentRange.GetText(-1)
}

function State {
    $values = @{}
    foreach ($part in $script:state.Current.Name.Split(';')) {
        $separator = $part.IndexOf('=')
        if ($separator -gt 0) { $values[$part.Substring(0, $separator)] = $part.Substring($separator + 1) }
    }
    return $values
}

function ActiveUri { return [Uri]::UnescapeDataString((State)['ActiveDocumentUri']) }

function Keys([string]$keys) {
    $script:editor.SetFocus()
    [System.Windows.Forms.SendKeys]::SendWait($keys)
}

function SelectTab([string]$uri) {
    $tab = Until { FindById $script:window ('DocumentTab_' + $uri) } 'native document tab'
    $selection = [System.Windows.Automation.SelectionItemPattern]$tab.GetCurrentPattern(
        [System.Windows.Automation.SelectionItemPattern]::Pattern)
    $selection.Select()
    Until { (ActiveUri) -eq $uri } 'selected native tab activation' | Out-Null
}

function ClickViewOption([string]$automationId) {
    InvokeElement (Until { FindById $script:window 'ViewOptionsButton' } 'View Options menu')
    $option = Until {
        FindById ([System.Windows.Automation.AutomationElement]::RootElement) $automationId
    } $automationId
    # Real click exercises the sample's Click handler; changing IsChecked via UIA alone
    # would not prove that the editor's dependency property was changed.
    $bounds = $option.Current.BoundingRectangle
    if ($bounds.Width -le 0 -or $bounds.Height -le 0) { throw "Option has no clickable bounds: $automationId" }
    [WinUiDocumentTestMouse]::Click([int]($bounds.Left + $bounds.Width / 2), [int]($bounds.Top + $bounds.Height / 2))
}

function ClickSampleTabBarToggle {
    $toggle = Until { FindById $script:window 'TabBarToggle' } 'Sample Tab Bar toggle'
    $bounds = $toggle.Current.BoundingRectangle
    if ($bounds.Width -le 0 -or $bounds.Height -le 0) { throw 'Sample Tab Bar toggle has no clickable bounds.' }
    [WinUiDocumentTestMouse]::Click([int]($bounds.Left + $bounds.Width / 2), [int]($bounds.Top + $bounds.Height / 2))
}

function TabBounds([string]$uri) {
    $tab = Until { FindById $script:window ('DocumentTab_' + $uri) } 'tab bounds'
    return $tab.Current.BoundingRectangle
}

function TabElements([string]$uri) {
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, ('DocumentTab_' + $uri))
    return @($script:window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition))
}

function OpenTabMenuItem($tab, [string]$command) {
    $bounds = $tab.Current.BoundingRectangle
    [WinUiDocumentTestMouse]::RightClick([int]($bounds.Left + $bounds.Width / 2), [int]($bounds.Top + $bounds.Height / 2))
    return Until { FindById ([System.Windows.Automation.AutomationElement]::RootElement) $command } $command
}

function TabMenuAt($tab, [string]$command) {
    InvokeElement (OpenTabMenuItem $tab $command)
}

function TabMenu([string]$uri, [string]$command) {
    $tab = Until { FindById $script:window ('DocumentTab_' + $uri) } 'tab for context menu'
    TabMenuAt $tab $command
}

function TabMenuSubcommand($tab, [string]$command) {
    $submenu = OpenTabMenuItem $tab 'workbench.action.splitAndMove'
    try {
        $expand = [System.Windows.Automation.ExpandCollapsePattern]$submenu.GetCurrentPattern(
            [System.Windows.Automation.ExpandCollapsePattern]::Pattern)
        if ($expand.Current.ExpandCollapseState -eq [System.Windows.Automation.ExpandCollapseState]::Collapsed) {
            $expand.Expand()
        }
    }
    catch { InvokeElement $submenu }
    $item = Until { FindById ([System.Windows.Automation.AutomationElement]::RootElement) $command } $command
    InvokeElement $item
}

function SelectUiLanguage([string]$culture) {
    $combo = Until { FindById $script:window 'UiLanguageBox' } 'UI language selector'
    $expand = [System.Windows.Automation.ExpandCollapsePattern]$combo.GetCurrentPattern(
        [System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    $expand.Expand()
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $culture)
    $option = Until {
        [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
            [System.Windows.Automation.TreeScope]::Descendants, $condition)
    } "UI language option '$culture'"
    $selection = [System.Windows.Automation.SelectionItemPattern]$option.GetCurrentPattern(
        [System.Windows.Automation.SelectionItemPattern]::Pattern)
    $selection.Select()
    if ($expand.Current.ExpandCollapseState -eq [System.Windows.Automation.ExpandCollapseState]::Expanded) {
        $expand.Collapse()
    }
}

function DragTabBefore([string]$sourceUri, [string]$targetUri) {
    $source = TabBounds $sourceUri
    $target = TabBounds $targetUri
    [WinUiDocumentTestMouse]::Drag([int]($source.Left + $source.Width / 2), [int]($source.Top + $source.Height / 2),
        [int]($target.Left + 3), [int]($target.Top + $target.Height / 2))
}

function DialogButton([string]$automationId) {
    return Until {
        $dialog = FindById $script:window 'EditorDocumentCloseDialog'
        if ($dialog) {
            $button = FindById $dialog $automationId
            if ($button -and -not $button.Current.IsOffscreen) { return $button }
        }
    } "save dialog button '$automationId'"
}

try {
    if (-not [Environment]::UserInteractive) { throw 'An interactive Windows desktop is required.' }
    if (-not $SkipBuild) {
        & (Join-Path $repoRoot 'scripts\build-windows.ps1') -Configuration $Configuration -Platform x64
        if ($LASTEXITCODE -ne 0) { throw "Windows build failed ($LASTEXITCODE)." }
    }
    Add-Type -AssemblyName UIAutomationClient
    Add-Type -AssemblyName UIAutomationTypes
    Add-Type -AssemblyName System.Windows.Forms
    if (-not ('WinUiDocumentTestMouse' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class WinUiDocumentTestMouse {
    [DllImport("user32.dll", SetLastError=true)] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, int data, UIntPtr extraInfo);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindowDpiAwarenessContext(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool AreDpiAwarenessContextsEqual(IntPtr first, IntPtr second);
    public static bool IsPerMonitorV2(IntPtr hwnd) =>
        AreDpiAwarenessContextsEqual(GetWindowDpiAwarenessContext(hwnd), new IntPtr(-4));
    public static void RightClick(int x, int y) {
        if (!SetCursorPos(x,y)) throw new InvalidOperationException("SetCursorPos failed");
        System.Threading.Thread.Sleep(60);
        mouse_event(0x0008, 0, 0, 0, UIntPtr.Zero);
        mouse_event(0x0010, 0, 0, 0, UIntPtr.Zero);
    }
    public static void Drag(int x, int y, int targetX, int targetY) {
        if (!SetCursorPos(x,y)) throw new InvalidOperationException("SetCursorPos failed");
        mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
        try {
            System.Threading.Thread.Sleep(160);
            for (int i = 1; i <= 24; ++i) {
                SetCursorPos(x + (targetX - x) * i / 24, y + (targetY - y) * i / 24);
                System.Threading.Thread.Sleep(25);
            }
            System.Threading.Thread.Sleep(180);
        } finally { mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero); }
    }
    public static void Click(int x, int y) {
        if (!SetCursorPos(x,y)) throw new InvalidOperationException("SetCursorPos failed");
        System.Threading.Thread.Sleep(60);
        mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
        mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
    }
}
'@
    }

    $exe = Join-Path $repoRoot "src\MonacoNative.SampleApp\bin\x64\$Configuration\net10.0-windows10.0.26100.0\MonacoNative.SampleApp.exe"
    if (-not (Test-Path $exe)) { throw "Built sample missing: $exe" }
    $script:app = Start-Process -FilePath $exe -PassThru
    $windowCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $script:app.Id)
    $script:window = Until {
        [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
            [System.Windows.Automation.TreeScope]::Children, $windowCondition)
    } 'sample window'
    $script:editor = Until { FindById $script:window 'MonacoEditorSurface' } 'editor UIA document'
    $script:state = Until { FindById $script:window 'EditorStatus' } 'document telemetry'
    [WinUiDocumentTestMouse]::SetForegroundWindow($script:app.MainWindowHandle) | Out-Null
    Check ([WinUiDocumentTestMouse]::IsPerMonitorV2([IntPtr]$script:window.Current.NativeWindowHandle)) 'sample window uses per-monitor-v2 DPI awareness'
    Until { [int](State)['DocumentCount'] -eq 3 -and (ActiveUri).StartsWith('file:') } 'three initial file-backed sample tabs' | Out-Null
    Check ($null -ne (FindById $script:window 'EditorDocumentTabs')) 'editor owns a native visible tab strip'
    $script:sampleNames = @('demo.css', 'demo.html', 'demo.cs')
    foreach ($sampleName in $script:sampleNames) {
        $sampleCondition = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, $sampleName)
        Check ($null -ne $script:window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $sampleCondition)) "sample starts with $sampleName"
    }
    Check ([int](State)['DocumentCount'] -eq 3) 'sample starts with three different file-backed language tabs'
    $fileUri = ActiveUri
    $fileText = Text
    Check ($fileText.Length -gt 0) 'File provider loads source text through the native document'

    Keys '^n'
    Until { [int](State)['DocumentCount'] -eq 4 -and (ActiveUri).StartsWith('inmemory:') } 'first Memory provider document' | Out-Null
    $firstUri = ActiveUri
    Keys 'alpha'
    Until { (Text) -eq 'alpha' } 'first document content' | Out-Null
    Keys '^s'
    Until { (State)['IsDirty'] -eq 'False' } 'first memory save' | Out-Null
    Keys '1'
    Until { (Text) -eq 'alpha1' -and (State)['IsDirty'] -eq 'True' } 'first document unsaved edit' | Out-Null

    Keys '^n'
    Until { [int](State)['DocumentCount'] -eq 5 -and (ActiveUri) -ne $firstUri } 'second memory document' | Out-Null
    $secondUri = ActiveUri
    Keys 'beta'
    Until { (Text) -eq 'beta' } 'second document content' | Out-Null
    Keys '^s'
    Until { (State)['IsDirty'] -eq 'False' } 'second memory save' | Out-Null
    Keys '2'
    Until { (Text) -eq 'beta2' } 'second document unsaved edit' | Out-Null

    Keys '^+{TAB}'
    Until { (ActiveUri) -eq $firstUri -and (Text) -eq 'alpha1' } 'previous tab restores first document' | Out-Null
    Keys '^z'
    Until { (Text) -eq 'alpha' -and (State)['IsDirty'] -eq 'False' } 'first tab undo-to-save' | Out-Null
    Keys '^{TAB}'
    Until { (ActiveUri) -eq $secondUri -and (Text) -eq 'beta2' } 'second undo history remains independent' | Out-Null
    Keys '^z'
    Until { (Text) -eq 'beta' -and (State)['IsDirty'] -eq 'False' } 'second tab undo-to-save' | Out-Null
    Check ($true) 'switching tabs preserves independent native undo stacks and dirty baselines'
    Keys '^y'
    Until { (Text) -eq 'beta2' } 'second redo survives switching' | Out-Null
    Keys '^s'
    Until { (State)['IsDirty'] -eq 'False' } 'save second document' | Out-Null

    SelectTab $fileUri
    Check ((Text) -eq $fileText) 'native tab selection restores the unchanged file document'
    SelectTab $firstUri
    Keys '^y'
    Until { (Text) -eq 'alpha1' } 'first redo survives native tab selection' | Out-Null
    Keys '^s'
    Until { (State)['IsDirty'] -eq 'False' } 'save first document' | Out-Null

    # Reorder the native strip, then check that keyboard navigation uses the same order.
    DragTabBefore $secondUri $fileUri
    Until { (TabBounds $secondUri).Left -lt (TabBounds $fileUri).Left } 'dragged tab position' | Out-Null
    SelectTab $secondUri
    Keys '^{TAB}'
    Until { (ActiveUri) -eq $fileUri } 'keyboard order after drag' | Out-Null
    Check ($true) 'drag reorder updates both the tab strip and document navigation order'

    # Pin a non-active tab without changing its source text or the active document.
    TabMenu $firstUri 'workbench.action.pinEditor'
    Until { (TabBounds $firstUri).Left -lt (TabBounds $secondUri).Left } 'pinned tab partition' | Out-Null
    Check ((ActiveUri) -eq $fileUri) 'pinning a context tab does not activate or edit it'
    TabMenu $firstUri 'workbench.action.unpinEditor'
    DragTabBefore $fileUri $firstUri
    DragTabBefore $firstUri $secondUri
    DragTabBefore $fileUri $firstUri
    Until { (TabBounds $fileUri).Left -lt (TabBounds $firstUri).Left -and (TabBounds $firstUri).Left -lt (TabBounds $secondUri).Left } 'restored document order' | Out-Null
    SelectTab $firstUri

    # Right-click menu labels follow the editor's active UI culture.
    SelectUiLanguage 'zh-CN'
    $closeOthers = OpenTabMenuItem (Until { FindById $script:window ('DocumentTab_' + $firstUri) } 'localized tab') 'workbench.action.closeOtherEditors'
    Check (-not [string]::IsNullOrWhiteSpace($closeOthers.Current.Name) -and $closeOthers.Current.Name -ne 'Close Others') 'native tab context menu localizes Close Others'
    Check ($closeOthers.Current.IsEnabled) 'Close Others is enabled when an unpinned other tab exists'
    [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
    $closeRight = OpenTabMenuItem (Until { FindById $script:window ('DocumentTab_' + $firstUri) } 'localized tab') 'workbench.action.closeEditorsToTheRight'
    Check (-not [string]::IsNullOrWhiteSpace($closeRight.Current.Name) -and $closeRight.Current.Name -ne 'Close to the Right') 'native tab context menu localizes Close to the Right'
    Check ($closeRight.Current.IsEnabled) 'Close to the Right is enabled when a closeable right-hand tab exists'
    [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
    SelectUiLanguage 'en-US'

    # Splitting creates a linked view; closing or moving its final tab removes the empty region.
    TabMenu $firstUri 'workbench.action.splitEditor'
    Until { (TabElements $firstUri).Count -eq 2 } 'split editor creates a second tab view' | Out-Null
    $rightTab = TabElements $firstUri | Sort-Object { $_.Current.BoundingRectangle.Left } | Select-Object -Last 1
    SelectUiLanguage 'zh-CN'
    $childCloseOthers = OpenTabMenuItem $rightTab 'workbench.action.closeOtherEditors'
    Check (-not [string]::IsNullOrWhiteSpace($childCloseOthers.Current.Name) -and $childCloseOthers.Current.Name -ne 'Close Others') 'existing split groups follow UI language changes'
    [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
    SelectUiLanguage 'en-US'
    $rightTab = TabElements $firstUri | Sort-Object { $_.Current.BoundingRectangle.Left } | Select-Object -Last 1
    TabMenuAt $rightTab 'workbench.action.closeActiveEditor'
    Until { (TabElements $firstUri).Count -eq 1 } 'closing the final tab closes its split group' | Out-Null
    TabMenu $firstUri 'workbench.action.splitEditor'
    Until { (TabElements $firstUri).Count -eq 2 } 'split editor recreates the linked view' | Out-Null
    $rightTab = TabElements $firstUri | Sort-Object { $_.Current.BoundingRectangle.Left } | Select-Object -Last 1
    TabMenuSubcommand $rightTab 'workbench.action.moveEditorToLeftGroup'
    Until { (TabElements $firstUri).Count -eq 1 } 'moving the final tab out closes its split group' | Out-Null

    ClickSampleTabBarToggle
    Until { (State)['ShowTabs'] -eq 'False' } 'Sample Tab Bar toggle hides the native tab strip' | Out-Null
    $hiddenByToolbarToggle = Until {
        $tabs = FindById $script:window 'EditorDocumentTabs'
        if ($null -eq $tabs -or $tabs.Current.IsOffscreen) { return $true }
    } 'Sample Tab Bar toggle removes the tab strip from the visible UI'
    Check ($hiddenByToolbarToggle) 'Sample Tab Bar toggle removes the tab strip from the visible UI'
    ClickSampleTabBarToggle
    Until { (State)['ShowTabs'] -eq 'True' } 'Sample Tab Bar toggle restores the native tab strip' | Out-Null
    $shownByToolbarToggle = Until {
        $tabs = FindById $script:window 'EditorDocumentTabs'
        if ($null -ne $tabs -and -not $tabs.Current.IsOffscreen) { return $tabs }
    } 'Sample Tab Bar toggle restores visible tabs'
    Check ($null -ne $shownByToolbarToggle) 'Sample Tab Bar toggle restores visible tabs'

    ClickViewOption 'ShowTabsToggle'
    Until { (State)['ShowTabs'] -eq 'False' } 'hide native tab strip from View Options' | Out-Null
    $toolbarToggle = Until { FindById $script:window 'TabBarToggle' } 'Sample Tab Bar toggle after View Options change'
    $toolbarTogglePattern = [System.Windows.Automation.TogglePattern]$toolbarToggle.GetCurrentPattern(
        [System.Windows.Automation.TogglePattern]::Pattern)
    Check ($toolbarTogglePattern.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::Off) 'View Options keeps Sample Tab Bar toggle synchronized when hidden'
    $hiddenTabs = FindById $script:window 'EditorDocumentTabs'
    Check ($null -eq $hiddenTabs -or $hiddenTabs.Current.IsOffscreen) 'ShowTabs=false removes the tab strip from the visible UI'
    Check ([int](State)['DocumentCount'] -eq 5) 'hiding tabs keeps all sample and Memory documents open'
    Keys '^{TAB}'
    Until { (ActiveUri) -eq $secondUri -and (Text) -eq 'beta2' } 'switch document with hidden tab strip' | Out-Null
    ClickViewOption 'ShowTabsToggle'
    Until { (State)['ShowTabs'] -eq 'True' } 'restore native tab strip' | Out-Null
    $toolbarToggle = Until { FindById $script:window 'TabBarToggle' } 'Sample Tab Bar toggle after View Options restore'
    $toolbarTogglePattern = [System.Windows.Automation.TogglePattern]$toolbarToggle.GetCurrentPattern(
        [System.Windows.Automation.TogglePattern]::Pattern)
    Check ($toolbarTogglePattern.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On) 'View Options keeps Sample Tab Bar toggle synchronized when shown'
    Check ([int](State)['DocumentCount'] -eq 5) 'restoring the tab strip preserves its document collection'

    Keys 'x'
    Until { (Text) -eq 'beta2x' -and (State)['IsDirty'] -eq 'True' } 'dirty close fixture' | Out-Null
    Keys '^w'
    InvokeElement (DialogButton 'CloseButton')
    Until { [int](State)['DocumentCount'] -eq 5 -and (ActiveUri) -eq $secondUri } 'cancel dirty close' | Out-Null
    Check ((Text) -eq 'beta2x') 'canceling dirty close retains the complete unsaved working copy'

    SelectTab $firstUri
    ClickViewOption 'MultiTabToggle'
    InvokeElement (DialogButton 'CloseButton')
    Until { (State)['MultiTab'] -eq 'True' } 'cancel conversion to single-document mode' | Out-Null
    Check ([int](State)['DocumentCount'] -eq 5) 'canceling mode conversion leaves earlier clean tabs open too'
    SelectTab $secondUri
    Check ((Text) -eq 'beta2x') 'mode conversion cancellation preserves other tabs unsaved text'
    Keys '^s'
    Until { (State)['IsDirty'] -eq 'False' } 'save before disabling multi-tab mode' | Out-Null
    ClickViewOption 'MultiTabToggle'
    Until { (State)['MultiTab'] -eq 'False' -and [int](State)['DocumentCount'] -eq 1 } 'single-document mode' | Out-Null
    Check ((ActiveUri) -eq $secondUri -and (Text) -eq 'beta2x') 'disabling multi-tab mode keeps the active document'
    $singleTab = Until { FindById $script:window ('DocumentTab_' + $secondUri) } 'single remaining tab'
    $closeOthers = OpenTabMenuItem $singleTab 'workbench.action.closeOtherEditors'
    Check (-not $closeOthers.Current.IsEnabled) 'Close Others is disabled when no other tab can close'
    [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
    $singleTab = Until { FindById $script:window ('DocumentTab_' + $secondUri) } 'single remaining tab'
    $closeRight = OpenTabMenuItem $singleTab 'workbench.action.closeEditorsToTheRight'
    Check (-not $closeRight.Current.IsEnabled) 'Close to the Right is disabled when no right-hand tab can close'
    [System.Windows.Forms.SendKeys]::SendWait('{ESC}')

    Keys 'q'
    Until { (Text) -eq 'beta2xq' -and (State)['IsDirty'] -eq 'True' } 'single-document replacement fixture' | Out-Null
    Keys '^n'
    InvokeElement (DialogButton 'CloseButton')
    Until { [int](State)['DocumentCount'] -eq 1 -and (ActiveUri) -eq $secondUri } 'cancel single-document replacement' | Out-Null
    Check ((Text) -eq 'beta2xq') 'single-document replacement cancellation preserves unsaved content'
    Keys '^n'
    InvokeElement (DialogButton 'SecondaryButton')
    Until { [int](State)['DocumentCount'] -eq 1 -and (ActiveUri) -ne $secondUri -and (Text).Length -eq 0 } 'authorized single-document replacement' | Out-Null
    Check ((State)['MultiTab'] -eq 'False') 'single-document replacement does not reenable multiple tabs'

    ClickViewOption 'MultiTabToggle'
    Until { (State)['MultiTab'] -eq 'True' } 'reenable multiple documents' | Out-Null
    $leftUri = ActiveUri
    Keys '^n'
    Until { [int](State)['DocumentCount'] -eq 2 } 'open another tab after reenabling' | Out-Null
    $rightUri = ActiveUri
    TabMenu $leftUri 'workbench.action.closeEditorsToTheRight'
    Until { [int](State)['DocumentCount'] -eq 1 -and (ActiveUri) -eq $leftUri } 'Close to the Right closes only right-hand tabs' | Out-Null
    Keys '^n'
    Until { [int](State)['DocumentCount'] -eq 2 } 'open another tab for Close Others' | Out-Null
    $keptUri = ActiveUri
    TabMenu $keptUri 'workbench.action.closeOtherEditors'
    Until { [int](State)['DocumentCount'] -eq 1 -and (ActiveUri) -eq $keptUri } 'Close Others preserves the context tab and closes other tabs' | Out-Null
    Check ($leftUri -ne $keptUri -and $rightUri -ne $keptUri) 'close-scope operations use the current tab context'

    Check ($script:app.CloseMainWindow()) 'window accepts normal close after document lifecycle tests'
    Check ($script:app.WaitForExit(15000)) 'window exits after disposing all native documents'
    Check ($script:app.ExitCode -eq 0) 'multi-document disposal exits cleanly'
    $script:success = $true
}
catch {
    $script:failure = $_.Exception.Message
    Write-Error -Message $script:failure -ErrorAction Continue
}
finally {
    $report = [ordered]@{
        schemaVersion = 1
        timestampUtc = [DateTime]::UtcNow.ToString('o')
        pass = $script:success
        error = $script:failure
        steps = @($script:steps.ToArray())
    }
    $report | ConvertTo-Json -Depth 6 | Set-Content -Encoding UTF8 -Path (Join-Path $OutputDirectory 'document-tabs-results.json')
    if ($script:app -and -not $script:app.HasExited) {
        $script:app.CloseMainWindow() | Out-Null
        if (-not $script:app.WaitForExit(5000)) { $script:app.Kill() }
    }
}
if (-not $script:success) { exit 1 }
Write-Host 'Interactive native document tab tests PASS'
