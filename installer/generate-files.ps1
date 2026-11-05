param(
    [string]$SourceDir = (Join-Path $PSScriptRoot '..\artifacts\stage'),
    [string]$OutputPath = (Join-Path $PSScriptRoot 'GeneratedFiles.wxs')
)

$ErrorActionPreference = 'Stop'
$source = (Resolve-Path $SourceDir).Path
$excluded = @(
    'DriveRedirect.Service.exe',
    'DriveRedirect.Launcher.exe',
    'DriveRedirect.Desktop.exe'
)

function New-DeterministicGuid([string]$value) {
    $md5 = [System.Security.Cryptography.MD5]::Create()
    try {
        $bytes = $md5.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($value.ToLowerInvariant()))
    }
    finally {
        $md5.Dispose()
    }
    return ([Guid]::new([byte[]]$bytes)).ToString('B').ToUpperInvariant()
}

function Escape-Xml([string]$value) {
    return [System.Security.SecurityElement]::Escape($value)
}

function Get-StableHex([string]$value, [int]$length) {
    $md5 = [System.Security.Cryptography.MD5]::Create()
    try {
        $hash = $md5.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($value.ToLowerInvariant()))
    }
    finally {
        $md5.Dispose()
    }
    return ([System.BitConverter]::ToString($hash)).Replace('-', '').Substring(0, $length).ToUpperInvariant()
}

function Get-DirectoryId([string]$relativeDirectory) {
    if ([string]::IsNullOrWhiteSpace($relativeDirectory)) {
        return 'INSTALLFOLDER'
    }
    return 'dir_' + (Get-StableHex $relativeDirectory 12)
}

$files = Get-ChildItem -LiteralPath $source -File -Recurse |
    Where-Object { $excluded -notcontains $_.Name -and $_.Extension -ne '.pdb' } |
    Sort-Object FullName

$directories = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
foreach ($file in $files) {
    $relativeDirectory = [System.IO.Path]::GetDirectoryName(
        $file.FullName.Substring($source.Length).TrimStart('\'))
    if ([string]::IsNullOrWhiteSpace($relativeDirectory)) {
        continue
    }
    $parts = $relativeDirectory.Split('\')
    for ($i = 1; $i -le $parts.Length; $i++) {
        [void]$directories.Add(($parts[0..($i - 1)] -join '\'))
    }
}
$directories = @($directories | Sort-Object)

function Write-DirectoryChildren(
    [string]$parentRelative,
    [int]$depth,
    [System.Text.StringBuilder]$builder
) {
    $children = @($directories | Where-Object {
        $parent = [System.IO.Path]::GetDirectoryName($_)
        if ([string]::IsNullOrWhiteSpace($parentRelative)) {
            [string]::IsNullOrWhiteSpace($parent)
        }
        else {
            $parent -eq $parentRelative
        }
    } | Sort-Object)

    foreach ($directory in $children) {
        $name = [System.IO.Path]::GetFileName($directory)
        $indent = '      ' + ('  ' * $depth)
        [void]$builder.AppendLine(
            $indent + '<Directory Id="' + (Get-DirectoryId $directory) + '" Name="' + (Escape-Xml $name) + '">'
        )
        Write-DirectoryChildren $directory ($depth + 1) $builder
        [void]$builder.AppendLine($indent + '</Directory>')
    }
}

$builder = [System.Text.StringBuilder]::new()
[void]$builder.AppendLine('<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">')
[void]$builder.AppendLine('  <Fragment>')
[void]$builder.AppendLine('    <DirectoryRef Id="INSTALLFOLDER">')
Write-DirectoryChildren '' 0 $builder
[void]$builder.AppendLine('    </DirectoryRef>')
[void]$builder.AppendLine('    <ComponentGroup Id="AppFiles">')
foreach ($file in $files) {
    $relative = $file.FullName.Substring($source.Length).TrimStart('\')
    $key = $relative.ToLowerInvariant()
    $relativeDirectory = [System.IO.Path]::GetDirectoryName($relative)
    $id = Get-StableHex $key 16
    $guid = New-DeterministicGuid $key
    [void]$builder.AppendLine(
        '      <Component Id="cmp_' + $id + '" Guid="' + $guid +
        '" Directory="' + (Get-DirectoryId $relativeDirectory) + '">'
    )
    [void]$builder.AppendLine(
        '        <File Id="fil_' + $id + '" Source="' + (Escape-Xml ('$(var.SourceDir)\' + $relative)) + '" KeyPath="yes" />'
    )
    [void]$builder.AppendLine('      </Component>')
}
[void]$builder.AppendLine('    </ComponentGroup>')
[void]$builder.AppendLine('  </Fragment>')
[void]$builder.AppendLine('</Wix>')

$directory = Split-Path -Parent $OutputPath
if ($directory) {
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
}
[System.IO.File]::WriteAllText($OutputPath, $builder.ToString(), [System.Text.UTF8Encoding]::new($false))
Write-Output "Generated $OutputPath with $($files.Count) files."
