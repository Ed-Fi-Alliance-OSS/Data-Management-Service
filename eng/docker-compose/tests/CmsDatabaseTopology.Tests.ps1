# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

# DMS-1270: coverage for the optional separate Configuration Service database topology.
#
# Unit coverage for the contract's PowerShell functions (Resolve-CmsDatabaseTopologyEnvironmentFile,
# Confirm-CmsDatabaseTopologyAgreement, ConvertTo-DotenvSafeEnvValue,
# Get-DatabaseNameFromResolvedConnectionString, Get-EndpointFromResolvedConnectionString,
# Get-CmsDatabaseTopologyDefaultConnectionString, Test-PostgresDuplicateDatabaseError,
# Test-MssqlDuplicateDatabaseError), plus wiring-level coverage (the "wiring" Describe blocks below)
# for the topology-write sequence in start-local-dms.ps1, start-published-dms.ps1, and
# bootstrap-wrapper.psm1's own pre-resolution chain. Both database engines run the same sequence, so
# shared and separate mode are symmetric across PostgreSQL and SQL Server.

param()

Describe "Resolve-CmsDatabaseTopologyEnvironmentFile" {
    BeforeAll {
        $script:dockerComposeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
        Import-Module (Join-Path $script:dockerComposeRoot "env-utility.psm1") -Force
        Import-Module (Join-Path $script:dockerComposeRoot "database-safety.psm1") -Force

        # Round 8 finding: after the ambient-aware fix below, this function reads ambient
        # POSTGRES_DB_NAME/MSSQL_DB_NAME too, so a leftover value from the developer's own shell (or
        # a prior test) can now change these tests' outcome. Snapshot/clear/restore every ambient
        # variable either function under test consumes, not just the one a given test happens to set.
        #
        # The reorder-preservation cases below add a second reason: the proof treats a declaration whose
        # own key the ambient environment supplies as inert, so an ordinary shell variable named FEATURE or
        # PASSWORD would silently flip those tests from "rejects the unsafe move" to "permits it". Every
        # name any fixture in this Describe declares is therefore snapshotted and cleared too.
        $script:ambientKeys = @(
            "POSTGRES_DB_NAME", "MSSQL_DB_NAME", "POSTGRES_PASSWORD", "MSSQL_SA_PASSWORD",
            "DMS_CONFIG_DATABASE_NAME", "DMS_CONFIG_DATABASE_CONNECTION_STRING", "DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE",
            "FEATURE", "PASSWORD", "UNRELATED", "SET_ABOVE"
        )

        # SHA-256 and byte length read through [System.IO.File], never Get-FileHash / Get-Item. Every
        # fixture path in this Describe is dot-prefixed, and Linux PowerShell treats a leading dot as
        # hidden, so Get-Item -LiteralPath returns NOTHING for them without -Force. Measured on
        # ubuntu-latest: `(Get-Item $path).Length | Should -Be $lengthBefore` compared $null to $null and
        # passed vacuously, so a clobbered file would have been reported as unchanged. Reading the bytes
        # directly cannot be fooled that way, and it is the same byte-level notion of "unchanged" the
        # production rollback promises.
        function script:Get-FileFingerprint {
            param([Parameter(Mandatory)] [string]$Path)

            $bytes = [System.IO.File]::ReadAllBytes($Path)
            $algorithm = [System.Security.Cryptography.SHA256]::Create()
            try {
                $hash = [System.BitConverter]::ToString($algorithm.ComputeHash($bytes)).Replace('-', '')
            }
            finally {
                $algorithm.Dispose()
            }
            return [pscustomobject]@{ Sha256 = $hash; Length = $bytes.Length }
        }
    }

    BeforeEach {
        $script:work = Join-Path ([System.IO.Path]::GetTempPath()) "dms-cms-topology-$([Guid]::NewGuid().ToString('N'))"
        New-Item -ItemType Directory -Path $script:work -Force | Out-Null

        $script:ambientSnapshot = @{}
        foreach ($key in $script:ambientKeys) {
            $script:ambientSnapshot[$key] = [System.Environment]::GetEnvironmentVariable($key)
            Remove-Item "Env:\$key" -ErrorAction SilentlyContinue
        }
    }

    AfterEach {
        if (Test-Path -LiteralPath $script:work) {
            Remove-Item -LiteralPath $script:work -Recurse -Force -ErrorAction SilentlyContinue
        }
        foreach ($key in $script:ambientKeys) {
            if ($null -eq $script:ambientSnapshot[$key]) {
                Remove-Item "Env:\$key" -ErrorAction SilentlyContinue
            }
            else {
                [System.Environment]::SetEnvironmentVariable($key, $script:ambientSnapshot[$key])
            }
        }
    }

    Context "shared mode (switch omitted)" {
        It "returns the base file unchanged when DMS_CONFIG_DATABASE_NAME already aliases the datastore name" {
            $basePath = Join-Path $script:work ".env.base"
            Set-Content -LiteralPath $basePath -Value (@(
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'POSTGRES_PASSWORD=abcdefgh1!',
                'DMS_CONFIG_DATABASE_NAME=${POSTGRES_DB_NAME}',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_PASSWORD};database=${DMS_CONFIG_DATABASE_NAME};'
            ) -join "`n") -NoNewline

            $result = Resolve-CmsDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $basePath -DatabaseEngine "postgresql" -DockerComposeRoot $script:work

            $result | Should -Be $basePath -Because "nothing needs to change: the alias already resolves to the effective shared-mode name"
        }

        It "materializes DMS_CONFIG_DATABASE_NAME into a derived file for an old .env that never defined it" {
            # This is the fix for the old-file gap: a pre-existing developer .env predating this
            # story's template update has no DMS_CONFIG_DATABASE_NAME line at all.
            $basePath = Join-Path $script:work ".env.base"
            Set-Content -LiteralPath $basePath -Value (@(
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'POSTGRES_PASSWORD=abcdefgh1!',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_PASSWORD};database=${POSTGRES_DB_NAME};'
            ) -join "`n") -NoNewline

            $result = Resolve-CmsDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $basePath -DatabaseEngine "postgresql" -DockerComposeRoot $script:work

            $result | Should -Not -Be $basePath
            $values = ReadValuesFromEnvFile $result
            $values["DMS_CONFIG_DATABASE_NAME"] | Should -Be "edfi_datamanagementservice"
            $values["DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE"] | Should -Be "false"
        }

        It "reflects an ambient POSTGRES_DB_NAME override in the materialized DMS_CONFIG_DATABASE_NAME" {
            # Round 8 Blocker 1: the write side must resolve the datastore name the same
            # Compose-precedence-aware way Confirm-CmsDatabaseTopologyAgreement does, since an
            # ambient override genuinely moves the running database container.
            [System.Environment]::SetEnvironmentVariable("POSTGRES_DB_NAME", "ambient_override_db")
            $basePath = Join-Path $script:work ".env.base"
            Set-Content -LiteralPath $basePath -Value (@(
                'POSTGRES_DB_NAME=file_named_db',
                'POSTGRES_PASSWORD=abcdefgh1!'
            ) -join "`n") -NoNewline

            $result = Resolve-CmsDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $basePath -DatabaseEngine "postgresql" -DockerComposeRoot $script:work

            (ReadValuesFromEnvFile $result)["DMS_CONFIG_DATABASE_NAME"] | Should -Be "ambient_override_db"
        }

        It "recognizes an already-aliased file as unchanged even while an ambient override is active" {
            # The idempotency comparison must resolve the CURRENT DMS_CONFIG_DATABASE_NAME the same
            # ambient-aware way, or an active override would make an already-correct alias look
            # "changed" on every call, needlessly freezing a live alias into a derived file.
            [System.Environment]::SetEnvironmentVariable("POSTGRES_DB_NAME", "ambient_override_db")
            $basePath = Join-Path $script:work ".env.base"
            Set-Content -LiteralPath $basePath -Value (@(
                'POSTGRES_DB_NAME=file_named_db',
                'POSTGRES_PASSWORD=abcdefgh1!',
                'DMS_CONFIG_DATABASE_NAME=${POSTGRES_DB_NAME}',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_PASSWORD};database=${DMS_CONFIG_DATABASE_NAME};'
            ) -join "`n") -NoNewline

            $result = Resolve-CmsDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $basePath -DatabaseEngine "postgresql" -DockerComposeRoot $script:work

            $result | Should -Be $basePath -Because "Compose would resolve the existing alias to the same ambient value, so no rewrite is needed"
        }
    }

    Context "declaration order (Compose resolves --env-file references in file order)" {
        # This function's hashtable-based resolution is order-blind, so a value can look correct here
        # and still render empty for Compose: verified against a real `docker compose config` render,
        # DMS_CONFIG_DATABASE_NAME=`${POSTGRES_DB_NAME} declared ABOVE POSTGRES_DB_NAME produced
        # database= (empty). A disordered file therefore must not take the unchanged early return; the
        # derived-write path heals it, because the writer serializes the alias as the resolved literal
        # and the post-write reorder keeps the alias ahead of the connection string.
        It "does not early-return a shared-mode file whose alias is declared before the datastore name" {
            $basePath = Join-Path $script:work ".env.disordered"
            Set-Content -LiteralPath $basePath -Value (@(
                'DMS_CONFIG_DATABASE_NAME=${POSTGRES_DB_NAME}',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_PASSWORD};database=${DMS_CONFIG_DATABASE_NAME};',
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'POSTGRES_PASSWORD=abcdefgh1!',
                'DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE=false'
            ) -join "`n") -NoNewline

            $result = Resolve-CmsDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $basePath -DatabaseEngine "postgresql" -DockerComposeRoot $script:work

            $result | Should -Not -Be $basePath -Because "the disordered base file renders database= empty at real Compose render time"

            # The healed file must be order-viable: the alias is a resolved literal (nothing left to
            # forward-reference) and still precedes the connection string that references it.
            $derivedLines = [System.IO.File]::ReadAllLines($result)
            $aliasLine = @($derivedLines | Where-Object { $_ -like 'DMS_CONFIG_DATABASE_NAME=*' })[0]
            $aliasLine | Should -Be 'DMS_CONFIG_DATABASE_NAME=edfi_datamanagementservice'
            $aliasIndex = [Array]::IndexOf($derivedLines, $aliasLine)
            $connIndex = [Array]::IndexOf($derivedLines, @($derivedLines | Where-Object { $_ -like 'DMS_CONFIG_DATABASE_CONNECTION_STRING=*' })[0])
            $aliasIndex | Should -BeLessThan $connIndex
        }

        It "does not early-return a file whose connection string is declared before the alias it references" {
            $basePath = Join-Path $script:work ".env.conn-first"
            Set-Content -LiteralPath $basePath -Value (@(
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'POSTGRES_PASSWORD=abcdefgh1!',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_PASSWORD};database=${DMS_CONFIG_DATABASE_NAME};',
                'DMS_CONFIG_DATABASE_NAME=${POSTGRES_DB_NAME}',
                'DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE=false'
            ) -join "`n") -NoNewline

            $result = Resolve-CmsDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $basePath -DatabaseEngine "postgresql" -DockerComposeRoot $script:work

            $result | Should -Not -Be $basePath
            $derivedLines = [System.IO.File]::ReadAllLines($result)
            $aliasIndex = [Array]::IndexOf($derivedLines, @($derivedLines | Where-Object { $_ -like 'DMS_CONFIG_DATABASE_NAME=*' })[0])
            $connIndex = [Array]::IndexOf($derivedLines, @($derivedLines | Where-Object { $_ -like 'DMS_CONFIG_DATABASE_CONNECTION_STRING=*' })[0])
            $aliasIndex | Should -BeLessThan $connIndex
        }

        It "still early-returns a correctly-ordered, already-correct file" {
            # The order check must not turn every call into a rewrite: the checked-in profile shape
            # (datastore name, then alias, then connection string) stays untouched.
            $basePath = Join-Path $script:work ".env.ordered"
            Set-Content -LiteralPath $basePath -Value (@(
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'POSTGRES_PASSWORD=abcdefgh1!',
                'DMS_CONFIG_DATABASE_NAME=${POSTGRES_DB_NAME}',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_PASSWORD};database=${DMS_CONFIG_DATABASE_NAME};',
                'DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE=false'
            ) -join "`n") -NoNewline

            Resolve-CmsDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $basePath -DatabaseEngine "postgresql" -DockerComposeRoot $script:work |
                Should -Be $basePath
        }

        # Post-PR review: the order check must cover EVERY variable the connection string references,
        # not only the DMS_CONFIG_DATABASE_NAME alias. A shared-mode connection string legitimately
        # keeps its ${POSTGRES_DB_NAME} / ${MSSQL_DB_NAME} and credential references, and any of them
        # declared below the connection string renders that segment empty. Verified live: the
        # disordered file below rendered "password=;database=;" through a real `docker compose config`,
        # while the healed derived file rendered the real password and database name.
        It "does not early-return a shared-mode file whose connection string precedes the <Name> it references" -ForEach @(
            @{ Name = 'POSTGRES_DB_NAME'; Engine = 'postgresql'; DatastoreName = 'edfi_datamanagementservice' }
            @{ Name = 'MSSQL_DB_NAME'; Engine = 'mssql'; DatastoreName = 'edfi_datamanagementservice' }
        ) {
            $basePath = Join-Path $script:work ".env.forward-$($_.Name)"
            $datastoreKey = $_.Name
            Set-Content -LiteralPath $basePath -Value (@(
                "DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;port=5432;username=postgres;password=`${POSTGRES_PASSWORD};database=`${$datastoreKey};",
                "$datastoreKey=$($_.DatastoreName)",
                'POSTGRES_PASSWORD=abcdefgh1!',
                "DMS_CONFIG_DATABASE_NAME=`${$datastoreKey}",
                'DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE=false'
            ) -join "`n") -NoNewline

            $result = Resolve-CmsDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $basePath -DatabaseEngine $_.Engine -DockerComposeRoot $script:work

            $result | Should -Not -Be $basePath -Because "Compose renders the forward-referenced segments empty"

            # Every key the connection string references must now be declared above it.
            $derivedLines = [System.IO.File]::ReadAllLines($result)
            $connIndex = [Array]::IndexOf($derivedLines, @($derivedLines | Where-Object { $_ -like 'DMS_CONFIG_DATABASE_CONNECTION_STRING=*' })[0])
            foreach ($referencedKey in @($datastoreKey, 'POSTGRES_PASSWORD')) {
                $keyIndex = [Array]::IndexOf($derivedLines, @($derivedLines | Where-Object { $_ -like "$referencedKey=*" })[0])
                $keyIndex | Should -BeGreaterThan -1 -Because "$referencedKey must survive the rewrite"
                $keyIndex | Should -BeLessThan $connIndex -Because "$referencedKey is referenced by the connection string"
            }
        }

        It "leaves a connection string alone when the keys it references are already declared above it" {
            # Narrowness: an ordinary correctly-ordered profile carrying ${POSTGRES_DB_NAME} in its
            # connection string must not be rewritten just because it references variables.
            $basePath = Join-Path $script:work ".env.forward-ok"
            Set-Content -LiteralPath $basePath -Value (@(
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'POSTGRES_PASSWORD=abcdefgh1!',
                'DMS_CONFIG_DATABASE_NAME=${POSTGRES_DB_NAME}',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_PASSWORD};database=${POSTGRES_DB_NAME};',
                'DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE=false'
            ) -join "`n") -NoNewline

            Resolve-CmsDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $basePath -DatabaseEngine "postgresql" -DockerComposeRoot $script:work |
                Should -Be $basePath
        }

        It "does not treat a connection-string reference resolved from the ambient environment as order-broken" {
            # A key absent from the file is resolved by Compose from the ambient environment regardless
            # of line order, so it is not a forward reference and must not force a rewrite.
            [System.Environment]::SetEnvironmentVariable("POSTGRES_PASSWORD", "ambient-pass")
            $basePath = Join-Path $script:work ".env.ambient-ref"
            Set-Content -LiteralPath $basePath -Value (@(
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'DMS_CONFIG_DATABASE_NAME=${POSTGRES_DB_NAME}',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_PASSWORD};database=${DMS_CONFIG_DATABASE_NAME};',
                'DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE=false'
            ) -join "`n") -NoNewline

            Resolve-CmsDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $basePath -DatabaseEngine "postgresql" -DockerComposeRoot $script:work |
                Should -Be $basePath
        }

        It "refuses to reorder a forward-referenced key that itself references other variables" {
            # Relocating a reference-bearing line could break ITS own declaration order, so the
            # unsupported shape fails loudly with the manual fix rather than rendering empty.
            $basePath = Join-Path $script:work ".env.forward-chained"
            Set-Content -LiteralPath $basePath -Value (@(
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_PASSWORD};database=${CHAINED_DB_NAME};',
                'BASE_DB_NAME=edfi_datamanagementservice',
                'CHAINED_DB_NAME=${BASE_DB_NAME}',
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'POSTGRES_PASSWORD=abcdefgh1!',
                'DMS_CONFIG_DATABASE_NAME=${POSTGRES_DB_NAME}',
                'DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE=false'
            ) -join "`n") -NoNewline

            { Resolve-CmsDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $basePath -DatabaseEngine "postgresql" -DockerComposeRoot $script:work } |
                Should -Throw "*references 'CHAINED_DB_NAME'*declared after it*"
        }
    }

    # Order-dependence cuts both ways. Moving a key above the connection string also moves it above
    # everything in between, which can make a reference resolvable that previously resolved to nothing
    # and silently change a variable this seam does not own. The connection-string postcondition cannot
    # see that - it only checks the key the repair targets - so the move itself must prove it first.
    Context "a reorder must preserve every declaration whose visibility it changes (DMS-1270)" {
        BeforeAll {
            # Returns the names of the files this suite's derived directory holds, so a failed repair can
            # be shown to leave no artifact behind. -Force because derived files are dot-prefixed and
            # Linux PowerShell treats a leading dot as hidden.
            function script:Get-DerivedArtifactName {
                $derivedDir = Join-Path $script:work ".derived"
                if (-not (Test-Path -LiteralPath $derivedDir)) { return @() }
                return @(Get-ChildItem -LiteralPath $derivedDir -Name -Force)
            }
        }

        It "fails closed rather than firing an intervening ':-' default branch that had not fired" {
            # The measured reproduction. Before the repair FEATURE renders 'disabled'; relocating
            # PASSWORD above the connection string also places it above FEATURE, so the ':-' default
            # arm stops firing and FEATURE silently becomes the password. The connection string itself
            # repairs correctly, which is exactly why the postcondition passed and this went unnoticed.
            $basePath = Join-Path $script:work ".env.unsafe-lazy"
            $sourceLines = @(
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'DMS_CONFIG_DATABASE_NAME=${POSTGRES_DB_NAME}',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=host=db;password=${PASSWORD};database=${DMS_CONFIG_DATABASE_NAME};',
                'FEATURE=${PASSWORD:-disabled}',
                'PASSWORD=secret',
                'DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE=false'
            )
            Set-Content -LiteralPath $basePath -Value ($sourceLines -join "`n") -NoNewline
            $sourceFingerprintBefore = Get-FileFingerprint -Path $basePath

            (Resolve-DotenvFileSequentially -Path $basePath).Effective["FEATURE"] |
                Should -BeExactly 'disabled' -Because "this is what Compose renders before any repair"

            $thrown = { Resolve-CmsDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $basePath -DatabaseEngine "postgresql" -DockerComposeRoot $script:work } |
                Should -Throw -PassThru

            $thrown.Exception.Message | Should -BeLike "*would change the value Docker Compose renders for 'FEATURE' (line 4)*"
            $thrown.Exception.Message | Should -Not -BeLike "*secret*" -Because "an environment file carries credentials; the diagnostic names keys and lines only"
            $thrown.Exception.Message | Should -Not -BeLike "*disabled*" -Because "neither the before nor the after value may be rendered"

            (Get-FileFingerprint -Path $basePath).Sha256 |
                Should -Be $sourceFingerprintBefore.Sha256 -Because "the source environment file is never written to"
            Get-DerivedArtifactName | Should -BeNullOrEmpty -Because "the unsafe artifact this call created must not survive for a later run to pick up"
        }

        It "fails closed for an intervening '-' consumer too, not only ':-'" {
            # ${VAR-default} takes the default only when VAR is UNSET, which is a different Compose rule
            # from ${VAR:-default}. The move changes VAR from unset to set, so this consumer's value
            # changes as well - and the proof must be about evaluated values, not about which operator
            # spelling appears in the text.
            $basePath = Join-Path $script:work ".env.unsafe-dash"
            Set-Content -LiteralPath $basePath -Value (@(
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'DMS_CONFIG_DATABASE_NAME=${POSTGRES_DB_NAME}',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=host=db;password=${PASSWORD};database=${DMS_CONFIG_DATABASE_NAME};',
                'FEATURE=${PASSWORD-fallback}',
                'PASSWORD=secret',
                'DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE=false'
            ) -join "`n") -NoNewline

            { Resolve-CmsDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $basePath -DatabaseEngine "postgresql" -DockerComposeRoot $script:work } |
                Should -Throw "*renders for 'FEATURE'*"
        }

        It "fails closed for an unsafe DMS_CONFIG_DATABASE_NAME move, not only for a credential key" {
            # The topology alias is moved by the same function and gets the same proof. Covering only
            # PASSWORD would leave the alias move - the one this design performs on every migrated file -
            # unproven.
            $basePath = Join-Path $script:work ".env.unsafe-alias"
            Set-Content -LiteralPath $basePath -Value (@(
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'POSTGRES_PASSWORD=abcdefgh1!',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_PASSWORD};database=${DMS_CONFIG_DATABASE_NAME};',
                'FEATURE=${DMS_CONFIG_DATABASE_NAME:-disabled}',
                'DMS_CONFIG_DATABASE_NAME=edfi_datamanagementservice'
            ) -join "`n") -NoNewline

            $thrown = { Resolve-CmsDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $basePath -DatabaseEngine "postgresql" -DockerComposeRoot $script:work } |
                Should -Throw -PassThru

            $thrown.Exception.Message | Should -BeLike "*reordering 'DMS_CONFIG_DATABASE_NAME' above 'DMS_CONFIG_DATABASE_CONNECTION_STRING'*"
            $thrown.Exception.Message | Should -BeLike "*renders for 'FEATURE'*"
            Get-DerivedArtifactName | Should -BeNullOrEmpty
        }

        It "still repairs the same shape when no intervening declaration is affected" {
            # Identical to the reproduction minus the consumer in between. The proof must permit the
            # repair it exists to protect, or it would just be a refusal to repair anything.
            $basePath = Join-Path $script:work ".env.safe-hop"
            Set-Content -LiteralPath $basePath -Value (@(
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'DMS_CONFIG_DATABASE_NAME=${POSTGRES_DB_NAME}',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_PASSWORD};database=${DMS_CONFIG_DATABASE_NAME};',
                'UNRELATED=constant',
                'POSTGRES_PASSWORD=abcdefgh1!',
                'DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE=false'
            ) -join "`n") -NoNewline

            $result = Resolve-CmsDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $basePath -DatabaseEngine "postgresql" -DockerComposeRoot $script:work

            $derived = Resolve-DotenvFileSequentially -Path $result
            $derived.Effective["DMS_CONFIG_DATABASE_CONNECTION_STRING"] |
                Should -BeExactly 'host=dms-postgresql;port=5432;username=postgres;password=abcdefgh1!;database=edfi_datamanagementservice;'
            $derived.Effective["UNRELATED"] | Should -BeExactly 'constant'
        }

        It "still repairs when an intervening declaration only LOOKS like a consumer: an escaped '$$' reference" {
            # A '$$' escape is a literal, not a reference. A lexical scan over the line text would see
            # "${PASSWORD}" and refuse a perfectly safe move; the evaluated value is identical before and
            # after, so the repair proceeds.
            $basePath = Join-Path $script:work ".env.escaped-decoy"
            Set-Content -LiteralPath $basePath -Value (@(
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'DMS_CONFIG_DATABASE_NAME=${POSTGRES_DB_NAME}',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_PASSWORD};database=${DMS_CONFIG_DATABASE_NAME};',
                'FEATURE=$${POSTGRES_PASSWORD}',
                'POSTGRES_PASSWORD=abcdefgh1!',
                'DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE=false'
            ) -join "`n") -NoNewline

            $result = Resolve-CmsDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $basePath -DatabaseEngine "postgresql" -DockerComposeRoot $script:work

            $derived = Resolve-DotenvFileSequentially -Path $result
            $derived.Effective["FEATURE"] | Should -BeExactly '${POSTGRES_PASSWORD}' -Because "the escape is a literal in both orderings"
            $derived.Effective["DMS_CONFIG_DATABASE_CONNECTION_STRING"] |
                Should -BeExactly 'host=dms-postgresql;port=5432;username=postgres;password=abcdefgh1!;database=edfi_datamanagementservice;'
        }

        It "still repairs when an intervening lazy branch mentioning the moved key never fires" {
            # ${SET_ABOVE:-${POSTGRES_PASSWORD}} never evaluates its default arm, because SET_ABOVE is
            # declared above with a non-empty value. The moved key is textually present and behaviourally
            # absent, and it is the behaviour that decides.
            $basePath = Join-Path $script:work ".env.unfired-branch"
            Set-Content -LiteralPath $basePath -Value (@(
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'SET_ABOVE=already-set',
                'DMS_CONFIG_DATABASE_NAME=${POSTGRES_DB_NAME}',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_PASSWORD};database=${DMS_CONFIG_DATABASE_NAME};',
                'FEATURE=${SET_ABOVE:-${POSTGRES_PASSWORD}}',
                'POSTGRES_PASSWORD=abcdefgh1!',
                'DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE=false'
            ) -join "`n") -NoNewline

            $result = Resolve-CmsDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $basePath -DatabaseEngine "postgresql" -DockerComposeRoot $script:work

            $derived = Resolve-DotenvFileSequentially -Path $result
            $derived.Effective["FEATURE"] | Should -BeExactly 'already-set'
            $derived.Effective["DMS_CONFIG_DATABASE_CONNECTION_STRING"] |
                Should -BeExactly 'host=dms-postgresql;port=5432;username=postgres;password=abcdefgh1!;database=edfi_datamanagementservice;'
        }

        It "permits the move when the affected declaration's own key comes from the ambient environment" {
            # A matched pair with the ':-' rejection above: the same file, one variable changed. Ambient
            # precedence makes the FEATURE declaration inert - Compose ignores the file's value entirely,
            # both for the final environment and for every later reference - so the value frozen at that
            # line is not something a move can change. Comparing it anyway refused a safe repair over a
            # value that never reaches the container.
            $sharedLines = @(
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'DMS_CONFIG_DATABASE_NAME=${POSTGRES_DB_NAME}',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;port=5432;username=postgres;password=${PASSWORD};database=${DMS_CONFIG_DATABASE_NAME};',
                'FEATURE=${PASSWORD:-disabled}',
                'PASSWORD=abcdefgh1!',
                'DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE=false'
            )
            $basePath = Join-Path $script:work ".env.ambient-shadowed"
            Set-Content -LiteralPath $basePath -Value ($sharedLines -join "`n") -NoNewline

            # The other half of the pair, asserted on the SAME file: without the ambient value this must
            # still be rejected, or the exemption would be a blanket one.
            { Resolve-CmsDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $basePath -DatabaseEngine "postgresql" -DockerComposeRoot $script:work } |
                Should -Throw "*renders for 'FEATURE'*"

            [System.Environment]::SetEnvironmentVariable("FEATURE", "ambient-stable")
            $result = Resolve-CmsDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $basePath -DatabaseEngine "postgresql" -DockerComposeRoot $script:work

            $derived = Resolve-DotenvFileSequentially -Path $result
            $derived.Effective["FEATURE"] | Should -BeExactly 'ambient-stable' -Because "ambient wins in both orderings, so what Compose renders for FEATURE did not change"
            $derived.Effective["DMS_CONFIG_DATABASE_CONNECTION_STRING"] |
                Should -BeExactly 'host=dms-postgresql;port=5432;username=postgres;password=abcdefgh1!;database=edfi_datamanagementservice;'
        }

        It "counts a present-but-EMPTY ambient value as supplied, like the evaluator's own provenance rule" {
            # Resolve-DotenvFileSequentially tests ambient provenance with a $null check, not a blank check,
            # so an explicitly-empty ambient value still shadows the file declaration. The proof has to use
            # the same rule or the two disagree about which declarations are live.
            $basePath = Join-Path $script:work ".env.ambient-empty"
            Set-Content -LiteralPath $basePath -Value (@(
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'DMS_CONFIG_DATABASE_NAME=${POSTGRES_DB_NAME}',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;port=5432;username=postgres;password=${PASSWORD};database=${DMS_CONFIG_DATABASE_NAME};',
                'FEATURE=${PASSWORD:-disabled}',
                'PASSWORD=abcdefgh1!',
                'DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE=false'
            ) -join "`n") -NoNewline

            [System.Environment]::SetEnvironmentVariable("FEATURE", "")
            if ($null -eq [System.Environment]::GetEnvironmentVariable("FEATURE")) {
                # On Unix .NET deletes the variable when it is set to an empty string, so the state under
                # test cannot be established. Assert the precondition rather than letting the platform
                # silently decide whether this test means anything.
                Set-ItResult -Skipped -Because "this platform cannot represent a present-but-blank ambient environment variable"
                return
            }

            $result = Resolve-CmsDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $basePath -DatabaseEngine "postgresql" -DockerComposeRoot $script:work
            (Resolve-DotenvFileSequentially -Path $result).Effective["FEATURE"] | Should -BeExactly ''
        }
    }

    # A rejected repair must be a no-op on disk. Write-DerivedEnvFile replaces its target outright, so the
    # write has to sit inside the same protected region as the moves it precedes - otherwise the rejection
    # arrives after the previous artifact has already been destroyed.
    Context "a rejected repair leaves the derived target exactly as it was found (DMS-1270)" {
        BeforeAll {
            $script:unsafeReorderLines = @(
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'DMS_CONFIG_DATABASE_NAME=${POSTGRES_DB_NAME}',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=host=db;password=${PASSWORD};database=${DMS_CONFIG_DATABASE_NAME};',
                'FEATURE=${PASSWORD:-disabled}',
                'PASSWORD=secret',
                'DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE=false'
            )
        }

        It "restores a pre-existing derived target byte-for-byte instead of clobbering it" {
            $basePath = Join-Path $script:work ".env.preexisting-target"
            Set-Content -LiteralPath $basePath -Value ($script:unsafeReorderLines -join "`n") -NoNewline

            $derivedDir = Join-Path $script:work ".derived"
            New-Item -ItemType Directory -Path $derivedDir -Force | Out-Null
            $targetPath = Join-Path $derivedDir ".env.preexisting-target.topology"
            # Deliberately unlike anything this function would ever write, so a clobber cannot pass as a
            # coincidentally-correct result.
            [System.IO.File]::WriteAllBytes($targetPath, [System.Text.Encoding]::UTF8.GetBytes("PRIOR_RUN_MARKER=keep-me`n"))
            $fingerprintBefore = Get-FileFingerprint -Path $targetPath

            { Resolve-CmsDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $basePath -DatabaseEngine "postgresql" -DockerComposeRoot $script:work } |
                Should -Throw "*renders for 'FEATURE'*"

            $fingerprintAfter = Get-FileFingerprint -Path $targetPath
            $fingerprintAfter.Sha256 | Should -Be $fingerprintBefore.Sha256
            $fingerprintAfter.Length | Should -Be $fingerprintBefore.Length
            [System.IO.File]::ReadAllText($targetPath) | Should -BeExactly "PRIOR_RUN_MARKER=keep-me`n"
        }

        It "restores the caller's own input file in the same-path .topology re-entry shape" {
            # Re-deriving from a previous output makes $derivedPath IS $BaseEnvironmentFile, so an
            # unprotected write rewrote the very file the caller handed in - the one case where clobbering
            # the target and touching the source are the same event.
            $derivedDir = Join-Path $script:work ".derived"
            New-Item -ItemType Directory -Path $derivedDir -Force | Out-Null
            $reentryPath = Join-Path $derivedDir ".env.reentry.topology"
            [System.IO.File]::WriteAllBytes($reentryPath, [System.Text.Encoding]::UTF8.GetBytes((($script:unsafeReorderLines -join "`n") + "`n")))
            $fingerprintBefore = Get-FileFingerprint -Path $reentryPath

            { Resolve-CmsDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $reentryPath -DatabaseEngine "postgresql" -DockerComposeRoot $script:work } |
                Should -Throw "*renders for 'FEATURE'*"

            $fingerprintAfter = Get-FileFingerprint -Path $reentryPath
            $fingerprintAfter.Sha256 | Should -Be $fingerprintBefore.Sha256 -Because "the file the caller passed in must survive a rejected repair unchanged"
            $fingerprintAfter.Length | Should -Be $fingerprintBefore.Length
            [System.IO.File]::ReadAllLines($reentryPath) | Should -Be $script:unsafeReorderLines -Because "the alias must not be left literalized"
        }

        It "refuses to begin when an existing target cannot be snapshotted, and leaves it untouched" {
            # A write with no snapshot behind it is a write that cannot be undone, so the run must stop
            # before Write-DerivedEnvFile rather than discover the problem at rollback time.
            $basePath = Join-Path $script:work ".env.unreadable-target"
            Set-Content -LiteralPath $basePath -Value ($script:unsafeReorderLines -join "`n") -NoNewline

            $derivedDir = Join-Path $script:work ".derived"
            New-Item -ItemType Directory -Path $derivedDir -Force | Out-Null
            $targetPath = Join-Path $derivedDir ".env.unreadable-target.topology"
            [System.IO.File]::WriteAllBytes($targetPath, [System.Text.Encoding]::UTF8.GetBytes("PRIOR_RUN_MARKER=keep-me`n"))
            $fingerprintBefore = Get-FileFingerprint -Path $targetPath

            $handle = [System.IO.File]::Open($targetPath, [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
            try {
                # Unix does not enforce exclusive handles, so probe rather than assume the state exists.
                $isExclusive = $false
                try { [System.IO.File]::ReadAllBytes($targetPath) | Out-Null } catch { $isExclusive = $true }
                if (-not $isExclusive) {
                    Set-ItResult -Skipped -Because "this platform does not enforce exclusive file handles, so an unreadable target cannot be simulated"
                    return
                }

                $thrown = { Resolve-CmsDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $basePath -DatabaseEngine "postgresql" -DockerComposeRoot $script:work } |
                    Should -Throw -PassThru

                $thrown.Exception.Message | Should -BeLike "*could not be read*"
                $thrown.Exception.Message | Should -BeLike "*Nothing was written*"
                $thrown.Exception.Message | Should -Not -BeLike "*secret*"
            }
            finally {
                $handle.Dispose()
            }

            (Get-FileFingerprint -Path $targetPath).Sha256 | Should -Be $fingerprintBefore.Sha256
        }

        It "reports a failed rollback as its own, worse condition - and still withholds values" {
            # A rollback that itself fails leaves a file on disk that no longer means what its owner thinks.
            # A read-only target reproduces it precisely: the snapshot succeeds, so the run begins, and then
            # both the derived write and the restore fail.
            $basePath = Join-Path $script:work ".env.readonly-target"
            Set-Content -LiteralPath $basePath -Value ($script:unsafeReorderLines -join "`n") -NoNewline

            $derivedDir = Join-Path $script:work ".derived"
            New-Item -ItemType Directory -Path $derivedDir -Force | Out-Null
            $targetPath = Join-Path $derivedDir ".env.readonly-target.topology"
            [System.IO.File]::WriteAllBytes($targetPath, [System.Text.Encoding]::UTF8.GetBytes("PRIOR_RUN_MARKER=keep-me`n"))
            # [System.IO.FileInfo] directly, not Get-Item: the provider returns nothing for a dot-prefixed
            # path on Linux without -Force, and the property assignment then failed against $null.
            $targetFile = [System.IO.FileInfo]::new($targetPath)
            $targetFile.IsReadOnly = $true
            try {
                # A root-owned container ignores the read-only bit, so probe with a harmless empty append
                # rather than assume the state exists.
                $isReadOnly = $false
                try { [System.IO.File]::AppendAllText($targetPath, "") } catch { $isReadOnly = $true }
                if (-not $isReadOnly) {
                    Set-ItResult -Skipped -Because "this platform/user can write a read-only file, so a failed rollback cannot be simulated"
                    return
                }

                $thrown = { Resolve-CmsDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $basePath -DatabaseEngine "postgresql" -DockerComposeRoot $script:work } |
                    Should -Throw -PassThru

                $thrown.Exception.Message | Should -BeLike "*could not be restored to its previous contents*"
                $thrown.Exception.Message | Should -BeLike "*indeterminate state*"
                $thrown.Exception.Message | Should -Not -BeLike "*secret*" -Because "neither file's values may appear, even in the worst-case report"
                $thrown.Exception.Message | Should -Not -BeLike "*keep-me*"
            }
            finally {
                $targetFile.IsReadOnly = $false
            }
        }
    }

    # Docker Compose resolves an --env-file sequentially. Validation built on a complete hashtable
    # answers a different question and can approve a file Compose renders differently, so these pin the
    # required outcome for each input class. Each expectation was confirmed against a real
    # `docker compose config` render of the same file before the behavior was implemented.
    Context "sequential evaluation classes (DMS-1270)" {
        BeforeAll {
            $script:seamConnectionString = 'DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_PASSWORD};database=${DMS_CONFIG_DATABASE_NAME};'
        }

        It "rejects a duplicated seam dependency instead of silently approving it" {
            # Verified live: this file renders the CMS database as edfi_datamanagementservice (the FIRST
            # POSTGRES_DB_NAME, frozen into the alias) while the datastore container is created as
            # some_other_db (the LAST one, which the compose file sees). The old hashtable resolution saw
            # only some_other_db for both and passed the agreement check. Reordering cannot fix this:
            # every line between the two declarations legitimately sees the earlier value.
            $basePath = Join-Path $script:work ".env.duplicate"
            Set-Content -LiteralPath $basePath -Value (@(
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'POSTGRES_PASSWORD=abcdefgh1!',
                'DMS_CONFIG_DATABASE_NAME=${POSTGRES_DB_NAME}',
                $script:seamConnectionString,
                'DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE=false',
                'POSTGRES_DB_NAME=some_other_db'
            ) -join "`n") -NoNewline

            { Resolve-CmsDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $basePath -DatabaseEngine "postgresql" -DockerComposeRoot $script:work } |
                Should -Throw "*'POSTGRES_DB_NAME' is declared more than once*"
        }

        It "names both declaration line numbers in the duplicate diagnostic" {
            $basePath = Join-Path $script:work ".env.duplicate-lines"
            Set-Content -LiteralPath $basePath -Value (@(
                'POSTGRES_DB_NAME=first_db',
                'POSTGRES_PASSWORD=abcdefgh1!',
                'DMS_CONFIG_DATABASE_NAME=${POSTGRES_DB_NAME}',
                $script:seamConnectionString,
                'POSTGRES_DB_NAME=second_db'
            ) -join "`n") -NoNewline

            { Resolve-CmsDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $basePath -DatabaseEngine "postgresql" -DockerComposeRoot $script:work } |
                Should -Throw "*lines 1, 5*"
        }

        It "rejects a transitive forward dependency and reports the chain" {
            # Verified live: POSTGRES_PASSWORD freezes empty because LATE_PW is declared after it, so the
            # connection string renders password= with no value. A multi-line reorder could change other
            # lines' frozen values, so this fails closed with the chain rather than being repaired.
            $basePath = Join-Path $script:work ".env.transitive"
            Set-Content -LiteralPath $basePath -Value (@(
                'POSTGRES_PASSWORD=${LATE_PW}',
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'DMS_CONFIG_DATABASE_NAME=${POSTGRES_DB_NAME}',
                $script:seamConnectionString,
                'DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE=false',
                'LATE_PW=abcdefgh1!'
            ) -join "`n") -NoNewline

            { Resolve-CmsDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $basePath -DatabaseEngine "postgresql" -DockerComposeRoot $script:work } |
                Should -Throw "*DMS_CONFIG_DATABASE_CONNECTION_STRING -> POSTGRES_PASSWORD -> LATE_PW*"
        }

        It "repairs a simple forward reference written as '<_>'" -ForEach @(
            'POSTGRES_PASSWORD = abcdefgh1!'
            'export POSTGRES_PASSWORD=abcdefgh1!'
            '  POSTGRES_PASSWORD=abcdefgh1!'
        ) {
            # All three are valid dotenv assignments Compose honors. The detection grammar used to be
            # wider than the write grammar, so a spaced assignment was routed to repair, never matched,
            # and the file was returned still rendering password= empty.
            $basePath = Join-Path $script:work ".env.simple-$([Guid]::NewGuid().ToString('N'))"
            Set-Content -LiteralPath $basePath -Value (@(
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'DMS_CONFIG_DATABASE_NAME=${POSTGRES_DB_NAME}',
                $script:seamConnectionString,
                $_,
                'DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE=false'
            ) -join "`n") -NoNewline

            $result = Resolve-CmsDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $basePath -DatabaseEngine "postgresql" -DockerComposeRoot $script:work
            $result | Should -Not -Be $basePath

            # The repaired file must RENDER correctly, not merely contain the right lines: the password
            # key has to precede the connection string that references it.
            $repaired = Resolve-DotenvFileSequentially -Path $result
            $repaired.Effective["DMS_CONFIG_DATABASE_CONNECTION_STRING"] |
                Should -BeExactly 'host=dms-postgresql;port=5432;username=postgres;password=abcdefgh1!;database=edfi_datamanagementservice;'
        }

        It "moves a literal-dollar password rather than refusing it" {
            # pa$$word is a literal pa$word that references nothing. The old guard rejected any value
            # containing '$', so a perfectly safe credential blocked the run.
            $basePath = Join-Path $script:work ".env.literal-dollar"
            Set-Content -LiteralPath $basePath -Value (@(
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'DMS_CONFIG_DATABASE_NAME=${POSTGRES_DB_NAME}',
                $script:seamConnectionString,
                'POSTGRES_PASSWORD=pa$$word',
                'DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE=false'
            ) -join "`n") -NoNewline

            $result = Resolve-CmsDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $basePath -DatabaseEngine "postgresql" -DockerComposeRoot $script:work

            (Resolve-DotenvFileSequentially -Path $result).Effective["DMS_CONFIG_DATABASE_CONNECTION_STRING"] |
                Should -BeExactly 'host=dms-postgresql;port=5432;username=postgres;password=pa$word;database=edfi_datamanagementservice;'
        }

        It "catches a datastore/CMS database disagreement the hashtable resolution used to pass" {
            # The agreement validator's whole job. With the datastore name duplicated, Compose puts CMS
            # on the first value and the datastore container on the last; the two genuinely disagree.
            $basePath = Join-Path $script:work ".env.agreement"
            Set-Content -LiteralPath $basePath -Value (@(
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'POSTGRES_PASSWORD=abcdefgh1!',
                'DMS_CONFIG_DATABASE_NAME=${POSTGRES_DB_NAME}',
                $script:seamConnectionString,
                'DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE=false',
                'POSTGRES_DB_NAME=some_other_db'
            ) -join "`n") -NoNewline

            { Confirm-CmsDatabaseTopologyAgreement -EnvironmentFile $basePath -DatabaseEngine "postgresql" } |
                Should -Throw "*topology mismatch*"
        }

        It "still early-returns the checked-in profiles untouched: <_>" -ForEach @(
            '.env.e2e', '.env.example', '.env.multitenancy', '.env.routeContext.e2e',
            '.env.smoke', '.env.smoke.ds61', '.env.template', '.env.template.ds61'
        ) {
            # The stricter model must not turn every ordinary run into a rewrite.
            $profilePath = Join-Path $script:dockerComposeRoot $_
            Resolve-CmsDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $profilePath -DatabaseEngine "postgresql" -DockerComposeRoot $script:work |
                Should -Be $profilePath
        }

        It "accepts the seam keys spelled with an export prefix, and still migrates the legacy token" {
            # The raw connection string and marker are read through the shared assignment model, not the
            # legacy parser. Sourced from ReadValuesFromEnvFile, an export-spelled connection string was
            # stored under the key "export DMS_CONFIG_..." and was therefore invisible to separate
            # mode's legacy-token migration - the run failed later instead of taking the repair path.
            $basePath = Join-Path $script:work ".env.export-seam"
            Set-Content -LiteralPath $basePath -Value (@(
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'POSTGRES_PASSWORD=abcdefgh1!',
                'export DMS_CONFIG_DATABASE_NAME=${POSTGRES_DB_NAME}',
                'export DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_PASSWORD};database=${POSTGRES_DB_NAME};'
            ) -join "`n") -NoNewline

            $result = Resolve-CmsDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $basePath -DatabaseEngine "postgresql" -SeparateConfigDatabase -DockerComposeRoot $script:work

            (Resolve-DotenvFileSequentially -Path $result).Effective["DMS_CONFIG_DATABASE_CONNECTION_STRING"] |
                Should -BeExactly 'host=dms-postgresql;port=5432;username=postgres;password=abcdefgh1!;database=edfi_configurationservice;'
        }

        It "migrates the legacy token through export, whitespace around '=', an outer quote, and a trailing comment" {
            # All four are individually valid and Compose accepts the combination. The raw value then
            # BEGINS WITH WHITESPACE, so the wrapper quote is not at index zero; detecting it only there
            # left the value unwrapped, and the connection-string scanner mistook the wrapper quote for
            # an ADO.NET value quote and found no segment to migrate.
            $basePath = Join-Path $script:work ".env.wrapped-combined"
            Set-Content -LiteralPath $basePath -Value (@(
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'POSTGRES_PASSWORD=abcdefgh1!',
                'export DMS_CONFIG_DATABASE_CONNECTION_STRING = "host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_PASSWORD};database=${POSTGRES_DB_NAME};" # cms'
            ) -join "`n") -NoNewline

            $result = Resolve-CmsDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $basePath -DatabaseEngine "postgresql" -SeparateConfigDatabase -DockerComposeRoot $script:work

            # Asserted as the EXACT line, not by wildcard: the claim is that only the legacy token span
            # changes and everything else - the raw value's leading space, the wrapper quote, and the
            # trailing comment - survives byte for byte. Wildcards would pass even if surrounding bytes
            # moved. (The `export ` prefix is dropped because Write-DerivedEnvFile rewrites the line
            # under the canonical key, which Compose treats identically.)
            $derivedLine = @([System.IO.File]::ReadAllLines($result) | Where-Object { $_ -like '*DMS_CONFIG_DATABASE_CONNECTION_STRING*' })[0]
            $derivedLine | Should -BeExactly 'DMS_CONFIG_DATABASE_CONNECTION_STRING= "host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_PASSWORD};database=${DMS_CONFIG_DATABASE_NAME};" # cms'

            # And it must RENDER against the dedicated database with the real password intact.
            (Resolve-DotenvFileSequentially -Path $result).Effective["DMS_CONFIG_DATABASE_CONNECTION_STRING"] |
                Should -BeExactly 'host=dms-postgresql;port=5432;username=postgres;password=abcdefgh1!;database=edfi_configurationservice;'
        }

        It "does not relocate a lowercase decoy in place of the real uppercase key" {
            # Movement keys off the shared assignment grammar. A case-insensitive match would move the
            # decoy and leave the real key below the connection string, still rendering empty.
            $basePath = Join-Path $script:work ".env.move-decoy"
            Set-Content -LiteralPath $basePath -Value (@(
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'DMS_CONFIG_DATABASE_NAME=${POSTGRES_DB_NAME}',
                $script:seamConnectionString,
                'postgres_password=DECOY',
                'POSTGRES_PASSWORD=abcdefgh1!'
            ) -join "`n") -NoNewline

            $result = Resolve-CmsDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $basePath -DatabaseEngine "postgresql" -DockerComposeRoot $script:work

            (Resolve-DotenvFileSequentially -Path $result).Effective["DMS_CONFIG_DATABASE_CONNECTION_STRING"] |
                Should -BeExactly 'host=dms-postgresql;port=5432;username=postgres;password=abcdefgh1!;database=edfi_datamanagementservice;'
        }

        It "does not treat a case-variant of the seam alias as the seam alias itself" {
            # Only the exact DMS_CONFIG_DATABASE_NAME identifier is healed structurally. A case-variant
            # is an ordinary dependency, so a forward-referencing one must be reported as a chain rather
            # than silently assumed to be repaired by the alias literalization.
            $basePath = Join-Path $script:work ".env.alias-case-variant"
            Set-Content -LiteralPath $basePath -Value (@(
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'POSTGRES_PASSWORD=abcdefgh1!',
                'DMS_CONFIG_DATABASE_NAME=${POSTGRES_DB_NAME}',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_PASSWORD};database=${dms_config_database_name};',
                'dms_config_database_name=${LATE_ONE}',
                'LATE_ONE=late'
            ) -join "`n") -NoNewline

            { Resolve-CmsDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $basePath -DatabaseEngine "postgresql" -DockerComposeRoot $script:work } |
                Should -Throw "*dms_config_database_name -> LATE_ONE*"
        }

        It "repairs a case-variant alias reference as an ordinary forward reference, using its own value" {
            # The connection string references a LOWERCASE variant declared after it, with a literal
            # value deliberately different from the datastore name. Two things must hold: the reference
            # is an ordinary forward reference to be moved (not the seam alias, which is instead healed
            # by literalization and would never be moved), and resolving the repair target must not let
            # that lowercase name pick up the uppercase alias's override value. Either mistake makes the
            # rendered result disagree with the target.
            $basePath = Join-Path $script:work ".env.alias-variant-forward"
            Set-Content -LiteralPath $basePath -Value (@(
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'POSTGRES_PASSWORD=abcdefgh1!',
                'DMS_CONFIG_DATABASE_NAME=${POSTGRES_DB_NAME}',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_PASSWORD};database=${dms_config_database_name};',
                'dms_config_database_name=lowercase_target_db'
            ) -join "`n") -NoNewline

            $result = Resolve-CmsDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $basePath -DatabaseEngine "postgresql" -DockerComposeRoot $script:work

            (Resolve-DotenvFileSequentially -Path $result).Effective["DMS_CONFIG_DATABASE_CONNECTION_STRING"] |
                Should -BeExactly 'host=dms-postgresql;port=5432;username=postgres;password=abcdefgh1!;database=lowercase_target_db;'
        }

        It "does not let a case-variant declaration satisfy the seam's uppercase reference" {
            # Compose is case-sensitive on the Linux CI/runtime path, so a lowercase typo leaves
            # POSTGRES_DB_NAME unset and the alias renders empty. The preflight must not accept it.
            $basePath = Join-Path $script:work ".env.case-variant"
            Set-Content -LiteralPath $basePath -Value (@(
                'postgres_db_name=edfi_datamanagementservice',
                'POSTGRES_PASSWORD=abcdefgh1!',
                'DMS_CONFIG_DATABASE_NAME=${POSTGRES_DB_NAME}',
                $script:seamConnectionString
            ) -join "`n") -NoNewline

            { Resolve-CmsDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $basePath -DatabaseEngine "postgresql" -DockerComposeRoot $script:work } |
                Should -Throw "*could not resolve a non-blank database name*"
        }
    }

    # A value the shell supplies wins over every declaration of the same name, so the file's own
    # declarations of it never contributed anything. Classification must not attribute file-authored
    # problems to a value the file did not provide, and the repair postcondition must compare against
    # what Compose will actually use.
    Context "ambient overrides during classification and repair (DMS-1270)" {
        BeforeAll {
            $script:ambientSeamConnectionString = 'DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_PASSWORD};database=${DMS_CONFIG_DATABASE_NAME};'
            $script:ambientDuplicateLines = @(
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'POSTGRES_PASSWORD=abcdefgh1!',
                'DMS_CONFIG_DATABASE_NAME=${POSTGRES_DB_NAME}',
                $script:ambientSeamConnectionString,
                'DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE=false',
                'POSTGRES_DB_NAME=some_other_db'
            )
        }

        It "rejects the duplicated datastore name when no ambient override is present" {
            $basePath = Join-Path $script:work ".env.dup-no-ambient"
            Set-Content -LiteralPath $basePath -Value ($script:ambientDuplicateLines -join "`n") -NoNewline

            { Resolve-CmsDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $basePath -DatabaseEngine "postgresql" -DockerComposeRoot $script:work } |
                Should -Throw "*declared more than once*"
        }

        It "treats the same duplicated datastore name as inert under an ambient override" {
            $basePath = Join-Path $script:work ".env.dup-ambient"
            Set-Content -LiteralPath $basePath -Value ($script:ambientDuplicateLines -join "`n") -NoNewline

            [System.Environment]::SetEnvironmentVariable("POSTGRES_DB_NAME", "ambient_db")
            try {
                { Resolve-CmsDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $basePath -DatabaseEngine "postgresql" -DockerComposeRoot $script:work } |
                    Should -Not -Throw -Because "Compose uses the ambient value, so neither file declaration contributes"
            }
            finally { Remove-Item Env:\POSTGRES_DB_NAME -ErrorAction SilentlyContinue }
        }

        It "treats a duplicated password as inert under an ambient override, but rejects it without one" {
            $lines = @(
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'POSTGRES_PASSWORD=first-pw',
                'DMS_CONFIG_DATABASE_NAME=${POSTGRES_DB_NAME}',
                $script:ambientSeamConnectionString,
                'POSTGRES_PASSWORD=second-pw'
            )
            $basePath = Join-Path $script:work ".env.pwdup"
            Set-Content -LiteralPath $basePath -Value ($lines -join "`n") -NoNewline

            { Resolve-CmsDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $basePath -DatabaseEngine "postgresql" -DockerComposeRoot $script:work } |
                Should -Throw "*declared more than once*"

            [System.Environment]::SetEnvironmentVariable("POSTGRES_PASSWORD", "ambient-pw")
            try {
                { Resolve-CmsDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $basePath -DatabaseEngine "postgresql" -DockerComposeRoot $script:work } |
                    Should -Not -Throw
            }
            finally { Remove-Item Env:\POSTGRES_PASSWORD -ErrorAction SilentlyContinue }
        }

        It "does not report a transitive chain inside a key the ambient environment supplied" {
            # The file declares POSTGRES_PASSWORD=${LATE_PW} with LATE_PW below it - a genuine chain on
            # paper. But an ambient POSTGRES_PASSWORD means Compose never evaluated that declaration, so
            # dependency traversal must stop at the ambient value instead of reporting a defect in a
            # value the file did not supply.
            $lines = @(
                'POSTGRES_PASSWORD=${LATE_PW}',
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'DMS_CONFIG_DATABASE_NAME=${POSTGRES_DB_NAME}',
                $script:ambientSeamConnectionString,
                'LATE_PW=file-pw'
            )
            $basePath = Join-Path $script:work ".env.ambient-chain"
            Set-Content -LiteralPath $basePath -Value ($lines -join "`n") -NoNewline

            { Resolve-CmsDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $basePath -DatabaseEngine "postgresql" -DockerComposeRoot $script:work } |
                Should -Throw "*POSTGRES_PASSWORD -> LATE_PW*" -Because "without an ambient override this really is a chain"

            [System.Environment]::SetEnvironmentVariable("POSTGRES_PASSWORD", "ambient-pw")
            try {
                { Resolve-CmsDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $basePath -DatabaseEngine "postgresql" -DockerComposeRoot $script:work } |
                    Should -Not -Throw -Because "traversal must stop where ambient supplied the value"
            }
            finally { Remove-Item Env:\POSTGRES_PASSWORD -ErrorAction SilentlyContinue }
        }

        It "does not pull keys reached only through an ambient-supplied value into the seam's dependencies" {
            # POSTGRES_PASSWORD is ambient, so its file value - which references LATE_PW - is never
            # evaluated. LATE_PW is duplicated, but nothing the run renders depends on it, so dependency
            # traversal must stop at the ambient value rather than reaching LATE_PW and rejecting the
            # file for a duplicate that cannot affect anything.
            $lines = @(
                'POSTGRES_PASSWORD=${LATE_PW}',
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'DMS_CONFIG_DATABASE_NAME=${POSTGRES_DB_NAME}',
                $script:ambientSeamConnectionString,
                'LATE_PW=first',
                'LATE_PW=second'
            )
            $basePath = Join-Path $script:work ".env.ambient-closure"
            Set-Content -LiteralPath $basePath -Value ($lines -join "`n") -NoNewline

            [System.Environment]::SetEnvironmentVariable("POSTGRES_PASSWORD", "ambient-pw")
            try {
                { Resolve-CmsDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $basePath -DatabaseEngine "postgresql" -DockerComposeRoot $script:work } |
                    Should -Not -Throw -Because "LATE_PW is reachable only through a value ambient replaced"
            }
            finally { Remove-Item Env:\POSTGRES_PASSWORD -ErrorAction SilentlyContinue }
        }

        It "completes a required derived write while the connection string itself is ambient-overridden" {
            # Separate mode forces a write. Compose hands the container the ambient connection string
            # verbatim, so the postcondition target must be that value; comparing against the
            # file-authored string instead reported a valid override as a repair failure.
            $basePath = Join-Path $script:work ".env.ambient-conn"
            Set-Content -LiteralPath $basePath -Value (@(
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'POSTGRES_PASSWORD=abcdefgh1!',
                'DMS_CONFIG_DATABASE_NAME=${POSTGRES_DB_NAME}',
                $script:ambientSeamConnectionString,
                'DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE=false'
            ) -join "`n") -NoNewline

            [System.Environment]::SetEnvironmentVariable(
                "DMS_CONFIG_DATABASE_CONNECTION_STRING",
                'host=dms-postgresql;port=5432;username=postgres;password=ambient-pw;database=edfi_configurationservice;')
            try {
                $result = Resolve-CmsDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $basePath -DatabaseEngine "postgresql" -SeparateConfigDatabase -DockerComposeRoot $script:work
                $result | Should -Not -Be $basePath
            }
            finally { Remove-Item Env:\DMS_CONFIG_DATABASE_CONNECTION_STRING -ErrorAction SilentlyContinue }
        }
    }

    # The resolver and the validator run back to back on the start path, so they must agree about what
    # the topology marker says. Sourcing it from the legacy parser in the validator made them disagree:
    # the resolver could early-return an already-correct separate-mode file and the validator would then
    # reject it as shared mode.
    Context "the marker is read identically by the resolver and the validator (DMS-1270)" {
        BeforeAll {
            $script:markerConnectionString = 'DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_PASSWORD};database=${DMS_CONFIG_DATABASE_NAME};'
        }

        It "early-returns an already-correct separate file whose marker is spelled '<_>', then passes validation" -ForEach @(
            'DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE=true'
            'export DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE = "true"'
            '  DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE="true" # topology'
            "export DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE = 'true'"
        ) {
            $basePath = Join-Path $script:work ".env.marker-$([Guid]::NewGuid().ToString('N'))"
            Set-Content -LiteralPath $basePath -Value (@(
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'POSTGRES_PASSWORD=abcdefgh1!',
                $_,
                'DMS_CONFIG_DATABASE_NAME=edfi_configurationservice',
                $script:markerConnectionString
            ) -join "`n") -NoNewline

            # Nothing needs changing, so the source file is handed straight to Compose ...
            Resolve-CmsDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $basePath -DatabaseEngine "postgresql" -SeparateConfigDatabase -DockerComposeRoot $script:work |
                Should -Be $basePath

            # ... and the validator must read the same marker from that same file.
            { Confirm-CmsDatabaseTopologyAgreement -EnvironmentFile $basePath -DatabaseEngine "postgresql" } |
                Should -Not -Throw
        }

        It "treats a case-variant marker value as shared mode rather than a topology declaration" {
            # The marker is written by this design as exactly "true" or "false". A hand-edited case
            # variant is not a declaration, so it must not silently redirect CMS to the dedicated
            # database; here shared mode is validated and the dedicated target is correctly rejected.
            $basePath = Join-Path $script:work ".env.marker-case"
            Set-Content -LiteralPath $basePath -Value (@(
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'POSTGRES_PASSWORD=abcdefgh1!',
                'DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE=TRUE',
                'DMS_CONFIG_DATABASE_NAME=edfi_configurationservice',
                $script:markerConnectionString
            ) -join "`n") -NoNewline

            { Confirm-CmsDatabaseTopologyAgreement -EnvironmentFile $basePath -DatabaseEngine "postgresql" } |
                Should -Throw "*topology mismatch*"
        }

        It "ignores an ambient marker value entirely" {
            # A stray shell variable of the marker's name must not decide which mode is validated. The
            # file below is shared mode, so an ambient "true" must not make the validator expect the
            # dedicated database.
            $basePath = Join-Path $script:work ".env.marker-ambient"
            Set-Content -LiteralPath $basePath -Value (@(
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'POSTGRES_PASSWORD=abcdefgh1!',
                'DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE=false',
                'DMS_CONFIG_DATABASE_NAME=${POSTGRES_DB_NAME}',
                $script:markerConnectionString
            ) -join "`n") -NoNewline

            [System.Environment]::SetEnvironmentVariable("DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE", "true")
            try {
                { Confirm-CmsDatabaseTopologyAgreement -EnvironmentFile $basePath -DatabaseEngine "postgresql" } |
                    Should -Not -Throw -Because "the marker is read from the file's own declaration, never from the environment"
            }
            finally { Remove-Item Env:\DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE -ErrorAction SilentlyContinue }
        }
    }

    Context "repair-failure diagnostics withhold credentials (DMS-1270)" {
        It "names the differing connection-string segments without rendering any value" {
            # A repair-failure message reaches terminals and CI logs, and both engines' connection
            # strings carry a password. The diagnostic must be actionable without disclosing anything.
            $report = Get-ConnectionStringSegmentDifference `
                -Expected 'host=h;port=1;username=u;password=SUPERSECRET1;database=want_db;' `
                -Actual 'host=h;port=1;username=u;password=;database=got_db;'

            $report | Should -BeLike "*password*"
            $report | Should -BeLike "*database*"
            $report | Should -Not -BeLike "*SUPERSECRET1*"
            $report | Should -Not -BeLike "*want_db*"
            $report | Should -Not -BeLike "*got_db*"
        }

        It "reports no difference for equivalent connection strings" {
            Get-ConnectionStringSegmentDifference `
                -Expected 'host=h;database=d;password=p;' `
                -Actual 'host=h;database=d;password=p;' |
                Should -BeExactly '(none identified)'
        }
    }

    Context "separate mode" {
        It "sets DMS_CONFIG_DATABASE_NAME to the fixed edfi_configurationservice literal" {
            $basePath = Join-Path $script:work ".env.base"
            Set-Content -LiteralPath $basePath -Value (@(
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'POSTGRES_PASSWORD=abcdefgh1!',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_PASSWORD};database=${POSTGRES_DB_NAME};'
            ) -join "`n") -NoNewline

            $result = Resolve-CmsDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $basePath -DatabaseEngine "postgresql" -SeparateConfigDatabase -DockerComposeRoot $script:work

            $values = ReadValuesFromEnvFile $result
            $values["DMS_CONFIG_DATABASE_NAME"] | Should -Be "edfi_configurationservice"
            $values["DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE"] | Should -Be "true"
        }

        It "migrates a legacy connection string whose raw text is exactly the datastore-name token" {
            $basePath = Join-Path $script:work ".env.base"
            Set-Content -LiteralPath $basePath -Value (@(
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'POSTGRES_PASSWORD=abcdefgh1!',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_PASSWORD};database=${POSTGRES_DB_NAME};'
            ) -join "`n") -NoNewline

            $result = Resolve-CmsDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $basePath -DatabaseEngine "postgresql" -SeparateConfigDatabase -DockerComposeRoot $script:work

            $values = ReadValuesFromEnvFile $result
            $values["DMS_CONFIG_DATABASE_CONNECTION_STRING"] | Should -Be 'host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_PASSWORD};database=${DMS_CONFIG_DATABASE_NAME};'
        }

        It "migrates the MSSQL legacy token (not the PostgreSQL one) for an mssql base file" {
            $basePath = Join-Path $script:work ".env.base"
            Set-Content -LiteralPath $basePath -Value (@(
                'MSSQL_DB_NAME=edfi_datamanagementservice',
                'MSSQL_SA_PASSWORD=abcdefgh1!',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=Server=dms-mssql,1433;Database=${MSSQL_DB_NAME};User Id=sa;Password=${MSSQL_SA_PASSWORD};TrustServerCertificate=true;'
            ) -join "`n") -NoNewline

            $result = Resolve-CmsDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $basePath -DatabaseEngine "mssql" -SeparateConfigDatabase -DockerComposeRoot $script:work

            $values = ReadValuesFromEnvFile $result
            $values["DMS_CONFIG_DATABASE_CONNECTION_STRING"] | Should -Be 'Server=dms-mssql,1433;Database=${DMS_CONFIG_DATABASE_NAME};User Id=sa;Password=${MSSQL_SA_PASSWORD};TrustServerCertificate=true;'
        }

        It "guarantees DMS_CONFIG_DATABASE_NAME precedes DMS_CONFIG_DATABASE_CONNECTION_STRING in the derived file" {
            # Empirically confirmed against a real Docker Compose invocation (DMS-1270 Phase 1a Round 9
            # spike): --env-file interpolation is order-dependent, like shell `source` semantics - a
            # ${VAR} reference resolves only against variables defined EARLIER in the same file. A
            # forward reference (the referenced key's own definition appears later) resolves to empty.
            # Write-DerivedEnvFile appends a genuinely new key after whatever the base file already
            # contains, so introducing DMS_CONFIG_DATABASE_NAME into a base file that already defines
            # DMS_CONFIG_DATABASE_CONNECTION_STRING - exactly today's checked-in templates' shape -
            # would otherwise leave the migrated ${DMS_CONFIG_DATABASE_NAME} reference resolving to
            # empty at real Compose render time. See Move-EnvFileKeyBeforeAnotherKey.
            $basePath = Join-Path $script:work ".env.base"
            Set-Content -LiteralPath $basePath -Value (@(
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'POSTGRES_PASSWORD=abcdefgh1!',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_PASSWORD};database=${POSTGRES_DB_NAME};'
            ) -join "`n") -NoNewline

            $result = Resolve-CmsDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $basePath -DatabaseEngine "postgresql" -SeparateConfigDatabase -DockerComposeRoot $script:work

            $lines = Get-Content -LiteralPath $result
            $nameIndex = -1
            $connectionStringIndex = -1
            for ($i = 0; $i -lt $lines.Count; $i++) {
                if ($nameIndex -lt 0 -and $lines[$i] -match '^DMS_CONFIG_DATABASE_NAME=') { $nameIndex = $i }
                if ($connectionStringIndex -lt 0 -and $lines[$i] -match '^DMS_CONFIG_DATABASE_CONNECTION_STRING=') { $connectionStringIndex = $i }
            }

            $nameIndex | Should -BeGreaterThan -1
            $connectionStringIndex | Should -BeGreaterThan -1
            $nameIndex | Should -BeLessThan $connectionStringIndex
        }

        It "migrates the token inside an outer double-quoted dotenv connection string, preserving the outer quotes" {
            # Round 10 Blocker 1: Get-EnvValue returns the raw dotenv value verbatim, including any
            # outer dotenv-level quote wrapper. Without stripping it first, the scanner mistook the
            # wrapper's opening quote for an ADO.NET value-quote, swallowing every real ';' inside as
            # "quoted" and finding no segments at all - the token was left unmigrated.
            $basePath = Join-Path $script:work ".env.base"
            Set-Content -LiteralPath $basePath -Value (@(
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'POSTGRES_PASSWORD=abcdefgh1!',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING="host=dms-postgresql;database=${POSTGRES_DB_NAME};"'
            ) -join "`n") -NoNewline

            $result = Resolve-CmsDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $basePath -DatabaseEngine "postgresql" -SeparateConfigDatabase -DockerComposeRoot $script:work

            $values = ReadValuesFromEnvFile $result
            $values["DMS_CONFIG_DATABASE_CONNECTION_STRING"] | Should -Be '"host=dms-postgresql;database=${DMS_CONFIG_DATABASE_NAME};"' -Because "only the token changes; the outer double quotes are preserved exactly as authored"
        }

        It "migrates the token inside an outer single-quoted dotenv connection string, preserving the outer quotes" {
            $basePath = Join-Path $script:work ".env.base"
            Set-Content -LiteralPath $basePath -Value (@(
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'POSTGRES_PASSWORD=abcdefgh1!',
                "DMS_CONFIG_DATABASE_CONNECTION_STRING='host=dms-postgresql;database=`${POSTGRES_DB_NAME};'"
            ) -join "`n") -NoNewline

            $result = Resolve-CmsDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $basePath -DatabaseEngine "postgresql" -SeparateConfigDatabase -DockerComposeRoot $script:work

            $values = ReadValuesFromEnvFile $result
            $values["DMS_CONFIG_DATABASE_CONNECTION_STRING"] | Should -Be "'host=dms-postgresql;database=`${DMS_CONFIG_DATABASE_NAME};'"
        }

        It "migrates the token inside an outer double-quoted dotenv value carrying a trailing inline comment, preserving both" {
            # Round 11 Blocker 2: Get-EnvValue returns the raw dotenv value verbatim, including a
            # trailing inline comment after the closing quote. The prior "last character equals the
            # opening quote" check mistook the comment's own trailing character for proof the value
            # was not quoted at all, so the wrapper went undetected and the token stayed unmigrated.
            $basePath = Join-Path $script:work ".env.base"
            Set-Content -LiteralPath $basePath -Value (@(
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'POSTGRES_PASSWORD=abcdefgh1!',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING="host=dms-postgresql;database=${POSTGRES_DB_NAME};" # keep'
            ) -join "`n") -NoNewline

            $result = Resolve-CmsDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $basePath -DatabaseEngine "postgresql" -SeparateConfigDatabase -DockerComposeRoot $script:work

            $values = ReadValuesFromEnvFile $result
            $values["DMS_CONFIG_DATABASE_CONNECTION_STRING"] | Should -Be '"host=dms-postgresql;database=${DMS_CONFIG_DATABASE_NAME};" # keep' -Because "the outer quotes and the trailing comment are both preserved byte-for-byte; only the inner token changes"

            { Confirm-CmsDatabaseTopologyAgreement -EnvironmentFile $result -DatabaseEngine "postgresql" } | Should -Not -Throw -Because "the migrated file must validate cleanly end to end through the validator"
        }

        It "migrates the token when the connection string contains regex replacement-directive sequences elsewhere (`$&, `$0)" {
            # Round 11 Blocker 1: Write-DerivedEnvFile's underlying Regex.Replace call previously
            # treated the replacement string as a REPLACEMENT PATTERN, so a literal '$&' or '$0'
            # anywhere in the caller-authored value (a password, for instance) was corrupted -
            # duplicating the entire matched line into the middle of the value - rather than written
            # verbatim.
            $basePath = Join-Path $script:work ".env.base"
            Set-Content -LiteralPath $basePath -Value (@(
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'POSTGRES_PASSWORD=abcdefgh1!',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;password=p$&q$0r;database=${POSTGRES_DB_NAME};'
            ) -join "`n") -NoNewline

            $result = Resolve-CmsDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $basePath -DatabaseEngine "postgresql" -SeparateConfigDatabase -DockerComposeRoot $script:work

            $values = ReadValuesFromEnvFile $result
            $values["DMS_CONFIG_DATABASE_CONNECTION_STRING"] | Should -Be 'host=dms-postgresql;password=p$&q$0r;database=${DMS_CONFIG_DATABASE_NAME};' -Because "the password must survive verbatim; only the database segment's token changes"
        }

        It "does NOT rewrite the legacy token when it appears outside the database segment" {
            # Round 8 Blocker 2: a blind Contains/Replace across the whole connection string could
            # rewrite the token inside an unrelated segment (here, the password) that merely happens
            # to carry the identical literal text. Only the database-segment's own value is a
            # migration signature.
            $basePath = Join-Path $script:work ".env.base"
            Set-Content -LiteralPath $basePath -Value (@(
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'POSTGRES_PASSWORD=abcdefgh1!',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_DB_NAME};database=custom;'
            ) -join "`n") -NoNewline

            $result = Resolve-CmsDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $basePath -DatabaseEngine "postgresql" -SeparateConfigDatabase -DockerComposeRoot $script:work

            $values = ReadValuesFromEnvFile $result
            $values["DMS_CONFIG_DATABASE_CONNECTION_STRING"] | Should -Be 'host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_DB_NAME};database=custom;' -Because "the token is not in a recognized database-name key's value, so it must be left untouched"
        }

        It "does NOT rewrite the legacy token when it appears inside a quoted, unrelated segment" {
            # Round 9 Blocker 1: a plain regex lookbehind/lookahead has no concept of quoting, so a
            # ';' inside a quoted password value was mistaken for a real segment boundary, letting the
            # token text embedded in that unrelated quoted value be matched and rewritten.
            $basePath = Join-Path $script:work ".env.base"
            Set-Content -LiteralPath $basePath -Value (@(
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'POSTGRES_PASSWORD=abcdefgh1!',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=Password="keep;Database=${POSTGRES_DB_NAME};inside-password";Database=custom;'
            ) -join "`n") -NoNewline

            $result = Resolve-CmsDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $basePath -DatabaseEngine "postgresql" -SeparateConfigDatabase -DockerComposeRoot $script:work

            $values = ReadValuesFromEnvFile $result
            $values["DMS_CONFIG_DATABASE_CONNECTION_STRING"] | Should -Be 'Password="keep;Database=${POSTGRES_DB_NAME};inside-password";Database=custom;' -Because "the token is inside a quoted password value, not a real Database= segment, and the real Database=custom segment does not match the legacy token either"
        }

        It "does NOT rewrite a genuinely custom reference that currently resolves to the same value as the datastore name" {
            # Round 7 Blocker 6: matching on the *resolved* value (rather than the exact raw
            # token) could silently rewrite a caller's own, unrelated ${CUSTOM_DATABASE}
            # reference merely because it happens to currently equal the datastore name.
            $basePath = Join-Path $script:work ".env.base"
            Set-Content -LiteralPath $basePath -Value (@(
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'CUSTOM_DATABASE=edfi_datamanagementservice',
                'POSTGRES_PASSWORD=abcdefgh1!',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_PASSWORD};database=${CUSTOM_DATABASE};'
            ) -join "`n") -NoNewline

            $result = Resolve-CmsDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $basePath -DatabaseEngine "postgresql" -SeparateConfigDatabase -DockerComposeRoot $script:work

            $values = ReadValuesFromEnvFile $result
            $values["DMS_CONFIG_DATABASE_CONNECTION_STRING"] | Should -Be 'host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_PASSWORD};database=${CUSTOM_DATABASE};' -Because "only the exact legacy token is a migration signature, never a resolved-value coincidence"
        }

        It "never wraps the migrated connection string in single quotes, which would freeze the new reference as a literal" {
            $basePath = Join-Path $script:work ".env.base"
            Set-Content -LiteralPath $basePath -Value (@(
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'POSTGRES_PASSWORD=abcdefgh1!',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_PASSWORD};database=${POSTGRES_DB_NAME};'
            ) -join "`n") -NoNewline

            $result = Resolve-CmsDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $basePath -DatabaseEngine "postgresql" -SeparateConfigDatabase -DockerComposeRoot $script:work

            (Get-Content -LiteralPath $result -Raw) | Should -Not -Match "DMS_CONFIG_DATABASE_CONNECTION_STRING='"
        }
    }

    Context "idempotency and mode transitions" {
        It "returns the same derived path on a repeated separate-mode call (no growth, no re-derivation)" {
            $basePath = Join-Path $script:work ".env.base"
            Set-Content -LiteralPath $basePath -Value (@(
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'POSTGRES_PASSWORD=abcdefgh1!',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_PASSWORD};database=${POSTGRES_DB_NAME};'
            ) -join "`n") -NoNewline

            $first = Resolve-CmsDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $basePath -DatabaseEngine "postgresql" -SeparateConfigDatabase -DockerComposeRoot $script:work
            $second = Resolve-CmsDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $first -DatabaseEngine "postgresql" -SeparateConfigDatabase -DockerComposeRoot $script:work

            $second | Should -Be $first
            $second | Should -Not -Match '\.topology\.topology'
        }

        It "reverts DMS_CONFIG_DATABASE_NAME to the shared alias when a later call omits the switch (shared -> separate -> shared)" {
            $basePath = Join-Path $script:work ".env.base"
            Set-Content -LiteralPath $basePath -Value (@(
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'POSTGRES_PASSWORD=abcdefgh1!',
                'DMS_CONFIG_DATABASE_NAME=${POSTGRES_DB_NAME}',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_PASSWORD};database=${DMS_CONFIG_DATABASE_NAME};'
            ) -join "`n") -NoNewline

            $separate = Resolve-CmsDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $basePath -DatabaseEngine "postgresql" -SeparateConfigDatabase -DockerComposeRoot $script:work
            (ReadValuesFromEnvFile $separate)["DMS_CONFIG_DATABASE_NAME"] | Should -Be "edfi_configurationservice"

            $revertedToShared = Resolve-CmsDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $separate -DatabaseEngine "postgresql" -DockerComposeRoot $script:work
            $revertedToShared | Should -Be $separate -Because "the same deterministic derived path is reused, not a new one"
            (ReadValuesFromEnvFile $revertedToShared)["DMS_CONFIG_DATABASE_NAME"] | Should -Be "edfi_datamanagementservice"
            (ReadValuesFromEnvFile $revertedToShared)["DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE"] | Should -Be "false"
        }

        It "preserves a datastore name containing a literal '$' intact across a shared -> separate -> shared transition" {
            # Round 10 Blocker 2: an unquoted written value like tenant$db, once re-read (by Compose or
            # by Get-ComposeResolvedEnvValue), has $db misinterpreted as a reference to an unset "db"
            # variable and silently collapses to just "tenant". ConvertTo-DotenvSafeEnvValue must quote
            # any concrete value containing '$'.
            $basePath = Join-Path $script:work ".env.base"
            Set-Content -LiteralPath $basePath -Value (@(
                "POSTGRES_DB_NAME='tenant`$db'",
                'POSTGRES_PASSWORD=abcdefgh1!',
                'DMS_CONFIG_DATABASE_NAME=${POSTGRES_DB_NAME}',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_PASSWORD};database=${DMS_CONFIG_DATABASE_NAME};'
            ) -join "`n") -NoNewline

            $separate = Resolve-CmsDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $basePath -DatabaseEngine "postgresql" -SeparateConfigDatabase -DockerComposeRoot $script:work
            $revertedToShared = Resolve-CmsDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $separate -DatabaseEngine "postgresql" -DockerComposeRoot $script:work

            $revertedValues = ReadValuesFromEnvFile $revertedToShared
            $revertedValues["DMS_CONFIG_DATABASE_NAME"] | Should -Be "'tenant`$db'" -Because "the written value itself must be quoted"
            (Get-ComposeResolvedEnvValue -EnvironmentValues $revertedValues -Name "DMS_CONFIG_DATABASE_NAME") | Should -Be 'tenant$db' -Because "re-reading it must not lose the literal `$db suffix to interpolation"
        }
    }
}

