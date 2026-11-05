param(
    [string]$MsiPath,
    [string]$OutputPath,
    [string]$RootFolders = 'Program Files',
    [string]$DirectoryId = 'AcmePrivateDir',
    [string]$FolderName = 'AcmePrivate',
    [string]$RootValue = 'D:\',
    [switch]$UpdateRoot
)

$ErrorActionPreference = 'Stop'
$installer = New-Object -ComObject WindowsInstaller.Installer
$temp = Join-Path $env:TEMP ('dr-mst-' + [guid]::NewGuid().ToString('N') + '.msi')
Copy-Item $MsiPath $temp -Force

function Invoke-MsiSql {
    param($Database, [string]$Sql)
    $view = $Database.OpenView($Sql)
    $view.Execute()
    $view.Close()
}

try {
    $db = $installer.OpenDatabase($temp, 1)
    if ($UpdateRoot) {
        Invoke-MsiSql $db "UPDATE ``Directory`` SET ``DefaultDir``='$RootValue' WHERE ``Directory``='TARGETDIR'"
    }
    Invoke-MsiSql $db "INSERT INTO ``Directory`` (``Directory``, ``Directory_Parent``, ``DefaultDir``) VALUES ('DDROOTPF', 'TARGETDIR', '$RootFolders')"
    Invoke-MsiSql $db "UPDATE ``Directory`` SET ``Directory_Parent``='DDROOTPF', ``DefaultDir``='$FolderName' WHERE ``Directory``='$DirectoryId'"
    $db.Commit()

    $reference = $installer.OpenDatabase((Resolve-Path $MsiPath).Path, 0)
    if (Test-Path $OutputPath) { Remove-Item -Force $OutputPath }
    $db.GenerateTransform($reference, $OutputPath)
    $db.CreateTransformSummaryInfo($reference, $OutputPath, 0, 0)
    Write-Output ("MST: $OutputPath size=$((Get-Item $OutputPath).Length)")
}
finally {
    Remove-Item -Force $temp -ErrorAction SilentlyContinue
}
