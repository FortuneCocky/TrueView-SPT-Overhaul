@echo off
setlocal
cd /d "%~dp0"
set LOG=%~dp0build_log.txt
echo === build %date% %time% === > "%LOG%"
set DN=dotnet
where dotnet >> "%LOG%" 2>&1 || set DN="%~dp0..\dotnet10\dotnet.exe"
%DN% --version >> "%LOG%" 2>&1
%DN% build EuphoriaReaction.csproj -c Release -nologo -v minimal >> "%LOG%" 2>&1
if errorlevel 1 (echo BUILD FAILED >> "%LOG%" & exit /b 1)
copy /y "bin\Release\EuphoriaReaction.dll" "J:\Games\SPT 4.1.6\BepInEx\plugins\EuphoriaReaction\EuphoriaReaction.dll" >> "%LOG%" 2>&1
if errorlevel 1 (echo DEPLOY FAILED - is the game running? >> "%LOG%" & exit /b 1)
copy /y "getups.bin" "J:\Games\SPT 4.1.6\BepInEx\plugins\EuphoriaReaction\getups.bin" >> "%LOG%" 2>&1
echo BUILD+DEPLOY OK >> "%LOG%"
