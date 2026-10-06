# Diagnostic: dump every type in the Timberborn modding assemblies, with
# load errors printed (not swallowed), to find the real mod entrypoint
# interface. Run from the game PC:
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\find-mod-types.ps1
#
# Output is also saved to tools\mod-types.txt so it can be pasted/attached whole.

param(
    [string]$ManagedDir = 'C:\Program Files (x86)\Steam\steamapps\common\Timberborn\Timberborn_Data\Managed'
)

$out = New-Object System.Collections.Generic.List[string]
function Emit([string]$line) { Write-Host $line; $out.Add($line) }

$targets = Get-ChildItem -Path $ManagedDir -Filter 'Timberborn.Mod*.dll'
Emit ("matched " + $targets.Count + " assemblies: " + (($targets | ForEach-Object { $_.Name }) -join ', '))

foreach ($file in $targets) {
    Emit ""
    Emit ("=== " + $file.Name + " ===")

    $asm = $null
    try {
        $asm = [Reflection.Assembly]::LoadFrom($file.FullName)
    } catch {
        Emit ("LOAD FAIL: " + $_.Exception.Message)
        continue
    }

    $types = $null
    try {
        $types = $asm.GetTypes()
    } catch [Reflection.ReflectionTypeLoadException] {
        Emit "partial type load; first loader errors:"
        $_.Exception.LoaderExceptions | Select-Object -First 3 | ForEach-Object { Emit ("  " + $_.Message) }
        $types = $_.Exception.Types | Where-Object { $_ -ne $null }
    } catch {
        Emit ("GETTYPES FAIL: " + $_.Exception.Message)
        continue
    }

    foreach ($t in ($types | Sort-Object FullName)) {
        $kind = if ($t.IsInterface) { 'interface' } elseif ($t.IsEnum) { 'enum' } else { 'class' }
        Emit ($kind + ' ' + $t.FullName)
    }
}

$dest = Join-Path $PSScriptRoot 'mod-types.txt'
$out | Set-Content -Path $dest -Encoding UTF8
Write-Host ""
Write-Host ("saved to " + $dest)
