Add-Type -AssemblyName 'System.Reflection'
$asmPath = 'C:\Users\roman\source\repos\ec22761\SFR\SFD\SFD.GameScriptInterface.dll'
$asm = [System.Reflection.Assembly]::LoadFrom($asmPath)
try { $types = $asm.GetTypes() } catch [System.Reflection.ReflectionTypeLoadException] { $types = $_.Exception.Types | Where-Object { $null -ne $_ } }

$patterns = 'Chain|Oil|Canister|Barrel|Fire|Burn|SetOnFire|Push|AddVelocity|ApplyImpulse|AddForce|Player'
foreach ($type in $types) {
    if ($type.FullName -match $patterns) {
        Write-Host "--- $($type.FullName) ---"
        try {
            $type.GetMembers([System.Reflection.BindingFlags]::Public -bor [System.Reflection.BindingFlags]::Instance -bor [System.Reflection.BindingFlags]::Static) | 
                Where-Object { $_.MemberType -match 'Method|Field|Property' } | 
                ForEach-Object {
                    if ($_.MemberType -eq 'Method') {
                        $params = [System.String]::Join(', ', ($_.GetParameters() | ForEach-Object { "$($_.ParameterType.Name) $($_.Name)" }))
                        "$($_.MemberType): $($_.ReturnType.Name) $($_.Name)($params)"
                    } else {
                        "$($_.MemberType): $($_.Name)"
                    }
                }
        } catch { "Could not get members" }
    }
}
