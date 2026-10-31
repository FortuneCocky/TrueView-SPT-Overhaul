@echo off
setlocal
cd /d "%~dp0"
set LOG=%~dp0build_log.txt
echo === build %date% %time% === > "%LOG%"
set DN=dotnet
where dotnet >> "%LOG%" 2>&1 || set DN="%~dp0..\dotnet10\dotnet.exe"
%DN% --version >> "%LOG%" 2>&1
%DN% build DynamicDOF.csproj -c Release -nologo -v minimal >> "%LOG%" 2>&1
if errorlevel 1 (echo BUILD FAILED >> "%LOG%" & exit /b 1)
if not exist "J:\Games\SPT 4.1.6\BepInEx\plugins\DynamicDOF" mkdir "J:\Games\SPT 4.1.6\BepInEx\plugins\DynamicDOF"
copy /y "bin\Release\DynamicDOF.dll" "J:\Games\SPT 4.1.6\BepInEx\plugins\DynamicDOF\DynamicDOF.dll" >> "%LOG%" 2>&1
if errorlevel 1 (echo DEPLOY FAILED - is the game running? >> "%LOG%" & exit /b 1)
echo BUILD+DEPLOY OK >> "%LOG%"
