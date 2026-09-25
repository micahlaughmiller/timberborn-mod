# Diagnostic: reflect over every DLL in the Timberborn Managed folder and
# print anything matching the mod-entrypoint naming pattern, plus load/
# reflection failures instead of swallowing them. Run from the game PC:
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\find-mod-types.ps1
#
# Optionally pass a different Managed path as the first argument.

param(
    [string]$ManagedDir = 'C:\Program Files (x86)\Steam\steamapps\common\Timberborn\Timberborn_Data\Managed'
)

$dlls = Get-ChildItem -Path $ManagedDir -Filter *.dll
Write-Host ("found " + $dlls.Count + " dlls in " + $ManagedDir)

foreach ($file in $dlls) {
    $asm = $null
    try {
        $asm = [Reflection.Assembly]::LoadFrom($file.FullName)
    } catch {
        continue
    }

    $types = $null
    try {
        $types = $asm.GetTypes()
    } catch [Reflection.ReflectionTypeLoadException] {
        $types = $_.Exception.Types | Where-Object { $_ -ne $null }
    } catch {
        continue
    }

    $hits = $types | Where-Object {
        $_.Name -like 'IMod*' -or $_.Name -like '*ModStarter*' -or $_.Name -like '*ModEnvironment*' -or $_.Name -like '*ModEntry*'
    }
    foreach ($hit in $hits) {
        $kind = if ($hit.IsInterface) { 'interface' } else { 'class' }
        Write-Host ($file.Name + ' :: ' + $hit.FullName + ' [' + $kind + ']')
    }
}

Write-Host "done"
