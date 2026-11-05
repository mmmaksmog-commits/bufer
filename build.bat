@echo off
chcp 65001 > nul
cd /d "%~dp0"
title Сборка Буфер by mysik

set "FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"
if not exist "%FW%\csc.exe" set "FW=%WINDIR%\Microsoft.NET\Framework\v4.0.30319"
if not exist "%FW%\csc.exe" (
    echo [!] Не найден компилятор .NET Framework 4.x ^(csc.exe^).
    echo     Установи .NET Framework 4.8: https://dotnet.microsoft.com/download/dotnet-framework/net48
    pause
    exit /b 1
)

echo.
echo   Буфер by mysik — сборка
echo   ----------------------------
echo.

"%FW%\csc.exe" /nologo /target:winexe /optimize+ /codepage:65001 ^
  /out:Bufer.exe /win32manifest:app.manifest /win32icon:app.ico ^
  /lib:"%FW%\WPF" ^
  /r:System.dll ^
  /r:System.Core.dll ^
  /r:System.Xaml.dll ^
  /r:WindowsBase.dll ^
  /r:PresentationCore.dll ^
  /r:PresentationFramework.dll ^
  /r:System.Drawing.dll ^
  /r:System.Windows.Forms.dll ^
  /r:System.IO.Compression.dll ^
  /r:System.IO.Compression.FileSystem.dll ^
  /resource:MainWindow.xaml,MainWindow.xaml /resource:Theme.xaml,Theme.xaml ^
  Shell.cs Bufer.cs

if errorlevel 1 (
    echo.
    echo [!] Ошибка сборки. Скопируй текст выше и пришли — поправлю.
    pause
    exit /b 1
)

echo.
echo   Готово: Bufer.exe
echo   Можно перенести его в любую папку и запускать.
echo.
pause
