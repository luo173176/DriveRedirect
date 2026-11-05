param(
    [Parameter(Mandatory = $true)][ValidateSet('rename', 'setprop')][string]$Mode,
    [Parameter(Mandatory = $true)][string]$Source,
    [Parameter(Mandatory = $true)][string]$TargetPath,
    [string]$DirectoryProperty = 'AcmePrivateDir',
    [string]$NewProperty = 'ACMEDIR'
)

$ErrorActionPreference = 'Stop'
$tmp = Join-Path $env:TEMP ('dd-variant-' + [guid]::NewGuid().ToString('N') + '.msi')
Copy-Item -LiteralPath (Resolve-Path $Source).Path -Destination $tmp -Force

$installer = New-Object -ComObject WindowsInstaller.Installer
$db = $installer.OpenDatabase($tmp, 1)
function Invoke-MsiSql([string]$sql) {
    Write-Output "SQL: $sql"
    $view = $db.OpenView($sql)
    $view.Execute()
    $view.Close()
}

if ($Mode -eq 'rename') {
    Invoke-MsiSql "UPDATE ``Directory`` SET ``Directory``='$NewProperty' WHERE ``Directory``='$DirectoryProperty'"
    Invoke-MsiSql "UPDATE ``Directory`` SET ``Directory_Parent``='$NewProperty' WHERE ``Directory_Parent``='$DirectoryProperty'"
    Invoke-MsiSql "UPDATE ``Component`` SET ``Directory_``='$NewProperty' WHERE ``Directory_``='$DirectoryProperty'"
}
else {
    $escaped = $TargetPath.Replace("'", "''")
    try {
        $probe = $db.OpenView("SELECT * FROM ``CustomAction``")
        $probe.Close()
    }
    catch {
        Invoke-MsiSql "CREATE TABLE ``CustomAction`` (``Action`` CHAR(72) NOT NULL PRIMARY KEY, ``Type`` SHORT NOT NULL, ``Source`` CHAR(255), ``Target`` CHAR(255), ``ExtendedType`` LONG)"
    }
    Invoke-MsiSql "INSERT INTO ``CustomAction`` (``Action``,``Type``,``Source``,``Target``) VALUES ('DDSetInstallDir',51,'$DirectoryProperty','$escaped')"
    Invoke-MsiSql "INSERT INTO ``InstallExecuteSequence`` (``Action``,``Condition``,``Sequence``) VALUES ('DDSetInstallDir','1',999)"
    Invoke-MsiSql "INSERT INTO ``InstallUISequence`` (``Action``,``Condition``,``Sequence``) VALUES ('DDSetInstallDir','1',999)"
}

$db.Commit()
Write-Output "VARIANT=$tmp"
Write-Output "PROPERTY=$NewProperty"
