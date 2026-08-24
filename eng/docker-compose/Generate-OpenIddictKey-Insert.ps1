<#
.SYNOPSIS
    Generates a 2048-bit RSA key pair and outputs a SQL insert statement for "dmscs"."OpenIddictKey".
.DESCRIPTION
    This script creates a new RSA key pair, encodes the keys in base64, and prints a SQL statement
    to insert them into the "dmscs"."OpenIddictKey" table.
#>

param(
    [string]$KeyId = "key-$(Get-Random)",
    [string]$EncryptionKey = ""
)

# The key id and the encryption key are caller-supplied values, so the SQL literals below are
# built by the shared quoting helper instead of bare quotes in the template. A key containing a
# single quote would otherwise close its literal and emit invalid SQL.
Import-Module (Join-Path $PSScriptRoot "OpenIddict-Crypto.psm1")

$rsa = [System.Security.Cryptography.RSA]::Create(2048)
$privateKey = $rsa.ExportPkcs8PrivateKey()
$publicKey = $rsa.ExportSubjectPublicKeyInfo()

$privateKeyBase64 = [Convert]::ToBase64String($privateKey)
$publicKeyBase64 = [Convert]::ToBase64String($publicKey)

$keyIdLiteral = ConvertTo-PostgresSqlLiteral -Value $KeyId
$publicKeyLiteral = ConvertTo-PostgresSqlLiteral -Value $publicKeyBase64
$privateKeyLiteral = ConvertTo-PostgresSqlLiteral -Value $privateKeyBase64
$encryptionKeyLiteral = ConvertTo-PostgresSqlLiteral -Value $EncryptionKey

$sql = @"
INSERT INTO "dmscs"."OpenIddictKey" ("KeyId", "PublicKey", "PrivateKey", "IsActive")
VALUES ($keyIdLiteral, decode($publicKeyLiteral, 'base64'), pgp_sym_encrypt($privateKeyLiteral, $encryptionKeyLiteral), TRUE);
"@

Write-Output $sql
