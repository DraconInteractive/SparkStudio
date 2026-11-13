<#
.SYNOPSIS
    Decrypts notes encrypted by SPARK Studio, without the app.

.DESCRIPTION
    An escape hatch: given a project's vault file (.spark-vault.json, in the project's folder on the server) and the
    passphrase or recovery key, decrypts encrypted notes (*.md.enc, or any file starting with
    "-----BEGIN SPARK ENCRYPTED NOTE-----") to plain text. Needs PowerShell 7+ (for AES-GCM); no other software.

    Formats (see src/Spark.Notes/Crypto in the SPARK Studio repo):
      Vault:  data key wrapped with AES-256-GCM under PBKDF2-HMAC-SHA256(passphrase, salt, iterations) or
              HKDF-SHA256(recovery key bytes, salt = key ID, info = "spark-vault/recovery/1"); AAD "spark-vault/1/<key id>".
      Note:   armoured Base64 of nonce[12] | ciphertext | tag[16]; AAD "spark-note/1/<key id>".

.EXAMPLE
    ./Unprotect-SparkNote.ps1 -Vault .\.spark-vault.json -Path .\Plan.md.enc
    Asks for the passphrase and writes Plan.md next to the encrypted file.

.EXAMPLE
    ./Unprotect-SparkNote.ps1 -Vault .\.spark-vault.json -Path .\notes\*.enc -OutDir .\plain -UseRecoveryKey
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Vault,
    [Parameter(Mandatory)] [string[]] $Path,
    [string] $OutDir,
    [switch] $UseRecoveryKey,
    # For scripts; otherwise you're prompted (input hidden for the passphrase).
    [securestring] $Passphrase,
    [string] $RecoveryKey,
    [switch] $Force
)

$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'PowerShell 7 or later is required (AES-GCM).' }
Add-Type -AssemblyName System.Security.Cryptography

function Unprotect-Gcm([byte[]] $Key, [byte[]] $Nonce, [byte[]] $CipherAndTag, [string] $Aad) {
    $plain = [byte[]]::new($CipherAndTag.Length - 16)
    $gcm = [System.Security.Cryptography.AesGcm]::new($Key, 16)
    try {
        $gcm.Decrypt($Nonce, $CipherAndTag[0..($plain.Length - 1)], $CipherAndTag[$plain.Length..($CipherAndTag.Length - 1)],
            $plain, [Text.Encoding]::UTF8.GetBytes($Aad))
    } finally { $gcm.Dispose() }
    return , $plain
}

function ConvertFrom-RecoveryCode([string] $Code) {
    $alphabet = '0123456789ABCDEFGHJKMNPQRSTVWXYZ'
    $chars = ($Code.ToUpperInvariant() -replace '[\s-]', '' -replace 'O', '0' -replace '[IL]', '1').ToCharArray()
    if ($chars.Count -ne 32) { throw 'A recovery key has 32 characters (8 groups of 4).' }
    $bytes = [Collections.Generic.List[byte]]::new(); $buffer = 0; $bits = 0
    foreach ($c in $chars) {
        $v = $alphabet.IndexOf($c); if ($v -lt 0) { throw "Invalid recovery key character '$c'." }
        $buffer = (($buffer -shl 5) -bor $v) -band 0xFFFF; $bits += 5
        if ($bits -ge 8) { $bytes.Add([byte](($buffer -shr ($bits - 8)) -band 0xFF)); $bits -= 8 }
    }
    return , $bytes.ToArray()
}

$v = Get-Content -LiteralPath $Vault -Raw | ConvertFrom-Json
if ($v.format -ne 'spark-vault' -or $v.version -ne 1) { throw "$Vault isn't a SPARK vault (version 1)." }
$keyId = $v.key_id

if ($UseRecoveryKey -or $RecoveryKey) {
    $code = if ($RecoveryKey) { $RecoveryKey } else { Read-Host 'Recovery key' }
    $kek = [System.Security.Cryptography.HKDF]::DeriveKey([System.Security.Cryptography.HashAlgorithmName]::SHA256,
        (ConvertFrom-RecoveryCode $code), 32, [Text.Encoding]::UTF8.GetBytes($keyId), [Text.Encoding]::UTF8.GetBytes('spark-vault/recovery/1'))
    $wrapped = $v.recovery
} else {
    if ($v.kdf.name -ne 'pbkdf2-sha256') { throw "Unsupported key derivation '$($v.kdf.name)'." }
    $secure = if ($Passphrase) { $Passphrase } else { Read-Host 'Passphrase' -AsSecureString }
    $pass = [Net.NetworkCredential]::new('', $secure).Password.Normalize([Text.NormalizationForm]::FormC)
    $kek = [System.Security.Cryptography.Rfc2898DeriveBytes]::Pbkdf2([Text.Encoding]::UTF8.GetBytes($pass),
        [Convert]::FromBase64String($v.kdf.salt), [int]$v.kdf.iterations, [System.Security.Cryptography.HashAlgorithmName]::SHA256, 32)
    $wrapped = $v.passphrase
}
try {
    $key = Unprotect-Gcm $kek ([Convert]::FromBase64String($wrapped.nonce)) ([Convert]::FromBase64String($wrapped.ciphertext)) "spark-vault/1/$keyId"
} catch {
    throw 'That passphrase / recovery key does not unlock this vault.'
}

$count = 0
foreach ($file in Get-ChildItem -Path $Path -File) {
    $text = (Get-Content -LiteralPath $file.FullName -Raw) -replace "`r`n", "`n"
    $lines = $text.Trim([char]0xFEFF, ' ', "`t", "`r", "`n").Split("`n")
    if ($lines[0].Trim() -ne '-----BEGIN SPARK ENCRYPTED NOTE-----') { Write-Warning "$($file.Name): not an encrypted note, skipped"; continue }
    $headers = @{}; $i = 1
    for (; $i -lt $lines.Count -and $lines[$i].Trim() -ne ''; $i++) {
        $name, $value = $lines[$i] -split ':', 2
        $headers[$name.Trim()] = $value.Trim()
    }
    if ($headers['Version'] -ne '1') { Write-Warning "$($file.Name): unsupported version, skipped"; continue }
    if ($headers['Key'] -ne $keyId) { Write-Warning "$($file.Name): encrypted with another project's key, skipped"; continue }
    $blob = [Convert]::FromBase64String((($lines[($i + 1)..($lines.Count - 2)]) | ForEach-Object Trim) -join '')
    try {
        $plain = Unprotect-Gcm $key $blob[0..11] $blob[12..($blob.Length - 1)] "spark-note/1/$keyId"
    } catch {
        Write-Warning "$($file.Name): failed to decrypt (altered or damaged), skipped"; continue
    }
    $outName = $file.Name -replace '\.enc$', ''
    if ($outName -eq $file.Name) { $outName += '.txt' }
    $dir = if ($OutDir) { New-Item -ItemType Directory -Force -Path $OutDir | Select-Object -ExpandProperty FullName } else { $file.DirectoryName }
    $out = Join-Path $dir $outName
    if ((Test-Path -LiteralPath $out) -and -not $Force) { Write-Warning "$out exists; use -Force to overwrite"; continue }
    [IO.File]::WriteAllBytes($out, $plain)
    Write-Host "Decrypted $($file.Name) -> $out"
    $count++
}
Write-Host "$count note(s) decrypted."
