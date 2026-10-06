# Look up game types by name and print them, optionally with their public members.
# Run from the game PC (game does not need to be running):
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\find-types.ps1 -Pattern "Weather|GameCycle"
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\find-types.ps1 -Pattern "^WeatherService$" -Members
#
# -Pattern  regex matched against the simple type name (case-insensitive)
# -Members  also list constructors, properties, fields and methods of each match
# Output is printed and saved to tools\find-types-out.txt

param(
    [Parameter(Mandatory = $true)][string]$Pattern,
    [switch]$Members,
    [string]$ManagedDir = 'C:\Program Files (x86)\Steam\steamapps\common\Timberborn\Timberborn_Data\Managed'
)

$out = New-Object System.Collections.Generic.List[string]
function Emit([string]$line) { Write-Host $line; $out.Add($line) }

$files = Get-ChildItem -Path $ManagedDir -Filter '*.dll' |
    Where-Object { $_.Name -like 'Timberborn.*' -or $_.Name -like 'Bindito.*' }

$flags = [Reflection.BindingFlags]'Public,Instance,Static,DeclaredOnly'

foreach ($file in $files) {
    $asm = $null
    try { $asm = [Reflection.Assembly]::LoadFrom($file.FullName) } catch { continue }

    $types = $null
    try { $types = $asm.GetTypes() }
    catch [Reflection.ReflectionTypeLoadException] { $types = $_.Exception.Types | Where-Object { $_ -ne $null } }
    catch { continue }

    foreach ($t in ($types | Where-Object { $_.Name -match $Pattern -and $_.FullName -notmatch '[<+]' } | Sort-Object FullName)) {
        $kind = if ($t.IsInterface) { 'interface' } elseif ($t.IsEnum) { 'enum' } elseif ($t.IsAbstract) { 'abstract class' } else { 'class' }
        Emit ($kind + ' ' + $t.FullName + '   [' + $file.Name + ']')

        if (-not $Members) { continue }

        try {
            foreach ($c in $t.GetConstructors($flags)) {
                $ps = ($c.GetParameters() | ForEach-Object { $_.ParameterType.Name + ' ' + $_.Name }) -join ', '
                Emit ('    ctor(' + $ps + ')')
            }
            foreach ($p in $t.GetProperties($flags)) { Emit ('    prop   ' + $p.PropertyType.Name + ' ' + $p.Name) }
            foreach ($f in $t.GetFields($flags))     { Emit ('    field  ' + $f.FieldType.Name + ' ' + $f.Name) }
            foreach ($m in ($t.GetMethods($flags) | Where-Object { -not $_.IsSpecialName })) {
                $ps = ($m.GetParameters() | ForEach-Object { $_.ParameterType.Name + ' ' + $_.Name }) -join ', '
                Emit ('    method ' + $m.ReturnType.Name + ' ' + $m.Name + '(' + $ps + ')')
            }
        } catch {
            Emit ('    (could not read members: ' + $_.Exception.Message + ')')
        }
    }
}

$dest = Join-Path $PSScriptRoot 'find-types-out.txt'
$out | Set-Content -Path $dest -Encoding UTF8
Write-Host ''
Write-Host ('saved to ' + $dest)
