@echo off
rem Compila el instalador de Windows con Inno Setup (ISCC.exe).
setlocal
set "ISCC="
for %%P in ("%LOCALAPPDATA%\Programs\Inno Setup 7\ISCC.exe" "%ProgramFiles%\Inno Setup 7\ISCC.exe" "%ProgramFiles(x86)%\Inno Setup 7\ISCC.exe" "%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe" "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" "C:\Users\kiooo\AndroidBuild\InnoSetup\ISCC.exe") do (
  if exist %%P set "ISCC=%%~P"
)
if not defined ISCC (
  echo No se encontro Inno Setup. Instalalo desde https://jrsoftware.org/isdl.php y vuelve a ejecutar.
  pause
  exit /b 1
)
cd /d "%~dp0"
"%ISCC%" ExpedicionUniverso.iss || (pause & exit /b 1)
echo.
echo Instalador creado en ..\exports\ExpedicionUniverso_Setup_1.0.exe
