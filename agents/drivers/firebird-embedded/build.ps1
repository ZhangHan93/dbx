# Build the Firebird Embedded agent as a 32-bit single-file self-contained executable.
#
# x86 ONLY, and only one RID.
#   All three engines (fb25/fb30/fb50) are Win32/x86, and native DLL bitness is hard:
#   an AnyCPU or x64 build launched by dbx.exe would P/Invoke into an x86 fbclient and
#   inevitably fail. (Contrast ../odbc/build.ps1: that one needs x64 AND x86 because the
#   ODBC driver manager is bitness-split. This driver is pure embedded - one build is enough.)
#
# Output: publish/agent.exe + publish/engines/{fb25_x86,fb30_x86,fb50_x86}/
#         That layout IS the content of data/agents/drivers/firebird-embedded/, ~90 MB unpacked.
#
# NOTE: engines/ is not in git (~89 MB). It does not exist on CI, so the copy below MUST stay
#       behind a Test-Path guard, otherwise CI goes red for a missing folder.
#       See agents/docs/firebird-embedded-plan.zh-CN.md S4-10.
#
# NOTE: keep this file pure ASCII. Windows PowerShell 5.1 reads a BOM-less .ps1 as ANSI/GBK;
#       non-ASCII characters then eat quotes and braces and the script fails to parse.
#       Every other .ps1 under dbx-src/ follows the same rule.

$ErrorActionPreference = "Stop"

Push-Location $PSScriptRoot
try {
    dotnet publish dbx-agent-firebird-embedded.csproj -c Release -r win-x86 `
        --self-contained true -p:PublishSingleFile=true -o publish

    Copy-Item "publish/dbx-agent-firebird-embedded.exe" "publish/agent.exe" -Force

    # Copy the engines whole - native DLLs plus plugins/intl cannot be packed into the
    # single file, and they must sit in engines/ next to agent.exe (EnginePin.EngineRoot).
    if (Test-Path "engines") {
        foreach ($k in 'fb25_x86', 'fb30_x86', 'fb50_x86') {
            $src = "engines/$k"
            if (-not (Test-Path $src)) {
                Write-Warning "engines/$k is missing - the agent will refuse databases that need it"
                continue
            }
            $dst = "publish/engines/$k"
            New-Item -ItemType Directory -Force -Path $dst | Out-Null
            # firebird.log is a runtime artifact; carrying it over only makes a stale log look fresh.
            Copy-Item "$src/*" $dst -Recurse -Force -Exclude 'firebird.log'
        }
        Write-Host ""
        Write-Host "Engines bundled:"
        Get-ChildItem "publish/engines" -Directory | ForEach-Object {
            $size = (Get-ChildItem $_.FullName -Recurse -File | Measure-Object -Property Length -Sum).Sum
            Write-Host ("  {0,-12} {1,8:N1} MB" -f $_.Name, ($size / 1MB))
        }
    } else {
        Write-Host ""
        Write-Host "engines/ not found - CI mode, skipping engine copy"
        Write-Host "To run against a real database locally, put the three x86 engines under engines/fb25_x86, fb30_x86, fb50_x86."
    }

    Write-Host ""
    Write-Host "Done -> publish/agent.exe  (for data/agents/drivers/firebird-embedded/)"
}
finally {
    Pop-Location
}
