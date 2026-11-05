param(
    [string]$MsiPath,
    [string]$TransformPath,
    [string]$LogPath,
    [string]$ExtraArguments,
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'

if ($Uninstall) {
    $package = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue |
        Where-Object { $_.DisplayName -eq 'Acme Private' } |
        Select-Object -First 1
    if ($null -ne $package) {
        Start-Process msiexec.exe -ArgumentList @('/x', $package.PSChildName, '/qn') -Wait
    }
    return
}

$arguments = @('/i', ('"' + $MsiPath + '"'), ('TRANSFORMS="' + $TransformPath + '"'), '/qn', '/L*v', ('"' + $LogPath + '"'))
if (-not [string]::IsNullOrWhiteSpace($ExtraArguments)) {
    $arguments += $ExtraArguments
}
$process = Start-Process msiexec.exe -ArgumentList $arguments -Wait -PassThru
Write-Output ('exit=' + $process.ExitCode)
Write-Output ('D exists: ' + (Test-Path 'D:\Program Files\AcmePrivate\AcmePrivate.exe'))
Write-Output ('C exists: ' + (Test-Path 'C:\Program Files\AcmePrivate\AcmePrivate.exe'))
