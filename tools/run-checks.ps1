param(
    [string]$DotNet = 'dotnet',
    [string]$Output = 'work/checks',
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
Push-Location $projectRoot
try {
    if (-not $SkipBuild) {
        & $DotNet build QuietTranslate.csproj -c Release
        if ($LASTEXITCODE -ne 0) { throw 'Product build failed.' }
        & $DotNet build tests/QuietTranslate.Tests.csproj -c Release
        if ($LASTEXITCODE -ne 0) { throw 'Test host build failed.' }
    }
    $destination = [IO.Path]::GetFullPath((Join-Path $projectRoot $Output))
    New-Item -ItemType Directory -Force -Path $destination | Out-Null
    $hostExecutable = Join-Path $projectRoot 'tests/bin/Release/net8.0-windows10.0.19041.0/QuietTranslate.Tests.exe'
    $cases = @('self-test','v13-test','refinement-test','upgrade-test','bubble-test','hover-test','lifecycle-test','interaction-test','motion-test','polish-test','copy-test','growth-repro','ui-smoke','discord-selection-test')
    if (Test-Path "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe") { $cases += 'table-test' }
    $summary = @()
    foreach ($case in $cases) {
        $target = Join-Path $destination $case
        if ($case -eq 'self-test') { $target += '.json' }
        Write-Output ('Running ' + $case)
        $process = Start-Process -FilePath $hostExecutable -ArgumentList @('--'+$case, ('"'+$target+'"')) -PassThru -WindowStyle Hidden
        if (-not $process.WaitForExit(60000)) {
            # This process belongs to this runner; never terminate an existing user instance.
            Stop-Process -Id $process.Id -Force
            throw ($case + ' timed out; remaining checks were not run.')
        }
        $summary += [PSCustomObject]@{ name=$case; exitCode=$process.ExitCode; passed=($process.ExitCode -eq 0) }
        Write-Output ($case + ': exit ' + $process.ExitCode)
    }
    $product = Join-Path $projectRoot 'bin/Release/net8.0-windows10.0.19041.0/QuietTranslate.dll'
    $structure = Join-Path $destination 'product-structure.json'
    $process = Start-Process -FilePath $hostExecutable -ArgumentList @('--product-check', ('"'+$product+'"'), ('"'+$structure+'"')) -PassThru -Wait -WindowStyle Hidden
    $summary += [PSCustomObject]@{ name='product-structure'; exitCode=$process.ExitCode; passed=($process.ExitCode -eq 0) }
    $summary | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $destination 'summary.json') -Encoding utf8
    $failures = @($summary | Where-Object { -not $_.passed })
    if ($failures.Count -gt 0) { throw ('Failed checks: ' + (($failures | ForEach-Object name) -join ', ')) }
    Write-Output ('All ' + $summary.Count + ' suites passed. Reports: ' + $Output)
} finally { Pop-Location }
