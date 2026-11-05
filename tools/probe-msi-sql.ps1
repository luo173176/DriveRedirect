$ErrorActionPreference = 'Stop'
$src = Join-Path $PSScriptRoot '..\tests\fixtures\wix\AcmePrivate.msi'
$tmp = Join-Path $env:TEMP ('msitest-' + [guid]::NewGuid().ToString('N') + '.msi')
Copy-Item -LiteralPath $src -Destination $tmp -Force

$installer = New-Object -ComObject WindowsInstaller.Installer
$db = $installer.OpenDatabase($tmp, 1)
$queries = @(
    "UPDATE ``Directory`` SET ``Directory_Parent``='TARGETDIR', ``DefaultDir``='D:\Program Files\AcmePrivate' WHERE ``Directory``='AcmePrivateDir'",
    "UPDATE ``Directory`` SET ``DefaultDir``='D:\Program Files\AcmePrivate' WHERE ``Directory``='AcmePrivateDir'",
    "UPDATE Directory SET DefaultDir='D:\Program Files\AcmePrivate' WHERE Directory='AcmePrivateDir'",
    "UPDATE ``Directory`` SET ``DefaultDir``='AcmePrivateD' WHERE ``Directory``='AcmePrivateDir'",
    "UPDATE ``Directory``
     SET ``Directory_Parent`` = 'TARGETDIR', ``DefaultDir`` = 'D:\Program Files\AcmePrivate'
     WHERE ``Directory`` = 'AcmePrivateDir'"
)

foreach ($query in $queries) {
    try {
        $view = $db.OpenView($query)
        $view.Execute()
        $view.Close()
        Write-Output "OK: $query"
    }
    catch {
        Write-Output "FAIL: $query"
        Write-Output ("  " + $_.Exception.Message)
    }
}

try { $db.Commit() } catch { Write-Output ("COMMIT FAIL: " + $_.Exception.Message) }
Write-Output ("TEMP=" + $tmp)
