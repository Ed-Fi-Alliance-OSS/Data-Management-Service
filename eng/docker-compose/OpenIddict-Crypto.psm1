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

.PARAMETER PlainTextSecret
    The plain text secret to hash.

.OUTPUTS
    System.String. Base64-encoded password hash.
#>
function New-AspNetPasswordHash {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Generates a hash in memory; callers decide whether to persist it.')]
    [OutputType([string])]
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$PlainTextSecret,
        [int]$Iterations = 210000
    )
    $version = 1
    $saltLength = 16
    $subkeyLength = 32
    $salt = New-Object byte[] $saltLength
    [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($salt)

    $passwordBytes = [System.Text.Encoding]::UTF8.GetBytes($PlainTextSecret)
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
    Quotes a value as a PostgreSQL string literal.

.DESCRIPTION
    Returns the value wrapped in single quotes with every embedded single quote doubled, which is
    the escape PostgreSQL defines for string literals. Backslashes need no handling: with
    standard_conforming_strings on -- the default since PostgreSQL 9.1 -- a backslash inside a plain
    literal is already an ordinary character.

    The surrounding quotes are part of the returned text, so callers embed the result as-is and
    there is no second place that has to remember to add them.

.PARAMETER Value
    The value to quote.

.OUTPUTS
    System.String. The quoted literal, including its surrounding single quotes.
#>
function ConvertTo-PostgresSqlLiteral {
    [OutputType([string])]
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$Value
    )

    return "'" + $Value.Replace("'", "''") + "'"
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
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Generates a key pair in memory; callers decide whether to persist it.')]
    [OutputType([pscustomobject])]
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
    Creates a SQL statement to insert RSA key pairs into the "dmscs"."OpenIddictKey" table.
    The private key is encrypted using PostgreSQL's pgcrypto extension.

.PARAMETER KeyId
    Unique identifier for the key. If not provided, a random key ID will be generated.
    The value is UTF-8 base64 encoded before being emitted, preserving existing seeded JWT kid behavior.

.PARAMETER EncryptionKey
    Key used to encrypt the private key in the database. Defaults to an empty string for the standalone generator.

.PARAMETER KeySize
    Size of the RSA key in bits. Default is 2048.

.PARAMETER DbType
    Target database engine: "Postgresql" (default) or "MSSQL". Selects the encryption
    function and literal typing used in the generated INSERT statement.

.EXAMPLE
    $sql = New-OpenIddictKeyInsertSql -EncryptionKey "myEncryptionKey123"
    Write-Host $sql

.OUTPUTS
    System.String. SQL INSERT statement for the OpenIddict key.
#>
function New-OpenIddictKeyInsertSql {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Generates SQL text in memory; callers decide whether to execute it.')]
    [OutputType([string])]
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $false)]
        [string]$KeyId = [guid]::NewGuid().ToString(),

        [Parameter(Mandatory = $false)]
        [string]$EncryptionKey = "",

        [Parameter(Mandatory = $false)]
        [int]$KeySize = 2048,

        [Parameter(Mandatory = $false)]
        [string]$DbType = "Postgresql"
    )

    if ($DbType -eq "MSSQL") {
        throw "New-OpenIddictKeyInsertSql does not support MSSQL because it would embed secrets in SQL text. Use New-OpenIddictKeyInsertCommand instead."
    }

    try {
        $keyPair = New-OpenIddictKeyPair -KeySize $KeySize
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($KeyId)
        $encodedKey = [Convert]::ToBase64String($bytes)

        # Every value below is quoted by ConvertTo-PostgresSqlLiteral rather than pasted between
        # bare quotes: the encryption key is a configured secret that may legitimately contain a
        # single quote, which would otherwise close the literal and produce invalid SQL.
        $keyIdLiteral = ConvertTo-PostgresSqlLiteral -Value $encodedKey
        $publicKeyLiteral = ConvertTo-PostgresSqlLiteral -Value $keyPair.PublicKey
        $privateKeyLiteral = ConvertTo-PostgresSqlLiteral -Value $keyPair.PrivateKey
        $encryptionKeyLiteral = ConvertTo-PostgresSqlLiteral -Value $EncryptionKey

        $sql = @"
INSERT INTO "dmscs"."OpenIddictKey" ("KeyId", "PublicKey", "PrivateKey", "IsActive")
VALUES ($keyIdLiteral, decode($publicKeyLiteral, 'base64'), pgp_sym_encrypt($privateKeyLiteral, $encryptionKeyLiteral), TRUE);
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
    Generates a SQL INSERT command for OpenIddict keys.

.DESCRIPTION
    Creates SQL text and parameter values for database-specific OpenIddict key insertion.
    SQL Server uses parameters so the encryption key is not embedded in generated SQL.

.PARAMETER KeyId
    Unique identifier for the key. If not provided, a random key ID will be generated.

.PARAMETER EncryptionKey
    Key used to encrypt the private key in the database.

.PARAMETER KeySize
    Size of the RSA key in bits. Default is 2048.

.PARAMETER DbType
    Target database engine: "Postgresql" (default) or "MSSQL".

.OUTPUTS
    System.Management.Automation.PSCustomObject with Sql and Parameters properties.
#>
function New-OpenIddictKeyInsertCommand {
    [CmdletBinding()]
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Pure generator: builds and returns SQL text and parameter values without executing them; no system state changes and no -WhatIf surface.')]
    param(
        [Parameter(Mandatory = $false)]
        [string]$KeyId = [guid]::NewGuid().ToString(),

        [Parameter(Mandatory = $true)]
        [string]$EncryptionKey,

        [Parameter(Mandatory = $false)]
        [int]$KeySize = 2048,

        [Parameter(Mandatory = $false)]
        [string]$DbType = "Postgresql"
    )

    try {
        if ($DbType -ne "MSSQL") {
            return [PSCustomObject]@{
                Sql = New-OpenIddictKeyInsertSql -KeyId $KeyId -EncryptionKey $EncryptionKey -KeySize $KeySize -DbType $DbType
                Parameters = @{}
            }
        }

        $keyPair = New-OpenIddictKeyPair -KeySize $KeySize
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($KeyId)
        $encodedKey = [Convert]::ToBase64String($bytes)

        $sql = @'
INSERT INTO dmscs.OpenIddictKey (KeyId, PublicKey, PrivateKey, IsActive)
SELECT @KeyId,
       @PublicKey,
       ENCRYPTBYPASSPHRASE(@EncryptionKey, @PrivateKey),
       1;
'@

        return [PSCustomObject]@{
            Sql = $sql
            Parameters = [PSCustomObject]@{
                KeyId = $encodedKey
                PublicKey = [Convert]::FromBase64String($keyPair.PublicKey)
                PrivateKey = $keyPair.PrivateKey
                EncryptionKey = $EncryptionKey
            }
        }
    }
    catch {
        Write-Error "Failed to generate OpenIddict key insert command: $($_.Exception.Message)"
        throw
    }
}

# The dollar-quote tag around the PostgreSQL key-initialization blocks. A configured encryption key or
# generated key material carrying it would end the block early, so the builders reject that input.
$script:OpenIddictKeyInitializationTag = '$openiddict_key_initialization$'

# Builds the PL/pgSQL body that reads the newest active key, the one the Configuration Service signs
# with, and sets key_state to 'active-ok' when it decrypts with this passphrase or
# 'active-undecryptable' when pgcrypto rejects it (SQLSTATE 39000: wrong key or corrupt data). Any
# other decrypt failure, such as a pgcrypto permission error, sets 'active-unverified', because it
# says nothing about the key. $WhenNoActiveKey runs when no active key exists. A failure outside the
# decrypt (the lookup, the lock or the insert) is caught by the block Join-OpenIddictKeyInitializationScript
# wraps around this body, never classified here.
function Get-OpenIddictNewestKeyStateBlock {
    param(
        [Parameter(Mandatory = $true)]
        [string]$EncryptionKeyLiteral,

        [string]$WhenNoActiveKey = ""
    )

    $elseClause = ""
    if ($WhenNoActiveKey) {
        $elseClause = "    ELSE`n$WhenNoActiveKey"
    }

    return @"
    SELECT "PrivateKey" INTO newest_private_key
    FROM "dmscs"."OpenIddictKey"
    WHERE "IsActive" = TRUE
    ORDER BY "CreatedAt" DESC
    LIMIT 1;
    IF newest_private_key IS NOT NULL THEN
        BEGIN
            PERFORM pgp_sym_decrypt(newest_private_key::bytea, $EncryptionKeyLiteral);
            key_state := 'active-ok';
        EXCEPTION
            WHEN SQLSTATE '39000' THEN
                key_state := 'active-undecryptable';
            WHEN OTHERS THEN
                key_state := 'active-unverified';
        END;
$elseClause
    END IF;
"@
}

# Wraps a key-initialization body in one DO block. The DO statement text carries the encryption key
# and, on the creation path, the candidate private key, and PostgreSQL's default
# log_min_error_statement = error writes the text of any statement that ends in an error to the
# server log. So every runtime error in the body (the lookup, the advisory lock, the insert) is caught
# here and reported as the fixed state 'database-error <SQLSTATE>', and a cancellation (statement_timeout
# or a cancel request), which WHEN OTHERS does not catch, as 'canceled'. Catching rolls back everything
# the body did, so no key is changed. This keeps the statement out of error-statement logging only:
# statement logging (log_statement, log_min_duration_statement), auditing extensions and a terminated
# backend are outside what a caught error can prevent.
function Join-OpenIddictKeyInitializationScript {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Body
    )

    $tag = $script:OpenIddictKeyInitializationTag
    if ($Body.Contains($tag)) {
        throw "The OpenIddict signing-key initialization script cannot be built: an input contains its reserved dollar-quote tag. The value is withheld."
    }

    return @"
CREATE TEMP TABLE openiddict_key_initialization_state (state text NOT NULL);
DO $tag
DECLARE
    newest_private_key text;
    key_state text := 'none';
BEGIN
    BEGIN
$Body
    EXCEPTION
        WHEN query_canceled THEN
            key_state := 'canceled';
        WHEN OTHERS THEN
            key_state := 'database-error ' || SQLSTATE;
    END;
    INSERT INTO openiddict_key_initialization_state (state) VALUES (key_state);
END
$tag;
SELECT state FROM openiddict_key_initialization_state;
"@
}