Describe "Get-DotenvClosingQuoteIndex" {
    BeforeAll {
        $script:dockerComposeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
        Import-Module (Join-Path $script:dockerComposeRoot "env-utility.psm1") -Force
    }

    It "returns -1 for a value with no outer quote wrapper" {
        Get-DotenvClosingQuoteIndex -RawValue "host=dms-postgresql;database=x;" | Should -Be -1
    }

    It "finds the closing quote for a simple double-quoted value" {
        $value = '"host=dms-postgresql;database=x;"'
        Get-DotenvClosingQuoteIndex -RawValue $value | Should -Be ($value.Length - 1)
    }

    It "finds the closing quote when a trailing inline comment follows it" {
        # Round 11 Blocker 2.
        $value = '"host=dms-postgresql;database=x;" # keep'
        $expectedIndex = '"host=dms-postgresql;database=x;"'.Length - 1
        Get-DotenvClosingQuoteIndex -RawValue $value | Should -Be $expectedIndex
    }

    It "returns -1 when trailing content after a candidate closing quote is neither empty nor a comment" {
        Get-DotenvClosingQuoteIndex -RawValue '"host=dms-postgresql" ;database=x;' | Should -Be -1
    }

    It "does not treat a backslash-escaped quote as the closing quote" {
        $value = '"host=dms-postgresql;pwd=\"escaped\";database=x;"'
        Get-DotenvClosingQuoteIndex -RawValue $value | Should -Be ($value.Length - 1)
    }
}

