<#
.SYNOPSIS
    Zips the self-contained SparkStudio.exe and uploads it to a SPARK instance as a versioned release
    (SparkStudio_v0.0.1.zip).

.DESCRIPTION
    Reads settings from .env next to this script (environment variables take precedence):

        SPARK_URL          Server to deploy to, e.g. https://spark.dracon.au
        SPARK_TOKEN        Device token (needs files.read and files.write)
        SPARK_DEVICE_ID    Optional: device ID the token was issued for; enables a credential check first
        SPARK_DEPLOY_DIR   Optional: destination directory (default /releases/SparkStudio)
        SPARK_DEPLOY_DRIVE Optional: drive letter (default: the server's choice for that directory)
        SPARK_DEPLOY_NAME  Optional: file name on the server; {version} is replaced with the release
                           version (default SparkStudio_v{version}.zip)

    The version is <Version> in src/SparkStudio/SparkStudio.csproj, which is also stamped into the exe and
    shown in the app's title bar. The script reads it from the built exe and refuses to deploy a build
    whose version doesn't match the csproj (rebuild with -Build). Use -Bump to raise the version and build.

    Each version is uploaded once: deploying a version that is already on the server fails unless -Force
    is given (which uploads it again, checks it, then removes the earlier copy). Older versions are kept as
    a release history unless -Prune is given.

.PARAMETER Build
    Run the self-contained publish (dotnet publish -p:PublishProfile=win-x64) before uploading.

.PARAMETER Bump
    Raise the version in SparkStudio.csproj (patch: 0.0.1 -> 0.0.2, minor: 0.1.0, major: 1.0.0), then build.

.PARAMETER Force
    Redeploy a version that is already on the server, replacing it.

.PARAMETER Prune
    After uploading, delete the other SparkStudio builds in the destination directory (older versions and
    unversioned SparkStudio.zip uploads).

.PARAMETER DryRun
    Check the credentials, artifact and destination, but don't upload or delete anything.

.PARAMETER AllowHttp
    Allow a plain http:// SPARK_URL (local test servers only: the token is sent unencrypted).

.EXAMPLE
    ./deploy.ps1 -Bump patch
.EXAMPLE
    ./deploy.ps1 -Build
#>
[CmdletBinding()]
param(
    [switch]$Build,
    [ValidateSet('patch', 'minor', 'major')]
    [string]$Bump,
    [switch]$Force,
    [switch]$Prune,
    [switch]$DryRun,
    [switch]$AllowHttp
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue' # Windows PowerShell's progress bar slows large uploads dramatically
$root = $PSScriptRoot

# Windows PowerShell 5.1 may not offer TLS 1.2 by default; https servers need it.
if ($PSVersionTable.PSVersion.Major -lt 6) {
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
}

function Read-DotEnv([string]$path) {
    $values = @{}
    if (-not (Test-Path $path)) { return $values }
    foreach ($line in Get-Content $path) {
        $t = $line.Trim()
        if ($t -eq '' -or $t.StartsWith('#')) { continue }
        $eq = $t.IndexOf('=')
        if ($eq -lt 1) { continue }
        $key = $t.Substring(0, $eq).Trim()
        if ($key.StartsWith('export ')) { $key = $key.Substring(7).Trim() }
        $value = $t.Substring($eq + 1).Trim()
        if ($value.Length -ge 2 -and (($value[0] -eq '"' -and $value[-1] -eq '"') -or ($value[0] -eq "'" -and $value[-1] -eq "'"))) {
            $value = $value.Substring(1, $value.Length - 2)
        }
        $values[$key] = $value
    }
    return $values
}

$dotenv = Read-DotEnv (Join-Path $root '.env')
function Setting([string]$name, [string]$default = '') {
    $fromEnv = [Environment]::GetEnvironmentVariable($name)
    if ($fromEnv) { return $fromEnv }
    if ($dotenv.ContainsKey($name) -and $dotenv[$name]) { return $dotenv[$name] }
    return $default
}

function Fail([string]$message) {
    Write-Host "ERROR: $message" -ForegroundColor Red
    exit 1
}

# ---------- Settings ----------
$baseUrl  = (Setting 'SPARK_URL').TrimEnd('/')
$token    = Setting 'SPARK_TOKEN'
$deviceId = Setting 'SPARK_DEVICE_ID'
$dir      = Setting 'SPARK_DEPLOY_DIR' '/releases/SparkStudio'
$drive    = Setting 'SPARK_DEPLOY_DRIVE'
$nameTemplate = Setting 'SPARK_DEPLOY_NAME' 'SparkStudio_v{version}.zip'

if (-not $baseUrl) { Fail "SPARK_URL is not set. Copy .env.example to .env and fill it in." }
if (-not $token)   { Fail "SPARK_TOKEN is not set. Add the device token to .env." }
if (-not $baseUrl.StartsWith('https://')) {
    if (-not $AllowHttp) { Fail "SPARK_URL must use https:// so the token isn't sent unencrypted ($baseUrl). Use -AllowHttp only for local test servers." }
    Write-Host "WARNING: plain http; the token is sent unencrypted." -ForegroundColor Yellow
}
if (-not $dir.StartsWith('/')) { Fail "SPARK_DEPLOY_DIR must be an absolute path such as /releases/SparkStudio." }

$headers = @{ Authorization = "Bearer $token" }

function Invoke-Spark([string]$method, [string]$path, $body = $null) {
    $request = @{ Method = $method; Uri = "$baseUrl/$($path.TrimStart('/'))"; Headers = $headers }
    if ($null -ne $body) {
        $request.ContentType = 'application/json'
        $request.Body = ($body | ConvertTo-Json -Compress)
    }
    try {
        return Invoke-RestMethod @request
    }
    catch {
        $status = $_.Exception.Response.StatusCode.value__
        $detail = $_.ErrorDetails.Message
        $code = try { ($detail | ConvertFrom-Json).error } catch { $detail }
        switch ($status) {
            401 { Fail "The server rejected the token (401 $code). Check SPARK_TOKEN, or ask for a new device token." }
            403 { Fail "Not allowed: $method $path (403 $code). The token needs files.read and files.write." }
            default { Fail "$method $path failed: $status $code $($_.Exception.Message)" }
        }
    }
}

function Query([hashtable]$q) {
    ($q.GetEnumerator() | Where-Object { $_.Value -ne $null -and $_.Value -ne '' } |
        ForEach-Object { "$($_.Key)=$([Uri]::EscapeDataString([string]$_.Value))" }) -join '&'
}

# ---------- Version ----------
$csproj = Join-Path $root 'src/SparkStudio/SparkStudio.csproj'
$csprojText = [IO.File]::ReadAllText($csproj)
$versionMatch = [regex]::Match($csprojText, '<Version>\s*(\d+)\.(\d+)\.(\d+)\s*</Version>')
if (-not $versionMatch.Success) { Fail "No <Version>x.y.z</Version> found in $csproj." }
$major = [int]$versionMatch.Groups[1].Value; $minor = [int]$versionMatch.Groups[2].Value; $patch = [int]$versionMatch.Groups[3].Value
$version = "$major.$minor.$patch"
if ($Bump) {
    switch ($Bump) {
        'major' { $major++; $minor = 0; $patch = 0 }
        'minor' { $minor++; $patch = 0 }
        'patch' { $patch++ }
    }
    $newVersion = "$major.$minor.$patch"
    if ($DryRun) {
        Write-Host "Version:   $version -> $newVersion (dry run: csproj not changed, nothing built)" -ForegroundColor Yellow
        $version = $newVersion
    }
    else {
        $csprojText = $csprojText.Substring(0, $versionMatch.Index) + "<Version>$newVersion</Version>" +
            $csprojText.Substring($versionMatch.Index + $versionMatch.Length)
        [IO.File]::WriteAllText($csproj, $csprojText, (New-Object Text.UTF8Encoding($false)))
        Write-Host "Version:   $version -> $newVersion (SparkStudio.csproj updated)" -ForegroundColor Cyan
        $version = $newVersion
        $Build = $true
    }
}
else {
    Write-Host "Version:   $version"
}

# ---------- Artifact ----------
$exe = Join-Path $root 'artifacts/publish/SparkStudio.exe'
if ($Build -and -not $DryRun) {
    Write-Host "Building self-contained release..." -ForegroundColor Cyan
    Remove-Item (Join-Path $root 'artifacts/publish') -Recurse -Force -ErrorAction SilentlyContinue
    dotnet publish (Join-Path $root 'src/SparkStudio') -p:PublishProfile=win-x64
    if ($LASTEXITCODE -ne 0) { Fail "dotnet publish failed." }
}
if (-not (Test-Path $exe)) { Fail "No build found at $exe. Run with -Build, or publish first." }

$exeInfo = Get-Item $exe
$exeVersion = $exeInfo.VersionInfo
$builtVersion = "$($exeVersion.FileMajorPart).$($exeVersion.FileMinorPart).$($exeVersion.FileBuildPart)"
if ($builtVersion -ne $version) {
    if ($DryRun) { Write-Host "WARNING: the build is v$builtVersion, not v$version; a real run needs -Build or -Bump." -ForegroundColor Yellow }
    else { Fail "The build is v$builtVersion but SparkStudio.csproj says v$version. Run with -Build to publish v$version." }
}
$name = $nameTemplate.Replace('{version}', $version)
$sizeMb = [math]::Round($exeInfo.Length / 1MB, 1)
$commit = (git -C $root log -1 --format='%h %s' 2>$null)
$dirty = (git -C $root status --porcelain 2>$null) -ne $null
Write-Host "Artifact:  $exe ($sizeMb MB, built $($exeInfo.LastWriteTime))"
if ($commit) { Write-Host "Commit:    $commit$(if ($dirty) { ' (+ uncommitted changes)' })" }
$newestSource = Get-ChildItem (Join-Path $root 'src') -Recurse -File -Include *.cs, *.xaml, *.csproj, *.pubxml |
    Where-Object { $_.FullName -notmatch '[\/](bin|obj)[\/]' } |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($newestSource -and $newestSource.LastWriteTime -gt $exeInfo.LastWriteTime) {
    Write-Host "WARNING: $($newestSource.Name) changed after this build. Use -Build to publish the current code." -ForegroundColor Yellow
}

# ---------- Package ----------
$zip = Join-Path $root "artifacts/$name"
Get-ChildItem (Join-Path $root 'artifacts') -Filter 'SparkStudio*.zip' -File -ErrorAction SilentlyContinue | Remove-Item -Force
Compress-Archive -Path $exe -DestinationPath $zip -CompressionLevel Optimal
$zipInfo = Get-Item $zip
$zipMb = [math]::Round($zipInfo.Length / 1MB, 1)
Write-Host "Package:   $zip ($zipMb MB)"

# ---------- Credentials ----------
Write-Host "Server:    $baseUrl"
if ($deviceId) {
    $auth = Invoke-Spark POST 'auth/verify' @{ device_id = $deviceId }
    if ($auth.authenticated) {
        Write-Host "Device:    $($auth.device_id) ($($auth.permissions -join ', '))"
        $missing = @('files.read', 'files.write') | Where-Object { $auth.permissions -notcontains $_ }
        if ($missing) { Fail "The token is missing $($missing -join ' and '), which deployment needs." }
    }
    else {
        Write-Host "Device:    (server is in open mode; no authentication)"
    }
}

# ---------- Existing uploads ----------
$target = if ($drive) { "${drive}:$dir/$name" } else { "$dir/$name" }
Write-Host "Target:    $target"
$inDir = @()
$offset = 0
do {
    $page = Invoke-Spark GET ("files?" + (Query @{ drive = $drive; directory = $dir; limit = 500; offset = $offset }))
    $inDir += @($page.files | Where-Object { $_.directory -eq $dir })
    $offset += 500
} while ($page.has_more)

# Builds of any version (plus the old unversioned name), for the release listing and -Prune.
$buildPattern = '^' + [regex]::Escape($nameTemplate).Replace('\{version}', '\d+(\.\d+)*') + '$'
$legacyName = $nameTemplate -replace '[_-]?v?\{version\}', ''
$builds = @($inDir | Where-Object { $_.name -match $buildPattern -or $_.name -eq $legacyName })
$existing = @($builds | Where-Object { $_.name -eq $name })
$others = @($builds | Where-Object { $_.name -ne $name })
if ($others.Count -gt 0) {
    Write-Host "Releases:  $(($others | ForEach-Object { $_.name } | Sort-Object -Unique) -join ', ')"
}
if ($existing.Count -gt 0) {
    $ids = ($existing | ForEach-Object { $_.id }) -join ', '
    if (-not $Force) {
        Fail "v$version is already deployed ($name, $ids). Bump the version (-Bump patch) or use -Force to replace it."
    }
    Write-Host "Replacing: $($existing.Count) existing upload(s) of $name ($ids)" -ForegroundColor Yellow
}

if ($DryRun) {
    Write-Host "Dry run: nothing uploaded or deleted." -ForegroundColor Yellow
    exit 0
}

# ---------- Upload ----------
Write-Host "Uploading $zipMb MB..." -ForegroundColor Cyan
$uploadUri = "$baseUrl/files?" + (Query @{ drive = $drive; directory = $dir; name = $name })
try {
    $uploaded = Invoke-RestMethod -Method Post -Uri $uploadUri -Headers $headers `
        -ContentType 'application/octet-stream' -InFile $zip -TimeoutSec 1800
}
catch {
    $status = $_.Exception.Response.StatusCode.value__
    Fail "Upload failed ($status $($_.ErrorDetails.Message)). The previous build is unchanged."
}

if ([long]$uploaded.size_bytes -ne $zipInfo.Length) {
    Fail "Uploaded size $($uploaded.size_bytes) doesn't match the local zip ($($zipInfo.Length)). Previous build kept; new upload id $($uploaded.id)."
}
Write-Host "Uploaded:  $($uploaded.id) on drive $($uploaded.drive_letter)" -ForegroundColor Green

# ---------- Remove replaced / pruned builds ----------
$remove = @($existing)
if ($Prune) { $remove += $others }
foreach ($old in $remove) {
    if ($old.id -eq $uploaded.id) { continue }
    Invoke-Spark DELETE "files/$($old.id)" | Out-Null
    Write-Host "Removed:   $($old.name) $($old.id) (uploaded $($old.created_at))"
}

Write-Host ""
Write-Host "Deployed $name to $baseUrl$($uploaded.download_url)" -ForegroundColor Green
