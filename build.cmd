@echo off
setlocal
cd /d "%~dp0"
title Pi 桌面 - 构建

set "CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
set "WV_VERSION=1.0.4258.31"
set "NUPKG=webview2-pkg"
set "OUT=bin"

echo ============================================
echo   Pi 桌面 构建脚本（只需跑一次，或改代码后重跑）
echo ============================================

if not exist "%OUT%" mkdir "%OUT%"

rem ---- 1. 下载并解压 WebView2 SDK（本机已装 WebView2 运行时，无需装 Chromium）----
if not exist "%NUPKG%\lib\net462\Microsoft.Web.WebView2.Core.dll" (
  echo [1/3] 下载 WebView2 SDK %WV_VERSION% ...
  curl.exe -sL -o webview2.nupkg "https://www.nuget.org/api/v2/package/Microsoft.Web.WebView2/%WV_VERSION%" || goto :err
  echo [1/3] 解压 ...
  copy /Y webview2.nupkg wv.zip >nul
  powershell -NoProfile -Command "Expand-Archive -LiteralPath '%CD%\wv.zip' -DestinationPath '%CD%\%NUPKG%' -Force"
  if errorlevel 1 goto :err
  del wv.zip
) else (
  echo [1/3] WebView2 SDK 已存在，跳过下载
)

rem ---- 2. 编译 ----
echo [2/3] 编译 pi-desktop.cs ...
"%CSC%" /nologo /target:winexe /win32icon:assets\app.ico /win32manifest:src\app.manifest /out:%OUT%\pi-desktop.exe ^
  /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ^
  /r:%NUPKG%\lib\net462\Microsoft.Web.WebView2.Core.dll ^
  /r:%NUPKG%\lib\net462\Microsoft.Web.WebView2.WinForms.dll ^
  src\pi-desktop.cs
if errorlevel 1 goto :err

rem ---- 3. 复制运行时 DLL ----
echo [3/3] 复制运行时 DLL ...
copy /Y "%NUPKG%\lib\net462\Microsoft.Web.WebView2.Core.dll" "%OUT%\" >nul 2>&1
copy /Y "%NUPKG%\lib\net462\Microsoft.Web.WebView2.WinForms.dll" "%OUT%\" >nul 2>&1
rem 64 位系统用 win-x64；不要复制 x86 版（同名会覆盖）
copy /Y "%NUPKG%\runtimes\win-x64\native\WebView2Loader.dll" "%OUT%\" >nul 2>&1
if not exist "%OUT%\WebView2Loader.dll" (
  echo 警告：未找到 WebView2Loader.dll，运行可能失败
)

echo.
echo 构建完成：%OUT%\pi-desktop.exe
echo 双击 %OUT%\pi-desktop.exe 即可启动 Pi 桌面
pause
exit /b 0

:err
echo.
echo 构建失败，请把上面的错误信息发给 pi 排查。
pause
exit /b 1
