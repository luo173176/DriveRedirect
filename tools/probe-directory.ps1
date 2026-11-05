param([string]$MsiPath)
$installer = New-Object -ComObject WindowsInstaller.Installer
$db = $installer.OpenDatabase((Resolve-Path $MsiPath).Path, 0)
$view = $db.OpenView('SELECT `Directory`, `Directory_Parent`, `DefaultDir` FROM `Directory`')
$view.Execute()
while ($record = $view.Fetch()) {
    Write-Output ($record.StringData(1) + ' | ' + $record.StringData(2) + ' | ' + $record.StringData(3))
}
$view.Close()
