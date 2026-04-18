Add-Type -AssemblyName 'System.Reflection'
$asmPath = 'C:\Users\roman\source\repos\ec22761\SFR\SFD\Superfighters Deluxe.pub.dll'
if (!(Test-Path $asmPath)) { Write-Error 'Assembly not found'; exit }
$asm = [System.Reflection.Assembly]::LoadFrom($asmPath)
Write-Host "Assembly loaded: $($asm.FullName)"
try {
    $types = $asm.GetTypes()
} catch [System.Reflection.ReflectionTypeLoadException] {
    $types = $_.Exception.Types | Where-Object { $null -ne $_ }
    Write-Host "Caught ReflectionTypeLoadException, count: $($types.Count)"
}

Write-Host "Total types loaded: $($types.Count)"

$filteredTypes = $types | Where-Object { $_.FullName -match 'Chain|Oil|Canister|Barrel' }
if ($null -eq $filteredTypes) {
    Write-Host 'No matching types found'
} else {
    foreach ($type in $filteredTypes) {
        Write-Host "--- Type: $($type.FullName) ---"
        try {
            $type.GetMembers([System.Reflection.BindingFlags]::Public -bor [System.Reflection.BindingFlags]::Instance -bor [System.Reflection.BindingFlags]::Static) | 
                Where-Object { $_.MemberType -match 'Method|Field|Property' } | 
                Select-Object MemberType, Name | Format-Table -AutoSize
        } catch {
            Write-Host "Could not get members for $($type.FullName)"
        }
    }
}

$playerType = $types | Where-Object { $_.FullName -match 'Player$' }
if ($playerType) {
    $playerType = $playerType | Select-Object -First 1
    Write-Host "--- Player Methods found in $($playerType.FullName) ---"
    try {
        $playerType.GetMethods([System.Reflection.BindingFlags]::Public -bor [System.Reflection.BindingFlags]::Instance) | 
            Where-Object { $_.Name -match 'Fire|Burn|SetOnFire|Push|AddVelocity|ApplyImpulse|AddForce' } | 
            ForEach-Object { "$($_.ReturnType.Name) $($_.Name)($(([System.String]::Join(', ', ($_.GetParameters() | ForEach-Object { "$($_.ParameterType.Name) $($_.Name)" }))))))" }
    } catch {
        Write-Host "Could not get methods for Player"
    }
} else {
    Write-Host 'Player type not found'
}
