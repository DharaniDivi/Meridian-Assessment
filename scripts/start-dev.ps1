# PowerShell helper — run backend + frontend in separate windows

$root = Split-Path -Parent $MyInvocation.MyCommand.Path

Write-Host "Starting Assessment API..."
Start-Process powershell -ArgumentList "-NoExit", "-Command", "cd '$root\src\Assessment.Api'; dotnet run"

Start-Sleep -Seconds 3

Write-Host "Starting Angular dev server..."
Start-Process powershell -ArgumentList "-NoExit", "-Command", "cd '$root\src\Assessment.Web'; npm install; npm start"

Write-Host "Done. API: https://localhost:7041  UI: http://localhost:4200"
