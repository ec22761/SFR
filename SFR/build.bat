SET SFD="C:\Program Files (x86)\Steam\steamapps\common\Superfighters Deluxe"

copy %~1SFR.exe.config %SFD%
copy %~1SFR.exe %SFD%
if not exist %SFD%\SFR mkdir %SFD%\SFR
copy %~10Harmony.dll %SFD%\SFR