Describe "ConvertTo-DotenvSafeEnvValue" {
    BeforeAll {
        $script:dockerComposeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
        Import-Module (Join-Path $script:dockerComposeRoot "env-utility.psm1") -Force
    }

    It "leaves an ordinary alphanumeric value bare" {
        ConvertTo-DotenvSafeEnvValue -Value "edfi_datamanagementservice" | Should -Be "edfi_datamanagementservice"
    }

    It "leaves the bare marker values 'true'/'false' unquoted" {
        ConvertTo-DotenvSafeEnvValue -Value "true" | Should -Be "true"
        ConvertTo-DotenvSafeEnvValue -Value "false" | Should -Be "false"
    }

    It "single-quotes a value containing a space" {
        ConvertTo-DotenvSafeEnvValue -Value "has space" | Should -Be "'has space'"
    }

    It "single-quotes a value containing a '#'" {
        ConvertTo-DotenvSafeEnvValue -Value "value#tag" | Should -Be "'value#tag'"
    }

    It "single-quotes a value containing a '`$'" {
        # Round 10 Blocker 2: Resolve-ComposeEnvReference matches a bare `$NAME (no braces required),
        # so an unquoted value like tenant`$db would have `$db misread as a reference and collapse to
        # "tenant" once re-read. Single-quoting suppresses interpolation entirely.
        ConvertTo-DotenvSafeEnvValue -Value 'tenant$db' | Should -Be "'tenant`$db'"
    }

    It "single-quotes a value opening with a quote character" {
        ConvertTo-DotenvSafeEnvValue -Value "'already-quoted" | Should -Be "'\'already-quoted'"
    }

    It "backslash-escapes an embedded apostrophe, never doubling it" {
        ConvertTo-DotenvSafeEnvValue -Value "value with a ' apostrophe" | Should -Be "'value with a \' apostrophe'"
    }
}

Describe "reserved-CMS-database collision authorities (one per physical creation path)" {
    # No ambient snapshot/clear/restore inventory here, deliberately and not by omission: both
    # predicates are pure functions of their two arguments and read no environment variable, no file,
    # and no Docker. The env-consuming call sites are covered under Confirm-CmsDatabaseTopologyAgreement
    # and the start-script wiring Describe, each of which carries its own presence-aware inventory.
    BeforeAll {
        $script:dockerComposeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
        Import-Module (Join-Path $script:dockerComposeRoot "env-utility.psm1") -Force
    }

    Context "the API cannot be called without naming a creation path and an engine" {
        It "<_> requires -DatabaseEngine" -ForEach @(
            'Test-InitializedDatastoreNameCollidesWithReservedCmsDatabase',
            'Test-RegisteredDatastoreNameCollidesWithReservedCmsDatabase'
        ) {
            $parameter = (Get-Command $_).Parameters['DatabaseEngine']
            $parameter | Should -Not -BeNullOrEmpty
            @($parameter.Attributes |
                Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] } |
                ForEach-Object { $_.Mandatory }) | Should -Contain $true -Because "an omitted engine would silently pick one creation mechanism's answer for the other"
            @($parameter.Attributes |
                Where-Object { $_ -is [System.Management.Automation.ValidateSetAttribute] } |
                ForEach-Object { $_.ValidValues }) | Should -Be @('postgresql', 'mssql')
        }

        It "leaves no engine-neutral collision predicate for a call site to pick up again" {
            # The engine-neutral helper is what let one call site answer for a creation mechanism it
            # could not see. Any future sibling has to declare the engine too.
            $predicates = @(Get-Command -Module env-utility -Name 'Test-*CollidesWithReservedCmsDatabase')
            $predicates.Count | Should -BeGreaterThan 0 -Because "the sweep must actually be looking at something"
            foreach ($predicate in $predicates) {
                $predicate.Parameters.ContainsKey('DatabaseEngine') |
                    Should -BeTrue -Because "$($predicate.Name) would otherwise answer for a creation path it cannot know"
            }
        }
    }

    Context "initialized path: the database the local initialization path creates from the datastore-name key" {
        It "postgresql treats <Label> as the reserved CMS database" -ForEach @(
            @{ Label = 'the reserved name itself'; Name = 'edfi_configurationservice' }
            @{ Label = 'a case variant'; Name = 'EDFI_ConfigurationService' }
            @{ Label = 'a trailing space'; Name = 'EDFI_ConfigurationService ' }
            @{ Label = 'a leading space'; Name = ' edfi_configurationservice' }
            @{ Label = 'a trailing tab'; Name = "edfi_configurationservice`t" }
            @{ Label = 'a trailing line feed'; Name = "edfi_configurationservice`n" }
            @{ Label = 'a trailing carriage return'; Name = "edfi_configurationservice`r" }
            @{ Label = 'a trailing form feed'; Name = "edfi_configurationservice$([char]0x0C)" }
            @{ Label = 'padding on both ends'; Name = "  EDFI_ConfigurationService`t" }
        ) {
            # Measured against postgres:16 by running postgresql-init.sh's own statement form,
            # `CREATE DATABASE ${POSTGRES_DB_NAME};`: every one of these lands in pg_database as
            # edfi_configurationservice, because the lexer discards the surrounding whitespace and
            # folds the unquoted identifier.
            Test-InitializedDatastoreNameCollidesWithReservedCmsDatabase -DatabaseEngine postgresql -DatastoreDatabaseName $Name |
                Should -BeTrue
        }

        It "postgresql does NOT treat <Label> as the reserved CMS database" -ForEach @(
            @{ Label = 'a trailing vertical tab (0x0B), which makes the CREATE fail outright'; Name = "edfi_configurationservice$([char]0x0B)" }
            @{ Label = 'a trailing no-break space (0xA0), which creates a genuinely different database'; Name = "edfi_configurationservice$([char]0x00A0)" }
            @{ Label = 'an unrelated datastore name'; Name = 'edfi_datamanagementservice' }
            @{ Label = 'a blank name, which callers report as absence instead'; Name = '' }
        ) {
            # The first two are exactly why the trim set is passed to Trim explicitly: .NET counts both
            # 0x0B and 0xA0 as whitespace, so String.Trim() with no argument would report a collision
            # PostgreSQL itself does not produce - a false positive traded for the fixed false negative.
            Test-InitializedDatastoreNameCollidesWithReservedCmsDatabase -DatabaseEngine postgresql -DatastoreDatabaseName $Name |
                Should -BeFalse
        }

        It "mssql treats <Label> as the reserved CMS database, under its default collation" -ForEach @(
            @{ Label = 'the reserved name itself'; Name = 'edfi_configurationservice' }
            @{ Label = 'a case variant'; Name = 'EDFI_ConfigurationService' }
        ) {
            Test-InitializedDatastoreNameCollidesWithReservedCmsDatabase -DatabaseEngine mssql -DatastoreDatabaseName $Name |
                Should -BeTrue
        }

        It "mssql accepts an unrelated datastore name" {
            Test-InitializedDatastoreNameCollidesWithReservedCmsDatabase -DatabaseEngine mssql -DatastoreDatabaseName 'edfi_datamanagementservice' |
                Should -BeFalse
        }
    }

    Context "registered path: the database value the provider receives after serialization and parsing" {
        It "postgresql treats the reserved name itself as a collision" {
            Test-RegisteredDatastoreNameCollidesWithReservedCmsDatabase -DatabaseEngine postgresql -DatastoreDatabaseName 'edfi_configurationservice' |
                Should -BeTrue
        }

        It "postgresql accepts <Label>, which reaches the provider as a different database" -ForEach @(
            @{ Label = 'a case variant'; Name = 'EDFI_ConfigurationService' }
            @{ Label = 'an upper-cased name'; Name = 'EDFI_CONFIGURATIONSERVICE' }
            @{ Label = 'a trailing space'; Name = 'edfi_configurationservice ' }
            @{ Label = 'a leading space'; Name = ' edfi_configurationservice' }
            @{ Label = 'an unrelated name'; Name = 'edfi_datamanagementservice' }
            @{ Label = 'a blank name, meaning the datastore name was not overridden'; Name = '' }
        ) {
            # Two measured facts back this, and the whitespace rows depend on BOTH. (1) The registered
            # connection string is serialized with escaping, so surrounding whitespace survives parsing
            # instead of being discarded - see the transport Describe, which fails if that regresses.
            # (2) SchemaTools then creates the database with a QUOTED identifier
            # (PgsqlDatabaseProvisioner emits `CREATE DATABASE "<name>"`), so nothing folds:
            # edfi_configurationservice and EDFI_ConfigurationService were observed coexisting in
            # pg_database. Borrowing the unquoted initialized path's case-insensitivity here would
            # refuse a working configuration.
            Test-RegisteredDatastoreNameCollidesWithReservedCmsDatabase -DatabaseEngine postgresql -DatastoreDatabaseName $Name |
                Should -BeFalse
        }

        It "mssql treats <Label> as a collision" -ForEach @(
            @{ Label = 'the reserved name itself'; Name = 'edfi_configurationservice' }
            @{ Label = 'a case variant'; Name = 'EDFI_ConfigurationService' }
        ) {
            Test-RegisteredDatastoreNameCollidesWithReservedCmsDatabase -DatabaseEngine mssql -DatastoreDatabaseName $Name |
                Should -BeTrue
        }

        It "mssql accepts an unrelated name" {
            Test-RegisteredDatastoreNameCollidesWithReservedCmsDatabase -DatabaseEngine mssql -DatastoreDatabaseName 'edfi_datamanagementservice' |
                Should -BeFalse
        }
    }

    Context "the two authorities differ exactly where the creation mechanisms differ" {
        It "a PostgreSQL case variant collides on the initialized path and not on the registered path" {
            # One predicate could not express this, and that is the whole defect it produced: it
            # accepted a colliding POSTGRES_DB_NAME and rejected a distinct -DataStoreDatabaseName.
            Test-InitializedDatastoreNameCollidesWithReservedCmsDatabase -DatabaseEngine postgresql -DatastoreDatabaseName 'EDFI_ConfigurationService' |
                Should -BeTrue
            Test-RegisteredDatastoreNameCollidesWithReservedCmsDatabase -DatabaseEngine postgresql -DatastoreDatabaseName 'EDFI_ConfigurationService' |
                Should -BeFalse
        }

        It "on MSSQL the two paths agree, because one collation rule governs both" {
            Test-InitializedDatastoreNameCollidesWithReservedCmsDatabase -DatabaseEngine mssql -DatastoreDatabaseName 'EDFI_ConfigurationService' |
                Should -BeTrue
            Test-RegisteredDatastoreNameCollidesWithReservedCmsDatabase -DatabaseEngine mssql -DatastoreDatabaseName 'EDFI_ConfigurationService' |
                Should -BeTrue
        }
    }
}

