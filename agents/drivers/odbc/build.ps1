# Build both 64-bit and 32-bit ODBC agents as single-file self-contained executables.
# Requires the .NET 8 SDK.
$ErrorActionPreference = "Stop"

# 64-bit agent -> publish/agent.exe
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
Copy-Item "publish/dbx-agent-odbc.exe" "publish/agent.exe" -Force

# 32-bit agent -> publish-x86/agent.exe
dotnet publish -c Release -r win-x86 --self-contained true -p:PublishSingleFile=true -o publish-x86
Copy-Item "publish-x86/dbx-agent-odbc.exe" "publish-x86/agent.exe" -Force

Write-Host ""
Write-Host "Done:"
Write-Host "  64-bit -> publish/agent.exe      (for data/agents/drivers/odbc/)"
Write-Host "  32-bit -> publish-x86/agent.exe  (for data/agents/drivers/odbc32/)"