<#
.SYNOPSIS
    Builds the PostgreSQL script that classifies the existing OpenIddict signing key without changing it.

.DESCRIPTION
    The first of two setup-openiddict.ps1 -InitDb phases. The script prints one state:
    'active-ok' when the newest active key decrypts with the configured encryption key,
    'active-undecryptable' when it does not, 'active-unverified' when the check itself fails for
    another reason, 'none' when no active key exists, or 'database-error <SQLSTATE>' / 'canceled'
    when the lookup fails or is canceled. It carries the encryption key but no key material, and it
    changes no row.

.PARAMETER EncryptionKey
    Key the Configuration Service uses to decrypt the private key.

.OUTPUTS
    System.String. A psql script for stdin.
#>
function New-OpenIddictKeyStateCheckSql {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Generates SQL text in memory; callers decide whether to execute it.')]
    [OutputType([string])]
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$EncryptionKey
    )

    $encryptionKeyLiteral = ConvertTo-PostgresSqlLiteral -Value $EncryptionKey
    return Join-OpenIddictKeyInitializationScript -Body (Get-OpenIddictNewestKeyStateBlock -EncryptionKeyLiteral $encryptionKeyLiteral)
}

<#
.SYNOPSIS
    Builds the PostgreSQL script that creates the initial OpenIddict signing key only when no active key exists.

