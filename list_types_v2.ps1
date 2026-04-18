Add-Type -AssemblyName 'System.Reflection'
$asmPath = 'C:\Users\roman\source\repos\ec22761\SFR\SFD\Superfighters Deluxe.pub.dll'
$asm = [System.Reflection.Assembly]::LoadFrom($asmPath)
try { $types = $asm.GetTypes() } catch [System.Reflection.ReflectionTypeLoadException] { $types = $_.Exception.Types | Where-Object { $null -ne $_ } }

$patterns = 'Chain', 'Oil', 'Canister', 'Barrel', 'Fire', 'Burn', 'Player'
foreach ($type in $types) {
    if ($type.FullName -match ($patterns -join '|')) {
        Write-Host "--- FullName: $($type.FullName) ---"
    } else {
        # Check interfaces and base class
        $match = $false
        try {
            if ($type.BaseType -and $type.BaseType.FullName -match ($patterns -join '|')) { $match = $true }
            foreach ($iface in $type.GetInterfaces()) {
                if ($iface.FullName -match ($patterns -join '|')) { $match = $true; break }
            }
        } catch {}
        if ($match) { Write-Host "--- (Base/Iface Match) FullName: $($type.FullName) ---" }
    }
}
