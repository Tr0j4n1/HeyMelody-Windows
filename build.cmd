@echo off
rem Builds MelodyDesk.exe with the C# compiler that ships with Windows (.NET Framework 4.x, WPF). No SDK needed.
cd /d "%~dp0"
set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
"%FW%\csc.exe" -nologo -optimize -target:winexe -out:MelodyDesk.exe -lib:"%FW%\WPF" -win32icon:Assets\app.ico ^
  -r:System.dll -r:System.Core.dll -r:System.Xaml.dll -r:WindowsBase.dll -r:PresentationCore.dll -r:PresentationFramework.dll ^
  -r:System.Windows.Forms.dll -r:System.Drawing.dll ^
  -resource:Styles.xaml,Styles.xaml -resource:MainWindow.xaml,MainWindow.xaml ^
  -resource:Assets\earbuds.png,earbuds.png -resource:Assets\app.png,app.png -resource:Assets\app.ico,app.ico ^
  Protocol.cs Bluetooth.cs Icons.cs Tray.cs NoiseMemory.cs MainWindow.cs Program.cs SelfTest.cs || exit /b 1
"%~dp0MelodyDesk.exe" --selftest | more