.DESCRIPTION
    The second setup-openiddict.ps1 -InitDb phase, run only after the state check found no active key.
    A transaction-scoped advisory lock serializes cooperating initializers, and the newest active key is
    read again under it. Without one, the script inserts a new key and prints 'created'. When another
    initializer created one in the meantime, nothing is inserted and that key is decrypt-checked with
    this caller's encryption key: 'active-ok', 'active-undecryptable' or 'active-unverified'. A failed
    or canceled lock, lookup or insert reports 'database-error <SQLSTATE>' or 'canceled' and inserts
    nothing. No existing row is changed.

.PARAMETER EncryptionKey
    Key used to encrypt the private key in the database.

.OUTPUTS
    System.String. A psql script for stdin.
#>
function New-OpenIddictKeyInitializationSql {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Generates SQL text in memory; callers decide whether to execute it.')]
    [OutputType([string])]
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$EncryptionKey
    )

    $encryptionKeyLiteral = ConvertTo-PostgresSqlLiteral -Value $EncryptionKey
    $insertSql = New-OpenIddictKeyInsertSql -EncryptionKey $EncryptionKey
    $createInitialKey = @"
        $($insertSql.Trim())
        key_state := 'created';
"@
    $stateBlock = Get-OpenIddictNewestKeyStateBlock -EncryptionKeyLiteral $encryptionKeyLiteral -WhenNoActiveKey $createInitialKey

    # The lock id is a fixed constant shared by every cooperating initializer of this table.
    $body = @"
    PERFORM pg_advisory_xact_lock(1292000001);
$stateBlock
"@

    return Join-OpenIddictKeyInitializationScript -Body $body
}

<#
.SYNOPSIS
    Builds the parameterized SQL Server command that creates the initial OpenIddict signing key only when no active key exists.

