[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Database,
    [Parameter(Mandatory)] [string] $OutputDirectory,
    [string] $HostName = "localhost",
    [ValidateRange(1, 65535)] [int] $Port = 5432,
    [Parameter(Mandatory)] [string] $UserName
)

$ErrorActionPreference = "Stop"
if ([string]::IsNullOrWhiteSpace($env:PGPASSWORD)) {
    throw "Set PGPASSWORD in the current process before running backup; do not pass passwords on the command line."
}
if ($Database -in @("postgres", "template0", "template1")) {
    throw "Refusing to back up a PostgreSQL system database."
}

$resolvedOutput = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $resolvedOutput | Out-Null
$timestamp = [DateTime]::UtcNow.ToString("yyyyMMddTHHmmssZ")
$safeDatabase = $Database -replace '[^A-Za-z0-9_-]', '_'
$backupPath = Join-Path $resolvedOutput "$safeDatabase-$timestamp.dump"

& pg_dump --host $HostName --port $Port --username $UserName --dbname $Database `
    --format custom --compress 9 --no-owner --no-privileges --file $backupPath
if ($LASTEXITCODE -ne 0) { throw "pg_dump failed with exit code $LASTEXITCODE." }

& pg_restore --list $backupPath | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Backup validation failed for $backupPath." }

$hash = (Get-FileHash -LiteralPath $backupPath -Algorithm SHA256).Hash.ToLowerInvariant()
$hashPath = "$backupPath.sha256"
Set-Content -LiteralPath $hashPath -Value "$hash  $([System.IO.Path]::GetFileName($backupPath))" -Encoding ascii
Write-Host "Backup verified: $backupPath"
Write-Host "SHA-256: $hash"

