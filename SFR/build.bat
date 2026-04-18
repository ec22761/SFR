SET SFD="C:\Program Files (x86)\Steam\steamapps\common\Superfighters Deluxe"
SET OUTDIR=%~1
SET PROJROOT=%~2

copy "%OUTDIR%SFR.runtimeconfig.json" %SFD%
copy "%OUTDIR%SFR.deps.json" %SFD%
copy "%OUTDIR%SFR.dll" %SFD%
copy "%OUTDIR%SFR.exe" %SFD%
copy "%OUTDIR%0Harmony.dll" %SFD%
if not exist %SFD%\SFR mkdir %SFD%\SFR
xcopy "%PROJROOT%\Content" %SFD%\SFR\Content /E /I /Y /Q