.DESCRIPTION
    One transaction, serialized by a transaction-owned application lock. Without an active key, it
    inserts one through the same bound parameters as New-OpenIddictKeyInsertCommand and returns
    'created'. Otherwise it returns 'active-ok' or 'active-undecryptable' for the newest active key and
    changes no row. The candidate key pair is always generated and bound, and is unused unless inserted.

.PARAMETER EncryptionKey
    Key used to encrypt the private key in the database.

.OUTPUTS
    System.Management.Automation.PSCustomObject with Sql and Parameters properties.
#>
function New-OpenIddictKeyInitializationCommand {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Pure generator: builds and returns SQL text and parameter values without executing them; no system state changes and no -WhatIf surface.')]
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$EncryptionKey
    )

    $insertCommand = New-OpenIddictKeyInsertCommand -EncryptionKey $EncryptionKey -DbType "MSSQL"

    $sql = @"
SET XACT_ABORT ON;
SET NOCOUNT ON;
BEGIN TRANSACTION;
DECLARE @lockResult INT;
EXEC @lockResult = sp_getapplock @Resource = N'dmscs.OpenIddictKey.Initialize', @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 30000;
IF @lockResult < 0
    THROW 51292, N'Could not acquire the OpenIddict signing-key initialization lock.', 1;
DECLARE @newestPrivateKey VARBINARY(MAX);
DECLARE @keyState NVARCHAR(32);
SELECT TOP 1 @newestPrivateKey = PrivateKey
FROM dmscs.OpenIddictKey
WHERE IsActive = 1
ORDER BY CreatedAt DESC;
IF @newestPrivateKey IS NULL
BEGIN
$($insertCommand.Sql.Trim())
    SET @keyState = N'created';
END
ELSE IF DECRYPTBYPASSPHRASE(@EncryptionKey, @newestPrivateKey) IS NOT NULL
    SET @keyState = N'active-ok';
ELSE
    SET @keyState = N'active-undecryptable';
COMMIT TRANSACTION;
SELECT @keyState;
"@

    return [PSCustomObject]@{
        Sql = $sql
        Parameters = $insertCommand.Parameters
    }
}

<#
.SYNOPSIS
    Updates a client secret in the OpenIddict database with proper hashing.

.DESCRIPTION
    Generates a properly hashed client secret and creates a SQL UPDATE statement
    to update the ClientSecret field in the "dmscs"."OpenIddictApplication" table.

.PARAMETER ClientId
    The ClientId of the application to update.

.PARAMETER PlainTextSecret
    The plain text secret to hash and store.

.EXAMPLE
    $sql = New-ClientSecretUpdateSql -ClientId "DmsConfigurationService" -PlainTextSecret "ValidClientSecret1234567890!Abcd"
    Invoke-DbQuery $sql

.OUTPUTS
    System.String. SQL UPDATE statement for the client secret.
#>
function New-ClientSecretUpdateSql {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Generates SQL text in memory; callers decide whether to execute it.')]
    [OutputType([string])]
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$ClientId,

        [Parameter(Mandatory = $true)]
        [string]$PlainTextSecret
    )

    try {
        $hashedSecret = New-AspNetPasswordHash -PlainTextSecret $PlainTextSecret

        # Quoted by ConvertTo-PostgresSqlLiteral rather than pasted between bare quotes, matching
        # every other PostgreSQL literal this module and setup-openiddict.ps1 emit: the client id is
        # a configured value (DMS_CONFIGURATION_SERVICE_CLIENT_ID) and one carrying a single quote would close
        # its literal and make the statement invalid.
        $hashedSecretLiteral = ConvertTo-PostgresSqlLiteral -Value $hashedSecret
        $clientIdLiteral = ConvertTo-PostgresSqlLiteral -Value $ClientId

        $sql = @"
UPDATE "dmscs"."OpenIddictApplication"
SET "ClientSecret" = $hashedSecretLiteral
WHERE "ClientId" = $clientIdLiteral;
"@

        return $sql
    }
    catch {
        Write-Error "Failed to generate client secret update SQL: $($_.Exception.Message)"
        throw
    }
}

# Export module functions
Export-ModuleMember -Function New-AspNetPasswordHash, New-OpenIddictKeyPair, New-OpenIddictKeyInsertSql, New-OpenIddictKeyInsertCommand, New-OpenIddictKeyStateCheckSql, New-OpenIddictKeyInitializationSql, New-OpenIddictKeyInitializationCommand, New-ClientSecretUpdateSql, ConvertTo-PostgresSqlLiteral
