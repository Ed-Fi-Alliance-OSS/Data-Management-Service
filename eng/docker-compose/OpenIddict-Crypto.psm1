# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    PowerShell module for OpenIddict cryptographic operations including password hashing and key generation.

.DESCRIPTION
    This module provides functions for:
    - Generating ASP.NET Core Identity compatible password hashes (PBKDF2-SHA256)
    - Creating RSA key pairs for OpenIddict JWT signing
    - Generating SQL insert statements for OpenIddict database setup

.NOTES
    Compatible with ASP.NET Core Identity PasswordHasher<T> implementation.
#>

<#
.SYNOPSIS
    Generates an .NET password hash.

.DESCRIPTION

.PARAMETER Password
    The plain text password to hash.

.OUTPUTS
    System.String. Base64-encoded password hash.
#>
function New-AspNetPasswordHash {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Password,
        [int]$Iterations = 210000
    )
    $version = 1
    $saltLength = 16
    $subkeyLength = 32
    $salt = New-Object byte[] $saltLength
    [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($salt)

    $passwordBytes = [System.Text.Encoding]::UTF8.GetBytes($Password)
    $pbkdf2 = New-Object System.Security.Cryptography.Rfc2898DeriveBytes(
        $passwordBytes, $salt, $Iterations, [System.Security.Cryptography.HashAlgorithmName]::SHA256
    )
    $subkey = $pbkdf2.GetBytes($subkeyLength)

    # Build binary payload
    $memoryStream = New-Object System.IO.MemoryStream
    $writer = New-Object System.IO.BinaryWriter($memoryStream)

    $writer.Write([byte]$version)
    $writer.Write([int]$saltLength)
    $writer.Write($salt)
    $writer.Write($subkey)
    $writer.Flush()

    $finalBytes = $memoryStream.ToArray()
    $encoded = [Convert]::ToBase64String($finalBytes)
    return $encoded;
}


<#
.SYNOPSIS
    Generates a 2048-bit RSA key pair for OpenIddict JWT signing.

.DESCRIPTION
    Creates a new RSA key pair and returns both the public and private keys in Base64 format.
    The keys are suitable for use with OpenIddict JWT token signing and verification.

.PARAMETER KeySize
    Size of the RSA key in bits. Default is 2048.

.EXAMPLE
    $keyPair = New-OpenIddictKeyPair
    Write-Host "Public Key: $($keyPair.PublicKey)"
    Write-Host "Private Key: $($keyPair.PrivateKey)"

.OUTPUTS
    System.Management.Automation.PSCustomObject with PublicKey and PrivateKey properties.
#>
function New-OpenIddictKeyPair {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $false)]
        [int]$KeySize = 2048
    )

    try {
        $rsa = [System.Security.Cryptography.RSA]::Create($KeySize)
        $privateKey = $rsa.ExportPkcs8PrivateKey()
        $publicKey = $rsa.ExportSubjectPublicKeyInfo()

        $privateKeyBase64 = [Convert]::ToBase64String($privateKey)
        $publicKeyBase64 = [Convert]::ToBase64String($publicKey)

        return [PSCustomObject]@{
            PublicKey = $publicKeyBase64
            PrivateKey = $privateKeyBase64
        }
    }
    catch {
        Write-Error "Failed to generate RSA key pair: $($_.Exception.Message)"
        throw
    }
    finally {
        if ($rsa) { $rsa.Dispose() }
    }
}

<#
.SYNOPSIS
    Generates a SQL INSERT statement for OpenIddict keys.

.DESCRIPTION
    Creates a SQL statement to insert RSA key pairs into the dmscs.OpenIddictKey table.
    The private key is encrypted using PostgreSQL's pgcrypto extension.

.PARAMETER KeyId
    Unique identifier for the key. If not provided, a random key ID will be generated.

.PARAMETER EncryptionKey
    Key used to encrypt the private key in the database.

.PARAMETER KeySize
    Size of the RSA key in bits. Default is 2048.

.EXAMPLE
    $sql = New-OpenIddictKeyInsertSql -EncryptionKey "myEncryptionKey123"
    Write-Host $sql

.OUTPUTS
    System.String. SQL INSERT statement for the OpenIddict key.
#>
function New-OpenIddictKeyInsertSql {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $false)]
        [string]$KeyId = [guid]::NewGuid().ToString(),

        [Parameter(Mandatory = $true)]
        [string]$EncryptionKey,

        [Parameter(Mandatory = $false)]
        [int]$KeySize = 2048
    )

    try {
        $keyPair = New-OpenIddictKeyPair -KeySize $KeySize
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($KeyId)
        $encodedKey = [Convert]::ToBase64String($bytes)

        $sql = @"
INSERT INTO dmscs.OpenIddictKey (KeyId, PublicKey, PrivateKey, IsActive)
VALUES ('$encodedKey', decode('$($keyPair.PublicKey)', 'base64'), pgp_sym_encrypt('$($keyPair.PrivateKey)', '$EncryptionKey'), TRUE);
"@

        return $sql
    }
    catch {
        Write-Error "Failed to generate OpenIddict key insert SQL: $($_.Exception.Message)"
        throw
    }
}

<#
.SYNOPSIS
    Updates a client secret in the OpenIddict database with proper hashing.

.DESCRIPTION
    Generates a properly hashed client secret and creates a SQL UPDATE statement
    to update the ClientSecret field in the dmscs.OpenIddictApplication table.

.PARAMETER ClientId
    The ClientId of the application to update.

.PARAMETER PlainTextSecret
    The plain text secret to hash and store.

.EXAMPLE
    $sql = New-ClientSecretUpdateSql -ClientId "DmsConfigurationService" -PlainTextSecret "s3creT@09"
    Invoke-DbQuery $sql

.OUTPUTS
    System.String. SQL UPDATE statement for the client secret.
#>
function New-ClientSecretUpdateSql {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$ClientId,

        [Parameter(Mandatory = $true)]
        [string]$PlainTextSecret
    )

    try {
        $hashedSecret = New-AspNetPasswordHash -Password $PlainTextSecret

        $sql = @"
UPDATE dmscs.OpenIddictApplication
SET ClientSecret = '$hashedSecret'
WHERE ClientId = '$ClientId';
"@

        return $sql
    }
    catch {
        Write-Error "Failed to generate client secret update SQL: $($_.Exception.Message)"
        throw
    }
}

# Export module functions
Export-ModuleMember -Function New-AspNetPasswordHash, New-OpenIddictKeyPair, New-OpenIddictKeyInsertSql, New-ClientSecretUpdateSql
