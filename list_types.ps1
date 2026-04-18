Add-Type -AssemblyName 'System.Reflection'
$asmPath = 'C:\Users\roman\source\repos\ec22761\SFR\SFD\Superfighters Deluxe.pub.dll'
$asm = [System.Reflection.Assembly]::LoadFrom($asmPath)
try { $types = $asm.GetTypes() } catch [System.Reflection.ReflectionTypeLoadException] { $types = $_.Exception.Types | Where-Object { $null -ne $_ } }
foreach ($type in $types) {
    Write-Host "$($type.FullName)"
}
