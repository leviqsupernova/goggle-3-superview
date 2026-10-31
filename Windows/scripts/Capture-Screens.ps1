# Dev only (CI job ui-screenshots): start the published viewer in its main states and save a PNG of each.
# Buttons are pressed through UI Automation (WPF exposes x:Name as AutomationId). No goggles needed:
# video comes from --replay with the test fixture; "--connect" on a PC without goggles shows the waiting state.
param([Parameter(Mandatory)][string]$App, [string]$Out = 'screenshots', [int]$Width = 1920, [int]$Height = 1080)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms, System.Drawing, UIAutomationClient, UIAutomationTypes
New-Item -ItemType Directory -Force $Out | Out-Null
$App = (Resolve-Path $App).Path

try { Set-DisplayResolution -Width $Width -Height $Height -Force -ErrorAction Stop } catch { Write-Warning "Resolution unchanged: $_" }
$screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
"screen $($screen.Width)x$($screen.Height)" | Set-Content (Join-Path $Out 'screen.txt')

# 30 s clip: the 2 s fixture repeated (Annex-B streams concatenate; each copy starts with SPS/PPS/IDR).
$fixture = [IO.File]::ReadAllBytes((Join-Path $PSScriptRoot '../Goggles3.Core.Tests/testdata/testsrc-320x180-60fps-2s.264'))
$clip = Join-Path $env:RUNNER_TEMP 'clip.264'
$stream = [IO.File]::Create($clip); 1..15 | ForEach-Object { $stream.Write($fixture, 0, $fixture.Length) }; $stream.Close()

function Shot([string]$name) {
    $bitmap = New-Object System.Drawing.Bitmap $screen.Width, $screen.Height
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.CopyFromScreen($screen.Location, [System.Drawing.Point]::Empty, $screen.Size)
    $bitmap.Save((Join-Path $Out "$name.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $graphics.Dispose(); $bitmap.Dispose()
    Write-Host "captured $name"
}

function Find([int]$processId, [string]$automationId) {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $condition = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $processId)),
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $automationId)))
    for ($i = 0; $i -lt 50; $i++) {
        $element = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
        if ($element) { return $element }
        Start-Sleep -Milliseconds 200
    }
    throw "Control $automationId not found"
}

function Press([int]$processId, [string]$automationId) {
    (Find $processId $automationId).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}

function Run([string[]]$arguments, [scriptblock]$steps) {
    $process = Start-Process $App -ArgumentList ($arguments + '--software-render') -PassThru
    try { & $steps $process.Id } finally { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue; Start-Sleep 1 }
}

Run @() { param($id) Start-Sleep 4; Shot '01-idle' }
Run @('--connect') { param($id) Start-Sleep 6; Shot '02-connect-no-goggles' }
Run @('--replay', "`"$clip`"") {
    param($id)
    Start-Sleep 5; Shot '03-replay-video'
    Press $id 'RecordButton'; Start-Sleep 3; Shot '04-recording'
    Press $id 'RecordButton'; Start-Sleep 3; Shot '05-recording-saved'
    Press $id 'FullscreenButton'; Start-Sleep 2; Shot '06-fullscreen'
    [System.Windows.Forms.SendKeys]::SendWait('{ESC}'); Start-Sleep 1
    Press $id 'TutorialButton'; Start-Sleep 2; Shot '07-tutorial'
}
Write-Host "Screenshots in $Out"
