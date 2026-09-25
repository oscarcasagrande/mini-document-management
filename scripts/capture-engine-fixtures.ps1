<#
.SYNOPSIS
Captura o OCR das amostras da Etapa 3 com cada perfil de modelo do ocr-service (ADR 0002), para comparar
a exatidão da extração por engine.

.DESCRIPTION
Sobe um contêiner descartável do ocr-service por perfil, com o código atual de services/ocr-service/app
montado sobre a imagem e os modelos no volume ocr-bench-models (o perfil v5 já vem na imagem; os v6 são
baixados na primeira vez). Depois roda scripts/capture-ocr-fixtures.py contra ele.

Cada perfil grava em $OutRoot/<perfil>/. Para medir a extração sobre uma delas:

  docker run --rm -v "${PWD}:/src" -v docreader-nuget:/root/.nuget/packages -w /src `
    -e DOCREADER_OCR_FIXTURES=/src/<pasta> mcr.microsoft.com/dotnet/sdk:10.0 `
    dotnet test tests/unit/DocReader.UnitTests --filter-method "*Campos_extraidos_da_amostra*"

.EXAMPLE
pwsh scripts/capture-engine-fixtures.ps1 -Profiles ppocrv6-small,ppocrv6-tiny -OutRoot .tmp/engines
#>
param(
    [string[]]$Profiles = @("ppocrv5-mobile", "ppocrv6-small", "ppocrv6-tiny", "ppocrv6-medium"),
    [string]$OutRoot = ".tmp/engines",
    [string]$Network = "docreader_internal",
    [string]$Image = "docreader/ocr-service:local"
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot

# Com "powershell -File" a lista chega como uma string só, "a,b".
$Profiles = @($Profiles | ForEach-Object { $_ -split "," } | Where-Object { $_ })

foreach ($profile in $Profiles) {
    $name = "ocr-exp-$profile"
    $out = Join-Path $OutRoot $profile
    New-Item -ItemType Directory -Force (Join-Path $repo $out) | Out-Null

    Write-Host "##### $profile"
    try { docker rm -f $name 2>&1 | Out-Null } catch { }
    docker run -d --name $name --network $Network --user root --cpus=4 --memory=3g `
        -e "OCR_MODEL_PROFILE=$profile" -e "OCR_MODEL_CACHE_DIR=/models" -e "PADDLE_PDX_CACHE_HOME=/models" `
        -v "${repo}/services/ocr-service/app:/app/app:ro" -v ocr-bench-models:/models `
        $Image | Out-Null

    do {
        Start-Sleep 3
        $health = docker inspect $name --format '{{.State.Health.Status}}'
    } until ($health -eq "healthy")

    docker run --rm --network $Network -v "${repo}:/w" -w /w python:3.12-slim `
        python scripts/capture-ocr-fixtures.py --base-url "http://${name}:8000" --out "/w/$($out -replace '\\','/')"

    try { docker rm -f $name 2>&1 | Out-Null } catch { }
}
