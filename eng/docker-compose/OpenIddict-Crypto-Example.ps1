# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    Example script demonstrating usage of the OpenIddict-Crypto module.

.DESCRIPTION
    This script shows how to use the OpenIddict-Crypto module functions for:
    - Generating ASP.NET Core Identity compatible password hashes
    - Creating RSA key pairs for JWT signing
    - Generating SQL statements for database operations
#>

# Import the module
Import-Module ./OpenIddict-Crypto.psm1

Write-Host "=== OpenIddict-Crypto Module Example Usage ===" -ForegroundColor Green

# Example 1: Generate a password hash
Write-Host "`n1. Generating ASP.NET Core Identity compatible password hash:" -ForegroundColor Yellow
$plainSecret = "s3creT@09"
$hashedSecret = New-AspNetPasswordHash -Password $plainSecret
Write-Host "Plain text: $plainSecret"
Write-Host "Hashed: $hashedSecret"

# Example 2: Generate RSA key pair
Write-Host "`n2. Generating RSA key pair for JWT signing:" -ForegroundColor Yellow
$keyPair = New-OpenIddictKeyPair
Write-Host "Public Key (first 50 chars): $($keyPair.PublicKey.Substring(0, 50))..."
Write-Host "Private Key (first 50 chars): $($keyPair.PrivateKey.Substring(0, 50))..."

# Example 3: Generate OpenIddict key insert SQL
Write-Host "`n3. Generating OpenIddict key insert SQL:" -ForegroundColor Yellow
$encryptionKey = "sample-encryption-key-123"
$keyInsertSql = New-OpenIddictKeyInsertSql -EncryptionKey $encryptionKey
Write-Host "SQL Statement:"
Write-Host $keyInsertSql -ForegroundColor Cyan

# Example 4: Generate client secret update SQL
Write-Host "`n4. Generating client secret update SQL:" -ForegroundColor Yellow
$clientSecretUpdateSql = New-ClientSecretUpdateSql -ClientId "DmsConfigurationService" -PlainTextSecret $plainSecret
Write-Host "SQL Statement:"
Write-Host $clientSecretUpdateSql -ForegroundColor Cyan

Write-Host "`n=== Example Usage Complete ===" -ForegroundColor Green
