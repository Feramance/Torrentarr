@echo off
REM Torrentarr Build Script for Windows
REM This script builds both the React frontend and .NET backend

setlocal enabledelayedexpansion

echo =========================================
echo    Torrentarr Build Script (Windows)
echo =========================================
echo.

REM Check prerequisites
echo Checking prerequisites...

where dotnet >nul 2>nul
if %ERRORLEVEL% NEQ 0 (
    echo Error: .NET SDK not found. Please install .NET 10.0 or later.
    exit /b 1
)

where node >nul 2>nul
if %ERRORLEVEL% NEQ 0 (
    echo Error: Node.js not found. Please install Node.js 18 or later.
    exit /b 1
)

echo [92m✓ Prerequisites met[0m
echo.

REM Build frontend
echo Building React frontend...
cd src\Torrentarr.WebUI\ClientApp

if not exist "node_modules" (
    echo Installing npm dependencies...
    call npm install
    if %ERRORLEVEL% NEQ 0 (
        echo [91m✗ npm install failed[0m
        exit /b 1
    )
)

echo Building production bundle...
call npm run build
if %ERRORLEVEL% NEQ 0 (
    echo [91m✗ Frontend build failed[0m
    exit /b 1
)

echo [92m✓ Frontend build successful[0m
cd ..\..\..
echo.

REM Build backend
echo Building .NET backend...
dotnet restore
if %ERRORLEVEL% NEQ 0 (
    echo [91m✗ dotnet restore failed[0m
    exit /b 1
)

dotnet build -c Release
if %ERRORLEVEL% NEQ 0 (
    echo [91m✗ Backend build failed[0m
    exit /b 1
)

echo [92m✓ Backend build successful[0m
echo.

echo [92m==========================================[0m
echo [92m   Build Complete![0m
echo [92m==========================================[0m
echo.
echo Run the application:
echo   dotnet run --project src\Torrentarr.Host\Torrentarr.Host.csproj -c Release
echo.
echo Or use Docker:
echo   docker-compose up -d
echo.

endlocal
