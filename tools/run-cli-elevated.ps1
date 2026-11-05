param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$CliArgs
)

$cli = Join-Path $PSScriptRoot 'DriveRedirect.Cli\bin\Debug\net10.0-windows\DriveRedirect.Cli.exe'
$env:DOTNET_ROOT = 'D:\AgentCache\dotnet'
$env:PATH = 'D:\AgentCache\dotnet;' + $env:PATH
$output = Join-Path $env:TEMP 'drive-redirect-cli-output.txt'
& $cli @CliArgs *> $output
exit $LASTEXITCODE
