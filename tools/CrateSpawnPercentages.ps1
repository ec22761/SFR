<#
.SYNOPSIS
    Calculates weapon spawn percentages per weapon-type crate.

.DESCRIPTION
    Reads the SpawnChance dictionary in SFR\Weapons\Database.cs and the new
    weapon registrations (LoadWeapons) to determine each weapon's weight and
    type, then prints the spawn percentage of every weapon for each crate type
    (Melee, Handgun, Rifle, Thrown, Powerup, InstantPickup).

    A weapon's spawn percentage in its type's crate is:
        weight / (sum of weights of all weapons of that type) * 100
#>

[CmdletBinding()]
param(
    [string]$DatabasePath = (Join-Path $PSScriptRoot '..\SFR\Weapons\Database.cs')
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $DatabasePath)) {
    throw "Database.cs not found at $DatabasePath"
}

# ---------------------------------------------------------------------------
# Vanilla weapon (ID 1..68) type mapping. Types match SFD.WeaponItemType.
# Entries omitted from the spawn-chance dictionary (Fists, Chair, etc.) are
# excluded here as well so they do not appear in any crate.
# ---------------------------------------------------------------------------
$vanillaTypes = @{
    1  = @{ Name = 'Magnum';          Type = 'Handgun' }
    2  = @{ Name = 'Shotgun';         Type = 'Rifle'   }
    3  = @{ Name = 'Katana';          Type = 'Melee'   }
    4  = @{ Name = 'PipeWrench';      Type = 'Melee'   }
    5  = @{ Name = 'Tommygun';        Type = 'Rifle'   }
    6  = @{ Name = 'M60';             Type = 'Rifle'   }
    8  = @{ Name = 'Machete';         Type = 'Melee'   }
    9  = @{ Name = 'Sniper';          Type = 'Rifle'   }
    10 = @{ Name = 'SawedOff';        Type = 'Rifle'   }
    11 = @{ Name = 'Bat';             Type = 'Melee'   }
    12 = @{ Name = 'Uzi';             Type = 'Handgun' }
    13 = @{ Name = 'Pills';           Type = 'Powerup' }
    14 = @{ Name = 'Medkit';          Type = 'Powerup' }
    15 = @{ Name = 'Slowmo5';         Type = 'Powerup' }
    16 = @{ Name = 'Slowmo10';        Type = 'Powerup' }
    17 = @{ Name = 'Bazooka';         Type = 'Rifle'   }
    18 = @{ Name = 'Axe';             Type = 'Melee'   }
    19 = @{ Name = 'AssaultRifle';    Type = 'Rifle'   }
    20 = @{ Name = 'Grenades';        Type = 'Thrown'  }
    21 = @{ Name = 'Lazer';           Type = 'Rifle'   }
    23 = @{ Name = 'Carbine';         Type = 'Rifle'   }
    24 = @{ Name = 'Pistol';          Type = 'Handgun' }
    25 = @{ Name = 'Molotovs';        Type = 'Thrown'  }
    26 = @{ Name = 'Flamethrower';    Type = 'Rifle'   }
    27 = @{ Name = 'Flaregun';        Type = 'Handgun' }
    28 = @{ Name = 'Revolver';        Type = 'Handgun' }
    29 = @{ Name = 'GrenadeLauncher'; Type = 'Rifle'   }
    30 = @{ Name = 'SMG';             Type = 'Handgun' }
    31 = @{ Name = 'Hammer';          Type = 'Melee'   }
    39 = @{ Name = 'SilencedPistol';  Type = 'Handgun' }
    40 = @{ Name = 'SilencedUzi';     Type = 'Handgun' }
    41 = @{ Name = 'Baton';           Type = 'Melee'   }
    42 = @{ Name = 'C4';              Type = 'Thrown'  }
    44 = @{ Name = 'Mines';           Type = 'Thrown'  }
    45 = @{ Name = 'Shuriken';        Type = 'Thrown'  }
    46 = @{ Name = 'Chain';           Type = 'Melee'   }
    49 = @{ Name = 'Knife';           Type = 'Melee'   }
    53 = @{ Name = 'MachinePistol';   Type = 'Handgun' }
    54 = @{ Name = 'DarkShotgun';     Type = 'Rifle'   }
    55 = @{ Name = 'MP50';            Type = 'Handgun' }
    56 = @{ Name = 'LeadPipe';        Type = 'Melee'   }
    57 = @{ Name = 'ShockBaton';      Type = 'Melee'   }
    59 = @{ Name = 'Chainsaw';        Type = 'Melee'   }
    61 = @{ Name = 'Pistol45';        Type = 'Handgun' }
    62 = @{ Name = 'StrengthBoost';   Type = 'Powerup' }
    63 = @{ Name = 'SpeedBoost';      Type = 'Powerup' }
    64 = @{ Name = 'Bow';             Type = 'Rifle'   }
    65 = @{ Name = 'Whip';            Type = 'Melee'   }
    66 = @{ Name = 'BouncingAmmo';    Type = 'Powerup' }
    67 = @{ Name = 'FireAmmo';        Type = 'Powerup' }
    68 = @{ Name = 'Drone';           Type = 'Powerup' }
}

