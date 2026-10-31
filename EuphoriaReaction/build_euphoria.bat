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
rem getups.bin is not copied: the GTA clips are personal; the get-up clips come from getup-tool (MotionBricks)
if not exist "J:\Games\SPT 4.1.6\BepInEx\plugins\EuphoriaReaction\presets" mkdir "J:\Games\SPT 4.1.6\BepInEx\plugins\EuphoriaReaction\presets"
copy /y "presets\*.txt" "J:\Games\SPT 4.1.6\BepInEx\plugins\EuphoriaReaction\presets\" >> "%LOG%" 2>&1
copy /y "body.json" "J:\Games\SPT 4.1.6\BepInEx\plugins\EuphoriaReaction\body.json" >> "%LOG%" 2>&1
echo BUILD+DEPLOY OK >> "%LOG%"
