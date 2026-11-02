@echo off
title ForzaOverlay Build and Run

rem 自动切换到脚本所在目录（无论从哪里双击或调用都有效）
cd /d "%~dp0"

echo ==============================================
echo   ForzaOverlay 构建并运行
echo ==============================================
echo.

where dotnet >nul 2>nul
if errorlevel 1 (
    echo [错误] 未检测到 dotnet 命令，请先安装 .NET SDK。
    echo 下载地址: https://dotnet.microsoft.com/download/dotnet/8.0
    echo.
    pause
    exit /b 1
)

echo [1/3] 清理旧的编译产物...
dotnet clean ForzaOverlay.sln -v q
echo.

echo [2/3] 编译解决方案（Release）...
dotnet build ForzaOverlay.sln -c Release
if errorlevel 1 (
    echo.
    echo 编译失败，请检查错误
    echo.
    pause
    exit /b 1
)

echo.
echo [3/3] 编译成功，正在启动程序...
dotnet run --project src\ForzaOverlay.App -c Release

echo.
echo 程序已退出。
pause
