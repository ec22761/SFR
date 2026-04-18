[Reflection.Assembly]::LoadFrom('C:\Users\roman\source\repos\ec22761\SFR\SFD\Superfighters Deluxe.pub.dll') | Out-Null
$types = [Reflection.Assembly]::LoadFrom('C:\Users\roman\source\repos\ec22761\SFR\SFD\Superfighters Deluxe.pub.dll').GetTypes()
$fv = $types | ? { $_.FullName -match 'ObjectData\+FireValues' }
Write-Host '--- FIREVALUES ---'
$fv.GetFields(60) | % { Write-Host "($($_.FieldType.Name)) $($_.Name)" }
$fv.GetProperties(60) | % { Write-Host "($($_.PropertyType.Name)) $($_.Name)" }
$fb = $types | ? { $_.FullName -eq 'SFD.Effects.FireBig' }
Write-Host '--- FIREBIG ---'
$fb.GetFields(60) | % { Write-Host "($($_.FieldType.Name)) $($_.Name)" }
$fb.GetProperties(60) | % { Write-Host "($($_.PropertyType.Name)) $($_.Name)" }
$f = $types | ? { $_.FullName -eq 'SFD.Effects.Fire' }
Write-Host '--- FIRE ---'
$f.GetFields(60) | % { Write-Host "($($_.FieldType.Name)) $($_.Name)" }
$f.GetProperties(60) | % { Write-Host "($($_.PropertyType.Name)) $($_.Name)" }
$pl = $types | ? { $_.FullName -eq 'SFD.Player' }
Write-Host '--- PLAYER ---'
$pl.GetMethods(60) | ? { $_.Name -match 'Burn|Fire|CheckFire' } | % { 
    $pa = $_.GetParameters() | % { "$($_.ParameterType.Name) $($_.Name)" }
    Write-Host "$($_.ReturnType.Name) $($_.Name)($($pa -join ', '))" 
}
