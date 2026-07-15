[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$DumpFile,
    [switch]$Force,
    [string]$ComposeFile = (Join-Path $PSScriptRoot '..\docker-compose.yml'),
    [string]$EnvFile = (Join-Path $PSScriptRoot '..\.env')
)

$ErrorActionPreference = 'Stop'
$DumpFile = [IO.Path]::GetFullPath($DumpFile)
$ComposeFile = [IO.Path]::GetFullPath($ComposeFile)
$EnvFile = [IO.Path]::GetFullPath($EnvFile)
if (-not (Test-Path -LiteralPath $DumpFile -PathType Leaf)) {
    throw "Backup file does not exist: $DumpFile"
}

if (-not $Force) {
    throw 'Restore overwrites the current database. Pass -Force after confirming the current backup.'
}

$checksumPath = "$DumpFile.sha256"
if (-not (Test-Path -LiteralPath $checksumPath -PathType Leaf)) {
    throw "Checksum file does not exist: $checksumPath"
}

$expected = ((Get-Content -LiteralPath $checksumPath -Raw).Trim() -split '\s+')[0].ToLowerInvariant()
$actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $DumpFile).Hash.ToLowerInvariant()
if ($expected -ne $actual) {
    throw 'Backup SHA256 verification failed.'
}

$compose = @('compose', '--env-file', $EnvFile, '-f', $ComposeFile)
$containerId = (& docker @compose ps -q postgres).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($containerId)) {
    throw 'The PostgreSQL container is not running.'
}

& docker @compose stop mt-api | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw 'Unable to stop mt-api.'
}

try {
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = 'docker'
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardInput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.Arguments = "exec -i $containerId pg_restore -U mtplayer -d mtplayer --clean --if-exists --no-owner --exit-on-error --single-transaction"

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) {
            throw 'Unable to start pg_restore.'
        }

        $errorTask = $process.StandardError.ReadToEndAsync()
        $input = [IO.File]::OpenRead($DumpFile)
        try {
            $input.CopyTo($process.StandardInput.BaseStream)
        }
        finally {
            $input.Dispose()
            $process.StandardInput.Close()
        }

        $process.WaitForExit()
        $errorText = $errorTask.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) {
            throw "pg_restore failed: $errorText"
        }
    }
    finally {
        $process.Dispose()
    }
}
finally {
    & docker @compose up -d mt-api | Out-Null
}

Write-Output 'Database restore completed. Verify DATA_ENCRYPTION_KEY and /health/ready.'
