param(
    [string]$Server = "(localdb)\MSSQLLocalDB",
    [switch]$SkipTests
)
$ErrorActionPreference = "Stop"
Set-Location (Split-Path $PSScriptRoot -Parent)

function Step($text) { Write-Host "`n==> $text" -ForegroundColor Cyan }
function Fail($text) { Write-Host "ERROR: $text" -ForegroundColor Red; exit 1 }

Step ".NET SDK"
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Fail "Falta el SDK de .NET 8. Instalalo con: winget install Microsoft.DotNet.SDK.8"
}
$sdk = (dotnet --version)
if ([version]($sdk -replace '-.*$', '') -lt [version]"8.0.0") { Fail "Se requiere .NET SDK 8 o superior (tienes $sdk)." }
Write-Host "dotnet $sdk"

Step "sqlcmd y SQL Server ($Server)"
if (-not (Get-Command sqlcmd -ErrorAction SilentlyContinue)) {
    Fail "Falta sqlcmd. Instalalo con: winget install Microsoft.SQLServer.2022.Express  (o 'winget install Microsoft.Sqlcmd')"
}
if ($Server -like "(localdb)*") { sqllocaldb start ($Server -replace '^\(localdb\)\\', '') | Out-Null }
sqlcmd -S $Server -E -C -b -Q "SELECT 1" | Out-Null
if ($LASTEXITCODE -ne 0) { Fail "No se pudo conectar a '$Server'. Verifica que el servicio este iniciado." }

Step "Base de datos (esquema + semilla)"
sqlcmd -S $Server -E -C -b -i db/01_schema.sql | Out-Null;           if ($LASTEXITCODE -ne 0) { Fail "Fallo db/01_schema.sql" }
sqlcmd -S $Server -E -C -b -i db/02_seed_and_reset.sql | Out-Null;   if ($LASTEXITCODE -ne 0) { Fail "Fallo db/02_seed_and_reset.sql" }
Write-Host "Base MiniCore lista con las 6 cuentas semilla."

Step "Restaurar paquetes NuGet y compilar"
dotnet build; if ($LASTEXITCODE -ne 0) { Fail "Fallo la compilacion." }

if (-not $SkipTests) {
    Step "Pruebas"
    if ($Server -notlike "(localdb)*") {
        $env:MINICORE_TEST_CONNECTION = "Server=$Server;Database=MiniCore;Trusted_Connection=True;TrustServerCertificate=True"
        $env:ConnectionStrings__MiniCore = $env:MINICORE_TEST_CONNECTION
    }
    dotnet test --no-build; if ($LASTEXITCODE -ne 0) { Fail "Hay pruebas fallando." }
}

Write-Host "`nListo. Ejecuta la API con: dotnet run --project src/MiniCore.Api" -ForegroundColor Green
