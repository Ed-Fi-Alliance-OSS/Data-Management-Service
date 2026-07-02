<#
.SYNOPSIS
    Generates a 2048-bit RSA key pair and outputs a SQL insert statement for "dmscs"."OpenIddictKey".
.DESCRIPTION
    This script delegates OpenIddict key SQL generation to the shared OpenIddict-Crypto module.
#>

param(
    [string]$KeyId = "key-$(Get-Random)",
    [Parameter(Mandatory = $true)]
    [string]$EncryptionKey
)

$modulePath = Join-Path $PSScriptRoot "OpenIddict-Crypto.psm1"
Import-Module $modulePath -Force

New-OpenIddictKeyInsertSql -KeyId $KeyId -EncryptionKey $EncryptionKey