Describe "the registered datastore database name survives the transport that carries it" {
    # The registered name does not reach the server as text. Add-DataStore serializes it into a
    # connection string and SchemaTools PARSES that string back before quoting the identifier, so the
    # only claim worth asserting is about the parsed value. Asserting that the name appears somewhere
    # inside an unparsed connection string is what previously let a trailing space look preserved while
    # the parser discarded it.
    #
    # Ambient-free and offline: every value is a parameter and no test here opens a socket, reads the
    # environment, or invokes Docker. Dms-Management shares no function name with env-utility, so
    # importing it cannot shadow anything the later Describes in this file rely on.
    BeforeAll {
        $script:engRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
        $script:dockerComposeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
        Import-Module (Join-Path $script:engRoot "Dms-Management.psm1") -Force
        Import-Module (Join-Path $script:dockerComposeRoot "env-utility.psm1") -Force

        # Parses a connection string the way a provider does. DbConnectionStringBuilder is the ADO.NET
        # base class NpgsqlConnectionStringBuilder derives from, so it applies the same quoting rules;
        # measured across whitespace, both quote characters, ';', '=', newline and '${...}', the two
        # return identical values.
        function script:Get-ParsedDatabaseValue {
            param([Parameter(Mandatory)] [string]$ConnectionString)

            $reader = [System.Data.Common.DbConnectionStringBuilder]::new()
            # .psbase is required: PowerShell's IDictionary adapter would store a literal
            # "ConnectionString" KEY instead of invoking the property setter, leaving nothing parsed and
            # every lookup silently missing.
            $reader.psbase.ConnectionString = $ConnectionString
            if (-not $reader.ContainsKey("database")) { return $null }
            return [string]$reader["database"]
        }
    }

    Context "Add-DataStore builds the registered string with the escaping serializer, not interpolation" {
        It "constructs its PostgreSQL connection string through New-DataStoreConnectionString" {
            # The load-bearing wiring assertion. Interpolation cannot escape a value, so reverting this
            # single call re-opens the whole class of defect: the guard judges the parameter text while
            # the provider consumes something else. Asserted on the parsed AST of the real function so
            # a comment or an unrelated mention cannot satisfy it.
            $modulePath = Join-Path $script:engRoot "Dms-Management.psm1"
            $ast = [System.Management.Automation.Language.Parser]::ParseFile($modulePath, [ref]$null, [ref]$null)
            $function = $ast.FindAll({
                param($node)
                $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Add-DataStore'
            }, $true) | Select-Object -First 1
            $function | Should -Not -BeNullOrEmpty -Because "the sweep must actually be looking at Add-DataStore"

            $factoryCalls = @($function.FindAll({
                param($node)
                $node -is [System.Management.Automation.Language.CommandAst] -and
                $node.GetCommandName() -eq 'New-DataStoreConnectionString'
            }, $true))
            $factoryCalls.Count | Should -BeGreaterThan 0 -Because "the registered string must be built by the escaping serializer"

            # And no interpolated connection-string literal survives anywhere in the function.
            $interpolated = @($function.FindAll({
                param($node)
                $node -is [System.Management.Automation.Language.ExpandableStringExpressionAst] -and
                $node.Value -match 'database='
            }, $true))
            $interpolated | Should -BeNullOrEmpty -Because "an interpolated database= segment cannot escape its value"
        }

        It "names the missing password instead of failing on a parameter it was never handed" {
            # ConvertTo-PostgresCredential accepts an empty secret by design, and the escaping serializer
            # rejects one - so the empty case has to be reported as the missing environment value it is.
            # Interpolation used to register `password=` here, which only failed later at connect time.
            $emptyCredential = ConvertTo-PostgresCredential -UserName 'postgres' -Secret ''

            { Add-DataStore -CmsUrl 'http://localhost:8081' -AccessToken 'token' `
                    -PostgresCredential $emptyCredential -PostgresDbName 'edfi_datamanagementservice' } |
                Should -Throw "*empty password*POSTGRES_PASSWORD*"
        }
    }

    Context "the serialized-then-parsed database value equals the registered name" {
        It "round-trips <Label> unchanged" -ForEach @(
            @{ Label = 'a plain name'; Name = 'edfi_datamanagementservice' }
            @{ Label = 'a mixed-case name'; Name = 'EDFI_ConfigurationService' }
            @{ Label = 'a trailing space'; Name = 'edfi_configurationservice ' }
            @{ Label = 'two trailing spaces'; Name = 'edfi_configurationservice  ' }
            @{ Label = 'a leading space'; Name = ' edfi_configurationservice' }
            @{ Label = 'a trailing tab'; Name = "edfi_configurationservice`t" }
            @{ Label = 'an embedded semicolon that would otherwise start a new segment'; Name = 'edfi_dms;Database=edfi_configurationservice' }
            @{ Label = 'a double quote'; Name = 'edfi_dms"x' }
            @{ Label = 'a single quote'; Name = "edfi_dms'x" }
            @{ Label = 'both quote characters'; Name = 'edfi_dms"x''y' }
            @{ Label = 'an equals sign'; Name = 'edfi_dms=x' }
        ) {
            # Before the serializer change, the unquoted forms of the first six of these parsed back as
            # a DIFFERENT name (whitespace discarded) and the semicolon form introduced a second
            # Database segment that won - so the datastore reached edfi_configurationservice while the
            # registered text said otherwise.
            $connectionString = New-DataStoreConnectionString -DatabaseEngine postgresql `
                -DbHost 'dms-postgresql' -Port 5432 -Username 'postgres' -Password 'abcdefgh1!' `
                -DatabaseName $Name

            Get-ParsedDatabaseValue -ConnectionString $connectionString |
                Should -BeExactly $Name -Because "the collision guard compares this value, so it must be the registered name"
        }

        It "introduces no second database segment for a semicolon-bearing name" {
            # The parsed value already proves this, but state it directly: the escape must keep the
            # value inside its own slot rather than adding a segment a provider would prefer.
            $connectionString = New-DataStoreConnectionString -DatabaseEngine postgresql `
                -DbHost 'dms-postgresql' -Port 5432 -Username 'postgres' -Password 'abcdefgh1!' `
                -DatabaseName 'edfi_dms;Database=edfi_configurationservice'

            $reader = [System.Data.Common.DbConnectionStringBuilder]::new()
            $reader.psbase.ConnectionString = $connectionString
            @($reader.Keys | Where-Object { $_ -match '^(database|dbname)$' }).Count |
                Should -Be 1 -Because "an escaped value cannot introduce another database key"
        }
    }

    Context "the collision authority judges the parsed value, so guard and transport cannot disagree" {
        It "agrees with the transport for <Label>" -ForEach @(
            @{ Label = 'the reserved name itself'; Name = 'edfi_configurationservice' }
            @{ Label = 'a mixed-case name'; Name = 'EDFI_ConfigurationService' }
            @{ Label = 'a trailing space'; Name = 'edfi_configurationservice ' }
            @{ Label = 'a leading space'; Name = ' edfi_configurationservice' }
            @{ Label = 'a semicolon-bearing name'; Name = 'edfi_dms;Database=edfi_configurationservice' }
        ) {
            # One assertion, both sides: whether the predicate says "collides" must equal whether the
            # database the provider will actually receive IS the reserved database. This is the property
            # the previous round asserted only by inspection.
            $connectionString = New-DataStoreConnectionString -DatabaseEngine postgresql `
                -DbHost 'dms-postgresql' -Port 5432 -Username 'postgres' -Password 'abcdefgh1!' `
                -DatabaseName $Name
            $parsedIsReserved = [string]::Equals(
                (Get-ParsedDatabaseValue -ConnectionString $connectionString),
                'edfi_configurationservice',
                [System.StringComparison]::Ordinal)

            Test-RegisteredDatastoreNameCollidesWithReservedCmsDatabase -DatabaseEngine postgresql -DatastoreDatabaseName $Name |
                Should -Be $parsedIsReserved
        }
    }

    Context "the modelled parser matches the provider parser SchemaTools uses" {
        It "reads the database back with NpgsqlConnectionStringBuilder in SchemaTools" {
            # Pins the provider side of the claim to real code rather than to a comment: if SchemaTools
            # stopped parsing the registered string, the transport this Describe models would no longer
            # be the transport in use.
            $provisioner = Join-Path $script:engRoot "../src/dms/clis/EdFi.DataManagementService.SchemaTools/Provisioning/PgsqlDatabaseProvisioner.cs"
            $provisioner = [System.IO.Path]::GetFullPath($provisioner)
            Test-Path -LiteralPath $provisioner | Should -BeTrue

            $source = Get-Content -LiteralPath $provisioner -Raw
            $source | Should -Match 'new NpgsqlConnectionStringBuilder\(connectionString\)'
            $source | Should -Match 'return\s+string\.IsNullOrWhiteSpace\(builder\.Database\)'
        }

        It "returns the same value as Npgsql itself for every transported shape" {
            # Skipped rather than silently vacuous when the SchemaTools Release output is not built: the
            # provider assembly is the only way to compare against the real parser.
            $npgsql = Join-Path $script:engRoot "../src/dms/clis/EdFi.DataManagementService.SchemaTools/bin/Release/net10.0/Npgsql.dll"
            $npgsql = [System.IO.Path]::GetFullPath($npgsql)
            if (-not (Test-Path -LiteralPath $npgsql)) {
                Set-ItResult -Skipped -Because "Npgsql.dll is only present once SchemaTools has been built in Release"
                return
            }
            [System.Reflection.Assembly]::LoadFrom($npgsql) | Out-Null

            foreach ($name in @(
                'edfi_configurationservice', 'EDFI_ConfigurationService', 'edfi_configurationservice ',
                ' edfi_configurationservice', "edfi_configurationservice`t",
                'edfi_dms;Database=edfi_configurationservice', 'edfi_dms"x', "edfi_dms'x", 'edfi_dms=x'
            )) {
                $connectionString = New-DataStoreConnectionString -DatabaseEngine postgresql `
                    -DbHost 'dms-postgresql' -Port 5432 -Username 'postgres' -Password 'abcdefgh1!' `
                    -DatabaseName $name
                $fromProvider = [Npgsql.NpgsqlConnectionStringBuilder]::new($connectionString).Database
                $fromModel = Get-ParsedDatabaseValue -ConnectionString $connectionString

                $fromProvider | Should -BeExactly $name
                $fromModel | Should -BeExactly $fromProvider
            }
        }
    }
}

Describe "Confirm-CmsDatabaseTopologyAgreement" {
    BeforeAll {
        $script:dockerComposeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
        Import-Module (Join-Path $script:dockerComposeRoot "env-utility.psm1") -Force
        Import-Module (Join-Path $script:dockerComposeRoot "database-safety.psm1") -Force

        # Round 8 Blocker 8: snapshot/clear/restore every ambient variable either function consumes,
        # not just DMS_CONFIG_DATABASE_NAME - a leftover shell value for a datastore name, password,
        # or the whole connection string can otherwise silently alter this suite's outcome.
        $script:ambientKeys = @(
            "POSTGRES_DB_NAME", "MSSQL_DB_NAME", "POSTGRES_PASSWORD", "MSSQL_SA_PASSWORD",
            "DMS_CONFIG_DATABASE_NAME", "DMS_CONFIG_DATABASE_CONNECTION_STRING", "DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE"
        )
    }

    BeforeEach {
        $script:work = Join-Path ([System.IO.Path]::GetTempPath()) "dms-cms-topology-confirm-$([Guid]::NewGuid().ToString('N'))"
        New-Item -ItemType Directory -Path $script:work -Force | Out-Null

        $script:ambientSnapshot = @{}
        foreach ($key in $script:ambientKeys) {
            $script:ambientSnapshot[$key] = [System.Environment]::GetEnvironmentVariable($key)
            Remove-Item "Env:\$key" -ErrorAction SilentlyContinue
        }
    }

    AfterEach {
        if (Test-Path -LiteralPath $script:work) {
            Remove-Item -LiteralPath $script:work -Recurse -Force -ErrorAction SilentlyContinue
        }
        foreach ($key in $script:ambientKeys) {
            if ($null -eq $script:ambientSnapshot[$key]) {
                Remove-Item "Env:\$key" -ErrorAction SilentlyContinue
            }
            else {
                [System.Environment]::SetEnvironmentVariable($key, $script:ambientSnapshot[$key])
            }
        }
    }

    Context "shared mode" {
        It "accepts a connection string that agrees with the resolved datastore name" {
            $path = Join-Path $script:work ".env"
            Set-Content -LiteralPath $path -Value (@(
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'POSTGRES_PASSWORD=abcdefgh1!',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_PASSWORD};database=${POSTGRES_DB_NAME};'
            ) -join "`n") -NoNewline

            { Confirm-CmsDatabaseTopologyAgreement -EnvironmentFile $path -DatabaseEngine "postgresql" } | Should -Not -Throw
        }

        It "rejects a connection string that disagrees with the resolved datastore name" {
            $path = Join-Path $script:work ".env"
            Set-Content -LiteralPath $path -Value (@(
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'POSTGRES_PASSWORD=abcdefgh1!',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_PASSWORD};database=some_other_db;'
            ) -join "`n") -NoNewline

            { Confirm-CmsDatabaseTopologyAgreement -EnvironmentFile $path -DatabaseEngine "postgresql" } | Should -Throw "*some_other_db*"
        }

        It "rejects a connection string whose explicit port disagrees, even though the host matches" {
            # Round 8 Blocker 4: PostgreSQL's own connection-string shape carries port as a standalone
            # "port=" key. Before the fix, the endpoint extractor never looked at that key at all, so
            # a wrong explicit port was silently defaulted to the expected port and accepted.
            $path = Join-Path $script:work ".env"
            Set-Content -LiteralPath $path -Value (@(
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'POSTGRES_PASSWORD=abcdefgh1!',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;port=9999;username=postgres;password=${POSTGRES_PASSWORD};database=${POSTGRES_DB_NAME};'
            ) -join "`n") -NoNewline

            { Confirm-CmsDatabaseTopologyAgreement -EnvironmentFile $path -DatabaseEngine "postgresql" } | Should -Throw "*9999*"
        }

        It "moves the expected name when an ambient POSTGRES_DB_NAME override is present, matching Compose's own precedence" {
            $path = Join-Path $script:work ".env"
            Set-Content -LiteralPath $path -Value (@(
                'POSTGRES_DB_NAME=file_named_db',
                'POSTGRES_PASSWORD=abcdefgh1!',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_PASSWORD};database=ambient_named_db;'
            ) -join "`n") -NoNewline

            [System.Environment]::SetEnvironmentVariable("POSTGRES_DB_NAME", "ambient_named_db")
            { Confirm-CmsDatabaseTopologyAgreement -EnvironmentFile $path -DatabaseEngine "postgresql" } | Should -Not -Throw -Because "the ambient override moves the expected value the same way Compose would resolve it"
        }

        It "constructs a concrete default and validates against it when the connection-string key is entirely absent" {
            $path = Join-Path $script:work ".env"
            Set-Content -LiteralPath $path -Value (@(
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'POSTGRES_PASSWORD=abcdefgh1!'
            ) -join "`n") -NoNewline

            { Confirm-CmsDatabaseTopologyAgreement -EnvironmentFile $path -DatabaseEngine "postgresql" } | Should -Not -Throw
        }
    }

    Context "separate mode" {
        It "accepts a connection string targeting edfi_configurationservice when the topology marker says separate" {
            $path = Join-Path $script:work ".env"
            Set-Content -LiteralPath $path -Value (@(
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'POSTGRES_PASSWORD=abcdefgh1!',
                'DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE=true',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_PASSWORD};database=edfi_configurationservice;'
            ) -join "`n") -NoNewline

            { Confirm-CmsDatabaseTopologyAgreement -EnvironmentFile $path -DatabaseEngine "postgresql" } | Should -Not -Throw
        }

        It "rejects a connection string still targeting the shared datastore name when the marker says separate" {
            $path = Join-Path $script:work ".env"
            Set-Content -LiteralPath $path -Value (@(
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'POSTGRES_PASSWORD=abcdefgh1!',
                'DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE=true',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_PASSWORD};database=edfi_datamanagementservice;'
            ) -join "`n") -NoNewline

            { Confirm-CmsDatabaseTopologyAgreement -EnvironmentFile $path -DatabaseEngine "postgresql" } | Should -Throw "*edfi_configurationservice*"
        }

        It "rejects a datastore name that IS edfi_configurationservice: separate mode must be physically separate (<_>)" -ForEach @('postgresql', 'mssql') {
            # Every equality check below would pass while both services silently share one database,
            # so distinctness is its own explicit assertion.
            $datastoreKeyLine = if ($_ -eq 'mssql') { 'MSSQL_DB_NAME=edfi_configurationservice' } else { 'POSTGRES_DB_NAME=edfi_configurationservice' }
            $connectionLine = if ($_ -eq 'mssql') {
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=Server=dms-mssql,1433;Database=edfi_configurationservice;User Id=sa;Password=abcdefgh1!;TrustServerCertificate=true;'
            } else {
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;port=5432;username=postgres;password=abcdefgh1!;database=edfi_configurationservice;'
            }
            $path = Join-Path $script:work ".env"
            Set-Content -LiteralPath $path -Value (@(
                $datastoreKeyLine,
                'POSTGRES_PASSWORD=abcdefgh1!',
                'MSSQL_SA_PASSWORD=abcdefgh1!',
                'DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE=true',
                $connectionLine
            ) -join "`n") -NoNewline

            { Confirm-CmsDatabaseTopologyAgreement -EnvironmentFile $path -DatabaseEngine $_ } |
                Should -Throw "*physically distinct*"
        }

        It "rejects a case-variant datastore collision on MSSQL (names are case-insensitive there)" {
            $path = Join-Path $script:work ".env"
            Set-Content -LiteralPath $path -Value (@(
                'MSSQL_DB_NAME=EDFI_ConfigurationService',
                'MSSQL_SA_PASSWORD=abcdefgh1!',
                'DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE=true',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=Server=dms-mssql,1433;Database=edfi_configurationservice;User Id=sa;Password=abcdefgh1!;TrustServerCertificate=true;'
            ) -join "`n") -NoNewline

            { Confirm-CmsDatabaseTopologyAgreement -EnvironmentFile $path -DatabaseEngine "mssql" } |
                Should -Throw "*physically distinct*"
        }

        It "rejects a case-variant datastore collision on PostgreSQL: the unquoted CREATE DATABASE folds it" {
            # postgresql-init.sh runs `CREATE DATABASE ${POSTGRES_DB_NAME};` with the identifier NOT
            # SQL-quoted, and PostgreSQL folds an unquoted identifier to lower case - so this datastore
            # physically creates edfi_configurationservice, the database separate mode reserves for CMS.
            # An ordinal comparison here accepted exactly that while promising two distinct databases.
            $path = Join-Path $script:work ".env"
            Set-Content -LiteralPath $path -Value (@(
                'POSTGRES_DB_NAME=EDFI_ConfigurationService',
                'POSTGRES_PASSWORD=abcdefgh1!',
                'DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE=true',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_PASSWORD};database=edfi_configurationservice;'
            ) -join "`n") -NoNewline

            $thrown = { Confirm-CmsDatabaseTopologyAgreement -EnvironmentFile $path -DatabaseEngine "postgresql" } |
                Should -Throw "*physically distinct*" -PassThru

            # BeLikeExactly, not BeLike: -like is case-insensitive, so the case-insensitive form would
            # match the reserved literal the message legitimately names and assert nothing.
            $thrown.Exception.Message | Should -Not -BeLikeExactly "*EDFI_ConfigurationService*" -Because "the diagnostic names the key, never the resolved database-name value"
        }

        It "rejects a file-authored POSTGRES_DB_NAME whose only difference is a trailing space" {
            # The dotenv quoting preserves the space (measured: the resolved value's last byte is 0x20),
            # the shell interpolates it into postgresql-init.sh's UNQUOTED
            # `CREATE DATABASE ${POSTGRES_DB_NAME};`, and PostgreSQL's lexer discards it - so this
            # datastore is physically created as edfi_configurationservice. Comparing without trimming
            # accepted exactly this configuration while promising two distinct databases.
            $path = Join-Path $script:work ".env"
            Set-Content -LiteralPath $path -Value (@(
                "POSTGRES_DB_NAME='EDFI_ConfigurationService '",
                'POSTGRES_PASSWORD=abcdefgh1!',
                'DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE=true',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_PASSWORD};database=edfi_configurationservice;'
            ) -join "`n") -NoNewline

            $thrown = { Confirm-CmsDatabaseTopologyAgreement -EnvironmentFile $path -DatabaseEngine "postgresql" } |
                Should -Throw "*physically distinct*" -PassThru

            $thrown.Exception.Message | Should -BeLike "*POSTGRES_DB_NAME*" -Because "the key is what the operator has to change"
            # BeLikeExactly, not BeLike: -like is case-insensitive, so the case-insensitive form would
            # match the reserved literal the message legitimately names and assert nothing.
            $thrown.Exception.Message | Should -Not -BeLikeExactly "*EDFI_ConfigurationService*" -Because "the diagnostic names the key, never the resolved database-name value"
        }

        It "rejects the same trailing-space collision when ambient precedence supplies POSTGRES_DB_NAME" {
            # Compose ignores the file's declaration entirely once the name is set ambiently, so the
            # ambient value is the one that moves the running datastore container and it has to reach
            # the same authority.
            $path = Join-Path $script:work ".env"
            Set-Content -LiteralPath $path -Value (@(
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'POSTGRES_PASSWORD=abcdefgh1!',
                'DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE=true',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_PASSWORD};database=edfi_configurationservice;'
            ) -join "`n") -NoNewline

            [System.Environment]::SetEnvironmentVariable("POSTGRES_DB_NAME", "EDFI_ConfigurationService ")
            $thrown = { Confirm-CmsDatabaseTopologyAgreement -EnvironmentFile $path -DatabaseEngine "postgresql" } |
                Should -Throw "*physically distinct*" -PassThru

            $thrown.Exception.Message | Should -Not -BeLikeExactly "*EDFI_ConfigurationService*" -Because "an ambient value is caller-authored too and must not be echoed"
        }

        It "keeps the connection-string comparison separate from the collision authority on PostgreSQL" {
            # The collision authority normalizes for an unquoted CREATE DATABASE; this comparison must
            # not, because a connection string's database name is passed to the provider verbatim and
            # EDFI_ConfigurationService really is a different database. Widening this rule to match the
            # collision rule would silently accept CMS pointed at a database that does not exist.
            $path = Join-Path $script:work ".env"
            Set-Content -LiteralPath $path -Value (@(
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'POSTGRES_PASSWORD=abcdefgh1!',
                'DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE=true',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_PASSWORD};database=EDFI_ConfigurationService;'
            ) -join "`n") -NoNewline

            { Confirm-CmsDatabaseTopologyAgreement -EnvironmentFile $path -DatabaseEngine "postgresql" } |
                Should -Throw "*EDFI_ConfigurationService*"
        }

        It "rejects an ambient POSTGRES_DB_NAME case variant of the dedicated CMS database name" {
            # The ambient value is what genuinely moves the running datastore container, so it goes
            # through the same collision authority as a file-declared name.
            $path = Join-Path $script:work ".env"
            Set-Content -LiteralPath $path -Value (@(
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'POSTGRES_PASSWORD=abcdefgh1!',
                'DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE=true',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_PASSWORD};database=edfi_configurationservice;'
            ) -join "`n") -NoNewline

            [System.Environment]::SetEnvironmentVariable("POSTGRES_DB_NAME", "EDFI_ConfigurationService")
            { Confirm-CmsDatabaseTopologyAgreement -EnvironmentFile $path -DatabaseEngine "postgresql" } |
                Should -Throw "*physically distinct*"
        }

        It "rejects an ambient datastore-name override that collides with the dedicated CMS database" {
            # Resolved with Compose precedence like everything else: an ambient POSTGRES_DB_NAME
            # genuinely moves the running datastore, so an ambient collision is a real collision.
            $path = Join-Path $script:work ".env"
            Set-Content -LiteralPath $path -Value (@(
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'POSTGRES_PASSWORD=abcdefgh1!',
                'DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE=true',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_PASSWORD};database=edfi_configurationservice;'
            ) -join "`n") -NoNewline

            [System.Environment]::SetEnvironmentVariable("POSTGRES_DB_NAME", "edfi_configurationservice")
            { Confirm-CmsDatabaseTopologyAgreement -EnvironmentFile $path -DatabaseEngine "postgresql" } |
                Should -Throw "*physically distinct*"
        }

        It "accepts a caller-authored connection string built on Compose default-value interpolation" {
            # database=${DMS_CONFIG_DATABASE_NAME:-${POSTGRES_DB_NAME}} is exactly what the checked-in
            # Compose fallback renders; Compose documents the operator, so validation must resolve it
            # rather than reject a working configuration as a literal mismatch.
            $path = Join-Path $script:work ".env"
            Set-Content -LiteralPath $path -Value (@(
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'POSTGRES_PASSWORD=abcdefgh1!',
                'DMS_CONFIG_DATABASE_NAME=edfi_configurationservice',
                'DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE=true',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_PASSWORD};database=${DMS_CONFIG_DATABASE_NAME:-${POSTGRES_DB_NAME}};'
            ) -join "`n") -NoNewline

            { Confirm-CmsDatabaseTopologyAgreement -EnvironmentFile $path -DatabaseEngine "postgresql" } | Should -Not -Throw
        }

        It "accepts the same default-value form in shared mode, resolving through the default arm" {
            $path = Join-Path $script:work ".env"
            Set-Content -LiteralPath $path -Value (@(
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'POSTGRES_PASSWORD=abcdefgh1!',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_PASSWORD};database=${DMS_CONFIG_DATABASE_NAME:-${POSTGRES_DB_NAME}};'
            ) -join "`n") -NoNewline

            { Confirm-CmsDatabaseTopologyAgreement -EnvironmentFile $path -DatabaseEngine "postgresql" } | Should -Not -Throw
        }
    }

    Context "ambient DMS_CONFIG_DATABASE_NAME conflict (never a resolved read-back)" {
        It "accepts an ambient DMS_CONFIG_DATABASE_NAME that agrees with the effective (separate-mode) contract" {
            $path = Join-Path $script:work ".env"
            Set-Content -LiteralPath $path -Value (@(
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'POSTGRES_PASSWORD=abcdefgh1!',
                'DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE=true',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_PASSWORD};database=edfi_configurationservice;'
            ) -join "`n") -NoNewline

            [System.Environment]::SetEnvironmentVariable("DMS_CONFIG_DATABASE_NAME", "edfi_configurationservice")
            { Confirm-CmsDatabaseTopologyAgreement -EnvironmentFile $path -DatabaseEngine "postgresql" } | Should -Not -Throw
        }

        It "rejects an ambient DMS_CONFIG_DATABASE_NAME that disagrees with the effective contract" {
            $path = Join-Path $script:work ".env"
            Set-Content -LiteralPath $path -Value (@(
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'POSTGRES_PASSWORD=abcdefgh1!',
                'DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE=true',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_PASSWORD};database=edfi_configurationservice;'
            ) -join "`n") -NoNewline

            [System.Environment]::SetEnvironmentVariable("DMS_CONFIG_DATABASE_NAME", "some_conflicting_value")
            { Confirm-CmsDatabaseTopologyAgreement -EnvironmentFile $path -DatabaseEngine "postgresql" } | Should -Throw "*some_conflicting_value*"
        }
    }

    Context "MSSQL-specific comparison rules" {
        It "accepts a case-different database name (MSSQL is ordinal-case-insensitive)" {
            $path = Join-Path $script:work ".env"
            Set-Content -LiteralPath $path -Value (@(
                'MSSQL_DB_NAME=edfi_datamanagementservice',
                'MSSQL_SA_PASSWORD=abcdefgh1!',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=Server=dms-mssql,1433;Database=EDFI_DATAMANAGEMENTSERVICE;User Id=sa;Password=${MSSQL_SA_PASSWORD};TrustServerCertificate=true;'
            ) -join "`n") -NoNewline

            { Confirm-CmsDatabaseTopologyAgreement -EnvironmentFile $path -DatabaseEngine "mssql" } | Should -Not -Throw
        }

        It "splits the MSSQL Server=host,port compound and validates both parts" {
            $path = Join-Path $script:work ".env"
            Set-Content -LiteralPath $path -Value (@(
                'MSSQL_DB_NAME=edfi_datamanagementservice',
                'MSSQL_SA_PASSWORD=abcdefgh1!',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=Server=dms-mssql,9999;Database=edfi_datamanagementservice;User Id=sa;Password=${MSSQL_SA_PASSWORD};TrustServerCertificate=true;'
            ) -join "`n") -NoNewline

            { Confirm-CmsDatabaseTopologyAgreement -EnvironmentFile $path -DatabaseEngine "mssql" } | Should -Throw "*9999*"
        }

        It "does not honor a standalone Port= key for MSSQL (SqlClient does not support that keyword)" {
            # Round 9 Blocker 2: honoring a standalone Port= for MSSQL would accept a keyword the real
            # SqlClient provider does not recognize, defaulting to the expected port instead of failing.
            $path = Join-Path $script:work ".env"
            Set-Content -LiteralPath $path -Value (@(
                'MSSQL_DB_NAME=edfi_datamanagementservice',
                'MSSQL_SA_PASSWORD=abcdefgh1!',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=Server=dms-mssql;Port=9999;Database=edfi_datamanagementservice;User Id=sa;Password=${MSSQL_SA_PASSWORD};TrustServerCertificate=true;'
            ) -join "`n") -NoNewline

            { Confirm-CmsDatabaseTopologyAgreement -EnvironmentFile $path -DatabaseEngine "mssql" } | Should -Not -Throw -Because "the standalone Port= key is not a real MSSQL keyword and must be ignored, defaulting to the expected port 1433"
        }

        It "fails clearly, without constructing a default, when the connection string is entirely absent" {
            # Round 8 Blocker 5 / spec Phase 1 rule: neither .yml file has an engine-aware inline
            # fallback for MSSQL yet, so guessing a default here could accept a connection Compose
            # itself would never render. Must fail clearly instead.
            $path = Join-Path $script:work ".env"
            Set-Content -LiteralPath $path -Value (@(
                'MSSQL_DB_NAME=edfi_datamanagementservice',
                'MSSQL_SA_PASSWORD=abcdefgh1!'
            ) -join "`n") -NoNewline

            { Confirm-CmsDatabaseTopologyAgreement -EnvironmentFile $path -DatabaseEngine "mssql" } | Should -Throw "*required for MSSQL*"
        }

        It "fails clearly, without constructing a default, when the connection string is ambient-blank" {
            $path = Join-Path $script:work ".env"
            Set-Content -LiteralPath $path -Value (@(
                'MSSQL_DB_NAME=edfi_datamanagementservice',
                'MSSQL_SA_PASSWORD=abcdefgh1!',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=Server=dms-mssql,1433;Database=edfi_datamanagementservice;User Id=sa;Password=${MSSQL_SA_PASSWORD};TrustServerCertificate=true;'
            ) -join "`n") -NoNewline

            [System.Environment]::SetEnvironmentVariable("DMS_CONFIG_DATABASE_CONNECTION_STRING", "")

            # A present-but-blank ambient value is only representable on Windows: on Unix, .NET
            # deletes the variable when it is set to an empty string, so the scenario under test
            # cannot be established and the file value (a valid connection string) would be used
            # instead. Assert the precondition rather than letting the platform silently decide
            # whether this test means anything.
            if ($null -eq [System.Environment]::GetEnvironmentVariable("DMS_CONFIG_DATABASE_CONNECTION_STRING")) {
                Set-ItResult -Skipped -Because "this platform cannot represent a present-but-blank ambient environment variable"
                return
            }

            { Confirm-CmsDatabaseTopologyAgreement -EnvironmentFile $path -DatabaseEngine "mssql" } | Should -Throw "*required for MSSQL*"
        }

        It "fails closed for a PostgreSQL-only host alias (Host=) that MSSQL does not recognize" {
            $path = Join-Path $script:work ".env"
            Set-Content -LiteralPath $path -Value (@(
                'MSSQL_DB_NAME=edfi_datamanagementservice',
                'MSSQL_SA_PASSWORD=abcdefgh1!',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=Host=dms-mssql;Database=edfi_datamanagementservice;User Id=sa;Password=${MSSQL_SA_PASSWORD};TrustServerCertificate=true;'
            ) -join "`n") -NoNewline

            { Confirm-CmsDatabaseTopologyAgreement -EnvironmentFile $path -DatabaseEngine "mssql" } | Should -Throw "*host*"
        }
    }

    Context "PostgreSQL-specific comparison rules" {
        It "rejects a case-different database name (PostgreSQL is ordinal-case-sensitive)" {
            $path = Join-Path $script:work ".env"
            Set-Content -LiteralPath $path -Value (@(
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'POSTGRES_PASSWORD=abcdefgh1!',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_PASSWORD};database=EDFI_DATAMANAGEMENTSERVICE;'
            ) -join "`n") -NoNewline

            { Confirm-CmsDatabaseTopologyAgreement -EnvironmentFile $path -DatabaseEngine "postgresql" } | Should -Throw "*EDFI_DATAMANAGEMENTSERVICE*"
        }

        It "fails closed for an MSSQL-only host alias (Address=) that PostgreSQL does not recognize" {
            # Round 8 Blocker 4: host-key recognition must be engine-specific, not a single union
            # applied to both engines.
            $path = Join-Path $script:work ".env"
            Set-Content -LiteralPath $path -Value (@(
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'POSTGRES_PASSWORD=abcdefgh1!',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=Address=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_PASSWORD};database=edfi_datamanagementservice;'
            ) -join "`n") -NoNewline

            { Confirm-CmsDatabaseTopologyAgreement -EnvironmentFile $path -DatabaseEngine "postgresql" } | Should -Throw "*host*"
        }

        It "rejects a comma-bearing PostgreSQL Host= value as a literal (malformed) host, rather than splitting it and hiding an explicit Port=" {
            # Round 9 Blocker 2: Npgsql has no Server=host,port compound - a comma in a PostgreSQL
            # Host= value is not a port separator. Before the fix, splitting it anyway extracted
            # "dms-postgresql" as Host (matching the expected host) and the comma-compound's second
            # half as Port, silently hiding the disagreeing explicit standalone Port=9999 key behind a
            # port that was never really specified that way. After the fix, the comma is not split, so
            # the whole value "dms-postgresql,5432" is correctly rejected as not matching the expected
            # host "dms-postgresql" - a comma-bearing value is simply not a valid PostgreSQL host.
            $path = Join-Path $script:work ".env"
            Set-Content -LiteralPath $path -Value (@(
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'POSTGRES_PASSWORD=abcdefgh1!',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=Host=dms-postgresql,5432;Port=9999;username=postgres;password=${POSTGRES_PASSWORD};database=edfi_datamanagementservice;'
            ) -join "`n") -NoNewline

            { Confirm-CmsDatabaseTopologyAgreement -EnvironmentFile $path -DatabaseEngine "postgresql" } | Should -Throw "*dms-postgresql,5432*"
        }
    }

    Context "host and port edge cases" {
        It "fails closed when the connection string has no recognized host key at all" {
            $path = Join-Path $script:work ".env"
            Set-Content -LiteralPath $path -Value (@(
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'POSTGRES_PASSWORD=abcdefgh1!',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=Database=edfi_datamanagementservice;Username=postgres;Password=abcdefgh1!;'
            ) -join "`n") -NoNewline

            { Confirm-CmsDatabaseTopologyAgreement -EnvironmentFile $path -DatabaseEngine "postgresql" } | Should -Throw "*host*"
        }

        It "defaults an omitted port to the engine's standard internal port when the host key is present" {
            $path = Join-Path $script:work ".env"
            Set-Content -LiteralPath $path -Value (@(
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'POSTGRES_PASSWORD=abcdefgh1!',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;database=edfi_datamanagementservice;username=postgres;password=abcdefgh1!;'
            ) -join "`n") -NoNewline

            { Confirm-CmsDatabaseTopologyAgreement -EnvironmentFile $path -DatabaseEngine "postgresql" } | Should -Not -Throw
        }

        It "rejects a host that does not match the expected container hostname" {
            $path = Join-Path $script:work ".env"
            Set-Content -LiteralPath $path -Value (@(
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'POSTGRES_PASSWORD=abcdefgh1!',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=host=some-other-host;port=5432;database=edfi_datamanagementservice;username=postgres;password=abcdefgh1!;'
            ) -join "`n") -NoNewline

            { Confirm-CmsDatabaseTopologyAgreement -EnvironmentFile $path -DatabaseEngine "postgresql" } | Should -Throw "*some-other-host*"
        }
    }

    # The database container is reachable inside the compose network under every identity Compose
    # gives it: the service key, container_name, and hostname. The validator previously hard-coded
    # only "dms-<engine>", so a connection string using the service key - which is what the compose
    # files themselves and the Ed-Fi docs use - was rejected even though it addresses the very same
    # container. The accepted set is now derived from the compose file, so it cannot drift from it.
    Context "accepted database hosts are pinned to the composed service's own identities" {
        BeforeEach {
            $script:hostCases = @(
                @{ Engine = 'postgresql'; ServiceAlias = 'db'; ContainerAlias = 'dms-postgresql'
                   Lines = @('POSTGRES_DB_NAME=edfi_datamanagementservice', 'POSTGRES_PASSWORD=abcdefgh1!')
                   Template = 'DMS_CONFIG_DATABASE_CONNECTION_STRING=host={0};port=5432;database=edfi_datamanagementservice;username=postgres;password=abcdefgh1!;' },
                @{ Engine = 'mssql'; ServiceAlias = 'db'; ContainerAlias = 'dms-mssql'
                   Lines = @('MSSQL_DB_NAME=edfi_datamanagementservice', 'MSSQL_SA_PASSWORD=abcdefgh1!')
                   Template = 'DMS_CONFIG_DATABASE_CONNECTION_STRING=Server={0},1433;Database=edfi_datamanagementservice;User Id=sa;Password=abcdefgh1!;TrustServerCertificate=true;' }
            )
        }

        It "accepts the compose service key as the database host" {
            foreach ($case in $script:hostCases) {
                $path = Join-Path $script:work ".env.service-$($case.Engine)"
                Set-Content -LiteralPath $path -NoNewline -Value ((
                    $case.Lines + ($case.Template -f $case.ServiceAlias)) -join "`n")

                { Confirm-CmsDatabaseTopologyAgreement -EnvironmentFile $path -DatabaseEngine $case.Engine } |
                    Should -Not -Throw -Because "'$($case.ServiceAlias)' is the $($case.Engine) service key inside the compose network"
            }
        }

        It "still accepts the container name as the database host" {
            foreach ($case in $script:hostCases) {
                $path = Join-Path $script:work ".env.container-$($case.Engine)"
                Set-Content -LiteralPath $path -NoNewline -Value ((
                    $case.Lines + ($case.Template -f $case.ContainerAlias)) -join "`n")

                { Confirm-CmsDatabaseTopologyAgreement -EnvironmentFile $path -DatabaseEngine $case.Engine } |
                    Should -Not -Throw -Because "widening the set must not drop the identity that already worked"
            }
        }

        It "still rejects a host that is not one of the composed service's identities" {
            foreach ($case in $script:hostCases) {
                $path = Join-Path $script:work ".env.foreign-$($case.Engine)"
                Set-Content -LiteralPath $path -NoNewline -Value ((
                    $case.Lines + ($case.Template -f 'someone-elses-database')) -join "`n")

                { Confirm-CmsDatabaseTopologyAgreement -EnvironmentFile $path -DatabaseEngine $case.Engine } |
                    Should -Throw "*someone-elses-database*" -Because "an unrelated host is still a topology violation"
            }
        }

        It "still rejects a wrong port on an accepted host" {
            # Widening the host set must not weaken the endpoint check as a whole.
            $path = Join-Path $script:work ".env.wrongport"
            Set-Content -LiteralPath $path -NoNewline -Value (@(
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'POSTGRES_PASSWORD=abcdefgh1!',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=host=db;port=9999;database=edfi_datamanagementservice;username=postgres;password=abcdefgh1!;'
            ) -join "`n")

            { Confirm-CmsDatabaseTopologyAgreement -EnvironmentFile $path -DatabaseEngine "postgresql" } |
                Should -Throw "*9999*"
        }

        It "names every accepted identity when it rejects a host" {
            # The operator has to be able to act on the diagnostic, so it must list what IS accepted.
            $path = Join-Path $script:work ".env.diagnostic"
            Set-Content -LiteralPath $path -NoNewline -Value (@(
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'POSTGRES_PASSWORD=abcdefgh1!',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=host=someone-elses-database;port=5432;database=edfi_datamanagementservice;username=postgres;password=abcdefgh1!;'
            ) -join "`n")

            $message = $null
            try { Confirm-CmsDatabaseTopologyAgreement -EnvironmentFile $path -DatabaseEngine "postgresql" }
            catch { $message = $_.Exception.Message }

            $message | Should -Not -BeNullOrEmpty
            foreach ($alias in @(Get-ComposeDatabaseServiceHostAlias -DatabaseEngine "postgresql" -DockerComposeRoot $script:dockerComposeRoot)) {
                $message | Should -BeLike "*'$alias'*" -Because "the operator needs the alternatives spelled out individually"
            }
        }
    }

    # The accepted-host set decides which endpoint the Configuration Service may talk to, so the read that
    # produces it is specified as an explicit state machine rather than a set of accept/skip rules: three
    # successive reviews each found a different input shape that slipped through the rule-based version,
    # twice returning a WRONG host. Every state transition and every fail-closed outcome has a fixture
    # here, and every assertion checks the COMPLETE returned set or the exact diagnostic class - asserting
    # only that an expected value is present is what let the nested-key and wrong-service defects pass.
    Context "Get-ComposeDatabaseServiceHostAlias state machine" {
        BeforeAll {
            # Writes <engine>.yml into its own directory under the test's temp root and returns that
            # directory, so each fixture is independent of every other.
            function script:New-ComposeFixtureRoot {
                param([string]$Engine, [string[]]$Line)

                $dir = Join-Path $script:work ("compose-" + [Guid]::NewGuid().ToString('N'))
                New-Item -ItemType Directory -Path $dir -Force | Out-Null
                Set-Content -LiteralPath (Join-Path $dir "$Engine.yml") -NoNewline -Value ($Line -join "`n")
                return $dir
            }

            # A conventional second service, used to prove no fixture ever adopts it.
            $script:siblingService = @(
                '  other:',
                '    container_name: NOT-THE-DATABASE',
                '    hostname: ALSO-NOT-THE-DATABASE'
            )
        }

        It "returns the service key then the declared identities for a conventional service" {
            foreach ($engine in @('postgresql', 'mssql')) {
                $root = New-ComposeFixtureRoot -Engine $engine -Line @(
                    'services:',
                    '  db:',
                    '    image: some/image:1',
                    "    container_name: dms-$engine",
                    "    hostname: dms-$engine"
                )

                # Equal container_name and hostname collapse to one entry: ordinal de-duplication,
                # file order, service key first. This is the shape of the checked-in compose files.
                @(Get-ComposeDatabaseServiceHostAlias -DatabaseEngine $engine -DockerComposeRoot $root) |
                    Should -Be @('db', "dms-$engine")
            }
        }

        It "returns both identities when container_name and hostname differ" {
            $root = New-ComposeFixtureRoot -Engine 'postgresql' -Line @(
                'services:',
                '  db:',
                '    container_name: dms-postgresql',
                '    hostname: dms-postgresql-internal'
            )

            @(Get-ComposeDatabaseServiceHostAlias -DatabaseEngine "postgresql" -DockerComposeRoot $root) |
                Should -Be @('db', 'dms-postgresql', 'dms-postgresql-internal')
        }

        It "returns the service key alone when the service declares neither alias key" {
            $root = New-ComposeFixtureRoot -Engine 'postgresql' -Line @(
                'services:',
                '  db:',
                '    image: some/image:1'
            )

            @(Get-ComposeDatabaseServiceHostAlias -DatabaseEngine "postgresql" -DockerComposeRoot $root) |
                Should -Be @('db')
        }

        It "supports a trailing comment on the service header" {
            # Valid Compose, and the shape that previously made the parser skip the database service and
            # adopt the NEXT one - so the sibling is present here to prove it is not adopted.
            $root = New-ComposeFixtureRoot -Engine 'postgresql' -Line (@(
                'services:',
                '  db: # primary',
                '    container_name: dms-postgresql'
            ) + $script:siblingService)

            @(Get-ComposeDatabaseServiceHostAlias -DatabaseEngine "postgresql" -DockerComposeRoot $root) |
                Should -Be @('db', 'dms-postgresql')
        }

        It "supports a trailing comment on the services header" {
            $root = New-ComposeFixtureRoot -Engine 'postgresql' -Line @(
                'services: # every service in the stack',
                '  db:',
                '    container_name: dms-postgresql'
            )

            @(Get-ComposeDatabaseServiceHostAlias -DatabaseEngine "postgresql" -DockerComposeRoot $root) |
                Should -Be @('db', 'dms-postgresql')
        }

        It "ignores blank and comment-only lines without letting them establish state" {
            # A comment line must not count as "the first entry under services:", and must not establish
            # the direct-child indent - either would change which lines are read.
            $root = New-ComposeFixtureRoot -Engine 'postgresql' -Line @(
                '# leading comment',
                '',
                'services:',
                '',
                '  # the database',
                '  db:',
                '      # deliberately deeper than the children',
                '    container_name: dms-postgresql',
                '',
                '    hostname: dms-postgresql'
            )

            @(Get-ComposeDatabaseServiceHostAlias -DatabaseEngine "postgresql" -DockerComposeRoot $root) |
                Should -Be @('db', 'dms-postgresql')
        }

        It "supports an inline comment on an alias value" {
            # Valid Compose: `docker compose config` resolves this to dms-postgresql. The pre-stabilization
            # regex anchored the value at end of line, so it matched nothing and the identity was dropped -
            # narrowing, but harmful, because the file legitimately declares it.
            $root = New-ComposeFixtureRoot -Engine 'postgresql' -Line @(
                'services:',
                '  db:',
                '    container_name: dms-postgresql # the database container',
                '    hostname: dms-alt   # and its network name'
            )

            @(Get-ComposeDatabaseServiceHostAlias -DatabaseEngine "postgresql" -DockerComposeRoot $root) |
                Should -Be @('db', 'dms-postgresql', 'dms-alt')
        }

        It "supports single- and double-quoted alias values" {
            $root = New-ComposeFixtureRoot -Engine 'postgresql' -Line @(
                'services:',
                '  db:',
                '    container_name: "dms-postgresql"',
                "    hostname: 'dms-alt'"
            )

            @(Get-ComposeDatabaseServiceHostAlias -DatabaseEngine "postgresql" -DockerComposeRoot $root) |
                Should -Be @('db', 'dms-postgresql', 'dms-alt')
        }

        It "moves the whole set when the service, container_name, and hostname are all renamed" {
            # Pins the set to the file rather than to a list: a rename in the compose file must move what
            # endpoint validation accepts, in the same commit, or the two can silently diverge.
            $root = New-ComposeFixtureRoot -Engine 'postgresql' -Line @(
                'services:',
                '  datastore:',
                '    image: postgres:16',
                '    container_name: renamed-database',
                '    hostname: renamed-host'
            )

            @(Get-ComposeDatabaseServiceHostAlias -DatabaseEngine "postgresql" -DockerComposeRoot $root) |
                Should -Be @('datastore', 'renamed-database', 'renamed-host')
        }

        It "ignores alias-like keys nested inside another mapping, including under healthcheck" {
            # Only DIRECT children of the service are network identities. Under environment: these are
            # environment variables, under healthcheck: probe arguments, under labels: labels - none of
            # which the container answers to. The service-level hostname is declared AFTER all three
            # nested blocks, so the rule must be "at the child indent", not "before the first nested
            # block", or a legitimate identity would be lost.
            $root = New-ComposeFixtureRoot -Engine 'postgresql' -Line @(
                'services:',
                '  db:',
                '    image: postgres:16',
                '    container_name: dms-postgresql',
                '    environment:',
                '      hostname: env-unrelated-host',
                '      container_name: env-unrelated-container',
                '    healthcheck:',
                '      test: ["CMD", "pg_isready"]',
                '      hostname: healthcheck-unrelated-host',
                '      container_name: healthcheck-unrelated-container',
                '    labels:',
                '      hostname: label-unrelated-host',
                '      container_name: label-unrelated-container',
                '    hostname: dms-alt'
            )

            @(Get-ComposeDatabaseServiceHostAlias -DatabaseEngine "postgresql" -DockerComposeRoot $root) |
                Should -Be @('db', 'dms-postgresql', 'dms-alt')
        }

        It "narrows past an interpolated alias value rather than accepting the reference text" {
            # Compose resolves ${DB_CONTAINER:-dms-mssql} to dms-mssql, but this function has no
            # environment context and will not acquire one, so it cannot know that. Adding the reference
            # text would assert a name nothing answers to; skipping narrows the set, the only direction an
            # unsupported value may move it.
            $root = New-ComposeFixtureRoot -Engine 'mssql' -Line @(
                'services:',
                '  db:',
                '    container_name: ${DB_CONTAINER:-dms-mssql}',
                '    hostname: dms-mssql'
            )

            @(Get-ComposeDatabaseServiceHostAlias -DatabaseEngine "mssql" -DockerComposeRoot $root) |
                Should -Be @('db', 'dms-mssql')
        }

        It "narrows past values that are not identifier-shaped" {
            # The identifier check is the authoritative gate, and is deliberately stricter than Compose,
            # which does not validate container_name content at config time. Each case below would
            # otherwise need its own branch; one rule covers them all.
            foreach ($case in @(
                @{ Label = 'a name containing a space'; Value = '"bad name"' },
                @{ Label = 'an empty value'; Value = '' },
                @{ Label = 'a hash inside the token'; Value = '"db#1"' },
                @{ Label = 'a quoted value the comment strip would mangle'; Value = '"a # b"' },
                @{ Label = 'a leading hyphen'; Value = '-leading-hyphen' },
                @{ Label = 'a comment-only value'; Value = '# just a comment' }
            )) {
                $root = New-ComposeFixtureRoot -Engine 'postgresql' -Line @(
                    'services:',
                    '  db:',
                    "    container_name: $($case.Value)"
                )

                @(Get-ComposeDatabaseServiceHostAlias -DatabaseEngine "postgresql" -DockerComposeRoot $root) |
                    Should -Be @('db') -Because "$($case.Label) must narrow the set, never enter it"
            }
        }

        It "stops at the next service rather than absorbing its identities" {
            $root = New-ComposeFixtureRoot -Engine 'postgresql' -Line (@(
                'services:',
                '  db:',
                '    container_name: dms-postgresql'
            ) + $script:siblingService + @(
                'volumes:',
                '  dms-postgresql:'
            ))

            @(Get-ComposeDatabaseServiceHostAlias -DatabaseEngine "postgresql" -DockerComposeRoot $root) |
                Should -Be @('db', 'dms-postgresql')
        }

        It "fails closed on an unsupported first-service header and never adopts a later sibling" {
            # These are all valid, working Compose - verified with docker compose config, which resolves
            # every one of them to service "db". Failing closed therefore refuses a stack that would run:
            # the deliberate trade, because the pre-stabilization parser skipped these headers, kept
            # scanning, and handed the accepted-host set to a DIFFERENT container - the database's own
            # identities absent and a wrong one accepted. Each fixture puts a conventional service second
            # so the wrong-service outcome is what the assertion rules out.
            foreach ($case in @(
                @{ Label = 'anchor'; Header = '  db: &database' },
                @{ Label = 'alias'; Header = '  db: *database' },
                @{ Label = 'double-quoted key'; Header = '  "db":' },
                @{ Label = 'single-quoted key'; Header = "  'db':" },
                @{ Label = 'inline mapping'; Header = '  db: {image: postgres:16}' },
                @{ Label = 'sequence entry'; Header = '  - db' },
                @{ Label = 'scalar value'; Header = '  db: postgres' }
            )) {
                $root = New-ComposeFixtureRoot -Engine 'postgresql' -Line (@(
                    'services:',
                    $case.Header,
                    '    container_name: dms-postgresql'
                ) + $script:siblingService)

                $message = $null
                try { $null = Get-ComposeDatabaseServiceHostAlias -DatabaseEngine "postgresql" -DockerComposeRoot $root }
                catch { $message = $_.Exception.Message }

                $message | Should -Not -BeNullOrEmpty -Because "$($case.Label) is not the supported header form"
                $message | Should -BeLike "*does not use the supported 'name:' header form*"
                $message | Should -BeLike "*postgresql.yml*" -Because "the operator needs to know which file"
                $message | Should -BeLike "*at line 2*" -Because "the operator needs to know where"
                $message | Should -Not -BeLike "*NOT-THE-DATABASE*" -Because "no later sibling may be adopted"
                $message | Should -Not -BeLike "*$($case.Header.Trim())*" -Because "diagnostics name the location, never the line content"
            }
        }

        It "fails closed when the file declares no top-level services mapping" {
            $root = New-ComposeFixtureRoot -Engine 'postgresql' -Line @(
                'volumes:',
                '  dms-postgresql:',
                'networks:',
                '  default:'
            )

            { Get-ComposeDatabaseServiceHostAlias -DatabaseEngine "postgresql" -DockerComposeRoot $root } |
                Should -Throw "*no top-level 'services:' mapping found*"
        }

        It "fails closed when the services mapping declares no entry" {
            foreach ($case in @(
                @{ Label = 'another top-level key follows'; Line = @('services:', 'volumes:', '  dms-postgresql:') },
                @{ Label = 'services: is the last line'; Line = @('services:') },
                @{ Label = 'only a comment follows'; Line = @('services:', '  # nothing here yet') }
            )) {
                $root = New-ComposeFixtureRoot -Engine 'postgresql' -Line $case.Line

                { Get-ComposeDatabaseServiceHostAlias -DatabaseEngine "postgresql" -DockerComposeRoot $root } |
                    Should -Throw "*declares no service entry*" -Because $case.Label
            }
        }

        It "fails closed on a tab-indented service block" {
            # Compose rejects tab indentation outright ("found character that cannot start any token"), so
            # this is not a runnable stack. Counting only spaces as indentation makes the tab line indent 0,
            # which routes it deterministically to a fail-closed outcome instead of a guess.
            $root = New-ComposeFixtureRoot -Engine 'postgresql' -Line @(
                'services:',
                "`tdb:",
                "`t`tcontainer_name: dms-postgresql"
            )

            { Get-ComposeDatabaseServiceHostAlias -DatabaseEngine "postgresql" -DockerComposeRoot $root } |
                Should -Throw "*declares no service entry*"
        }

        It "narrows to the canonical container name when the compose file is absent" {
            # Distinct from an unsupported header on purpose: a missing file means the module was staged
            # without the compose files, which is how isolated harnesses run, and the historical single
            # name is correct there. Never a guessed service key - absent input may only narrow.
            $root = Join-Path $script:work ("compose-empty-" + [Guid]::NewGuid().ToString('N'))
            New-Item -ItemType Directory -Path $root -Force | Out-Null

            foreach ($engine in @('postgresql', 'mssql')) {
                @(Get-ComposeDatabaseServiceHostAlias -DatabaseEngine $engine -DockerComposeRoot $root) |
                    Should -Be @("dms-$engine")
            }
        }

        It "derives exactly the identities the checked-in compose files declare" {
            # The regression anchor. Both files declare all three keys with hostname equal to
            # container_name, so de-duplication collapses each set to two. A rename in either file that is
            # not reflected here fails this test rather than silently diverging from validation.
            @(Get-ComposeDatabaseServiceHostAlias -DatabaseEngine "postgresql" -DockerComposeRoot $script:dockerComposeRoot) |
                Should -Be @('db', 'dms-postgresql')
            @(Get-ComposeDatabaseServiceHostAlias -DatabaseEngine "mssql" -DockerComposeRoot $script:dockerComposeRoot) |
                Should -Be @('db', 'dms-mssql')

            foreach ($engine in @('postgresql', 'mssql')) {
                $composeText = [System.IO.File]::ReadAllText((Join-Path $script:dockerComposeRoot "$engine.yml"))
                foreach ($alias in @(Get-ComposeDatabaseServiceHostAlias -DatabaseEngine $engine -DockerComposeRoot $script:dockerComposeRoot)) {
                    $composeText | Should -BeLike "*$alias*" -Because "every accepted host must appear in $engine.yml"
                }
            }
        }
    }

    Context "multiple agreeing aliases (must not be rejected as ambiguous)" {
        It "accepts a connection string carrying both Database and Initial Catalog when both agree" {
            $path = Join-Path $script:work ".env"
            Set-Content -LiteralPath $path -Value (@(
                'MSSQL_DB_NAME=edfi_datamanagementservice',
                'MSSQL_SA_PASSWORD=abcdefgh1!',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=Server=dms-mssql,1433;Database=edfi_datamanagementservice;Initial Catalog=edfi_datamanagementservice;User Id=sa;Password=${MSSQL_SA_PASSWORD};TrustServerCertificate=true;'
            ) -join "`n") -NoNewline

            { Confirm-CmsDatabaseTopologyAgreement -EnvironmentFile $path -DatabaseEngine "mssql" } | Should -Not -Throw
        }

        It "rejects a connection string carrying two disagreeing database-name aliases" {
            $path = Join-Path $script:work ".env"
            Set-Content -LiteralPath $path -Value (@(
                'MSSQL_DB_NAME=edfi_datamanagementservice',
                'MSSQL_SA_PASSWORD=abcdefgh1!',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=Server=dms-mssql,1433;Database=edfi_datamanagementservice;Initial Catalog=a_different_database;User Id=sa;Password=${MSSQL_SA_PASSWORD};TrustServerCertificate=true;'
            ) -join "`n") -NoNewline

            { Confirm-CmsDatabaseTopologyAgreement -EnvironmentFile $path -DatabaseEngine "mssql" } | Should -Throw "*a_different_database*"
        }
    }
}

