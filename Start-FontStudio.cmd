@echo off
setlocal
dotnet run --project "%~dp0FontStudio\FontStudio.csproj" --configuration Release
endlocal
