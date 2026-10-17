$ErrorActionPreference='Stop'
$out = Join-Path $PSScriptRoot 'artifacts\win-x64'
dotnet publish .\src\SonicForge.App\SonicForge.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o $out
Get-FileHash (Join-Path $out 'SonicForgeStudio.exe') -Algorithm SHA256 | Format-List
