<#
.SYNOPSIS
Runs the .NET SDK in a container, with the repository mounted, so a machine without the 10.x SDK can
still build, test and run dotnet-ef.

.EXAMPLE
pwsh scripts/dotnet.ps1 build DocReader.slnx

.EXAMPLE
pwsh scripts/dotnet.ps1 test tests/unit/DocReader.UnitTests
#>
[CmdletBinding()]
param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$DotnetArgs
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$sdkImage = if ($env:DOCREADER_SDK_IMAGE) { $env:DOCREADER_SDK_IMAGE } else { "mcr.microsoft.com/dotnet/sdk:10.0" }

if ($DotnetArgs.Count -gt 0 -and $DotnetArgs[0] -eq "ef") {
    $rest = ($DotnetArgs | Select-Object -Skip 1) -join " "
    $command = "dotnet tool restore >/dev/null && dotnet dotnet-ef $rest"
}
else {
    $command = "dotnet " + ($DotnetArgs -join " ")
}

docker run --rm -t `
    -v "${repoRoot}:/src" `
    -v docreader-nuget:/root/.nuget/packages `
    -w /src `
    -e DOTNET_CLI_TELEMETRY_OPTOUT=1 `
    -e DOTNET_NOLOGO=1 `
    $sdkImage `
    bash -lc $command

exit $LASTEXITCODE
