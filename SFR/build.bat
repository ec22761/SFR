SET SFD="C:\Program Files (x86)\Steam\steamapps\common\Superfighters Deluxe"
SET OUTDIR=%~1
SET PROJROOT=%~2

copy "%OUTDIR%SFR.exe.config" %SFD%
copy "%OUTDIR%SFR.exe" %SFD%
if not exist %SFD%\SFR mkdir %SFD%\SFR
copy "%OUTDIR%0Harmony.dll" %SFD%\SFR
xcopy "%PROJROOT%\Content" %SFD%\SFR\Content /E /I /Y /Q

