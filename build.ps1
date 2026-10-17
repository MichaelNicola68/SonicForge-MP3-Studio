$ErrorActionPreference='Stop'
dotnet restore .\SonicForge.sln
dotnet build .\SonicForge.sln -c Release --no-restore
Write-Host "SonicForge Build erfolgreich." -ForegroundColor Green
