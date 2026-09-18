# Build both 64-bit and 32-bit ODBC agents as framework-dependent net48 executables.
# Requires the .NET 8 SDK (can target net48) and .NET Framework 4.8 on the target machine.
$ErrorActionPreference = "Stop"

# 自定位：无论从哪个目录调用，都切到脚本所在目录（Firebird 版 build.ps1 同理）。
Push-Location $PSScriptRoot
try {
    # 64-bit agent -> publish/agent.exe (+ agent.dll 等依赖，framework-dependent 不复单文件)
    # 先清掉上次产物，避免残留的 agent.exe（旧版单文件 net8）被一并打进包。
    if (Test-Path "publish") { Remove-Item -Recurse -Force publish }
    dotnet publish -c Release -r win-x64 -o publish
    Get-ChildItem publish/dbx-agent-odbc.* | Rename-Item -NewName { $_.Name -replace 'dbx-agent-odbc','agent' }

    # 32-bit agent -> publish-x86/agent.exe
    if (Test-Path "publish-x86") { Remove-Item -Recurse -Force publish-x86 }
    dotnet publish -c Release -r win-x86 -o publish-x86
    Get-ChildItem publish-x86/dbx-agent-odbc.* | Rename-Item -NewName { $_.Name -replace 'dbx-agent-odbc','agent' }

    Write-Host ""
    Write-Host "Done:"
    Write-Host "  64-bit -> publish/agent.exe      (for data/agents/drivers/odbc/)"
    Write-Host "  32-bit -> publish-x86/agent.exe  (for data/agents/drivers/odbc32/)"
}
finally {
    Pop-Location
}
