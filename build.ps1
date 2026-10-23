param([switch]$Test)
$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (-not (Test-Path -LiteralPath (Join-Path $framework 'csc.exe'))) {
    $framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'
}
$compiler = Join-Path $framework 'csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'The Windows .NET Framework compiler was not found.' }
$buildDir = Join-Path $PSScriptRoot 'build'
New-Item -ItemType Directory -Path $buildDir -Force | Out-Null
$references = @('-r:System.dll', '-r:System.Drawing.dll', '-r:System.Windows.Forms.dll')
foreach ($assembly in @('UIAutomationClient.dll', 'UIAutomationTypes.dll', 'WindowsBase.dll')) {
    $references += '-r:' + (Join-Path $framework ('WPF\' + $assembly))
}
if ($Test) {
    $output = Join-Path $buildDir 'SafetyTests.exe'
    & $compiler -nologo -langversion:5 -target:exe -platform:anycpu -optimize+ @references ('-win32manifest:' + (Join-Path $PSScriptRoot 'app.manifest')) '-main:SamsungCONNECT.SafetyTests' ('-out:' + $output) (Join-Path $PSScriptRoot 'SamsungCONNECT.cs') (Join-Path $PSScriptRoot 'tests\SafetyTests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed.' }
    & $output $buildDir
    if ($LASTEXITCODE -ne 0) { throw 'Safety tests failed.' }
} else {
    $output = Join-Path $buildDir 'SamsungCONNECT.exe'
    & $compiler -nologo -langversion:5 -target:winexe -platform:anycpu -optimize+ @references ('-win32manifest:' + (Join-Path $PSScriptRoot 'app.manifest')) ('-out:' + $output) (Join-Path $PSScriptRoot 'SamsungCONNECT.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }
    Write-Output "Built $output"
    Get-FileHash -LiteralPath $output -Algorithm SHA256
}
