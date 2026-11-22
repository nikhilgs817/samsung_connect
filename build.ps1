param([switch]$Test, [switch]$Logos)
$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (-not (Test-Path -LiteralPath (Join-Path $framework 'csc.exe'))) {
    $framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'
}
$compiler = Join-Path $framework 'csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'The Windows .NET Framework compiler was not found.' }
$buildDir = Join-Path $PSScriptRoot 'build'
New-Item -ItemType Directory -Path $buildDir -Force | Out-Null
$references = @('-r:System.dll', '-r:System.Drawing.dll', '-r:System.Windows.Forms.dll', '-r:System.Xaml.dll')
foreach ($assembly in @('UIAutomationClient.dll', 'UIAutomationTypes.dll', 'WindowsBase.dll', 'PresentationCore.dll', 'PresentationFramework.dll')) {
    $references += '-r:' + (Join-Path $framework ('WPF\' + $assembly))
}
$sources = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Filter '*.cs' | Sort-Object Name | ForEach-Object { $_.FullName })
$common = @('-nologo', '-langversion:5', '-platform:anycpu', '-optimize+', ('-win32manifest:' + (Join-Path $PSScriptRoot 'app.manifest')))
if ($Test -or $Logos) {
    $output = Join-Path $buildDir 'SafetyTests.exe'
    & $compiler @common '-target:exe' @references '-main:SamsungCONNECT.SafetyTests' ('-out:' + $output) @sources (Join-Path $PSScriptRoot 'tests\SafetyTests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed.' }
    if ($Logos) { & $output $buildDir --logos } else { & $output $buildDir }
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
} else {
    $output = Join-Path $buildDir 'SamsungCONNECT.exe'
    & $compiler @common '-target:winexe' @references ('-out:' + $output) @sources
    if ($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }
    Write-Output "Built $output"
    Get-FileHash -LiteralPath $output -Algorithm SHA256
}
