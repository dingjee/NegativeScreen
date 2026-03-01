@echo off
echo ============================================
echo NegativeScreen Test Runner
echo ============================================
echo.

set MSBUILD=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe
set TEST_DIR=d:\Projects\NegativeScreen\test

echo Building test project...
%MSBUILD% "%TEST_DIR%\TestRunner.csproj" /p:Configuration=Release /p:Platform=x64

if %ERRORLEVEL% neq 0 (
    echo Build FAILED!
    exit /b 1
)

echo.
echo Running tests...
"%TEST_DIR%\bin\x64\Release\TestRunner.exe"

echo.
echo ============================================
echo Test completed
echo ============================================
