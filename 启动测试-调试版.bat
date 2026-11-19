@echo off
chcp 65001 >nul
cd /d "%~dp0ZhuangJiSoft\bin\Debug\net8.0-windows"
if not exist DeskApp.exe (
    echo [错误] 找不到 Debug 版 DeskApp.exe
    echo 请先让助手执行 Debug 构建
    pause
    exit /b 1
)
echo 正在启动【调试版 Debug】DeskApp.exe ...
echo 路径: %cd%\DeskApp.exe
start "" "%cd%\DeskApp.exe"
