@echo off
cd /d "%~dp0"
dotnet publish NetScope.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist
if errorlevel 1 exit /b 1
echo Built dist\NetScope.exe
