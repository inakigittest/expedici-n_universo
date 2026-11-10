@echo off
rem Expedicion Universo: compila el C# y lanza el juego.   jugar.cmd      -> jugar
rem                                          jugar.cmd -e   -> abrir el editor de Godot
setlocal
set "PATH=C:\Program Files\dotnet;%PATH%"
rem Ruta a Godot 4.7.2 mono: define la variable GODOT para usar otra instalacion.
if not defined GODOT set "GODOT=C:\Users\kiooo\Desktop\mechas_godot\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64.exe"
cd /d "%~dp0game"
dotnet build AresX.csproj -v q -nologo || (pause & exit /b 1)
if /i "%~1"=="-e" (
  "%GODOT%" --path . -e
) else (
  "%GODOT%" --path . %*
)
