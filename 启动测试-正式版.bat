@echo off
chcp 65001 >nul
cd /d "%~dp0ZhuangJiSoft\bin\Release\net8.0-windows"
if not exist DeskApp.exe (
    echo [错误] 找不到 Release 版 DeskApp.exe
    echo 请先让助手执行 Release 构建
    pause
    exit /b 1
)
echo 正在启动【正式版 Release】DeskApp.exe ...
echo 路径: %cd%\DeskApp.exe
start "" "%cd%\DeskApp.exe"
