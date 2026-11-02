@echo off
setlocal
title Forza Fat Fish HUD Publish

rem 自动切换到脚本所在目录（无论从哪里双击或调用都有效）
cd /d "%~dp0"

rem 发布产物输出到仓库外的同级目录，避免构建产物进入版本库
set "DISTDIR=%~dp0..\ForzaFatFishHUD-dist"

echo ==============================================
echo   ForzaOverlay 发布
echo   输出位置: %DISTDIR%
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

set "TFM="
for /f "delims=" %%i in ('dotnet msbuild src\ForzaOverlay.App\ForzaOverlay.App.csproj -getProperty:TargetFramework') do set "TFM=%%i"
if "%TFM%"=="" (
    echo [错误] 无法从项目读取 TargetFramework。
    echo.
    pause
    exit /b 1
)
echo 目标框架: %TFM%
echo.

echo [1/4] 发布主程序（自包含目录，首次约 1-2 分钟）...
dotnet publish src\ForzaOverlay.App -c Release -r win-x64 --self-contained true -p:DebugType=None -p:DebugSymbols=false
if errorlevel 1 (
    echo.
    echo 发布失败，请检查错误
    echo.
    pause
    exit /b 1
)

echo.
echo [2/4] 整理输出到 %DISTDIR% ...
if exist "%DISTDIR%" rd /s /q "%DISTDIR%"
mkdir "%DISTDIR%"
xcopy /E /I /Y "src\ForzaOverlay.App\bin\Release\%TFM%\win-x64\publish" "%DISTDIR%\" >nul
if errorlevel 1 (
    echo.
    echo 归档失败，请检查 publish 目录是否生成。
    echo.
    pause
    exit /b 1
)

echo.
echo [3/4] 归档资源文件（按类目整理）...
xcopy /E /I /Y app-dist-template "%DISTDIR%\" >nul
if errorlevel 1 (
    echo.
    echo 资源归档失败，请检查 app-dist-template 目录是否存在。
    echo.
    pause
    exit /b 1
)

echo.
echo [4/4] 生成发布压缩包...
set "VERSION="
for /f "delims=" %%i in ('dotnet msbuild src\ForzaOverlay.App\ForzaOverlay.App.csproj -getProperty:Version') do set "VERSION=%%i"
if "%VERSION%"=="" set "VERSION=0.0.0"

copy /Y "LICENSE" "%DISTDIR%\" >nul
copy /Y "README.md" "%DISTDIR%\" >nul
copy /Y "THIRD-PARTY-NOTICES.txt" "%DISTDIR%\" >nul

set "RELDIR=%~dp0..\ForzaFatFishHUD-releases"
if not exist "%RELDIR%" mkdir "%RELDIR%"
set "ZIP=%RELDIR%\ForzaFatFishHUD-v%VERSION%-win64.zip"
if exist "%ZIP%" del /q "%ZIP%"
powershell -NoProfile -Command "Compress-Archive -Path '%DISTDIR%\*' -DestinationPath '%ZIP%' -Force"
if errorlevel 1 (
    echo.
    echo 压缩失败，请检查 PowerShell 是否可用。
    echo.
    pause
    exit /b 1
)

echo.
echo ==============================================
echo   生成完成！输出位置: %DISTDIR%\
echo.
echo     ForzaFatFishHUD.exe       主程序（免安装运行时）
echo     Assets\Fonts\             内置字体（D-DIN，OFL 授权）
echo     docs\使用说明.txt          启动 / 游戏设置 / 操作说明
echo     docs\常见问题.txt          HUD 不显示等故障排查
echo     config\配置文件说明.txt    config.json 字段说明
echo     tools\模拟发送遥测数据.ps1 无游戏时测试 HUD 用
echo.
echo   压缩包已生成：%ZIP%
echo   该目录位于仓库之外，不会进入版本库；
echo   重复发布时旧文件会被覆盖，但不会自动清除残留。
echo ==============================================
echo.
pause