Describe "Get-DatabaseNameFromResolvedConnectionString / Get-EndpointFromResolvedConnectionString" {
    BeforeAll {
        $script:dockerComposeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
        Import-Module (Join-Path $script:dockerComposeRoot "database-safety.psm1") -Force
    }

    It "returns an empty array for a blank connection string" {
        @(Get-DatabaseNameFromResolvedConnectionString -ConnectionString "").Count | Should -Be 0
        @(Get-EndpointFromResolvedConnectionString -ConnectionString "" -DatabaseEngine "postgresql").Count | Should -Be 0
    }

    It "returns every present database-name candidate without picking a single winner" {
        $names = @(Get-DatabaseNameFromResolvedConnectionString -ConnectionString "Database=A;Initial Catalog=B;")
        $names.Count | Should -Be 2
        $names | Should -Contain "A"
        $names | Should -Contain "B"
    }

    It "does not re-resolve a ${...}-shaped literal already present in an already-resolved string" {
        # Simulates the opaque-ambient case: the caller already resolved the whole connection
        # string (e.g. via Get-ComposeResolvedEnvValue), so a literal, un-interpolated ${...}
        # token that survives into the extracted sub-value must be returned verbatim, not
        # resolved a second time.
        $names = @(Get-DatabaseNameFromResolvedConnectionString -ConnectionString 'Database=${SOME_LITERAL_TEXT};')
        $names | Should -Contain '${SOME_LITERAL_TEXT}'
    }

    It "splits an MSSQL host,port compound into separate Host and Port fields" {
        $endpoints = @(Get-EndpointFromResolvedConnectionString -ConnectionString "Server=dms-mssql,1433;Database=x;" -DatabaseEngine "mssql")
        $endpoints.Count | Should -Be 1
        $endpoints[0].Host | Should -Be "dms-mssql"
        $endpoints[0].Port | Should -Be "1433"
    }

    It "extracts a PostgreSQL standalone port key when the host value carries no comma compound" {
        # Round 8 Blocker 4: PostgreSQL's own shape (host=...;port=...;) is not a host,port compound
        # - the port must still be recognized from its own standalone key.
        $endpoints = @(Get-EndpointFromResolvedConnectionString -ConnectionString "host=dms-postgresql;port=9999;database=x;" -DatabaseEngine "postgresql")
        $endpoints[0].Host | Should -Be "dms-postgresql"
        $endpoints[0].Port | Should -Be "9999"
    }

    It "returns a null Port when neither a comma compound nor a standalone port key is present" {
        $endpoints = @(Get-EndpointFromResolvedConnectionString -ConnectionString "host=dms-postgresql;database=x;" -DatabaseEngine "postgresql")
        $endpoints[0].Host | Should -Be "dms-postgresql"
        $endpoints[0].Port | Should -BeNullOrEmpty
    }

    It "does not split a comma inside a PostgreSQL Host= value (Npgsql has no host,port compound)" {
        # Round 9 Blocker 2: splitting the comma hides an explicit standalone Port= key behind
        # whatever port a coincidental comma in the host value produced.
        $endpoints = @(Get-EndpointFromResolvedConnectionString -ConnectionString "Host=dms-postgresql,5432;Port=9999;Database=x;" -DatabaseEngine "postgresql")
        $endpoints[0].Host | Should -Be "dms-postgresql,5432"
        $endpoints[0].Port | Should -Be "9999"
    }

    It "does not honor a standalone Port= key for MSSQL (SqlClient does not support that keyword)" {
        $endpoints = @(Get-EndpointFromResolvedConnectionString -ConnectionString "Server=dms-mssql;Port=1433;Database=x;" -DatabaseEngine "mssql")
        $endpoints[0].Host | Should -Be "dms-mssql"
        $endpoints[0].Port | Should -BeNullOrEmpty
    }

    It "does not recognize an MSSQL-only alias (Address=) for PostgreSQL" {
        @(Get-EndpointFromResolvedConnectionString -ConnectionString "Address=some-host;Database=x;" -DatabaseEngine "postgresql").Count | Should -Be 0
    }

    It "does not recognize a PostgreSQL-only alias (Host=) for MSSQL" {
        @(Get-EndpointFromResolvedConnectionString -ConnectionString "Host=some-host;Database=x;" -DatabaseEngine "mssql").Count | Should -Be 0
    }

    It "recognizes Server= for both engines" {
        @(Get-EndpointFromResolvedConnectionString -ConnectionString "Server=some-host;Database=x;" -DatabaseEngine "postgresql").Count | Should -Be 1
        @(Get-EndpointFromResolvedConnectionString -ConnectionString "Server=some-host;Database=x;" -DatabaseEngine "mssql").Count | Should -Be 1
    }
}

Describe "Get-CmsDatabaseTopologyDefaultConnectionString" {
    BeforeAll {
        $script:dockerComposeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
        Import-Module (Join-Path $script:dockerComposeRoot "database-safety.psm1") -Force
    }

    It "constructs the exact shape local-config.yml / published-config.yml's nested fallback renders" {
        $result = Get-CmsDatabaseTopologyDefaultConnectionString -ExpectedHost "dms-postgresql" -ExpectedPort "5432" -ExpectedDatabaseName "edfi_datamanagementservice" -PostgresPassword "abcdefgh1!"
        $result | Should -Be 'host=dms-postgresql;port=5432;username=postgres;password=abcdefgh1!;database=edfi_datamanagementservice;'
    }
}

Describe "Test-PostgresDuplicateDatabaseError" {
    BeforeAll {
        $script:dockerComposeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
        Import-Module (Join-Path $script:dockerComposeRoot "database-safety.psm1") -Force
    }

    # Fixture text captured empirically (DMS-1270 Phase 1a spike) against a real PostgreSQL 16
    # instance running `psql -v VERBOSITY=sqlstate`: a direct duplicate CREATE DATABASE reported
    # "ERROR:  42P04"; a genuine concurrent race between two \gexec-driven sessions targeting a
    # not-yet-existing database reported "psql:<stdin>:2: ERROR:  23505" on the losing side.
    It "recognizes the empirically-captured 42P04 direct-duplicate format" {
        Test-PostgresDuplicateDatabaseError -CapturedOutput "ERROR:  42P04" | Should -BeTrue
    }

    It "recognizes the empirically-captured 23505 concurrent-race format" {
        Test-PostgresDuplicateDatabaseError -CapturedOutput "psql:<stdin>:2: ERROR:  23505" | Should -BeTrue
    }

    It "does not swallow a different SQLSTATE" {
        Test-PostgresDuplicateDatabaseError -CapturedOutput "ERROR:  42501" | Should -BeFalse
    }

    It "does not swallow malformed or empty output" {
        Test-PostgresDuplicateDatabaseError -CapturedOutput "" | Should -BeFalse
        Test-PostgresDuplicateDatabaseError -CapturedOutput "some unrelated text with no error code" | Should -BeFalse
    }
}

Describe "Test-MssqlDuplicateDatabaseError" {
    BeforeAll {
        $script:dockerComposeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
        Import-Module (Join-Path $script:dockerComposeRoot "database-safety.psm1") -Force
    }

    It "recognizes SQL Server error 1801 (database already exists)" {
        Test-MssqlDuplicateDatabaseError -CapturedOutput "Msg 1801, Level 16, State 3, Server x, Line 1" | Should -BeTrue
    }

    It "does not swallow a different error number" {
        Test-MssqlDuplicateDatabaseError -CapturedOutput "Msg 4060, Level 11, State 1" | Should -BeFalse
    }

    It "does not swallow a bare '1801' that is not in the structured error-number position" {
        # Round 8 Blocker 7: the prior regex ('\b1801\b') matched a standalone "1801" anywhere in the
        # output - a row count, a line number, or any other unrelated number could be misclassified
        # as the benign race. Only the anchored "Msg 1801," form counts.
        Test-MssqlDuplicateDatabaseError -CapturedOutput "Rows affected: 1801" | Should -BeFalse
        Test-MssqlDuplicateDatabaseError -CapturedOutput "(1801 rows affected)" | Should -BeFalse
    }

    It "does not swallow malformed or empty output" {
        Test-MssqlDuplicateDatabaseError -CapturedOutput "" | Should -BeFalse
    }
}

Describe "Exit-code-independent-of-error-text (Phase 1a design invariant)" {
    BeforeAll {
        $script:dockerComposeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
        Import-Module (Join-Path $script:dockerComposeRoot "database-safety.psm1") -Force
    }

    It "PostgreSQL detection depends on the SQLSTATE token, not a particular human-readable phrase" {
        # Same SQLSTATE, two different hypothetical locale/verbosity message bodies -- both must
        # be recognized, proving the match is on the code, not the surrounding text.
        Test-PostgresDuplicateDatabaseError -CapturedOutput 'ERROR:  42P04: la base de datos "x" ya existe' | Should -BeTrue
        Test-PostgresDuplicateDatabaseError -CapturedOutput 'ERROR:  42P04: database "x" already exists' | Should -BeTrue
    }
}

