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

$rsa = [System.Security.Cryptography.RSA]::Create(2048)
$privateKey = $rsa.ExportPkcs8PrivateKey()
$publicKey = $rsa.ExportSubjectPublicKeyInfo()

$privateKeyBase64 = [Convert]::ToBase64String($privateKey)
$publicKeyBase64 = [Convert]::ToBase64String($publicKey)

$sql = @"
INSERT INTO "dmscs"."OpenIddictKey" ("KeyId", "PublicKey", "PrivateKey", "IsActive")
VALUES ('$KeyId', decode('$publicKeyBase64', 'base64'), pgp_sym_encrypt('$privateKeyBase64', '$EncryptionKey'), TRUE);
"@

Write-Output $sql
