[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\backups'),
    [string]$ComposeFile = (Join-Path $PSScriptRoot '..\docker-compose.yml'),
    [string]$EnvFile = (Join-Path $PSScriptRoot '..\.env')
)

$ErrorActionPreference = 'Stop'
$ComposeFile = [IO.Path]::GetFullPath($ComposeFile)
$EnvFile = [IO.Path]::GetFullPath($EnvFile)
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

$compose = @('compose', '--env-file', $EnvFile, '-f', $ComposeFile)
$containerId = (& docker @compose ps -q postgres).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($containerId)) {
    throw 'The PostgreSQL container is not running.'
}

$timestamp = [DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmss')
$fileName = "mtplayer-$timestamp.dump"
$dumpPath = Join-Path $OutputDirectory $fileName
$temporaryPath = "$dumpPath.tmp"

$startInfo = [Diagnostics.ProcessStartInfo]::new()
$startInfo.FileName = 'docker'
$startInfo.UseShellExecute = $false
$startInfo.RedirectStandardOutput = $true
$startInfo.RedirectStandardError = $true
$startInfo.Arguments = "exec $containerId pg_dump -U mtplayer -d mtplayer -Fc --no-owner"

$process = [Diagnostics.Process]::new()
$process.StartInfo = $startInfo
try {
    if (-not $process.Start()) {
        throw 'Unable to start pg_dump.'
    }

    $errorTask = $process.StandardError.ReadToEndAsync()
    $file = [IO.File]::Create($temporaryPath)
    try {
        $process.StandardOutput.BaseStream.CopyTo($file)
    }
    finally {
        $file.Dispose()
    }

    $process.WaitForExit()
    $errorText = $errorTask.GetAwaiter().GetResult()
    if ($process.ExitCode -ne 0) {
        throw "pg_dump failed: $errorText"
    }

    Move-Item -LiteralPath $temporaryPath -Destination $dumpPath -Force
}
finally {
    $process.Dispose()
    if (Test-Path -LiteralPath $temporaryPath) {
        Remove-Item -LiteralPath $temporaryPath -Force
    }
}

$hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $dumpPath).Hash.ToLowerInvariant()
$checksumPath = "$dumpPath.sha256"
"$hash  $fileName" | Set-Content -LiteralPath $checksumPath -Encoding ascii
$manifest = [ordered]@{
    product = 'MTPlayer account and sync service'
    createdAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    database = 'mtplayer'
    format = 'PostgreSQL custom dump'
    dumpFile = $fileName
    sha256 = $hash
    encryptionKeyNotice = 'DATA_ENCRYPTION_KEY is not included. Back it up separately and use the matching key during restore.'
}
$manifest | ConvertTo-Json | Set-Content -LiteralPath "$dumpPath.manifest.json" -Encoding utf8
Write-Output $dumpPath