$source = Get-Content -Raw -Path $DatabasePath

# ---------------------------------------------------------------------------
# Parse new-weapon registrations: lines like
#   new WeaponItem(WeaponItemType.Melee, new Brick()), // 71
# ---------------------------------------------------------------------------
$newTypes = @{}
$regex = [regex]'WeaponItemType\.(?<type>\w+),\s*new\s+(?<name>\w+)\(\)\)\s*,?\s*//\s*(?<id>\d+)'
foreach ($m in $regex.Matches($source)) {
    $id = [int]$m.Groups['id'].Value
    $newTypes[$id] = @{
        Name = $m.Groups['name'].Value
        Type = $m.Groups['type'].Value
    }
}

# ---------------------------------------------------------------------------
# Parse spawn-chance dictionary (skipping commented-out lines).
# ---------------------------------------------------------------------------
$chances = @{}
$startIdx = $source.IndexOf('m_wpns ??= new Dictionary<int, int>')
if ($startIdx -lt 0) { throw 'Could not locate m_wpns dictionary.' }
$braceOpen = $source.IndexOf('{', $startIdx)
$depth = 0
$endIdx = -1
for ($i = $braceOpen; $i -lt $source.Length; $i++) {
    $c = $source[$i]
    if ($c -eq '{') { $depth++ }
    elseif ($c -eq '}') {
        $depth--
        if ($depth -eq 0) { $endIdx = $i; break }
    }
}
$dictBody = $source.Substring($braceOpen + 1, $endIdx - $braceOpen - 1)

foreach ($rawLine in $dictBody -split "`n") {
    $line = $rawLine.Trim()
    if (-not $line) { continue }
    if ($line.StartsWith('//')) { continue }              # skip commented entries
    $entry = [regex]::Match($line, '^\{\s*(\d+)\s*,\s*(\d+)\s*\}')
    if ($entry.Success) {
        $chances[[int]$entry.Groups[1].Value] = [int]$entry.Groups[2].Value
    }
}

# ---------------------------------------------------------------------------
# Combine into a flat list and group by type.
# ---------------------------------------------------------------------------
$entries = foreach ($id in $chances.Keys) {
    $info = if ($vanillaTypes.ContainsKey($id)) { $vanillaTypes[$id] }
            elseif ($newTypes.ContainsKey($id))  { $newTypes[$id] }
            else { @{ Name = "Unknown($id)"; Type = 'Unknown' } }

    [pscustomobject]@{
        Id     = $id
        Name   = $info.Name
        Type   = $info.Type
        Weight = $chances[$id]
    }
}

$groups = $entries | Group-Object Type | Sort-Object Name

foreach ($g in $groups) {
    $total = ($g.Group | Measure-Object Weight -Sum).Sum
    Write-Host ''
    Write-Host ("=== {0} crate (total weight = {1}) ===" -f $g.Name, $total) -ForegroundColor Cyan

    $g.Group |
        Sort-Object Weight -Descending |
        ForEach-Object {
            [pscustomobject]@{
                Id         = $_.Id
                Name       = $_.Name
                Weight     = $_.Weight
                Percentage = [math]::Round(($_.Weight / $total) * 100, 2)
            }
        } |
        Format-Table -AutoSize
}
