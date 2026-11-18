@echo off
rem Builds MelodyDesk.exe and its installer, MelodyDesk-Setup.exe, with the C# compiler that ships with Windows
rem (.NET Framework 4.x, WPF). No SDK needed.
cd /d "%~dp0"
set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
set REFS=-lib:"%FW%\WPF" -r:System.dll -r:System.Core.dll -r:System.Xaml.dll -r:WindowsBase.dll -r:PresentationCore.dll -r:PresentationFramework.dll
"%FW%\csc.exe" -nologo -optimize -target:winexe -out:MelodyDesk.exe -win32icon:Assets\app.ico -win32manifest:app.manifest %REFS% ^
  -r:System.Windows.Forms.dll -r:System.Drawing.dll -r:System.Runtime.dll ^
  -r:"%WINDIR%\System32\WinMetadata\Windows.Foundation.winmd" -r:"%WINDIR%\System32\WinMetadata\Windows.Media.winmd" ^
  -resource:Styles.xaml,Styles.xaml -resource:MainWindow.xaml,MainWindow.xaml ^
  -resource:Assets\earbuds.png,earbuds.png -resource:Assets\app.png,app.png -resource:Assets\app.ico,app.ico ^
  Version.cs Protocol.cs Bluetooth.cs Icons.cs Theme.cs Tray.cs Taskbar.cs Startup.cs Update.cs Media.cs NoiseMemory.cs MainWindow.cs Program.cs SelfTest.cs || exit /b 1
rem The setup only gets built from an exe that passes its self-test.
"%~dp0MelodyDesk.exe" --selftest > selftest.log
set SELFTEST=%errorlevel%
type selftest.log
del selftest.log
if not "%SELFTEST%"=="0" exit /b 1
"%FW%\csc.exe" -nologo -optimize -target:winexe -out:MelodyDesk-Setup.exe -win32icon:Assets\app.ico -win32manifest:app.manifest %REFS% ^
  -r:Microsoft.CSharp.dll ^
  -resource:MelodyDesk.exe,MelodyDesk.exe -resource:Styles.xaml,Styles.xaml -resource:Assets\app.png,app.png ^
  Version.cs Theme.cs Startup.cs Setup.cs || exit /b 1
echo built MelodyDesk.exe and MelodyDesk-Setup.exe