Describe "Compose-rendering oracle (empirical parity with local-config.yml / published-config.yml)" {
    BeforeAll {
        $script:dockerComposeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
        Import-Module (Join-Path $script:dockerComposeRoot "env-utility.psm1") -Force
        Import-Module (Join-Path $script:dockerComposeRoot "database-safety.psm1") -Force

        # Captured empirically (DMS-1270 Phase 1a spike) by rendering the genuine, checked-in
        # nested Compose fallback with a real Docker Compose invocation:
        #   docker compose -f postgresql.yml -f local-config.yml --env-file <fixture> config
        # where the fixture env file defined only POSTGRES_DB_NAME=edfi_datamanagementservice and
        # POSTGRES_PASSWORD=abcdefgh1! (DMS_CONFIG_DATABASE_CONNECTION_STRING left entirely absent,
        # matching an old .env predating that key). Docker Compose v5.1.3 rendered
        # DatabaseSettings__DatabaseConnection as the string below, verbatim. This is a frozen
        # empirical fixture, not a live Docker dependency of this suite -- re-capture it manually
        # if the checked-in nested-fallback syntax in those two files ever changes.
        $script:composeRenderedDefault = 'host=dms-postgresql;port=5432;username=postgres;password=abcdefgh1!;database=edfi_datamanagementservice;'

        # Captured empirically (DMS-1270 Phase 1a Round 9 spike) by running the production
        # Resolve-CmsDatabaseTopologyEnvironmentFile function (separate mode, against a base file
        # shaped exactly like today's checked-in templates - connection string present,
        # DMS_CONFIG_DATABASE_NAME absent) and feeding the ACTUAL resulting derived file to a real
        # Docker Compose invocation:
        #   docker compose -f postgresql.yml -f local-config.yml --env-file <derived file> config
        # This uncovered a genuine bug, since fixed (Move-EnvFileKeyBeforeAnotherKey): Docker
        # Compose's --env-file interpolation is order-dependent, like shell `source` semantics - a
        # forward reference (DMS_CONFIG_DATABASE_NAME's line appearing after the connection string
        # that references it) rendered database= as EMPTY, not the intended database name. After the
        # fix, Docker Compose v5.1.3 rendered DatabaseSettings__DatabaseConnection as the string below.
        $script:composeRenderedMigratedSeparateMode = 'host=dms-postgresql;port=5432;username=postgres;password=abcdefgh1!;database=edfi_configurationservice;'
    }

    BeforeEach {
        # Round 9 Blocker 4: this block calls Confirm-CmsDatabaseTopologyAgreement (and now
        # Resolve-CmsDatabaseTopologyEnvironmentFile too) without clearing ambient state, so a
        # leftover shell value for a datastore name, password, or the connection string itself could
        # silently change what these tests exercise - the same hermeticity already applied to the
        # other two Describe blocks.
        $script:ambientKeys = @(
            "POSTGRES_DB_NAME", "MSSQL_DB_NAME", "POSTGRES_PASSWORD", "MSSQL_SA_PASSWORD",
            "DMS_CONFIG_DATABASE_NAME", "DMS_CONFIG_DATABASE_CONNECTION_STRING", "DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE"
        )
        $script:ambientSnapshot = @{}
        foreach ($key in $script:ambientKeys) {
            $script:ambientSnapshot[$key] = [System.Environment]::GetEnvironmentVariable($key)
            Remove-Item "Env:\$key" -ErrorAction SilentlyContinue
        }
    }

    AfterEach {
        foreach ($key in $script:ambientKeys) {
            if ($null -eq $script:ambientSnapshot[$key]) {
                Remove-Item "Env:\$key" -ErrorAction SilentlyContinue
            }
            else {
                [System.Environment]::SetEnvironmentVariable($key, $script:ambientSnapshot[$key])
            }
        }
    }

    It "the checked-in local-config.yml / published-config.yml nested fallback still matches the captured oracle text" {
        # Guards against silent drift: if either file's nested default is ever edited without
        # re-running the live oracle capture, this fails loudly instead of the fixture going stale.
        # The database segment is itself a nested default so the fallback honors the topology seam:
        # DMS_CONFIG_DATABASE_NAME when set, POSTGRES_DB_NAME otherwise. Re-captured live against
        # Docker Compose for all three shapes (explicit key, seam set, seam absent) when this form
        # was introduced; both frozen oracle strings below matched the render unchanged.
        $localConfig = Get-Content -LiteralPath (Join-Path $script:dockerComposeRoot "local-config.yml") -Raw
        $publishedConfig = Get-Content -LiteralPath (Join-Path $script:dockerComposeRoot "published-config.yml") -Raw
        $expectedNestedSyntax = 'DMS_CONFIG_DATABASE_CONNECTION_STRING:-host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_PASSWORD};database=${DMS_CONFIG_DATABASE_NAME:-${POSTGRES_DB_NAME}};'

        $localConfig | Should -Match ([regex]::Escape($expectedNestedSyntax))
        $publishedConfig | Should -Match ([regex]::Escape($expectedNestedSyntax))
    }

    It "the nested fallback resolves the seam for separate mode and the datastore name otherwise" {
        # Both captured oracle strings come from the same nested fallback, differing only in whether
        # DMS_CONFIG_DATABASE_NAME was set - which is precisely the behavior the nesting exists for.
        # Asserting the resolver agrees with both renders pins the seam's two outcomes together.
        $sharedValues = @{ POSTGRES_DB_NAME = 'edfi_datamanagementservice'; POSTGRES_PASSWORD = 'abcdefgh1!' }
        $separateValues = @{ POSTGRES_DB_NAME = 'edfi_datamanagementservice'; POSTGRES_PASSWORD = 'abcdefgh1!'; DMS_CONFIG_DATABASE_NAME = 'edfi_configurationservice' }

        $sharedName = Get-ComposeResolvedEnvValue -EnvironmentValues $sharedValues -Name "DMS_CONFIG_DATABASE_NAME" -DefaultValue $sharedValues.POSTGRES_DB_NAME
        $separateName = Get-ComposeResolvedEnvValue -EnvironmentValues $separateValues -Name "DMS_CONFIG_DATABASE_NAME" -DefaultValue $separateValues.POSTGRES_DB_NAME

        (Get-CmsDatabaseTopologyDefaultConnectionString -ExpectedHost "dms-postgresql" -ExpectedPort "5432" -ExpectedDatabaseName $sharedName -PostgresPassword 'abcdefgh1!') |
            Should -BeExactly $script:composeRenderedDefault
        (Get-CmsDatabaseTopologyDefaultConnectionString -ExpectedHost "dms-postgresql" -ExpectedPort "5432" -ExpectedDatabaseName $separateName -PostgresPassword 'abcdefgh1!') |
            Should -BeExactly $script:composeRenderedMigratedSeparateMode
    }

    It "Get-CmsDatabaseTopologyDefaultConnectionString's construction matches the real Compose-rendered value byte-for-byte" {
        # Round 8 Blocker 6: the prior oracle test only checked "does not throw" on the production
        # validator, which proves internal self-consistency but not that the constructed default is
        # textually identical to what Compose actually renders. Comparing the extracted, independently
        # testable construction function directly against the captured oracle string closes that gap.
        $constructed = Get-CmsDatabaseTopologyDefaultConnectionString -ExpectedHost "dms-postgresql" -ExpectedPort "5432" -ExpectedDatabaseName "edfi_datamanagementservice" -PostgresPassword "abcdefgh1!"
        $constructed | Should -BeExactly $script:composeRenderedDefault
    }

    It "Confirm-CmsDatabaseTopologyAgreement's absent-key default agrees with the real Compose-rendered value" {
        $work = Join-Path ([System.IO.Path]::GetTempPath()) "dms-cms-topology-oracle-$([Guid]::NewGuid().ToString('N'))"
        New-Item -ItemType Directory -Path $work -Force | Out-Null
        try {
            $path = Join-Path $work ".env"
            Set-Content -LiteralPath $path -Value (@(
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'POSTGRES_PASSWORD=abcdefgh1!'
            ) -join "`n") -NoNewline

            # DMS_CONFIG_DATABASE_CONNECTION_STRING is absent, so the production function must
            # construct the same default Compose renders and validate cleanly against it.
            { Confirm-CmsDatabaseTopologyAgreement -EnvironmentFile $path -DatabaseEngine "postgresql" } | Should -Not -Throw
        }
        finally {
            Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It "the extractor functions parse the genuine Compose-rendered oracle string identically to a hand-written fixture" {
        $names = @(Get-DatabaseNameFromResolvedConnectionString -ConnectionString $script:composeRenderedDefault)
        $names | Should -Be @("edfi_datamanagementservice")

        # PostgreSQL's own connection-string shape carries port as a standalone "port=" key - now
        # correctly recognized (Round 8 Blocker 4 fix) rather than silently defaulted.
        $endpoints = @(Get-EndpointFromResolvedConnectionString -ConnectionString $script:composeRenderedDefault -DatabaseEngine "postgresql")
        $endpoints[0].Host | Should -Be "dms-postgresql"
        $endpoints[0].Port | Should -Be "5432"
    }

    It "the production migration function's actual derived file, run through Confirm-CmsDatabaseTopologyAgreement, agrees with the real Compose-rendered migrated value" {
        # Round 9 Blocker 3: the prior oracle only covered the absent-key default construction, never
        # the migration/serialization path - which is exactly where the order-dependent-interpolation
        # bug this round found and fixed was hiding. This exercises the real, unmodified production
        # function end to end and validates its output against the empirically-captured oracle above.
        $work = Join-Path ([System.IO.Path]::GetTempPath()) "dms-cms-topology-migration-oracle-$([Guid]::NewGuid().ToString('N'))"
        New-Item -ItemType Directory -Path $work -Force | Out-Null
        try {
            $basePath = Join-Path $work ".env.base"
            Set-Content -LiteralPath $basePath -Value (@(
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'POSTGRES_PASSWORD=abcdefgh1!',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_PASSWORD};database=${POSTGRES_DB_NAME};'
            ) -join "`n") -NoNewline

            $derived = Resolve-CmsDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $basePath -DatabaseEngine "postgresql" -SeparateConfigDatabase -DockerComposeRoot $work

            { Confirm-CmsDatabaseTopologyAgreement -EnvironmentFile $derived -DatabaseEngine "postgresql" } | Should -Not -Throw -Because "the migrated file must validate against its own now-separate-mode target"

            $migratedConnectionString = Get-ComposeResolvedEnvValue -EnvironmentValues (ReadValuesFromEnvFile $derived) -Name "DMS_CONFIG_DATABASE_CONNECTION_STRING"
            $migratedConnectionString | Should -BeExactly $script:composeRenderedMigratedSeparateMode -Because "this is the exact value a real Docker Compose invocation rendered for this same derived file"
        }
        finally {
            Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

Describe "start-local-dms.ps1 / start-published-dms.ps1 CMS database topology wiring (DMS-1270 Phase 1b)" {
    BeforeAll {
        $script:dockerComposeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
        Import-Module (Join-Path $script:dockerComposeRoot "env-utility.psm1") -Force

        # Runs a start script with a "docker" stand-in in place and reports everything the run is
        # asserted on: the recorded docker invocations, the terminating error, and the files the run
        # added under eng/docker-compose/.derived/.
        #
        # The stand-in is a global PowerShell *function* named docker, not an executable on PATH.
        # PowerShell resolves functions ahead of external commands, and function lookup walks the
        # scope chain into the `&`-invoked start script, so this intercepts every `docker ...` call
        # with no PATH manipulation and no platform-specific shim file. That matters because the
        # registered CI job for these tests runs on ubuntu-latest, where a docker.cmd would not be
        # resolved as `docker` at all and the runner's real Docker would be invoked instead - which
        # for `docker compose ... up` would start actual containers.
        #
        # It succeeds for the `network` subcommands the start scripts issue before any compose call,
        # then fails the first `compose` subcommand. That first compose invocation already carries
        # the complete -f file set, so the recorded arguments are the real compose file set the
        # script built, and the immediately-following exit-code check turns the failure into a
        # specific, assertable error rather than an ambient "docker is missing" one. The start
        # scripts' own topology wiring is pure PowerShell that runs to completion before any of
        # this, so its derived-file side effects are observable too.
        #
        # Deliberately a single function rather than composed helpers: `& $ScriptBlock` executes in a
        # child scope, so a nested helper cannot assign a result back into the caller's scope, and a
        # nested arrangement silently reported $null instead.
        #
        # The start scripts always resolve their compose root to their own directory, so the real
        # .derived/ is the only place they will write. Every fixture therefore uses a GUID-bearing
        # env-file leaf name, so derived files (named <leaf>.<token>) cannot collide between tests or
        # overwrite a developer's existing .derived/.env.mssql.
        function script:Invoke-StartScript {
            param([scriptblock]$ScriptBlock)

            # -Force is required on every .derived enumeration in this Describe, not optional
            # tidiness: derived files are all dot-prefixed, and Linux PowerShell treats a leading dot
            # as hidden, so without it the snapshots come back empty, every derived-file assertion
            # silently matches nothing, and the cleanup leaves files behind. Windows has no such
            # attribute, so omitting it passes locally and fails only on the ubuntu-latest CI runner.
            $derivedDir = Join-Path $script:dockerComposeRoot ".derived"
            $before = @{}
            if (Test-Path $derivedDir) {
                foreach ($name in (Get-ChildItem $derivedDir -Name -Force)) { $before[$name] = $true }
            }

            # The invoked start scripts write these five identity variables into the process environment
            # and nothing restores them - Get-BootstrapEnvSnapshot covers only the seven claims/schema
            # names. Measured: without this, running this Describe left DMS_CONFIG_IDENTITY_PROVIDER set
            # for every later test in the session, which is the order-dependence the ownership harness
            # below exists to eliminate. Presence is captured separately from value, because "absent" and
            # "present and empty" are different states.
            $identityEnvironmentName = @(
                'DMS_CONFIG_IDENTITY_PROVIDER', 'OAUTH_TOKEN_ENDPOINT', 'DMS_JWT_AUTHORITY',
                'DMS_JWT_METADATA_ADDRESS', 'DMS_CONFIG_IDENTITY_AUTHORITY'
            )
            $identityEnvironmentState = @{}
            foreach ($name in $identityEnvironmentName) {
                $identityEnvironmentState[$name] = @{
                    Present = (Test-Path -LiteralPath "Env:\$name")
                    Value   = [System.Environment]::GetEnvironmentVariable($name)
                }
            }

            # The stand-in has to be global to be visible inside the invoked start script, but the
            # list it records into is captured by closure rather than parked in a global variable,
            # so nothing of this harness leaks into the session beyond the function itself.
            $recorded = [System.Collections.Generic.List[string]]::new()
            $hadRealDocker = $null -ne (Get-Command docker -CommandType Application -ErrorAction SilentlyContinue)
            $caught = $null
            try {
                # Recorded as a single space-joined string per invocation, which is what the
                # assertions match against. The callers splat array variables (the compose -f file
                # set, the up flags), and a PowerShell function receives each of those as a single
                # array object rather than the flattened argv a native command would get - so
                # enumerate one level through the pipeline before joining, or the whole file set
                # renders as "System.Object[]" and every file-set assertion silently matches nothing.
                Set-Item -Path Function:\global:docker -Value {
                    $flattened = @($args | ForEach-Object { $_ })
                    $recorded.Add(($flattened -join " "))
                    if ($flattened.Count -gt 0 -and $flattened[0] -eq "compose") {
                        $global:LASTEXITCODE = 1
                    }
                    else {
                        $global:LASTEXITCODE = 0
                    }
                }.GetNewClosure()

                & $ScriptBlock
            }
            catch {
                $caught = $_
            }
            finally {
                Remove-Item Function:\docker -Force -ErrorAction SilentlyContinue

                foreach ($name in $identityEnvironmentName) {
                    $saved = $identityEnvironmentState[$name]
                    if ($saved.Present) {
                        [System.Environment]::SetEnvironmentVariable($name, $saved.Value)
                    }
                    else {
                        # Remove-Item, never SetEnvironmentVariable($name, $null): the latter leaves a
                        # present-but-blank variable in this environment instead of removing it.
                        Remove-Item -LiteralPath "Env:\$name" -Force -ErrorAction SilentlyContinue
                    }
                }
            }

            # The interception must have been the thing that stopped the run; if a real docker
            # executable were reached instead, these tests would be starting containers.
            if ($hadRealDocker -and (Get-Command docker -CommandType Function -ErrorAction SilentlyContinue)) {
                throw "The docker stand-in outlived the run; refusing to continue with a live docker on PATH."
            }

            $after = if (Test-Path $derivedDir) { @(Get-ChildItem $derivedDir -Name -Force) } else { @() }
            $newDerived = @($after | Where-Object { -not $before.ContainsKey($_) })
            $invocations = @($recorded)

            return [PSCustomObject]@{
                Invocations     = $invocations
                ComposeCommand  = ($invocations | Where-Object { $_ -like "compose *" } | Select-Object -First 1)
                Error           = $caught
                ErrorMessage    = if ($null -ne $caught) { $caught.Exception.Message } else { $null }
                NewDerivedFiles = $newDerived
                TopologyFile    = ($newDerived | Where-Object { $_ -like "*.topology" } | Select-Object -First 1)
            }
        }

        # Reads a derived file produced by a run under test.
        function script:ReadDerivedTopologyFile {
            param([string]$Name)
            return ReadValuesFromEnvFile (Join-Path (Join-Path $script:dockerComposeRoot ".derived") $Name)
        }

        # Writes a base env file under a unique leaf name and returns its path. Extra lines are
        # appended after the shared minimum.
        function script:New-WiringEnvFile {
            param([string[]]$AdditionalLines = @())

            $path = Join-Path $script:work ".env.wiring-$([Guid]::NewGuid().ToString('N'))"
            $lines = @(
                'POSTGRES_PASSWORD=abcdefgh1!',
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'DMS_CONFIG_IDENTITY_PROVIDER=self-contained'
            ) + $AdditionalLines
            Set-Content -LiteralPath $path -NoNewline -Value ($lines -join "`n")
            return $path
        }

        # A base env file already declaring the MSSQL engine, so the engine overlay is recognized as
        # composed and the CMS connection string under test is the one that gets validated.
        function script:New-MssqlWiringEnvFile {
            param([string]$CmsConnectionString)

            return New-WiringEnvFile -AdditionalLines @(
                'MSSQL_SA_PASSWORD=abcdefgh1!',
                'MSSQL_DB_NAME=edfi_datamanagementservice',
                'DMS_DATASTORE=mssql',
                'DMS_CONFIG_DATASTORE=mssql',
                "DMS_CONFIG_DATABASE_CONNECTION_STRING=$CmsConnectionString"
            )
        }
    }

    AfterAll {
        # Defensive: Invoke-StartScript removes this itself, including on failure.
        Remove-Item Function:\docker -Force -ErrorAction SilentlyContinue
    }

    BeforeEach {
        # Ambient hermeticity, which this Describe previously had none of - unlike the other three, it
        # relied on the start scripts reading their env FILE. That stopped being enough: the topology
        # resolver treats a declaration whose own key the ambient environment supplies as inert (Compose
        # ignores the file's value for it entirely), so an ambient value for any name a fixture declares
        # now changes which branch the run takes. Measured against the unsafe-reorder fixture: with
        # ambient CMS_MOVE_PROOF_FEATURE or CMS_MOVE_PROOF_PW set, the reorder was permitted, the run
        # reached the recording docker boundary, and the test failed on "Failed to start Postgresql".
        #
        # The inventory is every name the fixtures below declare, not just the two that exposed the gap -
        # each one is behaviourally significant for the same reason. Presence is captured separately from
        # value, because "absent" and "present and empty" are different states, and restoring uses
        # Remove-Item for the absent case: SetEnvironmentVariable(name, $null) leaves a present-but-blank
        # variable in this environment rather than removing it.
        #
        # DMS_CONFIG_IDENTITY_PROVIDER appears here as a name the fixtures declare. Invoke-StartScript
        # separately snapshots it (with four siblings) because the start scripts WRITE it; the two compose
        # cleanly - the inner restore returns it to whatever it was when the script was invoked, and this
        # outer restore returns it to the developer's own value.
        $script:wiringAmbientKeys = @(
            "POSTGRES_DB_NAME", "POSTGRES_PASSWORD", "MSSQL_DB_NAME", "MSSQL_SA_PASSWORD",
            "DMS_DATASTORE", "DMS_CONFIG_DATASTORE", "DMS_CONFIG_IDENTITY_PROVIDER",
            "DMS_CONFIG_DATABASE_NAME", "DMS_CONFIG_DATABASE_CONNECTION_STRING",
            "DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE",
            "CMS_MOVE_PROOF_FEATURE", "CMS_MOVE_PROOF_PW"
        )
        $script:wiringAmbientSnapshot = @{}
        foreach ($key in $script:wiringAmbientKeys) {
            $script:wiringAmbientSnapshot[$key] = @{
                Present = (Test-Path -LiteralPath "Env:\$key")
                Value   = [System.Environment]::GetEnvironmentVariable($key)
            }
            Remove-Item -LiteralPath "Env:\$key" -Force -ErrorAction SilentlyContinue
        }

        $script:work = Join-Path ([System.IO.Path]::GetTempPath()) "dms-startscript-wiring-$([Guid]::NewGuid().ToString('N'))"
        New-Item -ItemType Directory -Path $script:work -Force | Out-Null
        $derivedDir = Join-Path $script:dockerComposeRoot ".derived"
        $script:derivedBefore = @{}
        if (Test-Path $derivedDir) {
            foreach ($name in (Get-ChildItem $derivedDir -Name -Force)) { $script:derivedBefore[$name] = $true }
        }
    }

    AfterEach {
        foreach ($key in $script:wiringAmbientKeys) {
            $saved = $script:wiringAmbientSnapshot[$key]
            if ($saved.Present) {
                [System.Environment]::SetEnvironmentVariable($key, $saved.Value)
            }
            else {
                Remove-Item -LiteralPath "Env:\$key" -Force -ErrorAction SilentlyContinue
            }
        }

        if (Test-Path -LiteralPath $script:work) {
            Remove-Item -LiteralPath $script:work -Recurse -Force -ErrorAction SilentlyContinue
        }
        $derivedDir = Join-Path $script:dockerComposeRoot ".derived"
        if (Test-Path $derivedDir) {
            foreach ($name in (Get-ChildItem $derivedDir -Name -Force)) {
                if (-not $script:derivedBefore.ContainsKey($name)) {
                    Remove-Item (Join-Path $derivedDir $name) -Force -ErrorAction SilentlyContinue
                }
            }
        }
    }

    # Pester's discovery/run-phase separation does not reliably close over a plain PowerShell
    # `foreach` loop variable referenced inside `It` script blocks, so the two entry-point scripts
    # are covered by duplicated Context blocks (below) rather than a loop over their names.

    Context "start-local-dms.ps1" {
        It "postgresql separate mode: migrates DMS_CONFIG_DATABASE_NAME and reaches the docker boundary" {
            # PostgreSQL is at parity with SQL Server: the same topology-write sequence runs, so
            # -SeparateConfigDatabase is accepted rather than rejected by an engine guard.
            $envFile = New-WiringEnvFile

            $run = Invoke-StartScript {
                & "$script:dockerComposeRoot/start-local-dms.ps1" -SeparateConfigDatabase -DatabaseEngine postgresql -EnvironmentFile $envFile -InfraOnly *>$null
            }

            $run.ErrorMessage | Should -Not -BeLike "*not yet supported*"
            $run.TopologyFile | Should -Not -BeNullOrEmpty -Because "separate mode must write a topology-derived file on PostgreSQL too"

            $values = ReadDerivedTopologyFile -Name $run.TopologyFile
            $values["DMS_CONFIG_DATABASE_NAME"] | Should -Be "edfi_configurationservice"
            $values["DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE"] | Should -Be "true"

            $run.ComposeCommand | Should -BeLike "*--env-file *$($run.TopologyFile)*"
        }

        It "postgresql shared mode: writes the seam without redirecting it away from POSTGRES_DB_NAME" {
            $envFile = New-WiringEnvFile

            $run = Invoke-StartScript {
                & "$script:dockerComposeRoot/start-local-dms.ps1" -DatabaseEngine postgresql -EnvironmentFile $envFile -InfraOnly *>$null
            }

            $run.TopologyFile | Should -Not -BeNullOrEmpty -Because "the seam is written unconditionally so old .env files predating the key still resolve"
            $values = ReadDerivedTopologyFile -Name $run.TopologyFile
            $values["DMS_CONFIG_DATABASE_NAME"] | Should -Be "edfi_datamanagementservice"
            $values["DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE"] | Should -Be "false"

            $run.ComposeCommand | Should -Not -BeNullOrEmpty
        }

        It "shared mode (switch omitted): does not migrate DMS_CONFIG_DATABASE_NAME away from its .env.mssql alias" {
            $envFile = New-WiringEnvFile

            $run = Invoke-StartScript {
                & "$script:dockerComposeRoot/start-local-dms.ps1" -DatabaseEngine mssql -EnvironmentFile $envFile -InfraOnly *>$null
            }

            # Only the engine-overlay composition (<leaf>.mssql) is expected: the checked-in
            # .env.mssql already aliases DMS_CONFIG_DATABASE_NAME to MSSQL_DB_NAME, so the topology
            # function correctly recognizes shared mode as already-correct and writes nothing
            # further - no additional ".topology" derived file.
            $run.NewDerivedFiles | Should -HaveCount 1
            $run.NewDerivedFiles[0] | Should -BeLike "*.mssql"
            $run.TopologyFile | Should -BeNullOrEmpty

            # The run must have reached the docker boundary and stopped exactly there, not failed
            # earlier for an unrelated reason that would make the assertions above vacuous.
            $run.ComposeCommand | Should -Not -BeNullOrEmpty -Because "wiring must complete and reach the compose invocation"
            $run.ErrorMessage | Should -BeLike "*Failed to start SQL Server. Exit code 1*"
        }

        It "separate mode (-SeparateConfigDatabase): migrates DMS_CONFIG_DATABASE_NAME to edfi_configurationservice" {
            $envFile = New-WiringEnvFile

            $run = Invoke-StartScript {
                & "$script:dockerComposeRoot/start-local-dms.ps1" -DatabaseEngine mssql -SeparateConfigDatabase -EnvironmentFile $envFile -InfraOnly *>$null
            }

            $run.TopologyFile | Should -Not -BeNullOrEmpty -Because "separate mode must write a further-derived file on top of the engine overlay"

            $values = ReadDerivedTopologyFile -Name $run.TopologyFile
            $values["DMS_CONFIG_DATABASE_NAME"] | Should -Be "edfi_configurationservice"
            $values["DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE"] | Should -Be "true"

            # The derived topology file must be the one actually handed to Compose, not merely
            # written and then dropped on the floor.
            $run.ComposeCommand | Should -BeLike "*--env-file *$($run.TopologyFile)*"
            $run.ErrorMessage | Should -BeLike "*Failed to start SQL Server. Exit code 1*"
        }

        It "-DmsOnly: cmsParticipates is false, so the Phase 2 postgresql guard never fires (today's -DmsOnly shape is preserved)" {
            $envFile = New-WiringEnvFile

            $run = Invoke-StartScript {
                & "$script:dockerComposeRoot/start-local-dms.ps1" -DmsOnly -SeparateConfigDatabase -DatabaseEngine postgresql -EnvironmentFile $envFile *>$null
            }

            $run.ErrorMessage | Should -Not -BeLike "*Phase 2*" -Because "-DmsOnly is excluded from cmsParticipates, so the whole gate (including the postgresql guard) must be skipped, not just bypassed with a different error"
            $run.ComposeCommand | Should -Not -BeNullOrEmpty -Because "the run must proceed past the gate to the docker boundary"
        }

        It "-DmsOnly: does not write a CMS topology-derived file even with -SeparateConfigDatabase (cmsParticipates is false, so the topology functions never run)" {
            $envFile = New-WiringEnvFile

            $run = Invoke-StartScript {
                & "$script:dockerComposeRoot/start-local-dms.ps1" -DmsOnly -DatabaseEngine mssql -SeparateConfigDatabase -EnvironmentFile $envFile *>$null
            }

            $run.TopologyFile | Should -BeNullOrEmpty -Because "Resolve-CmsDatabaseTopologyEnvironmentFile must not run when cmsParticipates is false"
            $run.ComposeCommand | Should -Not -BeNullOrEmpty -Because "the run must proceed to the docker boundary"
        }

        # Two independent signals let a documented continuation past the legacy shared-mode invariant,
        # and each is exercised on its own here: the EXPLICIT SWITCH (a caller declaration, whatever
        # the file says) and the RESERVED DEDICATED NAME in the file's own CMS connection string (a
        # content declaration, no switch needed). The topology marker itself lives only in derived
        # files, so neither continuation carries it.
        It "mssql -DmsOnly -SeparateConfigDatabase: accepts a caller-authored file the reserved-name signal alone would reject" {
            # A third database name is not the reserved dedicated name, so only the explicit switch can
            # carry this file through. CMS does not start in this shape at all, so judging a shared-mode
            # invariant against a topology the caller explicitly declined would reject the documented
            # "accepted, gated no-op" continuation.
            $envFile = New-MssqlWiringEnvFile -CmsConnectionString 'Server=dms-mssql,1433;Database=caller_authored_cms;User Id=sa;Password=abcdefgh1!;TrustServerCertificate=true;'

            $run = Invoke-StartScript {
                & "$script:dockerComposeRoot/start-local-dms.ps1" -DmsOnly -DatabaseEngine mssql -SeparateConfigDatabase -EnvironmentFile $envFile *>$null
            }

            $run.ErrorMessage | Should -Not -BeLike "*shared-database configuration mismatch*" -Because "the shared-mode invariant is definitionally inapplicable to the topology the switch declares"
            $run.ComposeCommand | Should -Not -BeNullOrEmpty -Because "the continuation must reach the docker boundary"
        }

        It "mssql -DmsOnly WITHOUT the switch: accepts a file whose CMS connection string targets the reserved dedicated database" {
            # The standalone continuation passes no switch, so the file's own reserved-name target is
            # the only separate-topology signal available.
            $envFile = New-MssqlWiringEnvFile -CmsConnectionString 'Server=dms-mssql,1433;Database=edfi_configurationservice;User Id=sa;Password=abcdefgh1!;TrustServerCertificate=true;'

            $run = Invoke-StartScript {
                & "$script:dockerComposeRoot/start-local-dms.ps1" -DmsOnly -DatabaseEngine mssql -EnvironmentFile $envFile *>$null
            }

            $run.ErrorMessage | Should -Not -BeLike "*shared-database configuration mismatch*"
            $run.ComposeCommand | Should -Not -BeNullOrEmpty
        }

        It "mssql -DmsOnly WITHOUT the switch: still rejects a file targeting some third database (today's behavior preserved)" {
            # Neither signal is present, so the legacy check runs exactly as it does today.
            $envFile = New-MssqlWiringEnvFile -CmsConnectionString 'Server=dms-mssql,1433;Database=caller_authored_cms;User Id=sa;Password=abcdefgh1!;TrustServerCertificate=true;'

            $run = Invoke-StartScript {
                & "$script:dockerComposeRoot/start-local-dms.ps1" -DmsOnly -DatabaseEngine mssql -EnvironmentFile $envFile *>$null
            }

            $run.ErrorMessage | Should -BeLike "*shared-database configuration mismatch*" -Because "with no switch and no separate-topology content, today's shared-mode check runs exactly as before"
            $run.Invocations | Should -BeNullOrEmpty
        }
    }

    Context "start-published-dms.ps1" {
        It "postgresql separate mode: migrates DMS_CONFIG_DATABASE_NAME and reaches the docker boundary" {
            $envFile = New-WiringEnvFile

            $run = Invoke-StartScript {
                & "$script:dockerComposeRoot/start-published-dms.ps1" -SeparateConfigDatabase -DatabaseEngine postgresql -EnvironmentFile $envFile -InfraOnly *>$null
            }

            $run.ErrorMessage | Should -Not -BeLike "*not yet supported*"
            $run.TopologyFile | Should -Not -BeNullOrEmpty -Because "separate mode must write a topology-derived file on PostgreSQL too"

            $values = ReadDerivedTopologyFile -Name $run.TopologyFile
            $values["DMS_CONFIG_DATABASE_NAME"] | Should -Be "edfi_configurationservice"
            $values["DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE"] | Should -Be "true"

            $run.ComposeCommand | Should -BeLike "*--env-file *$($run.TopologyFile)*"
        }

        It "stops before docker, and leaves no derived artifact, when the repair would change an unrelated value" {
            # The end-to-end consequence of the reorder proof: a file whose repair would silently change
            # a variable the seam does not own must never reach a compose invocation, and the
            # half-repaired artifact produced on the way must not be left behind for a later run.
            $envFile = New-WiringEnvFile -AdditionalLines @(
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;port=5432;username=postgres;password=${CMS_MOVE_PROOF_PW};database=${POSTGRES_DB_NAME};',
                'CMS_MOVE_PROOF_FEATURE=${CMS_MOVE_PROOF_PW:-disabled}',
                'CMS_MOVE_PROOF_PW=abcdefgh1!'
            )

            $run = Invoke-StartScript {
                & "$script:dockerComposeRoot/start-published-dms.ps1" -DatabaseEngine postgresql -EnvironmentFile $envFile -InfraOnly *>$null
            }

            $run.ErrorMessage | Should -BeLike "*renders for 'CMS_MOVE_PROOF_FEATURE'*"
            $run.ErrorMessage | Should -Not -BeLike "*abcdefgh1!*" -Because "the diagnostic names keys and lines, never values"
            $run.Invocations | Should -BeNullOrEmpty -Because "no docker boundary may be reached once the repair has failed closed"
            $run.NewDerivedFiles | Should -BeNullOrEmpty -Because "the artifact this run created is removed on failure"
        }

        It "postgresql shared mode: writes the seam without redirecting it away from POSTGRES_DB_NAME" {
            $envFile = New-WiringEnvFile

            $run = Invoke-StartScript {
                & "$script:dockerComposeRoot/start-published-dms.ps1" -DatabaseEngine postgresql -EnvironmentFile $envFile -InfraOnly *>$null
            }

            $run.TopologyFile | Should -Not -BeNullOrEmpty
            $values = ReadDerivedTopologyFile -Name $run.TopologyFile
            $values["DMS_CONFIG_DATABASE_NAME"] | Should -Be "edfi_datamanagementservice"
            $values["DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE"] | Should -Be "false"

            $run.ComposeCommand | Should -Not -BeNullOrEmpty
        }

        It "shared mode (switch omitted): does not migrate DMS_CONFIG_DATABASE_NAME away from its .env.mssql alias" {
            $envFile = New-WiringEnvFile

            $run = Invoke-StartScript {
                & "$script:dockerComposeRoot/start-published-dms.ps1" -DatabaseEngine mssql -EnvironmentFile $envFile -InfraOnly *>$null
            }

            $run.NewDerivedFiles | Should -HaveCount 1
            $run.NewDerivedFiles[0] | Should -BeLike "*.mssql"
            $run.TopologyFile | Should -BeNullOrEmpty

            $run.ComposeCommand | Should -Not -BeNullOrEmpty -Because "wiring must complete and reach the compose invocation"
            $run.ErrorMessage | Should -BeLike "*Failed to start SQL Server. Exit code 1*"
        }

        It "separate mode (-SeparateConfigDatabase): migrates DMS_CONFIG_DATABASE_NAME to edfi_configurationservice" {
            $envFile = New-WiringEnvFile

            $run = Invoke-StartScript {
                & "$script:dockerComposeRoot/start-published-dms.ps1" -DatabaseEngine mssql -SeparateConfigDatabase -EnvironmentFile $envFile -InfraOnly *>$null
            }

            $run.TopologyFile | Should -Not -BeNullOrEmpty -Because "separate mode must write a further-derived file on top of the engine overlay"

            $values = ReadDerivedTopologyFile -Name $run.TopologyFile
            $values["DMS_CONFIG_DATABASE_NAME"] | Should -Be "edfi_configurationservice"
            $values["DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE"] | Should -Be "true"

            $run.ComposeCommand | Should -BeLike "*--env-file *$($run.TopologyFile)*"
            $run.ErrorMessage | Should -BeLike "*Failed to start SQL Server. Exit code 1*"
        }

        It "-DmsOnly: cmsParticipates is false, so the Phase 2 postgresql guard never fires (today's -DmsOnly shape is preserved)" {
            $envFile = New-WiringEnvFile

            $run = Invoke-StartScript {
                & "$script:dockerComposeRoot/start-published-dms.ps1" -DmsOnly -SeparateConfigDatabase -DatabaseEngine postgresql -EnvironmentFile $envFile *>$null
            }

            $run.ErrorMessage | Should -Not -BeLike "*Phase 2*" -Because "-DmsOnly is excluded from cmsParticipates, so the whole gate (including the postgresql guard) must be skipped, not just bypassed with a different error"
            $run.ComposeCommand | Should -Not -BeNullOrEmpty -Because "the run must proceed past the gate to the docker boundary"
        }

        It "-DmsOnly: does not write a CMS topology-derived file even with -SeparateConfigDatabase (cmsParticipates is false, so the topology functions never run)" {
            $envFile = New-WiringEnvFile

            $run = Invoke-StartScript {
                & "$script:dockerComposeRoot/start-published-dms.ps1" -DmsOnly -DatabaseEngine mssql -SeparateConfigDatabase -EnvironmentFile $envFile *>$null
            }

            $run.TopologyFile | Should -BeNullOrEmpty -Because "Resolve-CmsDatabaseTopologyEnvironmentFile must not run when cmsParticipates is false"
            $run.ComposeCommand | Should -Not -BeNullOrEmpty -Because "the run must proceed to the docker boundary"
        }

        It "mssql -DmsOnly -SeparateConfigDatabase: accepts a caller-authored file the reserved-name signal alone would reject" {
            $envFile = New-MssqlWiringEnvFile -CmsConnectionString 'Server=dms-mssql,1433;Database=caller_authored_cms;User Id=sa;Password=abcdefgh1!;TrustServerCertificate=true;'

            $run = Invoke-StartScript {
                & "$script:dockerComposeRoot/start-published-dms.ps1" -DmsOnly -DatabaseEngine mssql -SeparateConfigDatabase -EnvironmentFile $envFile *>$null
            }

            $run.ErrorMessage | Should -Not -BeLike "*shared-database configuration mismatch*" -Because "the shared-mode invariant is definitionally inapplicable to the topology the switch declares"
            $run.ComposeCommand | Should -Not -BeNullOrEmpty -Because "the continuation must reach the docker boundary"
        }

        It "mssql -DmsOnly WITHOUT the switch: accepts a file whose CMS connection string targets the reserved dedicated database" {
            $envFile = New-MssqlWiringEnvFile -CmsConnectionString 'Server=dms-mssql,1433;Database=edfi_configurationservice;User Id=sa;Password=abcdefgh1!;TrustServerCertificate=true;'

            $run = Invoke-StartScript {
                & "$script:dockerComposeRoot/start-published-dms.ps1" -DmsOnly -DatabaseEngine mssql -EnvironmentFile $envFile *>$null
            }

            $run.ErrorMessage | Should -Not -BeLike "*shared-database configuration mismatch*"
            $run.ComposeCommand | Should -Not -BeNullOrEmpty
        }

        It "mssql -DmsOnly WITHOUT the switch: still rejects a file targeting some third database (today's behavior preserved)" {
            $envFile = New-MssqlWiringEnvFile -CmsConnectionString 'Server=dms-mssql,1433;Database=caller_authored_cms;User Id=sa;Password=abcdefgh1!;TrustServerCertificate=true;'

            $run = Invoke-StartScript {
                & "$script:dockerComposeRoot/start-published-dms.ps1" -DmsOnly -DatabaseEngine mssql -EnvironmentFile $envFile *>$null
            }

            $run.ErrorMessage | Should -BeLike "*shared-database configuration mismatch*" -Because "with no switch and no separate-topology content, today's shared-mode check runs exactly as before"
            $run.Invocations | Should -BeNullOrEmpty
        }

        It "rejects -DataStoreDatabaseName edfi_configurationservice with -SeparateConfigDatabase, before any docker activity" {
            # -DataStoreDatabaseName renames the DMS datastore for the CMS data-store record AFTER
            # topology validation has already run, so an unguarded collision would silently
            # reintroduce the very sharing the switch opts out of. This is the full-start shape, the
            # only one that reaches the data-store registration the parameter feeds.
            $run = Invoke-StartScript {
                & "$script:dockerComposeRoot/start-published-dms.ps1" -SeparateConfigDatabase -DataStoreDatabaseName 'edfi_configurationservice' -EnvironmentFile (New-WiringEnvFile) *>$null
            }

            $run.ErrorMessage | Should -BeLike "*-DataStoreDatabaseName cannot be 'edfi_configurationservice'*"
            $run.Invocations | Should -BeNullOrEmpty -Because "the rejection must precede any docker invocation"
        }

        It "rejects a -DataStoreDatabaseName case variant on MSSQL, where the server collation resolves it to the same database" {
            # MSSQL only, and deliberately not "both engines". -DataStoreDatabaseName does NOT reach
            # postgresql-init.sh's unquoted CREATE DATABASE - it is copied verbatim into the registered
            # datastore connection string and created by SchemaTools with a QUOTED identifier - so on
            # PostgreSQL nothing folds and a case variant is a genuinely distinct database. Asserting a
            # single answer for both engines here rejected that working PostgreSQL configuration; the
            # PostgreSQL half of this behavior is covered by its own acceptance test.
            $run = Invoke-StartScript {
                & "$script:dockerComposeRoot/start-published-dms.ps1" -DatabaseEngine mssql -SeparateConfigDatabase -DataStoreDatabaseName 'EDFI_ConfigurationService' -EnvironmentFile (New-WiringEnvFile) *>$null
            }

            $run.ErrorMessage | Should -BeLike "*-DataStoreDatabaseName cannot be 'edfi_configurationservice'*" -Because "SQL Server matches database names case-insensitively"
            $run.Invocations | Should -BeNullOrEmpty -Because "the rejection must precede any docker invocation"
        }

        It "rejects the exact reserved -DataStoreDatabaseName on MSSQL as well as PostgreSQL" {
            $run = Invoke-StartScript {
                & "$script:dockerComposeRoot/start-published-dms.ps1" -DatabaseEngine mssql -SeparateConfigDatabase -DataStoreDatabaseName 'edfi_configurationservice' -EnvironmentFile (New-WiringEnvFile) *>$null
            }

            $run.ErrorMessage | Should -BeLike "*-DataStoreDatabaseName cannot be 'edfi_configurationservice'*"
            $run.Invocations | Should -BeNullOrEmpty -Because "the rejection must precede any docker invocation"
        }

        It "accepts -DataStoreDatabaseName EDFI_ConfigurationService on PostgreSQL and proceeds to the docker boundary" {
            # SchemaTools creates the registered datastore with a QUOTED identifier and CMS stores the
            # name verbatim, so this is a physically distinct database from the dedicated CMS one -
            # measured, both coexisting in pg_database. The guard must not fire. Requiring the run to
            # reach the recording docker boundary, and the derived file to still declare separate mode,
            # proves the guard was live on this shape and simply had nothing to reject - which the
            # absence of one error message alone would not show.
            $envFile = New-WiringEnvFile

            $run = Invoke-StartScript {
                & "$script:dockerComposeRoot/start-published-dms.ps1" -DatabaseEngine postgresql -SeparateConfigDatabase -DataStoreDatabaseName 'EDFI_ConfigurationService' -EnvironmentFile $envFile *>$null
            }

            $run.ErrorMessage | Should -Not -BeLike "*-DataStoreDatabaseName cannot be*"
            $run.ComposeCommand | Should -Not -BeNullOrEmpty -Because "the run must reach the compose invocation instead of stopping at the guard"
            $run.TopologyFile | Should -Not -BeNullOrEmpty -Because "this is a real separate-mode run, so the guard was on the executed path"

            $values = ReadDerivedTopologyFile -Name $run.TopologyFile
            $values["DMS_CONFIG_DATABASE_NAME"] | Should -Be "edfi_configurationservice"
            $values["DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE"] | Should -Be "true"
        }

        It "names the parameter and the reserved literal in the rejection, but never the caller's own value" {
            $run = Invoke-StartScript {
                & "$script:dockerComposeRoot/start-published-dms.ps1" -DatabaseEngine mssql -SeparateConfigDatabase -DataStoreDatabaseName 'EDFI_ConfigurationService' -EnvironmentFile (New-WiringEnvFile) *>$null
            }

            $run.ErrorMessage | Should -BeLike "*-DataStoreDatabaseName*"
            # BeLikeExactly, because -like would match the lower-cased reserved literal the message
            # legitimately names and assert nothing.
            $run.ErrorMessage | Should -Not -BeLikeExactly "*EDFI_ConfigurationService*" -Because "the diagnostic must not echo a caller-authored value"
        }

        It "does not reject a colliding -DataStoreDatabaseName in a shape that never consumes it" {
            # -InfraOnly, -DmsOnly, and -DbOnly all return before the data-store registration, and
            # -NoDataStore skips it, so the parameter is inert in those shapes. Rejecting there would
            # contradict the documented no-op continuation behavior of the switch combination.
            foreach ($inertShape in @(
                @{ Label = '-InfraOnly'; Args = @{ InfraOnly = $true } },
                @{ Label = '-DmsOnly'; Args = @{ DmsOnly = $true } },
                @{ Label = '-DbOnly'; Args = @{ DbOnly = $true } },
                @{ Label = '-NoDataStore'; Args = @{ NoDataStore = $true } }
            )) {
                $shapeArgs = $inertShape.Args
                $run = Invoke-StartScript {
                    & "$script:dockerComposeRoot/start-published-dms.ps1" -SeparateConfigDatabase -DataStoreDatabaseName 'edfi_configurationservice' -EnvironmentFile (New-WiringEnvFile) @shapeArgs *>$null
                }

                $run.ErrorMessage | Should -Not -BeLike "*-DataStoreDatabaseName cannot be*" -Because "$($inertShape.Label) never consumes -DataStoreDatabaseName"
            }
        }

        It "reports the mutually-exclusive diagnostic, not the collision, for -NoDataStore with -SchoolYearRange" {
            # -NoDataStore and -SchoolYearRange cannot be combined at all, so this caller's actual
            # mistake is the switch pair. The collision check is new and must not mask an established
            # diagnostic that describes the shape more accurately: a caller who made this mistake and
            # happened to also pass the reserved name needs to be told about the mistake they made.
            # The answer must be the same whether or not -SeparateConfigDatabase is present, because
            # the parameter shape is invalid independently of the topology choice.
            $withSwitch = Invoke-StartScript {
                & "$script:dockerComposeRoot/start-published-dms.ps1" -SeparateConfigDatabase -NoDataStore -SchoolYearRange '2024' -DataStoreDatabaseName 'edfi_configurationservice' -EnvironmentFile (New-WiringEnvFile) *>$null
            }
            $withSwitch.ErrorMessage | Should -BeLike "*-NoDataStore and -SchoolYearRange are mutually exclusive*"
            $withSwitch.ErrorMessage | Should -Not -BeLike "*-DataStoreDatabaseName cannot be*" -Because "the collision check must not preempt the established parameter-shape diagnostic"

            $withoutSwitch = Invoke-StartScript {
                & "$script:dockerComposeRoot/start-published-dms.ps1" -NoDataStore -SchoolYearRange '2024' -DataStoreDatabaseName 'edfi_configurationservice' -EnvironmentFile (New-WiringEnvFile) *>$null
            }
            $withoutSwitch.ErrorMessage | Should -BeLike "*-NoDataStore and -SchoolYearRange are mutually exclusive*" -Because "the pre-existing diagnostic is unchanged by this story"
        }

        It "reports the -DbOnly diagnostic, not the collision, when both apply" {
            # Same ordering contract for the other shape that combines an invalid switch pair with the
            # reserved name: -DbOnly with -NoDataStore has its own established message, and it wins.
            $run = Invoke-StartScript {
                & "$script:dockerComposeRoot/start-published-dms.ps1" -SeparateConfigDatabase -DbOnly -NoDataStore -DataStoreDatabaseName 'edfi_configurationservice' -EnvironmentFile (New-WiringEnvFile) *>$null
            }

            $run.ErrorMessage | Should -BeLike "*cannot be used with -DbOnly*"
            $run.ErrorMessage | Should -Not -BeLike "*-DataStoreDatabaseName cannot be*"
        }

        It "accepts -DataStoreDatabaseName edfi_configurationservice when the switch is not requested" {
            # Without -SeparateConfigDatabase there is no dedicated-CMS-database contract to protect;
            # the pre-existing parameter keeps its pre-existing latitude.
            $run = Invoke-StartScript {
                & "$script:dockerComposeRoot/start-published-dms.ps1" -DataStoreDatabaseName 'edfi_configurationservice' -EnvironmentFile (New-WiringEnvFile) -InfraOnly *>$null
            }

            $run.ErrorMessage | Should -Not -BeLike "*-DataStoreDatabaseName cannot be*"
        }
    }

    # The published script's CMS-participation gate is narrower than the local script's: CMS must
    # also actually be in the compose set. A bare published Keycloak start (no -EnableConfig, no
    # -InfraOnly, not bootstrap mode, no -SeparateConfigDatabase) omits published-config.yml
    # entirely, so CMS never runs and this story's topology validator must not pass judgment on its
    # endpoint. These cover both halves behaviorally: the compose file set as actually built, and
    # the gate that follows from it.
    Context "start-published-dms.ps1 Configuration Service participation" {
        BeforeAll {
            # Every "bare Keycloak omits CMS" case below depends on nothing ELSE pulling CMS into the
            # compose set. Bootstrap mode does, and it is enabled by the mere presence of a staged
            # workspace at eng/docker-compose/.bootstrap - real developer state this suite must not
            # move or delete, since it can be bind-mounted into a running stack. So these cases
            # declare that precondition and skip when it does not hold, rather than silently
            # inverting their own premise.
            function script:Assert-NoStagedBootstrapWorkspace {
                $manifest = Join-Path (Join-Path $script:dockerComposeRoot ".bootstrap") "bootstrap-manifest.json"
                if (Test-Path -LiteralPath $manifest -PathType Leaf) {
                    Set-ItResult -Skipped -Because "a staged .bootstrap workspace enables bootstrap mode, which includes CMS on its own and so removes this case's premise"
                    return $false
                }
                return $true
            }
        }

        It "omits published-config.yml for a bare Keycloak start (CMS opt-in preserved)" {
            if (-not (Assert-NoStagedBootstrapWorkspace)) { return }
            $envFile = New-WiringEnvFile

            $run = Invoke-StartScript {
                & "$script:dockerComposeRoot/start-published-dms.ps1" -IdentityProvider keycloak -EnvironmentFile $envFile *>$null
            }

            $run.ComposeCommand | Should -Not -BeNullOrEmpty
            $run.ComposeCommand | Should -Not -BeLike "*published-config.yml*"
        }

        It "includes published-config.yml for -SeparateConfigDatabase under Keycloak, which would otherwise omit it" {
            if (-not (Assert-NoStagedBootstrapWorkspace)) { return }
            $envFile = New-WiringEnvFile

            $run = Invoke-StartScript {
                & "$script:dockerComposeRoot/start-published-dms.ps1" -IdentityProvider keycloak -DatabaseEngine mssql -SeparateConfigDatabase -EnvironmentFile $envFile *>$null
            }

            $run.ComposeCommand | Should -BeLike "*published-config.yml*" -Because "CMS must actually run to create the dedicated database"
            $run.TopologyFile | Should -Not -BeNullOrEmpty -Because "CMS participates in this shape, so the topology sequence must run"
        }

        It "includes published-config.yml for -EnableConfig and for self-contained identity" {
            $enableConfigRun = Invoke-StartScript {
                & "$script:dockerComposeRoot/start-published-dms.ps1" -IdentityProvider keycloak -EnableConfig -EnvironmentFile (New-WiringEnvFile) *>$null
            }
            $enableConfigRun.ComposeCommand | Should -BeLike "*published-config.yml*"

            $selfContainedRun = Invoke-StartScript {
                & "$script:dockerComposeRoot/start-published-dms.ps1" -IdentityProvider self-contained -EnvironmentFile (New-WiringEnvFile) *>$null
            }
            $selfContainedRun.ComposeCommand | Should -BeLike "*published-config.yml*"
        }

        It "does not run this story's topology validator for a bare Keycloak start that omits CMS" {
            if (-not (Assert-NoStagedBootstrapWorkspace)) { return }
            # CMS is absent from the compose set, so Confirm-CmsDatabaseTopologyAgreement must not
            # run and no topology-derived file may be written. A consistent (shared) CMS connection
            # string keeps the legacy shared-database check satisfied, isolating the gate itself as
            # the behavior under test.
            $envFile = New-MssqlWiringEnvFile -CmsConnectionString 'Server=dms-mssql,1433;Database=${MSSQL_DB_NAME};User Id=sa;Password=${MSSQL_SA_PASSWORD};TrustServerCertificate=true;'

            $run = Invoke-StartScript {
                & "$script:dockerComposeRoot/start-published-dms.ps1" -IdentityProvider keycloak -DatabaseEngine mssql -EnvironmentFile $envFile *>$null
            }

            $run.ComposeCommand | Should -Not -BeNullOrEmpty -Because "the run must reach the docker boundary rather than being rejected by a validator that should not have run"
            $run.ComposeCommand | Should -Not -BeLike "*published-config.yml*"
            $run.TopologyFile | Should -BeNullOrEmpty -Because "the topology sequence is gated on CMS participation"
            $run.ErrorMessage | Should -Not -BeLike "*topology*"
        }

        It "keeps today's legacy shared-database rejection for a bare Keycloak start whose CMS database name disagrees" {
            if (-not (Assert-NoStagedBootstrapWorkspace)) { return }
            # Non-participating shapes must keep running Assert-MssqlCmsDatabaseIsShared exactly as
            # they do today (the spec's own requirement), so a CMS connection string naming a
            # different database is still rejected here - by the legacy DMS-1255 check, not by this
            # story's topology validator. Only the database name differs: that check inspects the
            # Database/Initial Catalog aliases and nothing else, so the database name alone is what
            # drives this rejection.
            $envFile = New-MssqlWiringEnvFile -CmsConnectionString 'Server=dms-mssql,1433;Database=a_totally_different_db;User Id=sa;Password=abcdefgh1!;TrustServerCertificate=true;'

            $run = Invoke-StartScript {
                & "$script:dockerComposeRoot/start-published-dms.ps1" -IdentityProvider keycloak -DatabaseEngine mssql -EnvironmentFile $envFile *>$null
            }

            $run.ErrorMessage | Should -BeLike "*shared-database configuration mismatch*"
            $run.ErrorMessage | Should -BeLike "*a_totally_different_db*"
            $run.ErrorMessage | Should -Not -BeLike "*CMS database topology mismatch*" -Because "the legacy check owns this non-participating shape, not this story's validator"
            $run.Invocations | Should -BeNullOrEmpty -Because "the legacy check rejects the invocation before any docker call, exactly as it does today"
        }

        It "accepts a custom CMS host under bare Keycloak, proving the endpoint validator did not run" {
            if (-not (Assert-NoStagedBootstrapWorkspace)) { return }
            # The sharpest available probe of the participation gate: host and port are checked only
            # by Confirm-CmsDatabaseTopologyAgreement, never by the legacy database-name check. So a
            # CMS connection string whose database name agrees but whose host is not dms-mssql must
            # be accepted for this non-participating shape - if the gate were wrongly broad the new
            # validator would run and reject the host. Complements the database-mismatch test above,
            # which the legacy check alone can explain.
            $envFile = New-MssqlWiringEnvFile -CmsConnectionString 'Server=some-other-host,1433;Database=${MSSQL_DB_NAME};User Id=sa;Password=abcdefgh1!;TrustServerCertificate=true;'

            $run = Invoke-StartScript {
                & "$script:dockerComposeRoot/start-published-dms.ps1" -IdentityProvider keycloak -DatabaseEngine mssql -EnvironmentFile $envFile *>$null
            }

            $run.ComposeCommand | Should -Not -BeNullOrEmpty -Because "a custom CMS host is irrelevant when CMS never starts, so the run must reach the docker boundary"
            $run.ComposeCommand | Should -Not -BeLike "*published-config.yml*"
            $run.ErrorMessage | Should -Not -BeLike "*topology*" -Because "Confirm-CmsDatabaseTopologyAgreement checks the host and must not have run"
            $run.TopologyFile | Should -BeNullOrEmpty
        }
    }
}

Describe "CMS database creation ownership (DMS-1270)" {
    # The acceptance contract assigns database creation per identity provider and forbids overlap:
    # self-contained creation belongs to the OpenIddict bootstrap (setup-openiddict.ps1 -InitDb),
    # Keycloak-mode creation belongs to CMS itself (its startup EnsureDatabase deploy, switched on
    # by AppSettings__DeployDatabaseOnStartup), and PostgreSQL container initialization never
    # creates the CMS database at all. These pin each side of that contract.
    BeforeAll {
        $script:dockerComposeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))

        # Imported here rather than relied on from an earlier Describe: the module-table assertions call
        # Get-ComposeDatabaseServiceHostAlias, and this Describe must pass when run on its own - which is
        # how the hostile-session and mutation runs invoke it.
        Import-Module (Join-Path $script:dockerComposeRoot "database-safety.psm1") -Force
        Import-Module (Join-Path $script:dockerComposeRoot "env-utility.psm1") -Force

        # Returns every setup-openiddict.ps1 -InitDb invocation in a script together with the
        # conditions of the enclosing if-clauses that actually decide on the identity provider.
        # AST-based, so it follows real block structure instead of guessing with regex distance, and
        # it returns the condition TEXT so the caller can evaluate it rather than pattern-match it - a
        # test that merely looked for the substring "self-contained" would pass an inverted ('-ne')
        # or widened ('-or ... keycloak') guard that let Keycloak run the bootstrap.
        function script:Get-InitDbInvocationGuard {
            param([Parameter(Mandatory)] [string]$ScriptPath)

            $ast = [System.Management.Automation.Language.Parser]::ParseFile($ScriptPath, [ref]$null, [ref]$null)
            $invocations = $ast.FindAll({
                param($node)
                $node -is [System.Management.Automation.Language.CommandAst] -and
                $node.Extent.Text -match 'setup-openiddict\.ps1' -and
                $node.Extent.Text -match '-InitDb'
            }, $true)

            return @($invocations | ForEach-Object {
                $invocation = $_
                $conditions = [System.Collections.Generic.List[string]]::new()
                $ancestor = $invocation.Parent
                while ($null -ne $ancestor) {
                    if ($ancestor -is [System.Management.Automation.Language.IfStatementAst]) {
                        foreach ($clause in $ancestor.Clauses) {
                            $body = $clause.Item2
                            if ($body.Extent.StartOffset -le $invocation.Extent.StartOffset -and
                                $body.Extent.EndOffset -ge $invocation.Extent.EndOffset -and
                                $clause.Item1.FindAll({
                                    param($node)
                                    $node -is [System.Management.Automation.Language.VariableExpressionAst] -and
                                    $node.VariablePath.UserPath -eq 'IdentityProvider'
                                }, $true).Count -gt 0) {
                                $conditions.Add($clause.Item1.Extent.Text)
                            }
                        }
                    }
                    $ancestor = $ancestor.Parent
                }
                [PSCustomObject]@{ Line = $invocation.Extent.StartLineNumber; Conditions = @($conditions) }
            })
        }

        # Runs one creation-ownership cell and reports whether setup-openiddict.ps1 -InitDb was actually
        # invoked. BEHAVIOURAL, because the structural check below cannot model the real control flow:
        # one correct -InitDb call sits inside `if ($InfraOnly)`, and another is reached only because
        # that branch RETURNS, which lexical ancestry cannot see.
        #
        # The start script runs from a staged copy, because it does Push-Location $PSScriptRoot and then
        # calls its siblings as ./setup-openiddict.ps1 - so the only way to intercept those without
        # running the real ones is for the script to live beside recording stubs. The stubs write to a
        # file rather than a closure because they are invoked as separate scripts.
        #
        # Cells run on PostgreSQL deliberately: engine is not one of the contract's dimensions, and
        # PostgreSQL is the one path that reaches the decision point without Wait-MssqlReady, which is
        # defined INSIDE the start scripts and so cannot be stubbed from a module.
        # The names whose global definitions the harness must be able to shadow deterministically.
        $script:ownershipInterceptedCommand = @('docker', 'Start-Sleep')

        # The staged sibling stub, as a single definition so the binding tests exercise the SAME text the
        # cells run rather than a copy that could drift from it. '__SCRIPT__' is replaced per stub.
        #
        # [CmdletBinding()] with an explicit param block and no $args is what makes the observation
        # authoritative: an unmodelled named argument fails BINDING, and a bound switch reports its real
        # value. '-InitDb:$false' reaches $args as the text "-InitDb:" plus "False", so a substring test
        # reports initialization for a switch that is bound false. Non-switch parameters are [string] so
        # no value can fail type conversion; strictness belongs on the parameter-name set.
        #
        # The record carries parameter NAMES and the bound switch value, never values: these calls pass
        # -NewClientSecret, and observations must not render credential material.
        $script:ownershipStubBody = @'
[CmdletBinding()]
param(
    [switch]$InitDb,
    [switch]$InsertData,
    [string]$EnvironmentFile,
    [string]$DbName,
    [string]$DbType,
    [string]$DbUser,
    [string]$DbPort,
    [string]$NewClientId,
    [string]$NewClientName,
    [string]$ClientScopeName,
    [string]$NewClientSecret,
    [string]$ClientSecretMinimumLength,
    [string]$ClientSecretMaximumLength
)

$record = [ordered]@{
    Script         = '__SCRIPT__'
    InitDb         = [bool]$InitDb.IsPresent
    BoundParameter = @($PSBoundParameters.Keys | Sort-Object)
}
Add-Content -LiteralPath (Join-Path $PSScriptRoot 'sibling-observations.jsonl') -Value ($record | ConvertTo-Json -Compress)
'@

        # Every module a staged start script imports from its own directory. A staged import adds an
        # instance whose $PSScriptRoot is the staging directory - which the cell then deletes - so
        # without restoration a later caller resolves module defaults against a path that is gone.
        # Ordered by dependency: env-utility.psm1 imports database-safety.psm1 internally.
        $script:ownershipStagedModule = @('database-safety', 'env-utility', 'bootstrap-manifest', 'bootstrap-claims-gate')

        # Environment variables a cell can change. The first five are written by the start scripts and
        # nothing restores them (Get-BootstrapEnvSnapshot covers only the seven that follow). The seven
        # are restored by the start script's own finally, and are inventoried anyway so a cell cannot
        # leak them if that finally is ever skipped.
        $script:ownershipEnvironmentVariable = @(
            'DMS_CONFIG_IDENTITY_PROVIDER', 'OAUTH_TOKEN_ENDPOINT', 'DMS_JWT_AUTHORITY',
            'DMS_JWT_METADATA_ADDRESS', 'DMS_CONFIG_IDENTITY_AUTHORITY',
            'DMS_CONFIG_CLAIMS_SOURCE', 'DMS_CONFIG_CLAIMS_DIRECTORY', 'DMS_CONFIG_CLAIMS_MOUNT_SOURCE',
            'USE_API_SCHEMA_PATH', 'API_SCHEMA_PATH', 'DMS_API_SCHEMA_MOUNT_SOURCE', 'SCHEMA_PACKAGES'
        )

        # Refuses to run when a name cannot be shadowed deterministically. PowerShell resolves Alias
        # before Function before Application, so an alias named docker outranks the stand-in and the real
        # executable could run; a Constant function cannot be replaced at all. Both are preconditions
        # rather than things to work around: this runs BEFORE any snapshot, staging, or mutation, so an
        # operator's alias is left exactly as they set it and there is nothing to restore.
        #
        # The predicate lives in ONE place, as text, because a Constant function cannot be created in this
        # session without poisoning it - a Constant function can be neither replaced nor removed. The
        # isolated test therefore runs this same text in a subprocess rather than restating the condition,
        # so deleting or inverting the Constant branch breaks both the matrix path and that test. A probe
        # holding its own copy of the condition is what let the branch be deleted with the suite still green.
        $script:ownershipPreconditionBody = @'
param([Parameter(Mandatory)] [string[]]$InterceptedCommand)

foreach ($name in $InterceptedCommand) {
    $alias = Get-Command $name -CommandType Alias -ErrorAction SilentlyContinue
    if ($null -ne $alias) {
        throw "Ownership cell precondition failed: an alias named '$name' takes precedence over the recording stand-in, so interception cannot be guaranteed. Remove the alias before running these tests."
    }

    $existing = Get-Item -LiteralPath "Function:\$name" -ErrorAction SilentlyContinue
    if ($null -ne $existing -and ($existing.Options -band [System.Management.Automation.ScopedItemOptions]::Constant)) {
        throw "Ownership cell precondition failed: the function '$name' is Constant and cannot be shadowed by the recording stand-in."
    }
}
'@

        function script:Assert-OwnershipCellPrecondition {
            & ([scriptblock]::Create($script:ownershipPreconditionBody)) `
                -InterceptedCommand $script:ownershipInterceptedCommand
        }

        # The default location stack as an ordered path list, top first. Uses .ToArray() rather than
        # piping the PathInfoStack: piping it can yield a PHANTOM element - the stack object itself,
        # whose .Path is null - which makes an empty stack look like a one-entry stack and sends
        # restoration down the non-empty branch to index a null. One helper, used by both the snapshot
        # and the verification, so the two cannot disagree about what the stack is.
        function script:Get-OwnershipLocationStack {
            $stack = Get-Location -Stack
            if ($null -eq $stack) { return @() }
            return @($stack.ToArray() | ForEach-Object { $_.Path } | Where-Object { -not [string]::IsNullOrEmpty($_) })
        }

        # Installs a recording stand-in over a global name, preserving any options the pre-existing
        # function carried. Set-Item -Force is enough to shadow a ReadOnly function but CANNOT shadow an
        # AllScope one - it fails with "The AllScope option cannot be removed from the function" - so when
        # options are present the stand-in is created carrying the same ones. Constant never reaches here;
        # it is refused by the precondition.
        function script:Set-OwnershipStandIn {
            param(
                [Parameter(Mandatory)] [string]$Name,
                [Parameter(Mandatory)] [scriptblock]$Body,
                [Parameter(Mandatory)] [hashtable]$State
            )

            $saved = $State.Function[$Name]
            if ($null -ne $saved -and $saved.Options -ne [System.Management.Automation.ScopedItemOptions]::None) {
                $null = New-Item -Path "Function:\global:$Name" -Value $Body -Options $saved.Options -Force
            }
            else {
                Set-Item -Path "Function:\global:$Name" -Force -Value $Body
            }
        }

        # Captures every process-global resource a cell can change, presence separately from value.
        function script:Get-OwnershipCellState {
            $functionState = @{}
            foreach ($name in $script:ownershipInterceptedCommand) {
                $item = Get-Item -LiteralPath "Function:\$name" -ErrorAction SilentlyContinue
                $functionState[$name] = if ($null -eq $item) { $null }
                    else { @{ Definition = $item.Definition; Options = $item.Options } }
            }

            $environmentState = @{}
            foreach ($name in $script:ownershipEnvironmentVariable) {
                $environmentState[$name] = @{
                    Present = (Test-Path -LiteralPath "Env:\$name")
                    Value   = [System.Environment]::GetEnvironmentVariable($name)
                }
            }

            $moduleState = @{}
            foreach ($name in $script:ownershipStagedModule) {
                $moduleState[$name] = @{
                    All = @(Get-Module $name -All | ForEach-Object { $_.Path })
                    Top = @(Get-Module $name | ForEach-Object { $_.Path })
                }
            }

            $exitCodeVariable = Get-Variable -Name LASTEXITCODE -Scope Global -ErrorAction SilentlyContinue

            return @{
                Function    = $functionState
                Environment = $environmentState
                Module      = $moduleState
                Provenance  = (Get-Command Get-ComposeDatabaseServiceHostAlias -ErrorAction SilentlyContinue).Module.Path
                ExitCode    = @{
                    Present = ($null -ne $exitCodeVariable)
                    Value   = if ($null -ne $exitCodeVariable) { $exitCodeVariable.Value } else { $null }
                }
                Location    = (Get-Location).Path
                Stack       = @(Get-OwnershipLocationStack)
            }
        }

        # Restores every resource captured by Get-OwnershipCellState. BEST EFFORT PER RESOURCE: a failure
        # restoring one must not prevent the rest from being repaired, so each step is guarded and the
        # errors are returned for the caller to assert on after cleanup. Cleanup that abandons the session
        # on its first problem is how a failing test contaminates the next one.
        function script:Restore-OwnershipCellState {
            param([Parameter(Mandatory)] [hashtable]$State, [Parameter(Mandatory)] [string]$StagingPath)

            $failure = [System.Collections.Generic.List[string]]::new()
            $imbalance = [System.Collections.Generic.List[string]]::new()

            foreach ($name in $script:ownershipInterceptedCommand) {
                try {
                    $saved = $State.Function[$name]
                    if ($null -eq $saved) {
                        Remove-Item -LiteralPath "Function:\$name" -Force -ErrorAction SilentlyContinue
                    }
                    else {
                        # New-Item with -Options is the only form that reproduces AllScope; a plain
                        # Set-Item silently downgrades an option-bearing function to a normal one.
                        Remove-Item -LiteralPath "Function:\$name" -Force -ErrorAction SilentlyContinue
                        $null = New-Item -Path "Function:\global:$name" `
                            -Value ([scriptblock]::Create($saved.Definition)) `
                            -Options $saved.Options -Force
                    }
                }
                catch { $failure.Add("function ${name}: $($_.Exception.Message)") }
            }

            try {
                if ($State.ExitCode.Present) { Set-Variable -Name LASTEXITCODE -Scope Global -Value $State.ExitCode.Value }
                else { Remove-Variable -Name LASTEXITCODE -Scope Global -ErrorAction SilentlyContinue }
            }
            catch { $failure.Add("LASTEXITCODE: $($_.Exception.Message)") }

            foreach ($name in $script:ownershipEnvironmentVariable) {
                try {
                    $saved = $State.Environment[$name]
                    if ($saved.Present) {
                        [System.Environment]::SetEnvironmentVariable($name, $saved.Value)
                    }
                    else {
                        # Remove-Item, never SetEnvironmentVariable($name, $null): the latter leaves a
                        # present-but-blank variable in this environment instead of removing it.
                        Remove-Item -LiteralPath "Env:\$name" -Force -ErrorAction SilentlyContinue
                    }
                }
                catch { $failure.Add("env ${name}: $($_.Exception.Message)") }
            }

            try {
                foreach ($name in $script:ownershipStagedModule) {
                    # -All also covers a nested instance. Every real cell leaves only top-level staged
                    # instances (both start scripts import database-safety directly), so this is
                    # defensive rather than load-bearing today.
                    foreach ($module in @(Get-Module $name -All | Where-Object { $_.Path -like "$StagingPath*" })) {
                        Remove-Module -ModuleInfo $module -Force -ErrorAction SilentlyContinue
                    }
                }
                # Removal alone leaves the real module listed but its commands unresolvable, so the
                # snapshot paths are re-imported in dependency order.
                #
                # Only paths that still exist. Other test files in the same session stage and delete their
                # own copies, so the pre-cell module table can already contain instances whose files are
                # gone. Those are not this cell's staged instances, the removal loop above left them
                # loaded, and re-importing them is both impossible and unnecessary - attempting it would
                # turn another file's residue into this cell's restoration failure.
                foreach ($name in $script:ownershipStagedModule) {
                    foreach ($path in $State.Module[$name].Top) {
                        if (Test-Path -LiteralPath $path -PathType Leaf) { Import-Module $path -Force }
                    }
                }
            }
            catch { $failure.Add("module table: $($_.Exception.Message)") }

            try {
                $currentStack = @(Get-OwnershipLocationStack)
                if (($currentStack -join '|') -ne (($State.Stack) -join '|')) {
                    $imbalance.Add("location stack changed: expected $($State.Stack.Count) entr$(if ($State.Stack.Count -eq 1) { 'y' } else { 'ies' }), found $($currentStack.Count)")
                }

                while ((Get-Location -Stack).Count -gt 0) { Pop-Location }
                if ($State.Stack.Count -eq 0) {
                    # Ordinary sessions take this branch; there is no bottom entry to index.
                    Set-Location -LiteralPath $State.Location
                }
                else {
                    Set-Location -LiteralPath $State.Stack[$State.Stack.Count - 1]
                    for ($index = $State.Stack.Count - 2; $index -ge 0; $index--) {
                        Push-Location -LiteralPath $State.Stack[$index]
                    }
                    Push-Location -LiteralPath $State.Location
                }
            }
            catch { $failure.Add("location: $($_.Exception.Message)") }

            # Last, so nothing that still needs the staged files runs after it, and only this cell's own
            # GUID-scoped directory.
            try {
                if (Test-Path -LiteralPath $StagingPath) {
                    Remove-Item -LiteralPath $StagingPath -Recurse -Force -ErrorAction Stop
                }
            }
            catch { $failure.Add("staging directory: $($_.Exception.Message)") }

            return @{ RestoreFailure = @($failure); Imbalance = @($imbalance) }
        }

        function script:Invoke-CreationOwnershipCell {
            param(
                [Parameter(Mandatory)] [string]$StartScript,
                [Parameter(Mandatory)] [string]$IdentityProvider,
                [switch]$InfraOnly,
                [switch]$SeparateConfigDatabase,
                # Forces staging to fail part-way, to prove restoration and cleanup still happen.
                [switch]$FailStaging,
                # Seeds an unparsable line into the real observation file, so the actual read/parse path
                # fails and restoration can be proven unconditional.
                [switch]$CorruptObservation
            )

            # Preconditions FIRST: before the snapshot, before the staging directory exists, before any
            # stand-in is installed. Nothing has been mutated yet, so a precondition failure leaves the
            # session exactly as it was.
            Assert-OwnershipCellPrecondition

            $state = Get-OwnershipCellState
            $stage = Join-Path ([System.IO.Path]::GetTempPath()) ("dms-ownership-" + [Guid]::NewGuid().ToString('N'))

            $recorded = [System.Collections.Generic.List[string]]::new()
            $caught = $null
            $restore = $null
            $observationFailure = $null
            try {
                New-Item -ItemType Directory -Path $stage -Force | Out-Null

                # The compose .yml files are deliberately NOT staged: the file set is assembled without
                # Test-Path and docker is intercepted, so their absence cannot affect the decision. It
                # also exercises the alias reader's missing-file narrowing, which is why the connection
                # string below uses the canonical container name.
                foreach ($name in @(
                    $StartScript, 'bootstrap-manifest.psm1', 'bootstrap-claims-gate.psm1',
                    'env-utility.psm1', 'database-safety.psm1'
                )) {
                    Copy-Item -LiteralPath (Join-Path $script:dockerComposeRoot $name) -Destination (Join-Path $stage $name)
                    if ($FailStaging) { throw "Forced staging failure after copying '$name'." }
                }

                foreach ($stub in @('setup-openiddict.ps1', 'setup-keycloak.ps1')) {
                    Set-Content -LiteralPath (Join-Path $stage $stub) `
                        -Value $script:ownershipStubBody.Replace('__SCRIPT__', $stub)
                }

                $envFile = Join-Path $stage '.env.ownership'
                Set-Content -LiteralPath $envFile -NoNewline -Value (@(
                    'POSTGRES_PASSWORD=abcdefgh1!',
                    'POSTGRES_DB_NAME=edfi_datamanagementservice',
                    "DMS_CONFIG_IDENTITY_PROVIDER=$IdentityProvider"
                ) -join "`n")

                # Succeed for the database and Keycloak bring-ups, fail at the next compose up - the
                # Configuration Service under -InfraOnly, the full stack otherwise. That is a
                # deterministic stop just past the -InitDb decision under both identity providers, and
                # the resulting error is asserted per shape so a cell that died EARLIER cannot pass by
                # reporting zero invocations.
                #
                Set-OwnershipStandIn -Name 'docker' -State $state -Body {
                    $flattened = @($args | ForEach-Object { $_ })
                    $recorded.Add(($flattened -join ' '))
                    if ($flattened -contains 'up' -and -not ($flattened -contains 'db' -or $flattened -contains 'keycloak')) {
                        $global:LASTEXITCODE = 1
                    }
                    else {
                        $global:LASTEXITCODE = 0
                    }
                }.GetNewClosure()
                # The start scripts sleep 20-30 seconds waiting for containers that do not exist here.
                Set-OwnershipStandIn -Name 'Start-Sleep' -State $state -Body { }

                $scriptArgs = @{
                    EnvironmentFile  = $envFile
                    IdentityProvider = $IdentityProvider
                    DatabaseEngine   = 'postgresql'
                }
                if ($InfraOnly) { $scriptArgs['InfraOnly'] = $true }
                if ($SeparateConfigDatabase) { $scriptArgs['SeparateConfigDatabase'] = $true }

                if ($CorruptObservation) {
                    # Seeded before the run, so the very first line the real reader meets is unparsable.
                    # Nothing about the read path is stubbed - this is the production observation file.
                    Add-Content -LiteralPath (Join-Path $stage 'sibling-observations.jsonl') -Value '{ this is not valid json'
                }

                & (Join-Path $stage $StartScript) @scriptArgs *>$null
            }
            catch { $caught = $_ }
            finally {
                # Reading the observation must not be able to skip restoration. Get-Content and
                # ConvertFrom-Json both throw on a malformed file, and an exception raised inside a finally
                # block abandons the rest of it - measured: one unparsable observation line left the
                # sentinel environment value clobbered and leaked 24 staging directories. So the read sits
                # in its own guarded block and restoration runs from a NESTED finally, which executes
                # whether staging, the start script, the read, or the parse failed.
                $observationRecord = @()
                try {
                    # Before the staging directory is removed, since the file lives inside it.
                    $observationFile = Join-Path $stage 'sibling-observations.jsonl'
                    if (Test-Path -LiteralPath $observationFile) {
                        $observationRecord = @(
                            Get-Content -LiteralPath $observationFile -ErrorAction Stop |
                                Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
                                ForEach-Object { $_ | ConvertFrom-Json -ErrorAction Stop }
                        )
                    }
                }
                catch {
                    # The exception TYPE only. A parse error's message can quote the offending text, and
                    # these records name parameters including -NewClientSecret; a diagnostic must not become
                    # the disclosure path the observation format itself avoids.
                    $observationFailure = "the sibling observation file could not be read or parsed ($($_.Exception.GetType().Name))"
                    $observationRecord = @()
                }
                finally {
                    $restore = Restore-OwnershipCellState -State $state -StagingPath $stage
                }
            }

            $observation = @($observationRecord)
            return [PSCustomObject]@{
                InitDbCount        = @($observation | Where-Object { $_.Script -eq 'setup-openiddict.ps1' -and $_.InitDb }).Count
                KeycloakCount      = @($observation | Where-Object { $_.Script -eq 'setup-keycloak.ps1' }).Count
                Observation        = $observation
                DockerCommand      = @($recorded)
                # The execution failure is preserved as-is; an observation failure is reported alongside it
                # rather than replacing it, so a malformed file cannot hide why the run actually stopped.
                ErrorMessage       = if ($null -ne $caught) { $caught.Exception.Message } else { $null }
                ObservationFailure = $observationFailure
                RestoreFailure     = @($restore.RestoreFailure)
                Imbalance          = @($restore.Imbalance)
                StagingPath        = $stage
            }
        }

        # Evaluates a guard condition with $IdentityProvider bound to the given value. The condition
        # must depend on nothing else, so the result is a deterministic function of the provider.
        function script:Test-GuardConditionForProvider {
            param(
                [Parameter(Mandatory)] [string]$Condition,
                [Parameter(Mandatory)] [string]$IdentityProviderValue
            )

            $referenced = @([System.Management.Automation.Language.Parser]::ParseInput($Condition, [ref]$null, [ref]$null).
                FindAll({ param($node) $node -is [System.Management.Automation.Language.VariableExpressionAst] }, $true) |
                ForEach-Object { $_.VariablePath.UserPath } | Sort-Object -Unique)
            if (@($referenced | Where-Object { $_ -ne 'IdentityProvider' }).Count -gt 0) {
                throw "The -InitDb guard condition '$Condition' depends on variables other than `$IdentityProvider ($($referenced -join ', ')); this test can no longer prove it by evaluation and must be updated deliberately."
            }

            $scriptBlock = [scriptblock]::Create("param(`$IdentityProvider) [bool]($Condition)")
            return [bool](& $scriptBlock $IdentityProviderValue)
        }
    }

    It "the identity-provider set is exactly keycloak and self-contained: <_>" -ForEach @(
        'start-local-dms.ps1', 'start-published-dms.ps1'
    ) {
        # The creation-ownership contract below reasons over every supported provider by name, so a
        # newly added provider must force that reasoning to be revisited rather than silently skipped.
        $ast = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $script:dockerComposeRoot $_), [ref]$null, [ref]$null)
        $parameter = $ast.ParamBlock.Parameters |
            Where-Object { $_.Name.VariablePath.UserPath -eq 'IdentityProvider' } |
            Select-Object -First 1
        $parameter | Should -Not -BeNullOrEmpty

        $validateSet = $parameter.Attributes |
            Where-Object { $_ -is [System.Management.Automation.Language.AttributeAst] -and $_.TypeName.Name -eq 'ValidateSet' } |
            Select-Object -First 1
        @($validateSet.PositionalArguments | ForEach-Object { $_.Value }) |
            Should -Be @('keycloak', 'self-contained')
    }

    # A NARROW STRUCTURAL SAFEGUARD, not a reachability proof. It asserts only that every -InitDb call
    # site sits behind some identity-provider condition that admits self-contained and excludes Keycloak.
    # It deliberately does NOT claim the call is reached, or that it is reached in the right shapes: it
    # discards every enclosing condition that does not mention $IdentityProvider, so nesting a call
    # inside `if ($SeparateConfigDatabase)` leaves it silent. The behavioural matrix below is what
    # proves ownership; this exists to catch an inverted or Keycloak-widened comparison at the call site
    # even if the matrix were ever narrowed.
    It "keeps every -InitDb call site behind an identity-provider condition that excludes Keycloak: <_>" -ForEach @(
        'start-local-dms.ps1', 'start-published-dms.ps1'
    ) {
        $invocations = Get-InitDbInvocationGuard -ScriptPath (Join-Path $script:dockerComposeRoot $_)

        $invocations.Count | Should -BeGreaterThan 0 -Because "the self-contained flow must bootstrap the identity store"
        foreach ($invocation in $invocations) {
            $invocation.Conditions.Count | Should -BeGreaterThan 0 -Because "the -InitDb call at line $($invocation.Line) must sit behind an identity-provider decision"

            foreach ($provider in @('self-contained', 'keycloak')) {
                # Whether EVERY identity-provider condition enclosing this call site admits the provider.
                # Conditions on other variables are not modelled here - see the note above.
                $runs = @($invocation.Conditions | ForEach-Object { Test-GuardConditionForProvider -Condition $_ -IdentityProviderValue $provider })
                $admits = @($runs | Where-Object { -not $_ }).Count -eq 0

                if ($provider -eq 'self-contained') {
                    $admits | Should -BeTrue -Because "the -InitDb call at line $($invocation.Line) creates the self-contained identity store"
                }
                else {
                    $admits | Should -BeFalse -Because "the -InitDb call at line $($invocation.Line) must not run under Keycloak, where CMS owns database creation"
                }
            }
        }
    }

    # The authoritative ownership coverage: 16 cells over both start scripts, both startup shapes, both
    # topologies, and both identity providers, each asserting whether setup-openiddict.ps1 -InitDb is
    # ACTUALLY invoked. Creation must happen in every self-contained cell, never in a Keycloak cell
    # (where CMS's own EnsureDatabase deploy owns it, and a second creator would duplicate ownership),
    # and must be independent of topology - the -SeparateConfigDatabase switch redirects WHICH database
    # CMS uses, never WHO creates it.
    It "invokes setup-openiddict.ps1 -InitDb exactly per the ownership contract: <Script> <Shape> <Topology> <Provider>" -ForEach @(
        foreach ($cellScript in @('start-local-dms.ps1', 'start-published-dms.ps1')) {
            foreach ($cellShape in @('ordinary', 'InfraOnly')) {
                foreach ($cellTopology in @('shared', 'separate')) {
                    foreach ($cellProvider in @('self-contained', 'keycloak')) {
                        @{
                            Script   = $cellScript
                            Shape    = $cellShape
                            Topology = $cellTopology
                            Provider = $cellProvider
                        }
                    }
                }
            }
        }
    ) {
        $cellArgs = @{ StartScript = $Script; IdentityProvider = $Provider }
        if ($Shape -eq 'InfraOnly') { $cellArgs['InfraOnly'] = $true }
        if ($Topology -eq 'separate') { $cellArgs['SeparateConfigDatabase'] = $true }

        $cell = Invoke-CreationOwnershipCell @cellArgs

        # The cell is a transaction over process-global state; these two assertions are what make it one.
        $cell.RestoreFailure | Should -BeNullOrEmpty -Because "every process-global resource must be restored"
        $cell.Imbalance | Should -BeNullOrEmpty -Because "the location stack must come back balanced"

        # Proof the cell actually traversed the decision point. Without this, a cell that failed earlier
        # would report zero -InitDb invocations and a Keycloak expectation would pass for the wrong
        # reason.
        $expectedStop =
            if ($Shape -eq 'InfraOnly') { "*Failed to start Configuration Service*" }
            else { "*Docker environment*" }
        $cell.ErrorMessage | Should -BeLike $expectedStop -Because "the cell must stop at the intercepted bring-up just past the -InitDb decision, not earlier"

        # Positive proof the recording stand-in intercepted the compose boundary rather than a real
        # docker running or nothing running at all.
        @($cell.DockerCommand | Where-Object { $_ -like 'compose *' }).Count |
            Should -BeGreaterThan 0 -Because "the stand-in must have received the compose bring-ups"

        if ($Provider -eq 'self-contained') {
            $cell.InitDbCount | Should -Be 1 -Because "self-contained creation belongs to the OpenIddict bootstrap, in $Shape/$Topology"
            $cell.KeycloakCount | Should -Be 0 -Because "no Keycloak client setup runs under self-contained identity"
        }
        else {
            $cell.InitDbCount | Should -Be 0 -Because "under Keycloak, CMS owns creation through its own startup deploy, in $Shape/$Topology"
            $cell.KeycloakCount | Should -BeGreaterThan 0 -Because "the cell must have reached the Keycloak identity branch, or its zero -InitDb count proves nothing"
        }
    }

    # The harness that produces the matrix above is itself a process-global mutation, so its contract is
    # tested rather than assumed: what "-InitDb was invoked" means, and that a cell is a transaction over
    # every resource it touches. Two earlier revisions of this harness observed a proxy for the property
    # (argument text) and mutated shared state without restoring it; these pin both closed.
    Context "ownership cell harness contract" {
        BeforeAll {
            # Writes the real stub into a throwaway directory and returns that directory, so the binding
            # tests exercise the text the cells run.
            function script:New-OwnershipStubRoot {
                $dir = Join-Path ([System.IO.Path]::GetTempPath()) ("dms-stub-" + [Guid]::NewGuid().ToString('N'))
                New-Item -ItemType Directory -Path $dir -Force | Out-Null
                Set-Content -LiteralPath (Join-Path $dir 'setup-openiddict.ps1') `
                    -Value $script:ownershipStubBody.Replace('__SCRIPT__', 'setup-openiddict.ps1')
                return $dir
            }

            function script:Get-StubObservation {
                param([Parameter(Mandatory)] [string]$StubRoot)

                $file = Join-Path $StubRoot 'sibling-observations.jsonl'
                if (-not (Test-Path -LiteralPath $file)) { return @() }
                return @(Get-Content -LiteralPath $file |
                    Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
                    ForEach-Object { $_ | ConvertFrom-Json })
            }
        }

        # These tests deliberately seed hostile session state - stand-in functions, sentinel exit codes,
        # drained or stacked locations - so they must themselves be transactions, or running this Context
        # inside an already-hostile session destroys the outer sentinels. Measured: without this, the
        # function tests' own cleanup deleted a caller's pre-existing `docker` (after which the REAL docker
        # ran), and the stack tests drained a caller's location stack and never rebuilt it.
        #
        # The snapshot/restore pair under test is reused deliberately: it is the one definition of "put
        # this session back", so a test cannot drift from the contract it is asserting. The assertions
        # still compare against independently captured values, so a broken restore fails the test rather
        # than hiding in the cleanup.
        BeforeEach {
            $script:ownershipSessionSnapshot = Get-OwnershipCellState
        }

        AfterEach {
            $null = Restore-OwnershipCellState -State $script:ownershipSessionSnapshot `
                -StagingPath (Join-Path ([System.IO.Path]::GetTempPath()) ("dms-ownership-none-" + [Guid]::NewGuid().ToString('N')))
        }

        It "records the bound switch value for every binding form, not the presence of its text" {
            # The whole point of the binding-aware stub. A production edit from -InitDb to -InitDb:$false
            # disables initialization while leaving the text intact, so text matching is not authoritative.
            $stubRoot = New-OwnershipStubRoot
            try {
                $stub = Join-Path $stubRoot 'setup-openiddict.ps1'
                & $stub -EnvironmentFile 'x' -DbName 'ENV:Y'
                & $stub -InitDb -EnvironmentFile 'x' -DbName 'ENV:Y'
                & $stub -InitDb:$false -EnvironmentFile 'x' -DbName 'ENV:Y'
                & $stub -InitDb:$true -EnvironmentFile 'x' -DbName 'ENV:Y'

                $observation = @(Get-StubObservation -StubRoot $stubRoot)
                @($observation | ForEach-Object { [bool]$_.InitDb }) |
                    Should -Be @($false, $true, $false, $true) -Because "omitted, bare, :`$false, and :`$true must be observed as their bound values"

                # And the text that a substring test would have matched IS present in the two false cases,
                # which is exactly why the bound value is what counts.
                $observation[2].BoundParameter | Should -Contain 'InitDb'
            }
            finally { Remove-Item -LiteralPath $stubRoot -Recurse -Force -ErrorAction SilentlyContinue }
        }

        It "fails parameter binding for a named argument the stub does not model" {
            # Fail-closed: production passing something unmodelled must break loudly rather than be
            # swallowed into $args and silently ignored.
            $stubRoot = New-OwnershipStubRoot
            try {
                { & (Join-Path $stubRoot 'setup-openiddict.ps1') -InitDb -NotAParameter 'zzz' } |
                    Should -Throw "*NotAParameter*"
            }
            finally { Remove-Item -LiteralPath $stubRoot -Recurse -Force -ErrorAction SilentlyContinue }
        }

        It "records parameter names but never secret-bearing values" {
            $stubRoot = New-OwnershipStubRoot
            try {
                & (Join-Path $stubRoot 'setup-openiddict.ps1') -InsertData -NewClientSecret 'sup3r-s3cret-value'

                $raw = Get-Content -LiteralPath (Join-Path $stubRoot 'sibling-observations.jsonl') -Raw
                $raw | Should -BeLike "*NewClientSecret*" -Because "the parameter name is useful and safe"
                $raw | Should -Not -BeLike "*sup3r-s3cret-value*" -Because "observations must never render credential material"
            }
            finally { Remove-Item -LiteralPath $stubRoot -Recurse -Force -ErrorAction SilentlyContinue }
        }

        It "restores an ordinary empty location stack" {
            # The branch every real cell takes. An empty snapshot stack has no bottom entry to index, so
            # it is a distinct code path from the hostile case below - and the one that breaks first if
            # the two are collapsed.
            while ((Get-Location -Stack).Count -gt 0) { Pop-Location }
            $locationBefore = (Get-Location).Path

            $cell = Invoke-CreationOwnershipCell -StartScript 'start-published-dms.ps1' -IdentityProvider 'self-contained'

            $cell.RestoreFailure | Should -BeNullOrEmpty
            $cell.Imbalance | Should -BeNullOrEmpty
            (Get-Location).Path | Should -Be $locationBefore
            (Get-Location -Stack).Count | Should -Be 0
        }

        It "restores a hostile non-empty location stack exactly" {
            $scratch = Join-Path ([System.IO.Path]::GetTempPath()) ("dms-stack-" + [Guid]::NewGuid().ToString('N'))
            $inner = Join-Path $scratch 'inner'
            New-Item -ItemType Directory -Path $inner -Force | Out-Null
            $stackBefore = $null
            try {
                Push-Location -LiteralPath $scratch
                Push-Location -LiteralPath $inner
                $locationBefore = (Get-Location).Path
                $stackBefore = @(Get-OwnershipLocationStack)
                $stackBefore.Count | Should -BeGreaterThan 1 -Because "this test needs a genuinely multi-entry stack"

                $cell = Invoke-CreationOwnershipCell -StartScript 'start-local-dms.ps1' -IdentityProvider 'self-contained'

                $cell.RestoreFailure | Should -BeNullOrEmpty
                $cell.Imbalance | Should -BeNullOrEmpty
                (Get-Location).Path | Should -Be $locationBefore
                @(Get-OwnershipLocationStack) | Should -Be $stackBefore -Because "the whole ordered stack must come back, not just its depth"
            }
            finally {
                if ($null -ne $stackBefore) { for ($i = 0; $i -lt $stackBefore.Count; $i++) { Pop-Location -ErrorAction SilentlyContinue } }
                Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
            }
        }

        It "reports a location imbalance after repairing the stack, not instead of repairing it" {
            # A production defect or mutation that leaks a Push-Location must fail the cell AND leave the
            # session correct. Asserting without repairing would make every later cell order-dependent.
            $scratch = Join-Path ([System.IO.Path]::GetTempPath()) ("dms-imbalance-" + [Guid]::NewGuid().ToString('N'))
            New-Item -ItemType Directory -Path $scratch -Force | Out-Null
            try {
                while ((Get-Location -Stack).Count -gt 0) { Pop-Location }
                $locationBefore = (Get-Location).Path

                # A Start-Sleep stand-in that leaks a pushed location, imitating an unbalanced callee.
                $state = Get-OwnershipCellState
                Push-Location -LiteralPath $scratch
                $restore = Restore-OwnershipCellState -State $state -StagingPath (Join-Path $scratch 'absent')

                @($restore.Imbalance).Count | Should -BeGreaterThan 0 -Because "the leaked push must be reported"
                @($restore.RestoreFailure) | Should -BeNullOrEmpty -Because "reporting is not a substitute for repairing"
                (Get-Location).Path | Should -Be $locationBefore -Because "the session must be repaired despite the imbalance"
                (Get-Location -Stack).Count | Should -Be 0
            }
            finally {
                while ((Get-Location -Stack).Count -gt 0) { Pop-Location }
                Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
            }
        }

        It "restores every inventoried environment variable across present, empty, and absent states" {
            # Presence is tracked separately from value because "absent" and "present and empty" are
            # different states, and collapsing them is how a restore silently changes the session.
            $present = 'DMS_CONFIG_IDENTITY_PROVIDER'
            $bootstrapManaged = 'DMS_CONFIG_CLAIMS_SOURCE'
            $absent = 'DMS_JWT_METADATA_ADDRESS'
            $emptyCandidate = 'OAUTH_TOKEN_ENDPOINT'
            $saved = @{}
            foreach ($name in @($present, $bootstrapManaged, $absent, $emptyCandidate)) {
                $saved[$name] = @{ Present = (Test-Path -LiteralPath "Env:\$name"); Value = [System.Environment]::GetEnvironmentVariable($name) }
            }
            try {
                [System.Environment]::SetEnvironmentVariable($present, 'sentinel-provider')
                [System.Environment]::SetEnvironmentVariable($bootstrapManaged, 'sentinel-claims-source')
                Remove-Item -LiteralPath "Env:\$absent" -Force -ErrorAction SilentlyContinue

                # Present-but-empty is not representable on every platform; probe rather than assume, and
                # skip only this subcase if the platform collapses it to absent.
                [System.Environment]::SetEnvironmentVariable($emptyCandidate, '')
                $emptyRepresentable = (Test-Path -LiteralPath "Env:\$emptyCandidate")

                $cell = Invoke-CreationOwnershipCell -StartScript 'start-published-dms.ps1' -IdentityProvider 'keycloak'
                $cell.RestoreFailure | Should -BeNullOrEmpty

                [System.Environment]::GetEnvironmentVariable($present) | Should -Be 'sentinel-provider' -Because "the start script overwrites this one"
                [System.Environment]::GetEnvironmentVariable($bootstrapManaged) | Should -Be 'sentinel-claims-source' -Because "bootstrap-managed names are inventoried too"
                Test-Path -LiteralPath "Env:\$absent" | Should -BeFalse -Because "an absent variable must come back absent, not blank"

                if ($emptyRepresentable) {
                    Test-Path -LiteralPath "Env:\$emptyCandidate" | Should -BeTrue -Because "a present-but-empty variable must stay present"
                    [System.Environment]::GetEnvironmentVariable($emptyCandidate) | Should -Be ''
                }
                else {
                    Set-ItResult -Skipped -Because "this platform cannot represent a present-but-empty environment variable; the other three states are asserted above"
                }
            }
            finally {
                foreach ($name in $saved.Keys) {
                    if ($saved[$name].Present) { [System.Environment]::SetEnvironmentVariable($name, $saved[$name].Value) }
                    else { Remove-Item -LiteralPath "Env:\$name" -Force -ErrorAction SilentlyContinue }
                }
            }
        }

        It "restores `$global:LASTEXITCODE, whose value the docker stand-in changes" {
            $had = $null -ne (Get-Variable -Name LASTEXITCODE -Scope Global -ErrorAction SilentlyContinue)
            $saved = if ($had) { $global:LASTEXITCODE } else { $null }
            try {
                Set-Variable -Name LASTEXITCODE -Scope Global -Value 99

                $cell = Invoke-CreationOwnershipCell -StartScript 'start-published-dms.ps1' -IdentityProvider 'self-contained'

                $cell.RestoreFailure | Should -BeNullOrEmpty
                $global:LASTEXITCODE | Should -Be 99 -Because "the stand-in sets exit codes and must not leave them behind"
            }
            finally {
                # AfterEach restores presence and value; these locals only document the expectation.
                $null = $had; $null = $saved
            }
        }

        It "restores pre-existing global docker and Start-Sleep functions, definition and invocation" {
            # The stand-ins overwrite these names, so a caller's own definitions must survive.
            # Installed through the harness's own installer so any options the session already had on
            # these names are preserved - a plain `function global:Start-Sleep` fails outright when the
            # session's definition is AllScope, which is precisely the hostile state this must survive.
            Set-OwnershipStandIn -Name 'docker' -State $script:ownershipSessionSnapshot -Body { 'sentinel-docker' }
            Set-OwnershipStandIn -Name 'Start-Sleep' -State $script:ownershipSessionSnapshot -Body { 'sentinel-sleep' }

            # Snapshot the VALUES, not the FunctionInfo objects: Get-Item returns a live wrapper whose
            # Definition follows the function as it is replaced, so holding it would compare the stand-in
            # against itself and pass no matter what restoration did.
            $dockerDefinition = (Get-Item Function:\docker).Definition
            $dockerOptions = (Get-Item Function:\docker).Options
            $sleepDefinition = (Get-Item Function:\Start-Sleep).Definition
            # AllScope propagates a copy into every new scope, so invoking the name here can resolve a
            # propagated copy no matter what the global item holds. Assert invocation only when it is not
            # in play; the item-level comparison is the proof either way.
            $invocationIsMeaningful = -not (
                ($dockerOptions -band [System.Management.Automation.ScopedItemOptions]::AllScope) -or
                ((Get-Item Function:\Start-Sleep).Options -band [System.Management.Automation.ScopedItemOptions]::AllScope)
            )
            try {
                $cell = Invoke-CreationOwnershipCell -StartScript 'start-published-dms.ps1' -IdentityProvider 'self-contained'

                $cell.RestoreFailure | Should -BeNullOrEmpty
                $cell.InitDbCount | Should -Be 1 -Because "the cell must still work with pre-existing definitions present"

                $dockerAfter = Get-Item -LiteralPath Function:\docker -ErrorAction SilentlyContinue
                $dockerAfter | Should -Not -BeNullOrEmpty -Because "a pre-existing function must be restored, not deleted"
                $dockerAfter.Definition | Should -Be $dockerDefinition
                $dockerAfter.Options | Should -Be $dockerOptions
                (Get-Item Function:\Start-Sleep).Definition | Should -Be $sleepDefinition

                if ($invocationIsMeaningful) {
                    docker | Should -Be 'sentinel-docker' -Because "the restored function must be the caller's, not the stand-in"
                    Start-Sleep | Should -Be 'sentinel-sleep'
                }
            }
            finally {
                # AfterEach puts the session's own definitions back, whatever they were; this must not
                # blindly delete, because in a hostile session these names belong to the caller.
            }
        }

        It "restores a non-default function option (AllScope) exactly" {
            # A naive Set-Item restore silently downgrades an option-bearing function to a normal one, so
            # options are part of "restored exactly". AllScope is also the reason the stand-in is installed
            # with New-Item carrying the same options: Set-Item -Force cannot shadow an AllScope function.
            #
            # The assertion is on the function ITEM rather than on invoking the name: AllScope propagates a
            # copy into every new scope, so a call in this scope can resolve a propagated copy regardless of
            # what the global item holds. That is a language semantic, not a restoration failure.
            $null = New-Item -Path Function:\global:Start-Sleep -Value { 'sentinel-allscope' } -Options AllScope -Force
            # Values, not the live FunctionInfo - see the note in the preceding test.
            $beforeDefinition = (Get-Item Function:\Start-Sleep).Definition
            $beforeOptions = (Get-Item Function:\Start-Sleep).Options
            $beforeOptions | Should -Be 'AllScope' -Because "this test needs a genuinely option-bearing function"
            try {
                $cell = Invoke-CreationOwnershipCell -StartScript 'start-published-dms.ps1' -IdentityProvider 'self-contained'

                $cell.RestoreFailure | Should -BeNullOrEmpty
                $cell.InitDbCount | Should -Be 1 -Because "the cell must still run when the name it shadows is AllScope"

                (Get-Item Function:\Start-Sleep).Definition | Should -Be $beforeDefinition
                (Get-Item Function:\Start-Sleep).Options | Should -Be $beforeOptions -Because "AllScope must survive the cell"
            }
            finally {
                # AfterEach restores whatever this session had, including a caller's own definition.
            }
        }

        It "shadows and restores a pre-existing ReadOnly function" {
            function global:docker { 'sentinel-readonly' }
            (Get-Item Function:\docker).Options = 'ReadOnly'
            # Independent SCALAR values, not a held FunctionInfo. A FunctionInfo is a live wrapper: for a
            # plain function replaced in place by Set-Item it follows the replacement, so holding one would
            # compare the stand-in against itself. (Measured: it does not follow here, because overwriting
            # a ReadOnly function replaces the object rather than mutating it - but the oracle must not
            # depend on that subtlety to be sound.)
            $definitionBefore = (Get-Item Function:\docker).Definition
            $optionsBefore = (Get-Item Function:\docker).Options
            $optionsBefore | Should -Be 'ReadOnly' -Because "this test needs a genuinely ReadOnly function"
            try {
                $cell = Invoke-CreationOwnershipCell -StartScript 'start-published-dms.ps1' -IdentityProvider 'self-contained'

                $cell.RestoreFailure | Should -BeNullOrEmpty
                @($cell.DockerCommand | Where-Object { $_ -like 'compose *' }).Count |
                    Should -BeGreaterThan 0 -Because "the stand-in must have shadowed the ReadOnly function (Set-Item -Force)"

                $restored = Get-Item -LiteralPath Function:\docker -ErrorAction SilentlyContinue
                $restored | Should -Not -BeNullOrEmpty -Because "the caller's function must exist again"
                $restored.Definition | Should -Be $definitionBefore
                $restored.Options | Should -Be $optionsBefore -Because "ReadOnly is part of the state contract, not just the definition"
                docker | Should -Be 'sentinel-readonly' -Because "the restored function must be the caller's, not the stand-in"
            }
            finally {
                # AfterEach restores whatever this session had; clearing ReadOnly here so the restore can
                # replace it is the one thing the snapshot cannot do for a function it did not create.
                $item = Get-Item -LiteralPath Function:\docker -ErrorAction SilentlyContinue
                if ($null -ne $item -and ($item.Options -band [System.Management.Automation.ScopedItemOptions]::ReadOnly)) {
                    $item.Options = 'None'
                }
            }
        }

        It "refuses to run when an alias outranks the stand-in, and leaves the alias untouched: <_>" -ForEach @(
            'docker', 'Start-Sleep'
        ) {
            # PowerShell resolves Alias before Function, so an alias of either name would bypass the
            # recording stand-in entirely and a real executable could run. The cell must therefore refuse
            # BEFORE it snapshots, stages, or mutates anything - and must not silently unbind a caller's
            # alias to get its way.
            $aliasName = $_
            $script:aliasTargetInvoked = $false
            function global:OwnershipAliasTarget { $script:aliasTargetInvoked = $true }
            Set-Alias -Name $aliasName -Value OwnershipAliasTarget -Scope Global
            $stagingBefore = @(Get-ChildItem ([System.IO.Path]::GetTempPath()) -Directory -Filter 'dms-ownership-*' -ErrorAction SilentlyContinue).Count
            try {
                { Invoke-CreationOwnershipCell -StartScript 'start-published-dms.ps1' -IdentityProvider 'self-contained' } |
                    Should -Throw "*alias named '$aliasName'*"

                $script:aliasTargetInvoked | Should -BeFalse -Because "nothing may run through the alias"
                (Get-Command $aliasName -CommandType Alias).Definition |
                    Should -Be 'OwnershipAliasTarget' -Because "the caller's alias must be left exactly as they set it"
                @(Get-ChildItem ([System.IO.Path]::GetTempPath()) -Directory -Filter 'dms-ownership-*' -ErrorAction SilentlyContinue).Count |
                    Should -Be $stagingBefore -Because "the precondition runs before any staging directory is created"
            }
            finally {
                Remove-Item -LiteralPath "Alias:\$aliasName" -Force -ErrorAction SilentlyContinue
                Remove-Item -LiteralPath Function:\OwnershipAliasTarget -Force -ErrorAction SilentlyContinue
            }
        }

        It "refuses to run when a Constant function cannot be shadowed: <_>" -ForEach @(
            'docker', 'Start-Sleep'
        ) {
            # Runs the REAL precondition text - $script:ownershipPreconditionBody, the same definition
            # Assert-OwnershipCellPrecondition executes for every cell - against a genuinely Constant
            # function. The subprocess exists because a Constant function can be neither replaced nor
            # removed, so creating one in this session would poison every later test; it is not a second
            # copy of the predicate. An earlier version of this test restated the condition itself, which
            # meant deleting the real branch left the suite green.
            $constantName = $_
            $probeScript = @"
`$ErrorActionPreference = 'Stop'
`$staging = [System.IO.Path]::GetTempPath()
`$before = @(Get-ChildItem `$staging -Directory -Filter 'dms-ownership-*' -ErrorAction SilentlyContinue).Count

# A Constant function must be created as such; promoting one fails with "Functions can be made constant
# only at creation time."
`$null = New-Item -Path 'Function:\global:$constantName' -Value { 'constant-sentinel' } -Options Constant
`$item = Get-Item -LiteralPath 'Function:\$constantName'
if (-not (`$item.Options -band [System.Management.Automation.ScopedItemOptions]::Constant)) { 'SETUP-FAILED'; exit 1 }
`$definitionBefore = `$item.Definition
`$optionsBefore = `$item.Options

`$predicate = [scriptblock]::Create(@'
$($script:ownershipPreconditionBody)
'@)

try {
    & `$predicate -InterceptedCommand @('docker', 'Start-Sleep')
    'NO-THROW'
}
catch {
    if (`$_.Exception.Message -like "*is Constant and cannot be shadowed*") { 'CONSTANT-REFUSED' } else { "WRONG-DIAGNOSTIC: `$(`$_.Exception.Message)" }
}

`$after = @(Get-ChildItem `$staging -Directory -Filter 'dms-ownership-*' -ErrorAction SilentlyContinue).Count
if (`$after -eq `$before) { 'NO-STAGING-CREATED' } else { 'STAGING-LEAKED' }

`$itemAfter = Get-Item -LiteralPath 'Function:\$constantName'
if (`$itemAfter.Definition -eq `$definitionBefore -and `$itemAfter.Options -eq `$optionsBefore) { 'CONSTANT-UNCHANGED' } else { 'CONSTANT-ALTERED' }
"@
            $probe = @(pwsh -NoProfile -Command $probeScript)

            $probe | Should -Contain 'CONSTANT-REFUSED' -Because "the real precondition must refuse a Constant '$constantName' with its own diagnostic"
            $probe | Should -Not -Contain 'NO-THROW' -Because "a Constant function makes interception impossible"
            $probe | Should -Contain 'NO-STAGING-CREATED' -Because "the refusal must precede any staging"
            $probe | Should -Contain 'CONSTANT-UNCHANGED' -Because "the caller's Constant function must be left exactly as it was"
            $LASTEXITCODE | Should -Be 0 -Because "the disposable context must terminate cleanly"
        }

        It "restores the module table: path multiset, command provenance, and default-root behavior" {
            # A staged start script imports its modules from the staging directory, which the cell then
            # deletes. Left alone, a later caller resolves module defaults against a path that is gone -
            # measured to turn the accepted-host set from 'db, dms-postgresql' into 'dms-postgresql'.
            $moduleBefore = @{}
            foreach ($name in $script:ownershipStagedModule) {
                $moduleBefore[$name] = @{
                    All = @(Get-Module $name -All | ForEach-Object { $_.Path })
                    Top = @(Get-Module $name | ForEach-Object { $_.Path })
                }
            }
            $provenanceBefore = (Get-Command Get-ComposeDatabaseServiceHostAlias).Module.Path

            $cell = Invoke-CreationOwnershipCell -StartScript 'start-local-dms.ps1' -IdentityProvider 'self-contained'
            $cell.RestoreFailure | Should -BeNullOrEmpty

            foreach ($name in $script:ownershipStagedModule) {
                @(Get-Module $name -All | ForEach-Object { $_.Path }) |
                    Should -Be $moduleBefore[$name].All -Because "$name's loaded instances must match the snapshot"
                @(Get-Module $name | ForEach-Object { $_.Path }) |
                    Should -Be $moduleBefore[$name].Top -Because "$name's top-level instances must match the snapshot"
            }
            (Get-Command Get-ComposeDatabaseServiceHostAlias).Module.Path |
                Should -Be $provenanceBefore -Because "commands must resolve from the same module as before the cell"
            @(Get-ComposeDatabaseServiceHostAlias -DatabaseEngine 'postgresql') |
                Should -Be @('db', 'dms-postgresql') -Because "the module's default compose root must still be the real one"
        }

        It "restores everything when the observation file cannot be parsed" {
            # Reading the observation happens inside the cleanup boundary, so a throw there used to abandon
            # the rest of it. Measured against the pre-fix harness: one unparsable line left the sentinel
            # environment value clobbered and leaked 24 staging directories. Restoration must therefore be
            # unconditional with respect to the read, and the read failure must be reported rather than
            # swallowed or allowed to replace the run's own error.
            $sentinelIdentity = 'sentinel-malformed-observation'
            [System.Environment]::SetEnvironmentVariable('DMS_CONFIG_IDENTITY_PROVIDER', $sentinelIdentity)
            [System.Environment]::SetEnvironmentVariable('DMS_CONFIG_CLAIMS_SOURCE', 'sentinel-claims')
            Set-Variable -Name LASTEXITCODE -Scope Global -Value 63
            function global:docker { 'sentinel-observation-docker' }
            $dockerDefinitionBefore = (Get-Item Function:\docker).Definition
            $locationBefore = (Get-Location).Path
            $stackBefore = @(Get-OwnershipLocationStack)
            $provenanceBefore = (Get-Command Get-ComposeDatabaseServiceHostAlias).Module.Path
            $moduleBefore = @{}
            foreach ($name in $script:ownershipStagedModule) {
                $moduleBefore[$name] = @(Get-Module $name -All | ForEach-Object { $_.Path })
            }

            $cell = Invoke-CreationOwnershipCell -StartScript 'start-published-dms.ps1' `
                -IdentityProvider 'self-contained' -CorruptObservation

            # Reported, and named by exception type only - never the malformed content, which sits in a
            # file whose records name -NewClientSecret.
            $cell.ObservationFailure | Should -Not -BeNullOrEmpty -Because "an unreadable observation must be reported"
            $cell.ObservationFailure | Should -BeLike "*could not be read or parsed*"
            $cell.ObservationFailure | Should -Not -BeLike "*not valid json*" -Because "diagnostics must not echo the malformed content"
            $cell.ErrorMessage | Should -BeLike "*Docker environment*" -Because "the run's own failure must not be replaced by the observation failure"

            # Every resource in the inventory, restored despite the parse failure.
            $cell.RestoreFailure | Should -BeNullOrEmpty
            $cell.Imbalance | Should -BeNullOrEmpty
            [System.Environment]::GetEnvironmentVariable('DMS_CONFIG_IDENTITY_PROVIDER') | Should -Be $sentinelIdentity
            [System.Environment]::GetEnvironmentVariable('DMS_CONFIG_CLAIMS_SOURCE') | Should -Be 'sentinel-claims'
            $global:LASTEXITCODE | Should -Be 63
            (Get-Item -LiteralPath Function:\docker -ErrorAction SilentlyContinue).Definition | Should -Be $dockerDefinitionBefore
            (Get-Location).Path | Should -Be $locationBefore
            @(Get-OwnershipLocationStack) | Should -Be $stackBefore
            (Get-Command Get-ComposeDatabaseServiceHostAlias).Module.Path | Should -Be $provenanceBefore
            foreach ($name in $script:ownershipStagedModule) {
                @(Get-Module $name -All | ForEach-Object { $_.Path }) | Should -Be $moduleBefore[$name]
            }
            @(Get-ComposeDatabaseServiceHostAlias -DatabaseEngine 'postgresql') | Should -Be @('db', 'dms-postgresql')
            Test-Path -LiteralPath $cell.StagingPath | Should -BeFalse -Because "the staging directory must be removed even when the parse failed"

            # And the interception still held: the recorded compose calls prove no real docker ran.
            @($cell.DockerCommand | Where-Object { $_ -like 'compose *' }).Count | Should -BeGreaterThan 0
        }

        It "restores everything and cleans up when staging fails part-way" {
            $locationBefore = (Get-Location).Path
            $stagingBefore = @(Get-ChildItem ([System.IO.Path]::GetTempPath()) -Directory -Filter 'dms-ownership-*' -ErrorAction SilentlyContinue).Count

            $cell = Invoke-CreationOwnershipCell -StartScript 'start-published-dms.ps1' -IdentityProvider 'self-contained' -FailStaging

            $cell.ErrorMessage | Should -BeLike "*Forced staging failure*"
            $cell.RestoreFailure | Should -BeNullOrEmpty -Because "restoration must run even when staging failed"
            $cell.Imbalance | Should -BeNullOrEmpty
            (Get-Location).Path | Should -Be $locationBefore
            # The stand-in must be gone - but "gone" means "back to whatever this session had", which in a
            # hostile session is a caller's own docker function rather than nothing.
            $dockerNow = Get-Item -LiteralPath Function:\docker -ErrorAction SilentlyContinue
            $dockerSnapshot = $script:ownershipSessionSnapshot.Function['docker']
            if ($null -eq $dockerSnapshot) { $dockerNow | Should -BeNullOrEmpty -Because "no docker function existed before the cell" }
            else { $dockerNow.Definition | Should -Be $dockerSnapshot.Definition -Because "the caller's docker function must be restored" }
            Test-Path -LiteralPath $cell.StagingPath | Should -BeFalse -Because "the partial staging directory must be removed"
            @(Get-ChildItem ([System.IO.Path]::GetTempPath()) -Directory -Filter 'dms-ownership-*' -ErrorAction SilentlyContinue).Count |
                Should -Be $stagingBefore -Because "only this cell's own GUID-scoped directory may be removed, and none may be left"
        }
    }

    It "keeps CMS the Keycloak-mode database-creation owner: both config compose files enable the startup deploy" {
        foreach ($composeFile in @('local-config.yml', 'published-config.yml')) {
            $content = Get-Content -LiteralPath (Join-Path $script:dockerComposeRoot $composeFile) -Raw
            $content | Should -Match 'AppSettings__DeployDatabaseOnStartup:\s*\$\{DMS_CONFIG_DEPLOY_DATABASE:-true\}' -Because "$composeFile must default CMS's own EnsureDatabase deploy on, so Keycloak-mode creation needs no script-side bootstrap"
        }
    }

    It "postgresql-init.sh creates only the datastore database, never the CMS one" {
        # Container-init creation of the CMS database would duplicate ownership held by the
        # OpenIddict bootstrap (self-contained) or CMS's deploy (Keycloak).
        $content = Get-Content -LiteralPath (Join-Path $script:dockerComposeRoot 'postgresql-init.sh') -Raw

        $content | Should -Match 'CREATE DATABASE \$\{POSTGRES_DB_NAME\}'
        $content | Should -Not -Match 'configurationservice' -Because "the CMS database is never a container-init concern"
        $content | Should -Not -Match 'DMS_CONFIG_DATABASE_NAME' -Because "the topology seam must not leak into container initialization"
    }
}
