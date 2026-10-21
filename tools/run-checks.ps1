param(
    [string]$DotNet = 'dotnet',
    [string]$Output = 'work/checks',
    [switch]$SkipBuild,
    [string[]]$Group = @('all')
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
Push-Location $projectRoot
try {
    $Group = @($Group | ForEach-Object { $_ -split ',' })
    foreach ($name in $Group) { if ($name -notin @('all','audit','speech','clipboard','ocr','selection','ui')) { throw "Unknown check group: $name" } }
    if (-not $SkipBuild) {
        & $DotNet build QuietTranslate.csproj -c Release
        if ($LASTEXITCODE -ne 0) { throw 'Product build failed.' }
        & $DotNet build tests/QuietTranslate.Tests.csproj -c Release
        if ($LASTEXITCODE -ne 0) { throw 'Test host build failed.' }
    }
    $destination = [IO.Path]::GetFullPath((Join-Path $projectRoot $Output))
    New-Item -ItemType Directory -Force -Path $destination | Out-Null
    $hostExecutable = Join-Path $projectRoot 'tests/bin/Release/net8.0-windows10.0.19041.0/QuietTranslate.Tests.exe'
    $groups = [ordered]@{
        audit = @('audit-regression-test')
        # speech-test already exercises the same App lifecycle fixture as speech-app-test.
        speech = @('direct-captions-test','speech-test','speech-polish-test')
        clipboard = @('copy-test','clipboard-race-test')
        ocr = @('ocr-settings-test')
        selection = @('interaction-test','discord-selection-test')
        ui = @('self-test','v13-test','refinement-test','upgrade-test','bubble-test','hover-test','lifecycle-test','motion-test','polish-test','growth-repro','ui-smoke')
    }
    if (Test-Path "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe") { $groups.selection += 'table-test' }
    $selected = if ($Group -contains 'all') { @($groups.Keys) } else { $Group }
    $cases = @($selected | ForEach-Object { $groups[$_] } | Select-Object -Unique)
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
