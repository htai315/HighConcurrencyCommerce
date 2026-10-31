[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $BackupPath,
    [Parameter(Mandatory)] [string] $Database,
    [Parameter(Mandatory)] [string] $ExpectedDatabase,
    [string] $HostName = "localhost",
    [ValidateRange(1, 65535)] [int] $Port = 5432,
    [Parameter(Mandatory)] [string] $UserName
)

$ErrorActionPreference = "Stop"
if ([string]::IsNullOrWhiteSpace($env:PGPASSWORD)) {
    throw "Set PGPASSWORD in the current process before running restore; do not pass passwords on the command line."
}
if ($Database -cne $ExpectedDatabase -or $Database -in @("postgres", "template0", "template1")) {
    throw "Target confirmation failed. Database must exactly equal ExpectedDatabase and must not be a system database."
}

$resolvedBackup = (Resolve-Path -LiteralPath $BackupPath).Path
$hashPath = "$resolvedBackup.sha256"
if (Test-Path -LiteralPath $hashPath) {
    $expectedHash = ((Get-Content -LiteralPath $hashPath -Raw).Trim() -split '\s+')[0]
    $actualHash = (Get-FileHash -LiteralPath $resolvedBackup -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -cne $expectedHash.ToLowerInvariant()) { throw "Backup SHA-256 verification failed." }
}

& pg_restore --list $resolvedBackup | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Backup archive is unreadable." }

$storeSchema = & psql --host $HostName --port $Port --username $UserName --dbname $Database `
    --tuples-only --no-align --command "SELECT 1 FROM pg_namespace WHERE nspname='store';"
if ($LASTEXITCODE -ne 0) { throw "Could not inspect target database." }
if (($storeSchema | Out-String).Trim() -eq "1") {
    throw "Refusing to restore into a database that already contains schema store. Restore into a new empty database."
}

& pg_restore --host $HostName --port $Port --username $UserName --dbname $Database `
    --exit-on-error --single-transaction --no-owner --no-privileges $resolvedBackup
if ($LASTEXITCODE -ne 0) { throw "pg_restore failed with exit code $LASTEXITCODE." }
Write-Host "Restore completed into confirmed empty database: $Database"

