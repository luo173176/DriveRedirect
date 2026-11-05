param([string]$MsiPath)
if (-not $MsiPath) { throw 'Usage: dump-msi-tables.ps1 <msi>' }
$installer = New-Object -ComObject WindowsInstaller.Installer
$db = $installer.OpenDatabase((Resolve-Path $MsiPath).Path, 0)
foreach ($table in @('Directory','CustomAction','InstallExecuteSequence','InstallUISequence','Property','Component')) {
    try {
        $view = $db.OpenView("SELECT * FROM ``$table``")
        $view.Execute()
        Write-Output "===== $table ====="
        while ($record = $view.Fetch()) {
            Write-Output ("fields=" + $record.FieldCount)
            $values = for ($i = 1; $i -le $record.FieldCount; $i++) { $record.StringData($i) | Out-String }
            Write-Output ($values -join ' | ')
        }
        $view.Close()
    }
    catch {
        Write-Output "===== $table (unavailable) ====="
    }
}
