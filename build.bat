@echo off
echo Building JoinForce...
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe -target:winexe -out:JoinForce.exe -r:System.dll -r:System.Drawing.dll -r:System.Windows.Forms.dll -r:System.ServiceProcess.dll -win32manifest:JoinForce.manifest Program.cs
if %ERRORLEVEL% EQU 0 (
    echo Build succeeded: JoinForce.exe
) else (
    echo Build failed.
)
pause
