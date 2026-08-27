@echo off
setlocal
set "PUBLISH_STAGING=%~dp0obj\publish-staging"
if exist "%PUBLISH_STAGING%" rmdir /s /q "%PUBLISH_STAGING%"
dotnet publish "%~dp0LaptopQaUsbBuilder.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "%PUBLISH_STAGING%"
if errorlevel 1 exit /b %errorlevel%
if not exist "%~dp0dist" mkdir "%~dp0dist"
copy /y "%PUBLISH_STAGING%\USB Drive Builder v*.exe" "%~dp0dist\" >nul
if errorlevel 1 exit /b %errorlevel%
echo Published versioned build to "%~dp0dist"
