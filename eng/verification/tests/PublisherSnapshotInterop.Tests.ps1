# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

# Docker-free tests for the pure helpers of the Publisher snapshot interoperability harness: snapshot and
# login naming, the connection string DMS receives, the PostgreSQL and SQL Server grant and snapshot text,
# the wait-state decision, and the seed-race signature. Nothing here starts Docker or touches a network.
#
# Each helper has a negative case, so a helper that stopped validating (or dropped a grant) fails here.

BeforeAll {
    $script:harnessRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../PublisherSnapshotInterop"))
    $script:entryScript = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../Invoke-PublisherSnapshotInteropCheck.ps1"))

    Import-Module (Join-Path $script:harnessRoot "PublisherSnapshotInterop.psm1") -Force

    # A throwaway literal that matches the shape New-InteropReadOnlyPassword produces. It is never sent anywhere.
    $script:password = "Abcdefgh23456789Abcdefgh23456789"
    $script:stamp = [datetime]::new(2026, 11, 3, 18, 30, 5, [System.DateTimeKind]::Utc)

    # Lines captured from real runs: the Publisher Debug log of Publisher v1.4.0, and the DMS JSON console
    # log of a stack with the Debug override for SelectEffectiveDataStoreTargetMiddleware. In the DMS
    # fixture the source data store is 1 and the target data store is 3.
    $fixtureRoot = Join-Path $PSScriptRoot "PublisherSnapshotInterop.fixtures"
    $script:publisherLines = [string[]](Get-Content -LiteralPath (Join-Path $fixtureRoot "publisher-debug.log.txt"))
    $script:dmsLines = [string[]](Get-Content -LiteralPath (Join-Path $fixtureRoot "dms-requests.jsonl"))
    $script:selectedPrimaryLine = $script:dmsLines | Where-Object { $_ -like '*"MessageTemplate":"Selected {TargetKind}*"DataStoreId":1,*' } | Select-Object -First 1
}

Describe "the entry script's PublisherImage default" {
    It "equals the module's default image, so the two literals cannot drift apart" {
        $tokens = $null
        $errors = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseFile($script:entryScript, [ref]$tokens, [ref]$errors)
        $errors | Should -BeNullOrEmpty

        $parameter = $ast.ParamBlock.Parameters | Where-Object { $_.Name.VariablePath.UserPath -eq "PublisherImage" }
        $parameter.DefaultValue.Value | Should -Be (Get-InteropDefaultPublisherImage)
    }

    It "is pinned by digest" {
        Get-InteropImageDigest -Image (Get-InteropDefaultPublisherImage) | Should -Match "^sha256:[0-9a-f]{64}$"
    }

    It "would be caught if the two diverged (an unpinned tag has no digest)" {
        Get-InteropImageDigest -Image "edfialliance/ods-api-publisher:v1.4.0" | Should -Be ""
    }
}

Describe "Get-InteropSnapshotName and Get-InteropSnapshotLoginName" {
    It "names the snapshot with the source name, a snapshot marker, and a UTC timestamp" {
        Get-InteropSnapshotName -SourceDatabaseName "edfi_datamanagementservice" -TimestampUtc $script:stamp |
            Should -Be "edfi_datamanagementservice_snapshot_20261103183005"
    }

    It "gives the login the same timestamp, so two snapshots never share a cluster-wide login" {
        Get-InteropSnapshotLoginName -TimestampUtc $script:stamp | Should -Be "dms_snapshot_reader_20261103183005"
        Get-InteropSnapshotLoginName -TimestampUtc $script:stamp.AddSeconds(1) | Should -Not -Be (Get-InteropSnapshotLoginName -TimestampUtc $script:stamp)
    }

    It "refuses a source name that is not a plain identifier" {
        { Get-InteropSnapshotName -SourceDatabaseName "bad;DROP" -TimestampUtc $script:stamp } | Should -Throw "*not a plain SQL identifier*"
    }

    It "refuses a name PostgreSQL would truncate" {
        { Get-InteropSnapshotName -SourceDatabaseName ("a" * 60) -TimestampUtc $script:stamp } | Should -Throw "*not a plain SQL identifier*"
    }
}

Describe "New-InteropReadOnlyPassword" {
    It "generates a different letters-and-digits password each time" {
        $first = New-InteropReadOnlyPassword
        $second = New-InteropReadOnlyPassword
        $first | Should -Match "^[A-Za-z0-9]{32}$"
        $first | Should -Not -Be $second
    }

    It "refuses a length too short to be a password" {
        { New-InteropReadOnlyPassword -Length 4 } | Should -Throw
    }
}

Describe "Get-InteropSnapshotConnectionString" {
    It "builds a PostgreSQL string with the read-only login and the Docker host" {
        $connection = Get-InteropSnapshotConnectionString -Engine postgresql -SnapshotName "snap_db" -LoginName "dms_snapshot_reader_1" -Password $script:password

        $connection | Should -Match "(?i)host=dms-postgresql"
        $connection | Should -Match "(?i)port=5432"
        $connection | Should -Match "(?i)username=dms_snapshot_reader_1"
        $connection | Should -Match "(?i)database=snap_db"
        $connection | Should -Match "(?i)password=$($script:password)"
        $connection | Should -Not -Match "(?i)username=postgres"
    }

    It "builds a SQL Server string with the read-only login and the Docker host" {
        $connection = Get-InteropSnapshotConnectionString -Engine mssql -SnapshotName "snap_db" -LoginName "dms_snapshot_reader_1" -Password $script:password

        $connection | Should -Match "(?i)Server=dms-mssql,1433"
        $connection | Should -Match "(?i)User Id=dms_snapshot_reader_1"
        $connection | Should -Match "(?i)Database=snap_db"
        $connection | Should -Not -Match "(?i)User Id=sa"
    }

    It "never names localhost or a published host port, which DMS in a container could not reach" {
        foreach ($engine in "postgresql", "mssql") {
            Get-InteropSnapshotConnectionString -Engine $engine -SnapshotName "snap_db" -LoginName "dms_snapshot_reader_1" -Password $script:password |
                Should -Not -Match "(?i)localhost|127\.0\.0\.1"
        }
    }

    It "refuses a login name that is not a plain identifier" {
        { Get-InteropSnapshotConnectionString -Engine mssql -SnapshotName "snap_db" -LoginName "sa; --" -Password $script:password } |
            Should -Throw "*not a plain SQL identifier*"
    }
}

Describe "Get-InteropPostgresReadOnlySql" {
    BeforeAll {
        $script:postgresSql = Get-InteropPostgresReadOnlySql -DatabaseName "snap_db" -LoginName "dms_snapshot_reader_1" -Password $script:password
    }

    It "creates a login role that cannot write, create, or inherit" {
        $script:postgresSql | Should -Match "CREATE ROLE dms_snapshot_reader_1 LOGIN PASSWORD '$($script:password)' NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT;"
    }

    It "revokes PUBLIC and grants CONNECT and TEMPORARY back (DMS creates temp tables on GET-many)" {
        $script:postgresSql | Should -Match "REVOKE ALL ON DATABASE snap_db FROM PUBLIC;"
        $script:postgresSql | Should -Match "GRANT CONNECT, TEMPORARY ON DATABASE snap_db TO dms_snapshot_reader_1;"
    }

    It "grants USAGE and SELECT on every schema of the data store database" {
        $schemas = "dms, edfi, tpdm, auth, tracked_changes_edfi, tracked_changes_tpdm"
        $script:postgresSql | Should -Match "GRANT USAGE ON SCHEMA $schemas TO dms_snapshot_reader_1;"
        $script:postgresSql | Should -Match "GRANT SELECT ON ALL TABLES IN SCHEMA $schemas TO dms_snapshot_reader_1;"
    }

    It "grants SELECT on the change version sequence, which SELECT ON ALL TABLES does not cover" {
        $script:postgresSql | Should -Match 'GRANT SELECT ON SEQUENCE dms\."ChangeVersionSequence" TO dms_snapshot_reader_1;'
    }

    It "grants nothing that writes" {
        $script:postgresSql | Should -Not -Match "(?i)INSERT|UPDATE|DELETE|ALL PRIVILEGES|OWNER|GRANT\s+\w*\s*SUPERUSER"
    }

    It "refuses a password that would break out of the SQL literal" {
        { Get-InteropPostgresReadOnlySql -DatabaseName "snap_db" -LoginName "dms_snapshot_reader_1" -Password "x'; DROP ROLE postgres; --xxxxxxxxxxxx" } |
            Should -Throw "*16 to 128 letters and digits*"
    }

    It "refuses an empty schema list" {
        { Get-InteropPostgresReadOnlySql -DatabaseName "snap_db" -LoginName "dms_snapshot_reader_1" -Password $script:password -Schema @() } |
            Should -Throw "*At least one schema*"
    }
}

Describe "Get-InteropSqlServerReadOnlySql" {
    BeforeAll {
        $script:mssqlGrantSql = Get-InteropSqlServerReadOnlySql -SourceDatabaseName "src_db" -LoginName "dms_snapshot_reader_1" -Password $script:password
    }

    It "turns QUOTED_IDENTIFIER on, since sqlcmd defaults it off and the grants touch dms.*" {
        $script:mssqlGrantSql | Should -Match "^SET QUOTED_IDENTIFIER ON;"
    }

    It "creates the login, then the user in the primary, with db_datareader" {
        $script:mssqlGrantSql | Should -Match "CREATE LOGIN dms_snapshot_reader_1 WITH PASSWORD = N'$($script:password)', CHECK_POLICY = OFF, DEFAULT_DATABASE = master;"
        $script:mssqlGrantSql | Should -Match "USE src_db;\s+CREATE USER dms_snapshot_reader_1 FOR LOGIN dms_snapshot_reader_1;"
        $script:mssqlGrantSql | Should -Match "ALTER ROLE db_datareader ADD MEMBER dms_snapshot_reader_1;"
    }

    It "grants EXECUTE on GetMaxChangeVersion and VIEW DEFINITION on the sequence" {
        $script:mssqlGrantSql | Should -Match "GRANT EXECUTE ON OBJECT::dms\.GetMaxChangeVersion TO dms_snapshot_reader_1;"
        $script:mssqlGrantSql | Should -Match "GRANT VIEW DEFINITION ON OBJECT::dms\.ChangeVersionSequence TO dms_snapshot_reader_1;"
    }

    It "never makes the user a writer or owner" {
        $script:mssqlGrantSql | Should -Not -Match "(?i)db_datawriter|db_owner|sysadmin"
    }

    It "refuses a password that would break out of the SQL literal" {
        { Get-InteropSqlServerReadOnlySql -SourceDatabaseName "src_db" -LoginName "dms_snapshot_reader_1" -Password "x'; DROP LOGIN sa; --xxxxxxxxxxxx" } |
            Should -Throw "*16 to 128 letters and digits*"
    }
}

Describe "Get-InteropSqlServerSnapshotSql" {
    It "lists one sparse file for a source with one logical data file" {
        Get-InteropSqlServerSnapshotSql -SnapshotName "snap_db" -SourceDatabaseName "src_db" -LogicalFileName @("src_db") |
            Should -Be "CREATE DATABASE snap_db ON (NAME = N'src_db', FILENAME = N'/var/opt/mssql/data/snap_db_1.ss') AS SNAPSHOT OF src_db;"
    }

    It "lists a sparse file per logical data file for a source with several, each with its own file" {
        Get-InteropSqlServerSnapshotSql -SnapshotName "snap_db" -SourceDatabaseName "src_db" -LogicalFileName @("src_db", "src_db_data2", "src_db_data3") |
            Should -Be ("CREATE DATABASE snap_db ON (NAME = N'src_db', FILENAME = N'/var/opt/mssql/data/snap_db_1.ss'), " +
                "(NAME = N'src_db_data2', FILENAME = N'/var/opt/mssql/data/snap_db_2.ss'), " +
                "(NAME = N'src_db_data3', FILENAME = N'/var/opt/mssql/data/snap_db_3.ss') AS SNAPSHOT OF src_db;")
    }

    It "doubles a single quote in a logical file name" {
        Get-InteropSqlServerSnapshotSql -SnapshotName "snap_db" -SourceDatabaseName "src_db" -LogicalFileName @("it's") |
            Should -Match "NAME = N'it''s'"
    }

    It "throws when no logical file name is given, because a snapshot must cover every data file" {
        { Get-InteropSqlServerSnapshotSql -SnapshotName "snap_db" -SourceDatabaseName "src_db" -LogicalFileName @() } |
            Should -Throw "*logical name of every data file*"
    }

    It "throws when one of several logical file names is empty" {
        { Get-InteropSqlServerSnapshotSql -SnapshotName "snap_db" -SourceDatabaseName "src_db" -LogicalFileName @("src_db", " ") } |
            Should -Throw "*number 2 is empty*"
    }

    It "refuses a snapshot name that is not a plain identifier" {
        { Get-InteropSqlServerSnapshotSql -SnapshotName "snap;db" -SourceDatabaseName "src_db" -LogicalFileName @("src_db") } |
            Should -Throw "*not a plain SQL identifier*"
    }
}

Describe "Get-InteropSnapshotRemovalSql" {
    It "drops the PostgreSQL database and role by name" {
        Get-InteropSnapshotRemovalSql -Engine postgresql -SnapshotName "snap_db" -LoginName "dms_snapshot_reader_1" |
            Should -Be "DROP DATABASE IF EXISTS snap_db WITH (FORCE);`nDROP ROLE IF EXISTS dms_snapshot_reader_1;"
    }

    It "drops the SQL Server snapshot, then the user in the primary, then the login" {
        $sql = Get-InteropSnapshotRemovalSql -Engine mssql -SnapshotName "snap_db" -LoginName "dms_snapshot_reader_1" -SourceDatabaseName "src_db"
        $sql | Should -Match "DROP DATABASE snap_db;"
        $sql | Should -Match "USE src_db;\s+IF USER_ID\(N'dms_snapshot_reader_1'\) IS NOT NULL DROP USER dms_snapshot_reader_1;"
        $sql.IndexOf("DROP USER") | Should -BeLessThan $sql.IndexOf("DROP LOGIN")
    }

    It "needs the source database to drop a SQL Server user" {
        { Get-InteropSnapshotRemovalSql -Engine mssql -SnapshotName "snap_db" -LoginName "dms_snapshot_reader_1" } |
            Should -Throw "*source database name*"
    }
}

Describe "snapshot wait decisions" {
    It "derives the wait bound from the cache expiry" {
        Get-InteropSnapshotWaitBound -CacheExpirationSeconds 10 | Should -Be 60
        Get-InteropSnapshotWaitBound -CacheExpirationSeconds 600 | Should -Be 1830
    }

    It "refuses a zero cache expiry rather than polling with no bound" {
        { Get-InteropSnapshotWaitBound -CacheExpirationSeconds 0 } | Should -Throw
    }

    It "reads a 200 as Active and a 404 'Snapshot not found.' as Absent" {
        Resolve-InteropSnapshotProbeState -StatusCode 200 -Body "[]" | Should -Be "Active"
        Resolve-InteropSnapshotProbeState -StatusCode 404 -Body '{"detail":"Snapshot not found.","status":404}' | Should -Be "Absent"
    }

    It "treats a 5xx as a fault instead of a state to wait out" {
        { Resolve-InteropSnapshotProbeState -StatusCode 500 -Body "{}" } | Should -Throw "*HTTP 500*"
    }

    It "treats a 404 that is not the snapshot being absent as a fault" {
        { Resolve-InteropSnapshotProbeState -StatusCode 404 -Body '{"detail":"Resource not found."}' } | Should -Throw "*HTTP 404*"
    }
}

Describe "Test-InteropSeedRaceFailure" {
    It "recognises the BulkLoadClient log4net race" {
        Test-InteropSeedRaceFailure -Text "ERROR LoadProcess - ---> System.InvalidOperationException: Stack empty." | Should -BeTrue
        Test-InteropSeedRaceFailure -Text "System.AggregateException: One or more errors occurred. (Stack empty.)" | Should -BeTrue
    }

    It "does not match any other seed failure, so only the known race is retried" {
        Test-InteropSeedRaceFailure -Text "BulkLoadClient exited with non-zero code: 1" | Should -BeFalse
        Test-InteropSeedRaceFailure -Text "System.InvalidOperationException: Sequence contains no elements" | Should -BeFalse
        Test-InteropSeedRaceFailure -Text "" | Should -BeFalse
    }
}

Describe "Start-InteropStack seed retry" {
    BeforeAll {
        $script:stackEnvironmentFile = Join-Path $TestDrive "stack.env"
        Set-Content -LiteralPath $script:stackEnvironmentFile -Value "DMS_HTTP_PORTS=8080"
    }

    BeforeEach {
        # The bootstrap and seed scripts are replaced by a queue of canned outcomes, so nothing reaches Docker.
        # The mock body runs inside the module, so it reaches the queue through a closure.
        $script:outcomes = [System.Collections.Generic.Queue[object]]::new()
        $outcomes = $script:outcomes
        Mock -ModuleName PublisherSnapshotInterop Assert-InteropNoForeignStack {}
        Mock -ModuleName PublisherSnapshotInterop Invoke-InteropScriptCaptured { return $outcomes.Dequeue() }.GetNewClosure()
    }

    It "retries only the seed step after the known race, then succeeds" {
        $script:outcomes.Enqueue([pscustomobject]@{ Failure = "exit code 1"; Output = "ERROR System.InvalidOperationException: Stack empty." })
        $script:outcomes.Enqueue([pscustomobject]@{ Failure = $null; Output = "Seed delivery complete." })

        Start-InteropStack -Engine mssql -EnvironmentFile $script:stackEnvironmentFile

        Should -Invoke -ModuleName PublisherSnapshotInterop Invoke-InteropScriptCaptured -Times 1 -Exactly -ParameterFilter { $Path -like "*bootstrap-local-dms.ps1" }
        Should -Invoke -ModuleName PublisherSnapshotInterop Invoke-InteropScriptCaptured -Times 1 -Exactly -ParameterFilter { $Path -like "*load-dms-seed-data.ps1" }
    }

    It "does not retry a failure that is not the known race" {
        $script:outcomes.Enqueue([pscustomobject]@{ Failure = "exit code 1"; Output = "BulkLoadClient exited with non-zero code: 1" })

        { Start-InteropStack -Engine mssql -EnvironmentFile $script:stackEnvironmentFile } | Should -Throw "*bootstrap-local-dms.ps1 failed*"

        Should -Invoke -ModuleName PublisherSnapshotInterop Invoke-InteropScriptCaptured -Times 1 -Exactly -ParameterFilter { $Path -like "*bootstrap-local-dms.ps1" }
        Should -Invoke -ModuleName PublisherSnapshotInterop Invoke-InteropScriptCaptured -Times 0 -Exactly -ParameterFilter { $Path -like "*load-dms-seed-data.ps1" }
    }

    It "gives up after three seed retries when the race keeps happening" {
        for ($index = 0; $index -lt 5; $index++) {
            $script:outcomes.Enqueue([pscustomobject]@{ Failure = "exit code 1"; Output = "System.InvalidOperationException: Stack empty." })
        }

        { Start-InteropStack -Engine mssql -EnvironmentFile $script:stackEnvironmentFile } | Should -Throw "*after 3 seed retries*"

        Should -Invoke -ModuleName PublisherSnapshotInterop Invoke-InteropScriptCaptured -Times 3 -Exactly -ParameterFilter { $Path -like "*load-dms-seed-data.ps1" }
    }
}

Describe "Get-InteropPublisherContainerName and Get-InteropPublisherArgument" {
    It "prefixes every container with dms1370-pub- so it can be removed by name" {
        Get-InteropPublisherContainerName -RunName "accept-full" | Should -Be "dms1370-pub-accept-full"
    }

    It "refuses a run name that is not a plain lowercase token" {
        { Get-InteropPublisherContainerName -RunName "Bad Name;rm" } | Should -Throw "*lowercase letters*"
    }

    It "always sets both names, because availableChangeVersions is requested only when both are present" {
        $arguments = Get-InteropPublisherArgument -SourceUrl "http://s" -TargetUrl "http://t" -SourceKey "KEYONE" -SourceSecret "SECRETONE" -TargetKey "KEYTWO" -TargetSecret "SECRETTWO"
        $arguments | Should -Contain "--sourceName=dms1370-source"
        $arguments | Should -Contain "--targetName=dms1370-target"
    }

    It "appends the run's own options after the connection arguments" {
        $arguments = Get-InteropPublisherArgument -SourceUrl "http://s" -TargetUrl "http://t" -SourceKey "KEYONE" -SourceSecret "SECRETONE" -TargetKey "KEYTWO" -TargetSecret "SECRETTWO" -Option "--ignoreIsolation=true", "--lastChangeVersionProcessed=7"
        $arguments[-2] | Should -Be "--ignoreIsolation=true"
        $arguments[-1] | Should -Be "--lastChangeVersionProcessed=7"
    }

    It "passes the real credentials when not redacting" {
        $arguments = Get-InteropPublisherArgument -SourceUrl "http://s" -TargetUrl "http://t" -SourceKey "KEYONE" -SourceSecret "SECRETONE" -TargetKey "KEYTWO" -TargetSecret "SECRETTWO"
        $arguments | Should -Contain "--sourceKey=KEYONE"
        $arguments | Should -Contain "--targetSecret=SECRETTWO"
    }

    It "replaces every credential with a placeholder when redacting, so nothing secret reaches the run folder" {
        $arguments = Get-InteropPublisherArgument -SourceUrl "http://s" -TargetUrl "http://t" -SourceKey "KEYONE" -SourceSecret "SECRETONE" -TargetKey "KEYTWO" -TargetSecret "SECRETTWO" -Redact
        ($arguments -join " ") | Should -Not -Match "KEYONE|SECRETONE|KEYTWO|SECRETTWO"
        $arguments | Should -Contain "--sourceKey=<SOURCE_KEY>"
        $arguments | Should -Contain "--sourceSecret=<SOURCE_SECRET>"
        $arguments | Should -Contain "--targetKey=<TARGET_KEY>"
        $arguments | Should -Contain "--targetSecret=<TARGET_SECRET>"
    }
}

Describe "ConvertFrom-InteropDockerTimestamp and Get-InteropLogWindow" {
    It "parses the nine fractional digits docker reports" {
        $parsed = ConvertFrom-InteropDockerTimestamp -Text "2026-11-03T19:46:06.100412345Z"
        $parsed.Kind | Should -Be ([System.DateTimeKind]::Utc)
        $parsed.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff") | Should -Be "2026-11-03T19:46:06.1004123"
    }

    It "floors the window start to the second" {
        $window = Get-InteropLogWindow -StartedUtc ([datetime]::new(2026, 11, 3, 19, 46, 6, 900, [System.DateTimeKind]::Utc)) -FinishedUtc ([datetime]::new(2026, 11, 3, 19, 46, 27, 10, [System.DateTimeKind]::Utc))
        $window.Since | Should -Be "2026-11-03T19:46:06Z"
    }

    It "ends the window after the finish second, so the last requests are not cut off by precision" {
        $window = Get-InteropLogWindow -StartedUtc ([datetime]::new(2026, 11, 3, 19, 46, 6, 900, [System.DateTimeKind]::Utc)) -FinishedUtc ([datetime]::new(2026, 11, 3, 19, 46, 27, 990, [System.DateTimeKind]::Utc))
        $window.Until | Should -Be "2026-11-03T19:46:29Z"
    }
}

Describe "Assert-InteropFileHasNoSecret" {
    It "throws when a credential value is in the file" {
        $path = Join-Path $TestDrive "leaky.log"
        Set-Content -LiteralPath $path -Value "token request for key AbCdEf123456 sent"
        { Assert-InteropFileHasNoSecret -Path $path -Secret "AbCdEf123456" } | Should -Throw "*credential value*"
    }

    It "passes when the file holds none of the values" {
        $path = Join-Path $TestDrive "clean.log"
        Set-Content -LiteralPath $path -Value "Processing finished in 17 seconds."
        { Assert-InteropFileHasNoSecret -Path $path -Secret "AbCdEf123456", "" } | Should -Not -Throw
    }
}

Describe "Get-InteropRequestSurface" {
    It "classifies <Method> <Path><Query> as <Expected>" -ForEach @(
        @{ Method = "GET"; Path = "/changeQueries/v1/availableChangeVersions"; Query = ""; Expected = "AvailableChangeVersions" }
        @{ Method = "GET"; Path = "/data/ed-fi/schools/partitions"; Query = "?number=1"; Expected = "Partitions" }
        @{ Method = "GET"; Path = "/data/ed-fi/schools/partitions"; Query = "?number=5&minChangeVersion=1&maxChangeVersion=9"; Expected = "Partitions" }
        @{ Method = "GET"; Path = "/data/ed-fi/schools"; Query = "?pageToken=YywxMjgz&pageSize=500&minChangeVersion=1&maxChangeVersion=9"; Expected = "CursorPage" }
        @{ Method = "GET"; Path = "/data/ed-fi/schools"; Query = "?limit=0&totalCount=true&minChangeVersion=1&maxChangeVersion=9"; Expected = "TotalCount" }
        @{ Method = "GET"; Path = "/data/ed-fi/schools"; Query = "?offset=0&limit=500&minChangeVersion=1&maxChangeVersion=9"; Expected = "GetMany" }
        @{ Method = "GET"; Path = "/data/ed-fi/classPeriods/deletes"; Query = "?limit=1"; Expected = "Deletes" }
        @{ Method = "GET"; Path = "/data/ed-fi/classPeriods/keyChanges"; Query = "?offset=0&limit=500"; Expected = "KeyChanges" }
        @{ Method = "GET"; Path = "/data/ed-fi/schools/48df39e2-be12-1064-e4db-d72957dc5ab6"; Query = ""; Expected = "GetById" }
        @{ Method = "POST"; Path = "/data/ed-fi/schools"; Query = ""; Expected = "Write" }
        @{ Method = "POST"; Path = "/oauth/token"; Query = ""; Expected = "Authentication" }
        @{ Method = "GET"; Path = "/"; Query = ""; Expected = "Discovery" }
    ) {
        Get-InteropRequestSurface -Method $Method -Path $Path -Query $Query | Should -Be $Expected
    }

    It "decides /deletes from the path before the query, so a totalCount read of /deletes is a Deletes request" {
        Get-InteropRequestSurface -Method "GET" -Path "/data/ed-fi/classPeriods/deletes" -Query "?limit=0&totalCount=true&minChangeVersion=1&maxChangeVersion=9" | Should -Be "Deletes"
    }

    It "reports an unknown GET shape as Unknown instead of dropping it" {
        Get-InteropRequestSurface -Method "GET" -Path "/data/ed-fi/classPeriods" -Query "?classPeriodName=CP-2-A&schoolId=930190101" | Should -Be "Unknown"
    }
}

Describe "ConvertFrom-InteropSelectionLine" {
    It "reads the kind, data store, and trace id from the DMS JSON event" {
        $selection = ConvertFrom-InteropSelectionLine -Line $script:selectedPrimaryLine
        $selection.Kind | Should -Be "Primary"
        $selection.DataStoreId | Should -Be 1
        $selection.TraceId | Should -Match "^0H[0-9A-Z]+:[0-9A-F]{8}$"
        $selection.TimestampUtc | Should -Not -BeNullOrEmpty
    }

    It "reads the plain rendered text" {
        $selection = ConvertFrom-InteropSelectionLine -Line 'Selected Snapshot target for data store 7 - TraceId: "0HABC:00000002"'
        $selection.Kind | Should -Be "Snapshot"
        $selection.DataStoreId | Should -Be 7
        $selection.TraceId | Should -Be "0HABC:00000002"
    }

    It "ignores the line DMS logs when a snapshot was requested but none is configured" {
        $line = 'A snapshot was requested but data store 1 ("Local Development Data Store") has none configured - TraceId: 0HABC:00000002'
        ConvertFrom-InteropSelectionLine -Line $line | Should -BeNullOrEmpty
    }

    It "ignores a line that is not a selection" {
        ConvertFrom-InteropSelectionLine -Line "Processing finished in 17 seconds." | Should -BeNullOrEmpty
    }
}

Describe "Get-InteropTargetSelections" {
    It "counts the source data store's Primary selections in the fixture" {
        $counts = Get-InteropTargetSelections -Line $script:dmsLines -DataStoreId 1
        $counts["Primary"] | Should -Be 11
        $counts["Snapshot"] | Should -Be 0
    }

    It "does not count the target data store's lines for the source" {
        $source = Get-InteropTargetSelections -Line $script:dmsLines -DataStoreId 1
        $target = Get-InteropTargetSelections -Line $script:dmsLines -DataStoreId 3
        $target["Primary"] | Should -Be 3
        ($source["Primary"] + $target["Primary"]) | Should -Be 14
    }

    It "counts a Primary line as Primary and a Snapshot line as Snapshot" {
        $lines = @(
            'Selected Primary target for data store 1 - TraceId: "0HA:00000001"'
            'Selected Snapshot target for data store 1 - TraceId: "0HA:00000002"'
            'Selected Snapshot target for data store 1 - TraceId: "0HA:00000003"'
            'Selected Snapshot target for data store 2 - TraceId: "0HA:00000004"'
        )
        $counts = Get-InteropTargetSelections -Line $lines -DataStoreId 1
        $counts["Primary"] | Should -Be 1
        $counts["Snapshot"] | Should -Be 2
    }

    It "reports a kind it does not know rather than dropping it" {
        $counts = Get-InteropTargetSelections -Line @('Selected Replica target for data store 1 - TraceId: "0HA:00000001"') -DataStoreId 1
        $counts["Replica"] | Should -Be 1
    }

    It "does not count a line outside the run window" {
        $window = [datetime]::new(2030, 1, 1, 0, 0, 0, [System.DateTimeKind]::Utc)
        (Get-InteropTargetSelections -Line $script:dmsLines -DataStoreId 1 -FromUtc $window)["Primary"] | Should -Be 0
    }
}

Describe "ConvertTo-InteropDmsRequest and Get-InteropSurfaceCount" {
    BeforeAll {
        $script:requests = ConvertTo-InteropDmsRequest -Line $script:dmsLines
    }

    It "joins the three events on the trace id, one record per request" {
        $script:requests.Count | Should -Be 19
        ($script:requests | Where-Object { $_.Path -eq "/changeQueries/v1/availableChangeVersions" }).Count | Should -Be 2
        ($script:requests | Where-Object { $null -eq $_.StatusCode }).Count | Should -Be 0
    }

    It "gives every data request a data store and target kind, and leaves token and Discovery calls without" {
        $without = @($script:requests | Where-Object { $null -eq $_.DataStoreId })
        $without.Count | Should -Be 5
        ($without | Where-Object { $_.Surface -in @("Authentication", "Discovery") }).Count | Should -Be 5
        ($script:requests | Where-Object { $null -ne $_.DataStoreId } | Where-Object { $_.TargetKind -ne "Primary" }).Count | Should -Be 0
    }

    It "counts every source surface for the source data store only" {
        $result = Get-InteropSurfaceCount -Request $script:requests -DataStoreId 1
        $result.Counts["AvailableChangeVersions"] | Should -Be 2
        $result.Counts["Partitions"] | Should -Be 2
        $result.Counts["CursorPage"] | Should -Be 1
        $result.Counts["TotalCount"] | Should -Be 1
        $result.Counts["GetMany"] | Should -Be 1
        $result.Counts["Deletes"] | Should -Be 2
        $result.Counts["KeyChanges"] | Should -Be 2
        $result.Counts["GetById"] | Should -Be 0
        $result.Unclassified | Should -BeNullOrEmpty
    }

    It "keeps the target's own reads and writes out of the source counts" {
        $target = Get-InteropSurfaceCount -Request $script:requests -DataStoreId 3
        $target.Counts["Write"] | Should -Be 2
        $target.Counts["Unknown"] | Should -Be 1
        $target.Unclassified | Should -Be @("GET /data/ed-fi/classPeriods?classPeriodName=CP-2-A&schoolId=930190101")
    }

    It "filters by target kind, so a Snapshot count is zero while every source request was Primary" {
        (Get-InteropSurfaceCount -Request $script:requests -DataStoreId 1 -TargetKind "Snapshot").Counts["CursorPage"] | Should -Be 0
        (Get-InteropSurfaceCount -Request $script:requests -DataStoreId 1 -TargetKind "Primary").Counts["CursorPage"] | Should -Be 1
    }

    It "leaves a request without its selection line unattributed, so the join cannot be faked by the data store filter" {
        $withoutSelections = $script:dmsLines | Where-Object { $_ -notlike '*"MessageTemplate":"Selected {TargetKind}*' }
        $joined = ConvertTo-InteropDmsRequest -Line $withoutSelections
        (Get-InteropSurfaceCount -Request $joined -DataStoreId 1).Counts["CursorPage"] | Should -Be 0
    }

    It "reports an unknown URL shape on the source instead of dropping it" {
        $extra = @(
            '{"Timestamp":"2026-11-03T21:30:50.0000000+00:00","Level":"Information","MessageTemplate":"Request starting {Protocol} {Method}","Properties":{"Method":"GET","Path":"/data/ed-fi/schools","QueryString":"?surprise=1","RequestId":"0HZ:00000001"}}'
            '{"Timestamp":"2026-11-03T21:30:50.0010000+00:00","Level":"Debug","MessageTemplate":"Selected {TargetKind} target for data store {DataStoreId} - TraceId: {TraceId}","Properties":{"TargetKind":"Primary","DataStoreId":1,"TraceId":"0HZ:00000001"}}'
        )
        $joined = ConvertTo-InteropDmsRequest -Line ($script:dmsLines + $extra)
        $result = Get-InteropSurfaceCount -Request $joined -DataStoreId 1
        $result.Counts["Unknown"] | Should -Be 1
        $result.Unclassified | Should -Be @("GET /data/ed-fi/schools?surprise=1")
    }
}

Describe "Get-InteropSourceSurfaces" {
    It "counts each surface from the captured Publisher Debug lines" {
        $result = Get-InteropSourceSurfaces -Line $script:publisherLines
        $result.Counts["AvailableChangeVersions"] | Should -Be 2
        $result.Counts["Partitions"] | Should -Be 2
        $result.Counts["CursorPage"] | Should -Be 1
        $result.Counts["TotalCount"] | Should -Be 1
        $result.Counts["GetMany"] | Should -Be 1
        $result.Counts["Deletes"] | Should -Be 3
        $result.Counts["KeyChanges"] | Should -Be 3
        $result.Counts["GetById"] | Should -Be 0
        $result.Unclassified | Should -BeNullOrEmpty
    }

    It "does not count lines that only talk about a request (header value, total count, Source returned, warnings, errors)" {
        $talk = $script:publisherLines | Where-Object { $_ -match "Total count header value|Retrieving total count of items|Source returned|using Cursor paging|Processing finished|Count request returned|GET by key" }
        $talk.Count | Should -BeGreaterThan 5
        $result = Get-InteropSourceSurfaces -Line $talk
        ($result.Counts.Values | Measure-Object -Sum).Sum | Should -Be 0
    }

    It "classifies the level tokens DBUG, INFO, WARN, EROR, and FATL alike" {
        foreach ($level in "DBUG", "INFO", "WARN", "EROR", "FATL") {
            $line = "[2026-11-03 19:48:06,470] [$level] [10] EdFi.Tools.ApiPublisher.Source.EdFiApiSourceTotalCountProvider - `"/ed-fi/schools`"): Getting item count from source (attempt #1)..."
            (Get-InteropSourceSurfaces -Line $line).Counts["TotalCount"] | Should -Be 1
        }
    }

    It "reads a resource name with stray or missing quotes" {
        $lines = @(
            '[2026-11-03 19:48:06,470] [DBUG] [10] Ctx - /ed-fi/classPeriods/deletes): Getting item count from source (attempt #1)...'
            '[2026-11-03 19:48:06,471] [DBUG] [10] Ctx - "/ed-fi/classPeriods/keyChanges"): Getting item count from source (attempt #1)...'
        )
        $result = Get-InteropSourceSurfaces -Line $lines
        $result.Counts["Deletes"] | Should -Be 1
        $result.Counts["KeyChanges"] | Should -Be 1
        $result.Counts["TotalCount"] | Should -Be 0
    }

    It "reports a GET-by-id line instead of dropping it" {
        $line = '[2026-11-03 19:48:06,470] [DBUG] [10] Ctx - "/ed-fi/students": Attempting to retrieve missing source item "abc".'
        $result = Get-InteropSourceSurfaces -Line $line
        $result.Counts["GetById"] | Should -Be 1
        $result.GetByIdLines | Should -Be @($line)
    }

    It "reports an unknown request shape instead of dropping it" {
        $line = '[2026-11-03 19:48:06,470] [DBUG] [10] Ctx - "/ed-fi/schools": Retrieving page items someday.'
        $result = Get-InteropSourceSurfaces -Line $line
        $result.Unclassified | Should -Be @($line)
        ($result.Counts.Values | Measure-Object -Sum).Sum | Should -Be 0
    }

    It "ignores continuation lines that have no log prefix" {
        $line = "  PublishingFailedException: Getting item count from source failed"
        (Get-InteropSourceSurfaces -Line $line).Counts["TotalCount"] | Should -Be 0
    }
}

Describe "Compare-InteropSurfaceCount" {
    It "returns nothing when the two sides agree" {
        $counts = [ordered]@{ Partitions = 3; CursorPage = 4 }
        @(Compare-InteropSurfaceCount -PublisherCount $counts -DmsCount $counts) | Should -BeNullOrEmpty
    }

    It "names a surface the two sides count differently" {
        $difference = @(Compare-InteropSurfaceCount -PublisherCount ([ordered]@{ Partitions = 3 }) -DmsCount ([ordered]@{ Partitions = 2 }))
        $difference.Count | Should -Be 1
        $difference[0].Surface | Should -Be "Partitions"
        $difference[0].Publisher | Should -Be 3
        $difference[0].Dms | Should -Be 2
    }
}

Describe "Get-InteropMarkerName" {
    It "puts the marker and the run token in the name, so a rerun cannot match stale data" {
        Get-InteropMarkerName -RunToken "20261103183005-ab12" -Marker "U1" | Should -Be "IP-U1-20261103183005-ab12"
        Get-InteropMarkerName -RunToken "20261103183005-cd34" -Marker "U1" | Should -Not -Be (Get-InteropMarkerName -RunToken "20261103183005-ab12" -Marker "U1")
    }

    It "adds the variant for the two names of a key change" {
        Get-InteropMarkerName -RunToken "t1" -Marker "K1" -Variant "B" | Should -Be "IP-K1-t1-B"
    }

    It "refuses a marker that is not one of the seven" {
        { Get-InteropMarkerName -RunToken "t1" -Marker "X9" } | Should -Throw "*not one of*"
        { Get-InteropMarkerName -RunToken "t1" -Marker "u1" } | Should -Throw "*not one of*"
    }

    It "refuses a token that would put anything but letters, digits, and hyphens in a name" {
        { Get-InteropMarkerName -RunToken "bad token;" -Marker "U1" } | Should -Throw "*letters, digits, and hyphens*"
    }

    It "refuses a name longer than classPeriodName allows" {
        { Get-InteropMarkerName -RunToken ("a" * 40) -Marker "K1" -Variant ("b" * 20) } | Should -Throw "*longer than*"
    }
}

Describe "New-InteropArrangementPlan" {
    BeforeAll {
        $script:plan = @(New-InteropArrangementPlan -RunToken "t1")
    }

    It "holds exactly the seven markers, three before the snapshot and four after" {
        ($script:plan | ForEach-Object Marker) -join "," | Should -Be "U1,D1,K1,I2,U2,D2,K2"
        @($script:plan | Where-Object Phase -eq "PreSnapshot").Count | Should -Be 3
        @($script:plan | Where-Object Phase -eq "PostSnapshot").Count | Should -Be 4
    }

    It "gives every marker its own natural key, so no two markers share a document" {
        $names = @($script:plan | ForEach-Object { $_.NameBefore; $_.NameAfter }) | Select-Object -Unique
        $names.Count | Should -Be 9
    }

    It "makes only key changes rename a document" {
        foreach ($item in $script:plan) {
            ($item.NameBefore -ne $item.NameAfter) | Should -Be ($item.Kind -eq "KeyChange")
        }
    }

    It "has every marker except the insert existing at the baseline, so the incremental publish has something to change" {
        @($script:plan | Where-Object ExistsBefore | ForEach-Object Marker) -join "," | Should -Be "U1,D1,K1,U2,D2,K2"
        ($script:plan | Where-Object Marker -eq "I2").ExistsBefore | Should -BeFalse
    }

    It "makes updates flip the non-key attendance flag and nothing else flip it" {
        foreach ($item in $script:plan | Where-Object Kind -eq "Update") {
            $item.AttendanceBefore | Should -BeFalse
            $item.AttendanceAfter | Should -BeTrue
        }
        foreach ($item in $script:plan | Where-Object Kind -eq "KeyChange") {
            $item.AttendanceAfter | Should -Be $item.AttendanceBefore
        }
    }

    It "deletes only non-descriptor documents: every marker is a classPeriod" {
        @($script:plan | Where-Object Resource -ne "classPeriods").Count | Should -Be 0
    }
}

Describe "Get-InteropMarkerExpectation" {
    BeforeAll {
        $script:plan = @(New-InteropArrangementPlan -RunToken "t1")
        $script:marker = { param($name) $script:plan | Where-Object Marker -eq $name }
    }

    It "shows a pre-snapshot update on both sides and a post-snapshot update only on the primary" {
        (Get-InteropMarkerExpectation -Marker (& $script:marker "U1") -Side Snapshot).OfficialAttendancePeriod | Should -BeTrue
        (Get-InteropMarkerExpectation -Marker (& $script:marker "U1") -Side Primary).OfficialAttendancePeriod | Should -BeTrue
        (Get-InteropMarkerExpectation -Marker (& $script:marker "U2") -Side Snapshot).OfficialAttendancePeriod | Should -BeFalse
        (Get-InteropMarkerExpectation -Marker (& $script:marker "U2") -Side Primary).OfficialAttendancePeriod | Should -BeTrue
    }

    It "expects the snapshot to list D1 as deleted and not D2, and the primary to list both" {
        (Get-InteropMarkerExpectation -Marker (& $script:marker "D1") -Side Snapshot).DeleteListed | Should -BeTrue
        (Get-InteropMarkerExpectation -Marker (& $script:marker "D2") -Side Snapshot).DeleteListed | Should -BeFalse
        (Get-InteropMarkerExpectation -Marker (& $script:marker "D1") -Side Primary).DeleteListed | Should -BeTrue
        (Get-InteropMarkerExpectation -Marker (& $script:marker "D2") -Side Primary).DeleteListed | Should -BeTrue
    }

    It "expects the document of a deleted marker to be absent where the delete shows and present where it does not" {
        $d2Snapshot = Get-InteropMarkerExpectation -Marker (& $script:marker "D2") -Side Snapshot
        $d2Primary = Get-InteropMarkerExpectation -Marker (& $script:marker "D2") -Side Primary
        $d2Snapshot.PresentName | Should -Be "IP-D2-t1"
        $d2Primary.PresentName | Should -BeNullOrEmpty
        $d2Primary.AbsentName | Should -Contain "IP-D2-t1"
    }

    It "expects a key change under the new name where it shows and the old name where it does not" {
        $k1 = Get-InteropMarkerExpectation -Marker (& $script:marker "K1") -Side Snapshot
        $k1.PresentName | Should -Be "IP-K1-t1-B"
        $k1.AbsentName | Should -Be @("IP-K1-t1-A")
        $k1.KeyChangeListed | Should -BeTrue
        $k2 = Get-InteropMarkerExpectation -Marker (& $script:marker "K2") -Side Snapshot
        $k2.PresentName | Should -Be "IP-K2-t1-A"
        $k2.AbsentName | Should -Be @("IP-K2-t1-B")
        $k2.KeyChangeListed | Should -BeFalse
        (Get-InteropMarkerExpectation -Marker (& $script:marker "K2") -Side Primary).PresentName | Should -Be "IP-K2-t1-B"
    }

    It "expects the insert on the primary only" {
        (Get-InteropMarkerExpectation -Marker (& $script:marker "I2") -Side Primary).PresentName | Should -Be "IP-I2-t1"
        (Get-InteropMarkerExpectation -Marker (& $script:marker "I2") -Side Snapshot).PresentName | Should -BeNullOrEmpty
    }

    It "does not assert the lists for a kind that does not use them" {
        $u1 = Get-InteropMarkerExpectation -Marker (& $script:marker "U1") -Side Primary
        $u1.DeleteListed | Should -BeNullOrEmpty
        $u1.KeyChangeListed | Should -BeNullOrEmpty
    }
}

Describe "Get-InteropMarkerMismatch" {
    BeforeAll {
        $script:plan = @(New-InteropArrangementPlan -RunToken "t1")
        $script:observe = {
            param($present, $attendance, $deleted, $keyChanged)
            [pscustomobject]@{ Present = $present; OfficialAttendancePeriod = $attendance; DeleteListed = $deleted; KeyChangeListed = $keyChanged }
        }
    }

    It "reports nothing when the observation matches" {
        $u2 = $script:plan | Where-Object Marker -eq "U2"
        $expectation = Get-InteropMarkerExpectation -Marker $u2 -Side Snapshot
        Get-InteropMarkerMismatch -Expectation $expectation -Observation (& $script:observe @{ "IP-U2-t1" = $true } $false $null $null) | Should -BeNullOrEmpty
    }

    It "reports a post-snapshot update that leaks into the snapshot" {
        $u2 = $script:plan | Where-Object Marker -eq "U2"
        $expectation = Get-InteropMarkerExpectation -Marker $u2 -Side Snapshot
        $result = @(Get-InteropMarkerMismatch -Expectation $expectation -Observation (& $script:observe @{ "IP-U2-t1" = $true } $true $null $null))
        $result.Count | Should -Be 1
        $result[0] | Should -BeLike "Marker U2 on the Snapshot should have officialAttendancePeriod False*read 'True'*"
    }

    It "reports a document that is missing" {
        $i2 = $script:plan | Where-Object Marker -eq "I2"
        $expectation = Get-InteropMarkerExpectation -Marker $i2 -Side Primary
        $result = Get-InteropMarkerMismatch -Expectation $expectation -Observation (& $script:observe @{ "IP-I2-t1" = $false } $null $null $null)
        $result | Should -Be @("Marker I2 on the Primary should have 'IP-I2-t1' but it was not found.")
    }

    It "reports a document that should be absent but is found" {
        $i2 = $script:plan | Where-Object Marker -eq "I2"
        $expectation = Get-InteropMarkerExpectation -Marker $i2 -Side Snapshot
        $result = Get-InteropMarkerMismatch -Expectation $expectation -Observation (& $script:observe @{ "IP-I2-t1" = $true } $false $null $null)
        $result | Should -Be @("Marker I2 on the Snapshot should not have 'IP-I2-t1' but it was found.")
    }

    It "reports a delete that is not listed and a key change that is listed too early" {
        $d1 = $script:plan | Where-Object Marker -eq "D1"
        $k2 = $script:plan | Where-Object Marker -eq "K2"
        $deleteResult = Get-InteropMarkerMismatch -Expectation (Get-InteropMarkerExpectation -Marker $d1 -Side Snapshot) -Observation (& $script:observe @{ "IP-D1-t1" = $false } $null $false $null)
        $deleteResult | Should -Be @("Marker D1 on the Snapshot should list the document in /deletes but it did not.")
        $keyResult = Get-InteropMarkerMismatch -Expectation (Get-InteropMarkerExpectation -Marker $k2 -Side Snapshot) -Observation (& $script:observe @{ "IP-K2-t1-A" = $true; "IP-K2-t1-B" = $false } $false $null $true)
        $keyResult | Should -Be @("Marker K2 on the Snapshot should not list the document in /keyChanges but it did.")
    }

    It "tells a snapshot that serves the primary from a real one: the primary's view fails every post-snapshot snapshot expectation" {
        $failing = 0
        foreach ($item in $script:plan | Where-Object Phase -eq "PostSnapshot") {
            $primary = Get-InteropMarkerExpectation -Marker $item -Side Primary
            $present = @{}
            foreach ($name in @($item.NameBefore, $item.NameAfter) | Select-Object -Unique) { $present[$name] = ($name -eq $primary.PresentName) }
            $observation = & $script:observe $present $primary.OfficialAttendancePeriod $primary.DeleteListed $primary.KeyChangeListed
            if (@(Get-InteropMarkerMismatch -Expectation (Get-InteropMarkerExpectation -Marker $item -Side Snapshot) -Observation $observation).Count -gt 0) { $failing++ }
        }
        $failing | Should -Be 4
    }
}

Describe "Get-InteropClassPeriodPath and New-InteropClassPeriodBody" {
    It "escapes the natural key in the query" {
        Get-InteropClassPeriodPath -Name "IP K1&x" -SchoolId 930190101 | Should -Be "/data/ed-fi/classPeriods?classPeriodName=IP%20K1%26x&schoolId=930190101&limit=10"
    }

    It "builds an update body with the id and the flag, and an insert body without the id" {
        $update = New-InteropClassPeriodBody -Name "n" -SchoolId 930190101 -OfficialAttendancePeriod $true -Id "abc"
        $update["id"] | Should -Be "abc"
        $update["officialAttendancePeriod"] | Should -BeTrue
        $update["schoolReference"]["schoolId"] | Should -Be 930190101
        (New-InteropClassPeriodBody -Name "n" -SchoolId 930190101).Contains("id") | Should -BeFalse
    }

    It "omits the flag when it is null, so a document without one is not given one" {
        (New-InteropClassPeriodBody -Name "n" -SchoolId 930190101).Contains("officialAttendancePeriod") | Should -BeFalse
    }
}

Describe "ConvertTo-InteropArrangementManifest" {
    BeforeAll {
        $script:plan = @(New-InteropArrangementPlan -RunToken "t1")
        foreach ($item in $script:plan) { $item.DocumentId = "id-$($item.Marker)" }
        $script:shaped = ConvertTo-InteropArrangementManifest -Marker $script:plan -StartingChangeVersion 3374 `
            -SnapshotNewestChangeVersion 3380 -PrimaryNewestChangeVersion 3390 -BaselineExitCode 0 -RunToken "t1" -SnapshotName "snap"
    }

    It "records T0 and both newest change versions" {
        $script:shaped.startingChangeVersion | Should -Be 3374
        $script:shaped.snapshotNewestChangeVersion | Should -Be 3380
        $script:shaped.primaryNewestChangeVersion | Should -Be 3390
    }

    It "lets a later stage tell a snapshot-time value from a primary value for the same marker" {
        $u2 = $script:shaped.markers | Where-Object marker -eq "U2"
        $u2.expectedSnapshot.officialAttendancePeriod | Should -BeFalse
        $u2.expectedPrimary.officialAttendancePeriod | Should -BeTrue
        $u2.documentId | Should -Be "id-U2"
    }

    It "passes the manifest secret scan, so it can be written into a run manifest" {
        { Assert-InteropManifestHasNoSecret -Manifest $script:shaped } | Should -Not -Throw
    }

    It "is carried into the run manifest when passed to ConvertTo-InteropManifest" {
        $versions = [pscustomobject]@{ DmsImageName = "a"; DmsImageId = "b"; DmsDiscoveryVersion = "c"; PublisherImage = "d"; PublisherImageDigest = "e"; DataStandard = "f"; IdentityProvider = "g"; Engine = "postgresql" }
        $manifest = ConvertTo-InteropManifest -RunId "r" -Engine "postgresql" -Scenario @("StackOnly") -StartedUtc $script:stamp -Versions $versions `
            -NonDefaultSetting @() -DataStore @() -Client @() -Arrangement $script:shaped
        $manifest["arrangement"].startingChangeVersion | Should -Be 3374
        (ConvertTo-InteropManifest -RunId "r" -Engine "postgresql" -Scenario @("StackOnly") -StartedUtc $script:stamp -Versions $versions `
                -NonDefaultSetting @() -DataStore @() -Client @()).Contains("arrangement") | Should -BeFalse
    }
}

Describe "Resolve-InteropScenario" {
    It "accepts NoSnapshot on its own" {
        Resolve-InteropScenario -Scenario NoSnapshot | Should -Be @("NoSnapshot")
    }

    It "resolves Isolated, and All as Isolated then NoSnapshot on one stack" {
        Resolve-InteropScenario -Scenario Isolated | Should -Be @("Isolated")
        Resolve-InteropScenario -Scenario All | Should -Be @("Isolated", "NoSnapshot")
    }
}

Describe "scenario results" {
    BeforeEach {
        $script:result = New-InteropResult -Scenario "NoSnapshot" -Engine "postgresql" -RunId "run-1"
    }

    It "records PASS only when expected and observed render the same" {
        (Add-InteropAssertion -Result $script:result -Name "a" -Expected 2 -Observed 2 -Evidence "e")["outcome"] | Should -Be "PASS"
        (Add-InteropAssertion -Result $script:result -Name "b" -Expected 2 -Observed "2" -Evidence "e")["outcome"] | Should -Be "PASS"
        (Add-InteropAssertion -Result $script:result -Name "c" -Expected 2 -Observed 1 -Evidence "e")["outcome"] | Should -Be "FAIL"
        (Add-InteropAssertion -Result $script:result -Name "d" -Expected "Abc" -Observed "abc" -Evidence "e")["outcome"] | Should -Be "FAIL"
    }

    It "compares collections as a whole, so an extra element fails" {
        (Add-InteropAssertion -Result $script:result -Name "a" -Expected @(404) -Observed @(404) -Evidence "e")["outcome"] | Should -Be "PASS"
        (Add-InteropAssertion -Result $script:result -Name "b" -Expected @(404) -Observed @(200, 404) -Evidence "e")["outcome"] | Should -Be "FAIL"
        (Add-InteropAssertion -Result $script:result -Name "c" -Expected @(404) -Observed @() -Evidence "e")["outcome"] | Should -Be "FAIL"
    }

    It "treats null as a value of its own" {
        (Add-InteropAssertion -Result $script:result -Name "a" -Expected "x" -Observed $null -Evidence "e")["outcome"] | Should -Be "FAIL"
        (Add-InteropAssertion -Result $script:result -Name "b" -Expected $null -Observed $null -Evidence "e")["outcome"] | Should -Be "PASS"
    }

    It "lists only the failed assertions" {
        $null = Add-InteropAssertion -Result $script:result -Name "ok" -Expected 1 -Observed 1 -Evidence "e"
        $null = Add-InteropAssertion -Result $script:result -Name "bad" -Expected 1 -Observed 2 -Evidence "e"
        $failed = @(Get-InteropResultFailure -Result $script:result)
        $failed.Count | Should -Be 1
        $failed[0]["name"] | Should -Be "bad"
    }

    Context "results.json" {
        BeforeEach {
            $script:folder = Join-Path ([System.IO.Path]::GetTempPath()) "interop-results-$([guid]::NewGuid().ToString('N'))"
            $null = New-Item -ItemType Directory -Path $script:folder
        }

        AfterEach {
            Remove-Item -LiteralPath $script:folder -Recurse -Force -ErrorAction SilentlyContinue
        }

        It "holds each assertion, the log lines, and an overall PASS when nothing failed" {
            $null = Add-InteropAssertion -Result $script:result -Name "ok" -Expected 2 -Observed 2 -Evidence "publisher.log"
            $script:result["logLines"]["warn"] = "[2026-11-03 19:44:39,455] [WARN] [07] X - text"
            $path = Write-InteropResultFile -RunFolder $script:folder -Result $script:result
            $document = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
            (Split-Path $path -Leaf) | Should -Be "results.json"
            $document.overall | Should -Be "PASS"
            $document.assertions[0].name | Should -Be "ok"
            $document.assertions[0].expected | Should -Be 2
            $document.assertions[0].observed | Should -Be 2
            $document.assertions[0].outcome | Should -Be "PASS"
            $document.assertions[0].evidence | Should -Be "publisher.log"
            $document.logLines.warn | Should -Be "[2026-11-03 19:44:39,455] [WARN] [07] X - text"
        }

        It "reports FAIL overall when an assertion failed" {
            $null = Add-InteropAssertion -Result $script:result -Name "bad" -Expected 2 -Observed 1 -Evidence "e"
            $document = Get-Content -LiteralPath (Write-InteropResultFile -RunFolder $script:folder -Result $script:result) -Raw | ConvertFrom-Json
            $document.overall | Should -Be "FAIL"
            $document.failed | Should -Be 1
        }

        It "reports FAIL overall when nothing was asserted" {
            $document = Get-Content -LiteralPath (Write-InteropResultFile -RunFolder $script:folder -Result $script:result) -Raw | ConvertFrom-Json
            $document.overall | Should -Be "FAIL"
        }

        It "refuses to keep a credential value" {
            $null = Add-InteropAssertion -Result $script:result -Name "leak" -Expected "a" -Observed "client-secret-value-123" -Evidence "e"
            { Write-InteropResultFile -RunFolder $script:folder -Result $script:result -Secret @("client-secret-value-123") } | Should -Throw "*credential*"
        }

        It "refuses a secret-shaped value" {
            $script:result["logLines"]["warn"] = "password=hunter2"
            { Write-InteropResultFile -RunFolder $script:folder -Result $script:result } | Should -Throw "*secret*"
        }
    }
}

Describe "ConvertTo-InteropProblemSummary" {
    It "keeps status, media type without parameters, type, title, and detail, and drops the correlation id" {
        $body = '{"detail":"Snapshot not found.","type":"urn:ed-fi:api:not-found","title":"Not Found","status":404,"correlationId":"0H1:00000001","validationErrors":{},"errors":[]}'
        $summary = ConvertTo-InteropProblemSummary -StatusCode 404 -ContentType "application/problem+json; charset=utf-8" -Content $body
        $summary["status"] | Should -Be 404
        $summary["contentType"] | Should -Be "application/problem+json"
        $summary["type"] | Should -Be "urn:ed-fi:api:not-found"
        $summary["title"] | Should -Be "Not Found"
        $summary["detail"] | Should -Be "Snapshot not found."
        $summary.Contains("correlationId") | Should -BeFalse
    }

    It "makes two responses that differ only in the correlation id equal, and two that differ in detail unequal" {
        $one = ConvertTo-InteropProblemSummary -StatusCode 404 -ContentType "application/problem+json" -Content '{"detail":"Snapshot not found.","type":"t","title":"Not Found","correlationId":"A"}'
        $two = ConvertTo-InteropProblemSummary -StatusCode 404 -ContentType "application/problem+json" -Content '{"detail":"Snapshot not found.","type":"t","title":"Not Found","correlationId":"B"}'
        $other = ConvertTo-InteropProblemSummary -StatusCode 404 -ContentType "application/problem+json" -Content '{"detail":"Other.","type":"t","title":"Not Found","correlationId":"A"}'
        (ConvertTo-InteropComparable -Value $one) | Should -BeExactly (ConvertTo-InteropComparable -Value $two)
        (ConvertTo-InteropComparable -Value $one) | Should -Not -BeExactly (ConvertTo-InteropComparable -Value $other)
    }

    It "reports a body that is not JSON as nulls instead of throwing" {
        $summary = ConvertTo-InteropProblemSummary -StatusCode 500 -ContentType "text/plain" -Content "boom"
        $summary["type"] | Should -BeNullOrEmpty
        $summary["detail"] | Should -BeNullOrEmpty
    }
}

Describe "Get-InteropNoSnapshotExpectation" {
    It "pins the exit code and the zero-write rule" {
        $expectation = Get-InteropNoSnapshotExpectation
        $expectation["PublisherExitCode"] | Should -Be 2
        $expectation["TargetWriteCount"] | Should -Be 0
        $expectation["AvailableChangeVersionsStatuses"] | Should -Be @(404)
    }

    It "replaces a value on request and leaves the pinned table itself unchanged" {
        (Get-InteropNoSnapshotExpectation -Override @{ PublisherExitCode = 1 })["PublisherExitCode"] | Should -Be 1
        (Get-InteropNoSnapshotExpectation)["PublisherExitCode"] | Should -Be 2
    }

    It "refuses an override name that is not an expectation" {
        { Get-InteropNoSnapshotExpectation -Override @{ PublisherExitCodee = 1 } } | Should -Throw "*Unknown NoSnapshot expectation*"
    }
}

Describe "Get-InteropOperatorLine" {
    BeforeAll {
        $script:operatorLines = [string[]]@(
            '[2026-11-03 19:44:39,455] [WARN] [07] A.B - Unable to get current change version from source API at ''http://x/"changeQueries/v1/availableChangeVersions"'' (response status: NotFound).'
            '[2026-11-03 19:44:39,766] [INFO] [18] A.C - Source API does not expose /partitions (status 404); offset/limit paging will be used.'
            '[2026-11-03 19:44:39,875] [EROR] [07] A.D - "/ed-fi/accommodationDescriptors": Count request returned NotFound.'
            '[2026-11-03 19:44:39,896] [EROR] [19] A.D - /ed-fi/accountTypeDescriptors: NotFound - Unable to obtain total count due to request failure. Response content: {"detail":"Snapshot not found."}'
            '[2026-11-03 19:44:40,434] [FATL] [05] A.E - An unhandled exception: Processing completed, but 2 source read error(s) mean an unknown number of documents was never attempted.'
            'Publishing run summary'
            '  ! 2 source read error(s) occurred.'
            '[2026-11-03 19:44:40,447] [INFO] [18] A.F - Processing finished in 1 seconds.'
        )
    }

    It "returns the verbatim line for a level and text" {
        (Get-InteropOperatorLine -Line $script:operatorLines -Level INFO -Text "does not expose /partitions").Line | Should -Be $script:operatorLines[1]
    }

    It "requires both the level and the text" {
        Get-InteropOperatorLine -Line $script:operatorLines -Level WARN -Text "does not expose /partitions" | Should -BeNullOrEmpty
        Get-InteropOperatorLine -Line $script:operatorLines -Level FATL -Text "no such text" | Should -BeNullOrEmpty
    }

    It "finds text that the Publisher wrote on the continuation line of an error entry" {
        $lines = [string[]]@(
            '[2026-11-03 19:44:39,896] [EROR] [19] A.D - /ed-fi/x: NotFound - Unable to obtain total count due to request failure. Downstream failures are possible.'
            'Response content: {"detail":"Snapshot not found."}  '
        )
        $entry = Get-InteropOperatorLine -Line $lines -Level EROR -Text "Unable to obtain total count"
        $entry.Line | Should -Not -BeLike "*Snapshot not found*"
        $entry.Continuation[0] | Should -BeLike "*Snapshot not found*"
    }

    It "picks the first matching error line, skipping an error without the text" {
        (Get-InteropOperatorLine -Line $script:operatorLines -Level EROR -Text "Unable to obtain total count").Line | Should -Be $script:operatorLines[3]
    }

    It "returns the continuation lines of an entry and stops at the next entry" {
        $fatal = Get-InteropOperatorLine -Line $script:operatorLines -Level FATL -Text "source read error(s) mean"
        $fatal.Continuation | Should -Be @("Publishing run summary", "  ! 2 source read error(s) occurred.")
    }
}

Describe "Get-InteropNoSnapshotObservation and Get-InteropNoSnapshotAssertion" {
    BeforeAll {
        $script:expectation = Get-InteropNoSnapshotExpectation
        $script:problem = [ordered]@{ status = 404; contentType = "application/problem+json"; type = "urn:ed-fi:api:not-found"; title = "Not Found"; detail = "Snapshot not found." }

        $script:newRequest = {
            param($Method, $Path, $Query, $Status)
            [pscustomobject]@{
                RequestId = [guid]::NewGuid().ToString("N"); TimestampUtc = $null; Method = $Method; Path = $Path; Query = $Query; StatusCode = $Status
                Surface = (Get-InteropRequestSurface -Method $Method -Path $Path -Query $Query); DataStoreId = $null; TargetKind = $null
            }
        }

        # A run shaped like the live baseline, with two resources instead of 384.
        $script:goodRequests = @(
            (& $script:newRequest "GET" "/oauth/token" "" 200)
            (& $script:newRequest "GET" "/changeQueries/v1/availableChangeVersions" "" 404)
            (& $script:newRequest "GET" "/data/ed-fi/schools/partitions" "?number=1" 404)
            (& $script:newRequest "GET" "/data/ed-fi/schools" "?limit=0&totalCount=true" 404)
            (& $script:newRequest "GET" "/data/ed-fi/students" "?limit=0&totalCount=true" 404)
        )
        $script:goodPublisherLines = [string[]]@(
            '[2026-11-03 19:44:39,455] [WARN] [07] A.B - Unable to get current change version from source API at ''x'' (response status: NotFound).'
            '[2026-11-03 19:44:39,766] [INFO] [18] A.C - Source API does not expose /partitions (status 404); offset/limit paging will be used.'
            '[2026-11-03 19:44:39,800] [DBUG] [07] A.G - "/ed-fi/schools"): Getting item count from source (attempt #1)...  '
            '[2026-11-03 19:44:39,801] [DBUG] [07] A.G - "/ed-fi/students"): Getting item count from source (attempt #1)...  '
            '[2026-11-03 19:44:39,896] [EROR] [19] A.D - /ed-fi/students: NotFound - Unable to obtain total count due to request failure. This resource will not be processed. Downstream failures are possible.'
            'Response content: {"detail":"Snapshot not found."}  '
            '[2026-11-03 19:44:40,434] [FATL] [05] A.E - Processing completed, but 2 source read error(s) mean an unknown number of documents was never attempted (of 2 error(s) in total).'
        )
        $script:goodDmsLines = [string[]]@(
            '{"Timestamp":"2026-11-03T19:44:39.5Z","MessageTemplate":"A snapshot was requested but data store {DataStoreId} ({Name}) has none configured - TraceId: {TraceId}"}'
            '{"Timestamp":"2026-11-03T19:44:39.6Z","MessageTemplate":"A snapshot was requested but data store {DataStoreId} ({Name}) has none configured - TraceId: {TraceId}"}'
            '{"Timestamp":"2026-11-03T19:44:39.7Z","MessageTemplate":"A snapshot was requested but data store {DataStoreId} ({Name}) has none configured - TraceId: {TraceId}"}'
            '{"Timestamp":"2026-11-03T19:44:39.8Z","MessageTemplate":"A snapshot was requested but data store {DataStoreId} ({Name}) has none configured - TraceId: {TraceId}"}'
        )

        $script:observe = {
            param([hashtable]$Change)
            $arguments = @{
                ExitCode = 2; PublisherLine = $script:goodPublisherLines; DmsRequest = $script:goodRequests; DmsLine = $script:goodDmsLines
                TargetDocumentsBefore = 47; TargetDocumentsAfter = 47; SnapshotActiveStatus = 200
                Retired = $script:problem; NeverConfigured = $script:problem; Expectation = $script:expectation
            }
            foreach ($key in $Change.Keys) { $arguments[$key] = $Change[$key] }
            Get-InteropNoSnapshotObservation @arguments
        }

        $script:evaluate = {
            param($Observation)
            @(Get-InteropNoSnapshotAssertion -Expectation $script:expectation -Observation $Observation)
        }
    }

    It "passes every assertion for a run shaped like the live baseline" {
        $assertions = & $script:evaluate (& $script:observe @{})
        $assertions.Count | Should -BeGreaterThan 15
        $result = New-InteropResult -Scenario "NoSnapshot" -Engine "postgresql" -RunId "r"
        foreach ($item in $assertions) {
            $null = Add-InteropAssertion -Result $result -Name $item.Name -Expected $item.Expected -Observed $item.Observed -Evidence $item.Evidence
        }
        @(Get-InteropResultFailure -Result $result | ForEach-Object { $_["name"] }) | Should -BeNullOrEmpty
    }

    It "observes the structure: 404 statuses, scheduled resources, count reads, and rejected requests" {
        $o = & $script:observe @{}
        $o.AvailableChangeVersionsStatuses | Should -Be @(404)
        $o.PartitionsProbeCount | Should -Be 1
        $o.CountReadCount | Should -Be 2
        $o.PublisherCountReadCount | Should -Be 2
        $o.RejectedSourceRequestCount | Should -Be 4
        $o.NoneConfiguredLineCount | Should -Be 4
        $o.TargetWriteCount | Should -Be 0
        $o.Fatal.Line | Should -BeLike "*source read error(s) mean an unknown number of documents*"
    }

    It "ignores none-configured log lines outside the run window" {
        $o = & $script:observe @{ FromUtc = [datetime]::Parse("2026-11-03T19:44:39.65Z").ToUniversalTime(); ToUtc = [datetime]::Parse("2026-11-03T19:44:40Z").ToUniversalTime() }
        $o.NoneConfiguredLineCount | Should -Be 2
    }

    # Each case changes one thing and names the one assertion that must catch it.
    It "fails <Assertion> when <Case>" -ForEach @(
        @{ Case = "the exit code is 0"; Assertion = "Publisher exit code"; Change = { @{ ExitCode = 0 } } }
        @{ Case = "the target gained documents"; Assertion = "Target documents after the run equal the target documents before it"; Change = { @{ TargetDocumentsAfter = 50 } } }
        @{ Case = "the snapshot was never active"; Assertion = "A real snapshot answers Use-Snapshot reads with 200 before it is retired"; Change = { @{ SnapshotActiveStatus = 404 } } }
        @{ Case = "the retire body differs"; Assertion = "The Snapshot Not Found response after the snapshot is retired equals the never-configured response"; Change = { @{ Retired = [ordered]@{ status = 404; contentType = "application/problem+json"; type = "urn:ed-fi:api:not-found"; title = "Not Found"; detail = "Something else." } } } }
        @{ Case = "the never-configured body drifts from the pinned shape"; Assertion = "The Snapshot Not Found response of a data store that never had a snapshot matches the pinned shape"; Change = { @{ NeverConfigured = [ordered]@{ status = 404; contentType = "application/json"; type = "urn:ed-fi:api:not-found"; title = "Not Found"; detail = "Snapshot not found." } } } }
        @{ Case = "the Publisher log has no WARN line"; Assertion = "The WARN line about availableChangeVersions is present"; Change = { @{ PublisherLine = [string[]]@($script:goodPublisherLines | Where-Object { $_ -notlike "*[WARN]*" }) } } }
        @{ Case = "the EROR line carries no Snapshot Not Found body"; Assertion = "The EROR count-failure line carries the Snapshot Not Found body"; Change = { @{ PublisherLine = [string[]]@($script:goodPublisherLines | Where-Object { $_ -notlike "Response content*" }) } } }
        @{ Case = "the Publisher log has no FATL line"; Assertion = "The FATL line about source read errors is present"; Change = { @{ PublisherLine = [string[]]@($script:goodPublisherLines | Where-Object { $_ -notlike "*[FATL]*" }) } } }
        @{ Case = "the Publisher log has no INFO line"; Assertion = "The INFO line about /partitions is present"; Change = { @{ PublisherLine = [string[]]@($script:goodPublisherLines | Where-Object { $_ -notlike "*[INFO]*" }) } } }
        @{ Case = "the Publisher logged a count read DMS never saw"; Assertion = "DMS count reads equal the count reads the Publisher logged"; Change = { @{ PublisherLine = [string[]]@($script:goodPublisherLines + '[2026-11-03 19:44:39,802] [DBUG] [07] A.G - "/ed-fi/sections"): Getting item count from source (attempt #1)...  ') } } }
        @{ Case = "a log line is missing for a rejected request"; Assertion = "DMS none-configured log lines equal the rejected source requests"; Change = { @{ DmsLine = [string[]]@($script:goodDmsLines[0..2]) } } }
        @{ Case = "a write reached DMS"; Assertion = "POST, PUT, and DELETE requests that reached DMS"; Change = { @{ DmsRequest = @($script:goodRequests + (& $script:newRequest "POST" "/data/ed-fi/schools" "" 201)) } } }
        @{ Case = "a source read answered 200"; Assertion = "count read status codes"; Change = { @{ DmsRequest = @($script:goodRequests + (& $script:newRequest "GET" "/data/ed-fi/sections" "?limit=0&totalCount=true" 200)) } } }
        @{ Case = "a page was read"; Assertion = "Other source reads (cursor pages, offset pages, deletes, keyChanges, get-by-id)"; Change = { @{ DmsRequest = @($script:goodRequests + (& $script:newRequest "GET" "/data/ed-fi/schools" "?offset=0&limit=500" 404)) } } }
        @{ Case = "availableChangeVersions was never requested"; Assertion = "availableChangeVersions status codes"; Change = { @{ DmsRequest = @($script:goodRequests | Where-Object { $_.Surface -ne "AvailableChangeVersions" }) } } }
        @{ Case = "the partitions probe answered 200"; Assertion = "partitions probe status codes"; Change = { @{ DmsRequest = @($script:goodRequests | ForEach-Object { if ($_.Surface -eq "Partitions") { $_.StatusCode = 200 }; $_ }) } } }
    ) {
        $observation = & $script:observe (& $Change)
        $failed = @(& $script:evaluate $observation | Where-Object { $_.Name -eq $Assertion } | Where-Object { (ConvertTo-InteropComparable -Value $_.Expected) -cne (ConvertTo-InteropComparable -Value $_.Observed) })
        $failed.Count | Should -Be 1
    }

    It "does not let zero Publisher count reads pass as zero DMS count reads" {
        $o = & $script:observe @{ PublisherLine = [string[]]@(); DmsRequest = @($script:goodRequests | Where-Object { $_.Surface -ne "TotalCount" }) }
        $guard = & $script:evaluate $o | Where-Object { $_.Name -eq "The Publisher made at least one count read" }
        $guard.Observed | Should -BeFalse
    }

    It "honours an override, so a wrong expectation fails its assertion" {
        $wrong = Get-InteropNoSnapshotExpectation -Override @{ PublisherExitCode = 1 }
        $assertion = @(Get-InteropNoSnapshotAssertion -Expectation $wrong -Observation (& $script:observe @{})) | Where-Object { $_.Name -eq "Publisher exit code" }
        $assertion.Expected | Should -Be 1
        $assertion.Observed | Should -Be 2
    }
}

Describe "Get-InteropIsolatedExpectation" {
    It "pins Run B to the pre-snapshot markers and Run C to every marker" {
        $e = Get-InteropIsolatedExpectation
        @($e["RunBReflected"].Keys | Where-Object { $e["RunBReflected"][$_] }) | Should -Be @("U1", "D1", "K1")
        @($e["RunCReflected"].Keys | Where-Object { -not $e["RunCReflected"][$_] }) | Should -BeNullOrEmpty
        $e["RunExitCodes"] | Should -Be @(0, 0)
    }

    It "replaces a value without changing the pinned table" {
        $changed = Get-InteropIsolatedExpectation -Override @{ RunBPrimarySelections = 4 }
        $changed["RunBPrimarySelections"] | Should -Be 4
        (Get-InteropIsolatedExpectation)["RunBPrimarySelections"] | Should -Be 0
        $null = (Get-InteropIsolatedExpectation)["RunBReflected"]["I2"]
        $mutated = Get-InteropIsolatedExpectation
        $mutated["RunBReflected"]["I2"] = $true
        (Get-InteropIsolatedExpectation)["RunBReflected"]["I2"] | Should -BeFalse
    }

    It "rejects an unknown name, so a typo cannot weaken the check" {
        { Get-InteropIsolatedExpectation -Override @{ RunBExitCode = 1 } } | Should -Throw "*Unknown Isolated expectation 'RunBExitCode'*"
    }

    It "lists every name an override may use" {
        Get-InteropIsolatedExpectationName | Should -Contain "RunBReflected"
        Get-InteropIsolatedExpectationName | Should -Not -Contain "PublisherExitCode"
    }
}

Describe "Get-InteropCountUnion" {
    It "sums counts key by key, including keys only one map has" {
        $union = Get-InteropCountUnion -Count @([ordered]@{ Partitions = 2; Deletes = 1 }, [ordered]@{ GetMany = 3; Deletes = 4 })
        $union["Partitions"] | Should -Be 2
        $union["GetMany"] | Should -Be 3
        $union["Deletes"] | Should -Be 5
    }

    It "keeps a surface at zero when no run issued it" {
        $union = Get-InteropCountUnion -Count @([ordered]@{ GetMany = 0 }, [ordered]@{ GetMany = 0 })
        $union["GetMany"] | Should -Be 0
    }

    It "returns an empty map for no input" {
        (Get-InteropCountUnion -Count @()).Count | Should -Be 0
    }
}

Describe "Get-InteropPublisherChangeVersion" {
    BeforeAll {
        $script:versionLines = [string[]]@(
            '[2026-11-03 19:48:06,220] [DBUG] [07] E.EdFiApiSourceCurrentChangeVersionProvider - Available change versions request from http://ed-fi-api:8080/changeQueries/v1/availableChangeVersions returned OK: {"oldestChangeVersion":0,"newestChangeVersion":3379}'
            '[2026-11-03 19:48:06,451] [INFO] [07] E.Producer - "/ed-fi/classPeriods": Retrieving total count of items in change versions 3375 to 3379.  '
            '[2026-11-03 19:48:06,452] [INFO] [08] E.Producer - "/ed-fi/classPeriods/deletes": Retrieving total count of items in change versions 3375 to 3379.  '
            '[2026-11-03 19:48:06,500] [INFO] [08] E.Producer - "/ed-fi/schools": Retrieving total count of items.'
        )
    }

    It "reads the newest change version and the window the Publisher logged" {
        $v = Get-InteropPublisherChangeVersion -Line $script:versionLines
        $v.Newest | Should -Be @(3379)
        $v.WindowStart | Should -Be 3375
        $v.WindowEnd | Should -Be 3379
        $v.WindowLines | Should -Be 2
    }

    It "reads the fixture log from a real run" {
        $v = Get-InteropPublisherChangeVersion -Line $script:publisherLines
        $v.Newest | Should -Be @(3374)
        $v.WindowEnd | Should -Be 3374
    }

    It "reports nothing when the log has no change-version lines, so a missing line cannot match" {
        $v = Get-InteropPublisherChangeVersion -Line @("[2026-11-03 19:48:06,500] [INFO] [08] E.Producer - nothing here")
        @($v.Newest).Count | Should -Be 0
        $v.WindowStart | Should -BeNullOrEmpty
        $v.WindowEnd | Should -BeNullOrEmpty
    }

    It "does not read the unavailable-change-versions warning as a version" {
        $v = Get-InteropPublisherChangeVersion -Line @('[2026-11-03 19:44:39,455] [WARN] [07] E.P - Unable to get current change version from source API at ''x'' (response status: NotFound)')
        @($v.Newest).Count | Should -Be 0
    }
}

Describe "Get-InteropTargetMarkerState" {
    BeforeAll {
        $script:plan = @(New-InteropArrangementPlan -RunToken "t1")
        $script:marker = { param($Name) $script:plan | Where-Object { $_.Marker -eq $Name } }
        $script:observe = {
            param($Marker, $Names, $Attendance)
            $present = @{}
            foreach ($name in @($Marker.NameBefore, $Marker.NameAfter) | Select-Object -Unique) { $present[$name] = ($Names -contains $name) }
            [pscustomobject]@{ Present = $present; OfficialAttendancePeriod = $Attendance }
        }
    }

    It "tells an update before and after by the attendance flag" {
        $u = & $script:marker "U1"
        Get-InteropTargetMarkerState -Marker $u -Observation (& $script:observe $u @($u.NameBefore) $false) | Should -Be "Before"
        Get-InteropTargetMarkerState -Marker $u -Observation (& $script:observe $u @($u.NameBefore) $true) | Should -Be "After"
        Get-InteropTargetMarkerState -Marker $u -Observation (& $script:observe $u @() $null) | Should -Be "Neither"
    }

    It "tells a delete before (found) and after (absent)" {
        $d = & $script:marker "D2"
        Get-InteropTargetMarkerState -Marker $d -Observation (& $script:observe $d @($d.NameBefore) $false) | Should -Be "Before"
        Get-InteropTargetMarkerState -Marker $d -Observation (& $script:observe $d @() $null) | Should -Be "After"
    }

    It "tells an insert before (absent) and after (found)" {
        $i = & $script:marker "I2"
        Get-InteropTargetMarkerState -Marker $i -Observation (& $script:observe $i @() $null) | Should -Be "Before"
        Get-InteropTargetMarkerState -Marker $i -Observation (& $script:observe $i @($i.NameAfter) $false) | Should -Be "After"
    }

    It "tells a key change by which of the two names is found, and calls both found a half-applied change" {
        $k = & $script:marker "K1"
        Get-InteropTargetMarkerState -Marker $k -Observation (& $script:observe $k @($k.NameBefore) $false) | Should -Be "Before"
        Get-InteropTargetMarkerState -Marker $k -Observation (& $script:observe $k @($k.NameAfter) $false) | Should -Be "After"
        Get-InteropTargetMarkerState -Marker $k -Observation (& $script:observe $k @($k.NameBefore, $k.NameAfter) $false) | Should -Be "Neither"
        Get-InteropTargetMarkerState -Marker $k -Observation (& $script:observe $k @() $null) | Should -Be "Neither"
    }
}

Describe "Get-InteropPublisherRunObservation" {
    It "reads the fixture run: every source request went to the Primary, not the Snapshot, and the seven surfaces are counted" {
        $requests = @(ConvertTo-InteropDmsRequest -Line $script:dmsLines)
        $o = Get-InteropPublisherRunObservation -RunName "fixture" -ExitCode 0 -SourceDataStoreId 1 -DmsRequest $requests -DmsLine $script:dmsLines -PublisherLine $script:publisherLines
        $o.Selections["Snapshot"] | Should -Be 0
        $o.Selections["Primary"] | Should -BeGreaterThan 0
        $o.NonSnapshotRequests | Should -Be $o.SourceRequestCount
        $o.NonPrimaryRequests | Should -Be 0
        $o.Surfaces["CursorPage"] | Should -Be 1
        $o.Surfaces["Deletes"] | Should -Be 2
        $o.ChangeVersion.Newest | Should -Be @(3374)
    }

    It "counts a data request with no selection line as unattributed" {
        $withoutSelections = $script:dmsLines | Where-Object { $_ -notlike '*"MessageTemplate":"Selected {TargetKind}*' }
        $requests = @(ConvertTo-InteropDmsRequest -Line $withoutSelections)
        $o = Get-InteropPublisherRunObservation -RunName "fixture" -ExitCode 0 -SourceDataStoreId 1 -DmsRequest $requests -DmsLine $withoutSelections -PublisherLine $script:publisherLines
        $o.UnattributedRequests | Should -BeGreaterThan 0
        $o.SourceRequestCount | Should -Be 0
    }
}

Describe "Get-InteropIsolatedAssertion" {
    BeforeAll {
        $script:arrangement = [pscustomobject]@{
            Markers                     = @(New-InteropArrangementPlan -RunToken "t1")
            StartingChangeVersion       = 100
            SnapshotNewestChangeVersion = 110
            PrimaryNewestChangeVersion  = 120
        }
        $script:newRun = {
            param($Name, $Mode, $Kind, $Newest)
            $surfaces = if ($Mode -eq "cursor") {
                [ordered]@{ GetMany = 0; Partitions = 2; CursorPage = 3; TotalCount = 5; Deletes = 4; KeyChanges = 3; AvailableChangeVersions = 1 }
            }
            else {
                [ordered]@{ GetMany = 2; Partitions = 0; CursorPage = 0; TotalCount = 1; Deletes = 1; KeyChanges = 1; AvailableChangeVersions = 1 }
            }
            [pscustomobject]@{
                RunName              = $Name
                ExitCode             = 0
                Selections           = [ordered]@{ Primary = $(if ($Kind -eq "Primary") { 20 } else { 0 }); Snapshot = $(if ($Kind -eq "Snapshot") { 20 } else { 0 }) }
                SourceRequestCount   = 20
                NonSnapshotRequests  = $(if ($Kind -eq "Snapshot") { 0 } else { 20 })
                NonPrimaryRequests   = $(if ($Kind -eq "Primary") { 0 } else { 20 })
                UnattributedRequests = 0
                Surfaces             = $surfaces
                Unclassified         = @()
                ChangeVersion        = [pscustomobject]@{ Newest = [long[]]@($Newest); WindowStart = 101; WindowEnd = $Newest; WindowLines = 3 }
            }
        }
        $script:good = {
            param($Change)
            $runB = @((& $script:newRun "b-cursor" "cursor" "Snapshot" 110), (& $script:newRun "b-offset" "offset" "Snapshot" 110))
            $runC = @((& $script:newRun "c-cursor" "cursor" "Primary" 120), (& $script:newRun "c-offset" "offset" "Primary" 120))
            $stateB = [ordered]@{ U1 = "After"; D1 = "After"; K1 = "After"; I2 = "Before"; U2 = "Before"; D2 = "Before"; K2 = "Before" }
            $stateC = [ordered]@{ U1 = "After"; D1 = "After"; K1 = "After"; I2 = "After"; U2 = "After"; D2 = "After"; K2 = "After" }
            $case = @{ RunB = $runB; RunC = $runC; StateB = $stateB; StateC = $stateC; Expectation = (Get-InteropIsolatedExpectation) }
            if ($null -ne $Change) { & $Change $case }
            Get-InteropIsolatedAssertion -Expectation $case.Expectation -Arrangement $script:arrangement -RunB $case.RunB -RunC $case.RunC -TargetStateB $case.StateB -TargetStateC $case.StateC
        }
        $script:failedNames = {
            param($Assertions)
            @($Assertions | Where-Object { (ConvertTo-InteropComparable -Value $_.Expected) -cne (ConvertTo-InteropComparable -Value $_.Observed) } | ForEach-Object { $_.Name })
        }
    }

    It "passes every assertion and every control flip for a run that behaves as designed" {
        $assertions = & $script:good $null
        & $script:failedNames $assertions | Should -BeNullOrEmpty
        $assertions.Count | Should -BeGreaterThan 60
    }

    It "records a flip assertion for each post-snapshot marker and for the selections and the processed version" {
        $names = @(& $script:good $null | ForEach-Object { $_.Name })
        foreach ($marker in "I2", "U2", "D2", "K2") {
            $names | Should -Contain "Control flip: $marker is not reflected after Run B and is reflected after Run C"
        }
        $names | Should -Contain "Control flip: source selections go from Snapshot (Run B) to Primary (Run C)"
        $names | Should -Contain "Control flip: processed change version goes from the snapshot's to the primary's"
    }

    # Each case changes one thing and names an assertion that must catch it.
    It "fails <Assertion> when <Case>" -ForEach @(
        @{ Case = "Run B exits 2"; Assertion = "Run B exit codes (cursor run, offset run)"; Change = { param($i) $i.RunB[0].ExitCode = 2 } }
        @{ Case = "Run C offset run exits 1"; Assertion = "Run C exit codes (cursor run, offset run)"; Change = { param($i) $i.RunC[1].ExitCode = 1 } }
        @{ Case = "Run B selected the Primary"; Assertion = "Run B Primary selections for the source data store"; Change = { param($i) $i.RunB[0].Selections["Primary"] = 1 } }
        @{ Case = "Run B selected a ReadReplica"; Assertion = "Run B ReadReplica selections for the source data store"; Change = { param($i) $i.RunB[1].Selections["ReadReplica"] = 2 } }
        @{ Case = "Run B never selected the Snapshot"; Assertion = "Run B selected the Snapshot for the source data store"; Change = { param($i) $i.RunB | ForEach-Object { $_.Selections["Snapshot"] = 0 } } }
        @{ Case = "a Run B source request was attributed to the Primary"; Assertion = "Run B source requests attributed to a target other than Snapshot"; Change = { param($i) $i.RunB[0].NonSnapshotRequests = 1 } }
        @{ Case = "a Run B request has no selection line"; Assertion = "Run B made no source request the DMS log left unattributed"; Change = { param($i) $i.RunB[0].UnattributedRequests = 1 } }
        @{ Case = "a Run B request has an unclassifiable shape"; Assertion = "Run B made no request of a shape the harness cannot classify"; Change = { param($i) $i.RunB[0].Unclassified = @("GET /data/ed-fi/schools?surprise=1") } }
        @{ Case = "no run issued /deletes"; Assertion = "Run B issued Deletes at least once (union of the cursor and offset runs)"; Change = { param($i) $i.RunB | ForEach-Object { $_.Surfaces["Deletes"] = 0 } } }
        @{ Case = "no run issued /keyChanges"; Assertion = "Run B issued KeyChanges at least once (union of the cursor and offset runs)"; Change = { param($i) $i.RunB | ForEach-Object { $_.Surfaces["KeyChanges"] = 0 } } }
        @{ Case = "no run issued /availableChangeVersions"; Assertion = "Run B issued AvailableChangeVersions at least once (union of the cursor and offset runs)"; Change = { param($i) $i.RunB | ForEach-Object { $_.Surfaces["AvailableChangeVersions"] = 0 } } }
        @{ Case = "no run issued totalCount"; Assertion = "Run B issued TotalCount at least once (union of the cursor and offset runs)"; Change = { param($i) $i.RunB | ForEach-Object { $_.Surfaces["TotalCount"] = 0 } } }
        @{ Case = "the offset run is dropped, so GET-many is missing"; Assertion = "Run B issued GetMany at least once (union of the cursor and offset runs)"; Change = { param($i) $i.RunB[1].Surfaces["GetMany"] = 0 } }
        @{ Case = "the cursor run did no cursor paging"; Assertion = "Run B issued CursorPage at least once (union of the cursor and offset runs)"; Change = { param($i) $i.RunB[0].Surfaces["CursorPage"] = 0 } }
        @{ Case = "the cursor run mixed in offset paging"; Assertion = "Run B cursor run pages with /partitions and pageToken only"; Change = { param($i) $i.RunB[0].Surfaces["GetMany"] = 1 } }
        @{ Case = "the offset run probed /partitions"; Assertion = "Run B offset run pages with offset and limit only"; Change = { param($i) $i.RunB[1].Surfaces["Partitions"] = 1 } }
        @{ Case = "the target is missing U1"; Assertion = "Run B target reflects U1"; Change = { param($i) $i.StateB["U1"] = "Before" } }
        @{ Case = "the target did not delete D1"; Assertion = "Run B target reflects D1"; Change = { param($i) $i.StateB["D1"] = "Before" } }
        @{ Case = "the target half-applied K1"; Assertion = "Run B target reflects K1"; Change = { param($i) $i.StateB["K1"] = "Neither" } }
        @{ Case = "the target shows I2 after Run B"; Assertion = "Run B target reflects I2"; Change = { param($i) $i.StateB["I2"] = "After" } }
        @{ Case = "the target shows U2 after Run B"; Assertion = "Run B target reflects U2"; Change = { param($i) $i.StateB["U2"] = "After" } }
        @{ Case = "the target shows D2 after Run B"; Assertion = "Run B target reflects D2"; Change = { param($i) $i.StateB["D2"] = "After" } }
        @{ Case = "the target shows K2 after Run B"; Assertion = "Run B target reflects K2"; Change = { param($i) $i.StateB["K2"] = "After" } }
        @{ Case = "the Run B Publisher recorded the primary's newest version"; Assertion = "Run B processed change version equals the snapshot's newestChangeVersion (110)"; Change = { param($i) $i.RunB | ForEach-Object { $_.ChangeVersion.Newest = [long[]]@(120) } } }
        @{ Case = "the Run B window ends at the primary's newest version"; Assertion = "Run B change-version window ends at the snapshot's newestChangeVersion"; Change = { param($i) $i.RunB[1].ChangeVersion.WindowEnd = 120 } }
        @{ Case = "the Run B window starts at T0 instead of after it"; Assertion = "Run B change-version window starts after T0"; Change = { param($i) $i.RunB[0].ChangeVersion.WindowStart = 100 } }
        @{ Case = "the control selected the Snapshot"; Assertion = "Run C Snapshot selections for the source data store"; Change = { param($i) $i.RunC[0].Selections["Snapshot"] = 3 } }
        @{ Case = "the control never selected the Primary"; Assertion = "Run C selected the Primary for the source data store"; Change = { param($i) $i.RunC | ForEach-Object { $_.Selections["Primary"] = 0 } } }
        @{ Case = "the control did not make I2 appear"; Assertion = "Run C target reflects I2"; Change = { param($i) $i.StateC["I2"] = "Before" } }
        @{ Case = "the control did not make K2 appear"; Assertion = "Run C target reflects K2"; Change = { param($i) $i.StateC["K2"] = "Before" } }
        @{ Case = "the control did not flip D2"; Assertion = "Control flip: D2 is not reflected after Run B and is reflected after Run C"; Change = { param($i) $i.StateC["D2"] = "Before" } }
        @{ Case = "the control did not flip the selections"; Assertion = "Control flip: source selections go from Snapshot (Run B) to Primary (Run C)"; Change = { param($i) $i.RunC | ForEach-Object { $_.Selections["Primary"] = 0; $_.Selections["Snapshot"] = 20 } } }
        @{ Case = "the control recorded the snapshot's version"; Assertion = "Control flip: processed change version goes from the snapshot's to the primary's"; Change = { param($i) $i.RunC | ForEach-Object { $_.ChangeVersion.Newest = [long[]]@(110) } } }
        @{ Case = "a pre-snapshot marker is missing on target C"; Assertion = "Control: U1 is reflected after Run B and after Run C"; Change = { param($i) $i.StateC["U1"] = "Neither" } }
        @{ Case = "a marker was not read from target B"; Assertion = "Every marker was read from both targets"; Change = { param($i) $i.StateB.Remove("K2") } }
    ) {
        $assertions = & $script:good $Change
        $failed = & $script:failedNames $assertions
        $failed | Should -Contain $Assertion
    }

    It "does not let the control flip unnoticed when a pinned expectation is wrong" {
        $wrong = { param($i) $i.Expectation = Get-InteropIsolatedExpectation -Override @{ RunBReflected = [ordered]@{ U1 = $true; D1 = $true; K1 = $true; I2 = $true; U2 = $false; D2 = $false; K2 = $false } } }
        $failed = & $script:failedNames (& $script:good $wrong)
        $failed | Should -Be @("Run B target reflects I2")
    }

    It "fails the arrangement guard when the snapshot is not behind the primary" {
        $flat = [pscustomobject]@{ Markers = $script:arrangement.Markers; StartingChangeVersion = 100; SnapshotNewestChangeVersion = 120; PrimaryNewestChangeVersion = 120 }
        $runB = @((& $script:newRun "b-cursor" "cursor" "Snapshot" 120), (& $script:newRun "b-offset" "offset" "Snapshot" 120))
        $runC = @((& $script:newRun "c-cursor" "cursor" "Primary" 120), (& $script:newRun "c-offset" "offset" "Primary" 120))
        $stateB = [ordered]@{ U1 = "After"; D1 = "After"; K1 = "After"; I2 = "Before"; U2 = "Before"; D2 = "Before"; K2 = "Before" }
        $stateC = [ordered]@{ U1 = "After"; D1 = "After"; K1 = "After"; I2 = "After"; U2 = "After"; D2 = "After"; K2 = "After" }
        $assertions = Get-InteropIsolatedAssertion -Expectation (Get-InteropIsolatedExpectation) -Arrangement $flat -RunB $runB -RunC $runC -TargetStateB $stateB -TargetStateC $stateC
        $failed = & $script:failedNames $assertions
        $failed | Should -Contain "Arrangement: the snapshot's newest change version is below the primary's"
        $failed | Should -Contain "Control flip: processed change version goes from the snapshot's to the primary's"
    }
}

Describe "Write-InteropResultFile -FileName" {
    BeforeAll {
        $script:folder = Join-Path ([System.IO.Path]::GetTempPath()) "interop-results-$([guid]::NewGuid().ToString('N'))"
        $null = New-Item -ItemType Directory -Path $script:folder
    }

    AfterAll {
        Remove-Item -LiteralPath $script:folder -Recurse -Force -ErrorAction SilentlyContinue
    }

    It "writes one file per scenario, so All keeps both results" {
        foreach ($name in "Isolated", "NoSnapshot") {
            $result = New-InteropResult -Scenario $name -Engine "postgresql" -RunId "run-1"
            $null = Add-InteropAssertion -Result $result -Name "a" -Expected 1 -Observed 1 -Evidence "e"
            $null = Write-InteropResultFile -RunFolder $script:folder -Result $result -FileName "results-$name.json"
        }
        (Get-Content -LiteralPath (Join-Path $script:folder "results-Isolated.json") -Raw | ConvertFrom-Json).scenario | Should -Be "Isolated"
        (Get-Content -LiteralPath (Join-Path $script:folder "results-NoSnapshot.json") -Raw | ConvertFrom-Json).scenario | Should -Be "NoSnapshot"
    }

    It "refuses a file name that is not a results file" {
        $result = New-InteropResult -Scenario "Isolated" -Engine "postgresql" -RunId "run-1"
        { Write-InteropResultFile -RunFolder $script:folder -Result $result -FileName "../manifest.json" } | Should -Throw
    }
}

Describe "the validation record" {
    BeforeAll {
        $script:recordRoot = Join-Path ([System.IO.Path]::GetTempPath()) "interop-record-$([guid]::NewGuid().ToString('N'))"
        $null = New-Item -ItemType Directory -Path $script:recordRoot

        $script:recordArrangement = [pscustomobject]@{
            Markers                     = @(New-InteropArrangementPlan -RunToken "t1")
            StartingChangeVersion       = 100
            SnapshotNewestChangeVersion = 110
            PrimaryNewestChangeVersion  = 120
        }
        $script:recordRun = {
            param($Name, $Mode, $Kind, $Newest)
            $surfaces = if ($Mode -eq "cursor") {
                [ordered]@{ GetMany = 0; Partitions = 2; CursorPage = 3; TotalCount = 5; Deletes = 4; KeyChanges = 3; AvailableChangeVersions = 1 }
            }
            else {
                [ordered]@{ GetMany = 2; Partitions = 0; CursorPage = 0; TotalCount = 1; Deletes = 1; KeyChanges = 1; AvailableChangeVersions = 1 }
            }
            [pscustomobject]@{
                RunName              = $Name
                ExitCode             = 0
                Selections           = [ordered]@{ Primary = $(if ($Kind -eq "Primary") { 20 } else { 0 }); Snapshot = $(if ($Kind -eq "Snapshot") { 20 } else { 0 }) }
                SourceRequestCount   = 20
                NonSnapshotRequests  = $(if ($Kind -eq "Snapshot") { 0 } else { 20 })
                NonPrimaryRequests   = $(if ($Kind -eq "Primary") { 0 } else { 20 })
                UnattributedRequests = 0
                Surfaces             = $surfaces
                Unclassified         = @()
                ChangeVersion        = [pscustomobject]@{ Newest = [long[]]@($Newest); WindowStart = 101; WindowEnd = $Newest; WindowLines = 3 }
            }
        }
        $script:problem = [ordered]@{ status = 404; contentType = "application/problem+json"; type = "urn:ed-fi:api:not-found"; title = "Not Found"; detail = "Snapshot not found." }

        # Assertions come from the real builders, so the criterion patterns are checked against real names.
        $script:isolatedAssertions = {
            $runB = @((& $script:recordRun "b-cursor" "cursor" "Snapshot" 110), (& $script:recordRun "b-offset" "offset" "Snapshot" 110))
            $runC = @((& $script:recordRun "c-cursor" "cursor" "Primary" 120), (& $script:recordRun "c-offset" "offset" "Primary" 120))
            $stateB = [ordered]@{ U1 = "After"; D1 = "After"; K1 = "After"; I2 = "Before"; U2 = "Before"; D2 = "Before"; K2 = "Before" }
            $stateC = [ordered]@{ U1 = "After"; D1 = "After"; K1 = "After"; I2 = "After"; U2 = "After"; D2 = "After"; K2 = "After" }
            Get-InteropIsolatedAssertion -Expectation (Get-InteropIsolatedExpectation) -Arrangement $script:recordArrangement -RunB $runB -RunC $runC -TargetStateB $stateB -TargetStateC $stateC
        }
        $script:noSnapshotAssertions = {
            $line = [pscustomobject]@{ Line = "x"; Continuation = @('Response content: {"detail":"Snapshot not found."}') }
            $observation = [pscustomobject]@{
                NeverConfigured = $script:problem; Retired = $script:problem; SnapshotActiveStatus = 200; ExitCode = 2
                AvailableChangeVersionsStatuses = @(404); PartitionsProbeCount = 1; PartitionsProbeStatuses = @(404); CountReadStatuses = @(404)
                PublisherCountReadCount = 2; CountReadCount = 2; OtherSourceReadCount = 0; SourceReadTwoXxCount = 0
                RejectedSourceRequestCount = 4; NoneConfiguredLineCount = 4; TargetWriteCount = 0; TargetDocumentsBefore = 47; TargetDocumentsAfter = 47
                Warn = $line; Info = $line; Fatal = $line; Error = $line
            }
            Get-InteropNoSnapshotAssertion -Expectation (Get-InteropNoSnapshotExpectation) -Observation $observation
        }

        # Writes a run folder the way the entry script does: manifest.json plus one results file per scenario.
        $script:newRunFolder = {
            param($Engine, $Scenarios, $Change)
            $case = @{
                Engine = $Engine; Version = "8.1.0"; Setting = @(Get-InteropNonDefaultSetting -Engine $Engine)
                Overall = @{}; Details = @{}; Drop = @{}; Extra = @{}
            }
            if ($null -ne $Change) { & $Change $case }
            $runId = "20261103T200000Z-$Engine"
            $folder = Join-Path $script:recordRoot "$([guid]::NewGuid().ToString('N'))/$runId"
            $null = New-Item -ItemType Directory -Path $folder -Force
            $versions = [pscustomobject]@{
                DmsImageName = "ed-fi-api-local"; DmsImageId = "sha256:" + ("a" * 64); DmsDiscoveryVersion = $case.Version
                PublisherImage = Get-InteropDefaultPublisherImage; PublisherImageDigest = "sha256:" + ("b" * 64)
                DataStandard = "Ed-Fi 5.2.0"; IdentityProvider = "self-contained"; Engine = $Engine
                EngineVersion = $(if ($Engine -eq "mssql") { "SQL Server 17.0.1000.7" } else { "PostgreSQL 16.4" }); RepositoryCommit = "0123456789ab"
            }
            if ($case.Drop.Contains("EngineVersion")) { $versions.EngineVersion = "" }
            foreach ($key in $case.Extra.Keys) { Add-Member -InputObject $versions -NotePropertyName $key -NotePropertyValue $case.Extra[$key] -Force }
            $manifest = ConvertTo-InteropManifest -RunId $runId -Engine $Engine -Scenario $Scenarios -StartedUtc $script:stamp -Versions $versions `
                -NonDefaultSetting $case.Setting -DataStore @() -Client @()
            $null = Write-InteropRunManifest -RunFolder $folder -Manifest $manifest
            foreach ($scenario in $Scenarios) {
                $result = New-InteropResult -Scenario $scenario -Engine $Engine -RunId $runId
                $assertions = if ($scenario -eq "Isolated") { & $script:isolatedAssertions } else { & $script:noSnapshotAssertions }
                foreach ($item in $assertions) {
                    $null = Add-InteropAssertion -Result $result -Name $item.Name -Expected $item.Expected -Observed $item.Observed -Evidence $item.Evidence
                }
                foreach ($key in $case.Details.Keys) { $result["details"][$key] = $case.Details[$key] }
                if ($case.Overall.Contains($scenario)) {
                    $null = Add-InteropAssertion -Result $result -Name "forced" -Expected 1 -Observed 2 -Evidence "e"
                }
                $null = Write-InteropResultFile -RunFolder $folder -Result $result -FileName $(if ($Scenarios.Count -gt 1) { "results-$scenario.json" } else { "results.json" })
            }
            $folder
        }
        $script:both = @("Isolated", "NoSnapshot")
        $script:render = { param($Folders) ConvertTo-InteropValidationRecord -Run @($Folders | ForEach-Object { Read-InteropRun -RunFolder $_ }) }
        $script:rowOf = { param($Text, $Number) ($Text -split "`n" | Where-Object { $_ -match "^\| $Number \|" }) }
    }

    AfterAll {
        Remove-Item -LiteralPath $script:recordRoot -Recurse -Force -ErrorAction SilentlyContinue
    }

    Context "a passing PostgreSQL run" {
        BeforeAll {
            $script:pgFolder = & $script:newRunFolder "postgresql" $script:both $null
            $script:text = & $script:render @($script:pgFolder)
        }

        It "reports every version the criterion names" {
            $script:text | Should -Match "\| DMS image digest \| ``sha256:a{64}`` \|"
            $script:text | Should -Match "\| Discovery version \| ``8.1.0`` \|"
            $script:text | Should -Match "\| Publisher image digest \| ``sha256:b{64}`` \|"
            $script:text | Should -Match "\| Data Standard \| ``Ed-Fi 5.2.0`` \|"
            $script:text | Should -Match "\| Identity provider \| ``self-contained`` \|"
            $script:text | Should -Match "\| Engine version \| ``PostgreSQL 16.4`` \|"
        }

        It "names the Publisher version in the opening line, from the manifest" {
            $script:text | Should -Match "DMS 8\.1\.0, validated against Ed-Fi API Publisher v1\.4\.0 with snapshot isolation\."
        }

        It "keeps a connection limit visible while redacting a connection string" {
            $script:text | Should -Match "\| ``PostgreSQL max_connections`` \| ``300`` \| ``100`` \|"
            (Protect-InteropRecordText -Text "300" -Name "ConnectionString") | Should -Be "[redacted]"
        }

        It "lists every non-default setting" {
            foreach ($setting in Get-InteropNonDefaultSetting -Engine postgresql) {
                $script:text | Should -Match ([regex]::Escape("| ``$($setting.Name)`` |"))
            }
        }

        It "states the exact harness command" {
            $script:text | Should -Match "pwsh eng/verification/Invoke-PublisherSnapshotInteropCheck.ps1 -Engine postgresql -Scenario All\r?\n"
        }

        It "maps the harness criteria to PASS with the run folder and results file as evidence" {
            foreach ($number in 1, 2, 3, 4, 5) {
                $row = & $script:rowOf $script:text $number
                $row | Should -Match "\| PASS \|"
            }
            (& $script:rowOf $script:text 2) | Should -Match "20261103T200000Z-postgresql/results-Isolated.json"
            (& $script:rowOf $script:text 5) | Should -Match "20261103T200000Z-postgresql/results-NoSnapshot.json"
        }

        It "lists the assertion names behind a criterion" {
            $script:text | Should -Match "- PASS: Run B target reflects I2\r?\n"
            $script:text | Should -Match "- PASS: Run B issued KeyChanges at least once"
        }

        It "says not run for the engine that has no run, and never PASS for the both-engines criterion" {
            (& $script:rowOf $script:text 6) | Should -Match "\| not run \|"
            $script:text | Should -Match "\| SQL Server \| not run \| not run \| not run \| not run \|"
            $script:text | Should -Not -Match "## Engine: SQL Server"
        }

        It "cites the per-request contract, and the contract rows are never PASS" {
            $script:text | Should -Match "SnapshotAndReadReplicaRouting.feature``? 06 and 07"
            $script:text | Should -Match "SnapshotFailures.feature`` 01"
            $script:text | Should -Match "SnapshotFailures.feature`` 02"
            $script:text | Should -Match "header-absent and header-false cases"
            (& $script:rowOf $script:text 7) | Should -Match "\| cited \|"
            foreach ($number in 8, 9, 10, 11, 12, 13) {
                (& $script:rowOf $script:text $number) | Should -Match "\| cited \|"
            }
        }

        It "states the GET-by-id finding against the design" {
            $script:text | Should -Match "SnapshotAndReadReplicaRouting.feature`` scenario 02"
            $script:text | Should -Match "Publisher never issues a source GET-by-id against a DMS target"
            $script:text | Should -Match "29-snapshot-support.md`` section API Publisher Interoperability"
        }

        It "states the gate as satisfied and never as pending" {
            $script:text | Should -Match "epics/10-update-tracking-change-queries/EPIC.md"
            $script:text | Should -Match "DMS-1369 is Done \(PR #1257, ``55e914e2f``\)"
            $script:text | Should -Match "DMS-1371 is Done \(DMS-side PR #1205, ``b0b68972c``; the upstream packages are at ``1.0.335``\)"
            $script:text | Should -Match "re-confirmed when the 8.1 release candidate is assembled"
            $script:text | Should -Not -Match "(?i)pending"
        }

        It "carries no story file, task list, task number, or host path" {
            $script:text | Should -Not -Match "(?i)docs/stories|tasks\.md|\btask \d"
            $script:text | Should -Not -Match "/Users/|/private/|/var/folders|/home/"
            $script:text | Should -Not -Match [char]0x2014
        }

        It "is byte-identical when generated twice" {
            (& $script:render @($script:pgFolder)) | Should -BeExactly $script:text
        }
    }

    Context "both engines" {
        BeforeAll {
            $script:pg = & $script:newRunFolder "postgresql" $script:both $null
            $script:ms = & $script:newRunFolder "mssql" $script:both $null
            $script:bothText = & $script:render @($script:ms, $script:pg)
        }

        It "writes one section per engine in a fixed order, whatever the argument order" {
            $headings = @($script:bothText -split "`n" | Where-Object { $_ -like "## Engine: *" })
            $headings | Should -Be @("## Engine: PostgreSQL", "## Engine: SQL Server")
        }

        It "marks the both-engines criterion PASS only when both lanes passed" {
            (& $script:rowOf $script:bothText 6) | Should -Match "\| PASS \|"
        }

        It "refuses runs that advertise different DMS versions" {
            $other = & $script:newRunFolder "mssql" $script:both { param($c) $c.Version = "8.2.0" }
            { & $script:render @($script:pg, $other) } | Should -Throw "*different DMS versions*"
        }

        It "refuses two runs for one engine" {
            { & $script:render @($script:pg, $script:pg) } | Should -Throw "*same engine*"
        }
    }

    Context "a published-image run" {
        BeforeAll {
            $script:digest = "sha256:" + ("c" * 64)
            $script:csDigest = "sha256:" + ("d" * 64)
            $script:published = {
                param($Case)
                $Case.Version = "8.1.0"
                $Case.Setting = @(Get-InteropNonDefaultSetting -Engine $Case.Engine -DmsImageTag "8.1.0-beta2.23" -ConfigurationServiceImage "edfialliance/ed-fi-api-configuration-service@$($script:csDigest)")
                $Case.Extra = @{
                    ImageSource = "published"; DmsImageDigest = "edfialliance/ed-fi-api@$($script:digest)"; DmsImageRevision = "9bbea26c12e9dd1040a83ce9c7e0a4976778bcfa"
                    ConfigurationServiceImageName = "edfialliance/ed-fi-api-configuration-service@$($script:csDigest)"
                    ConfigurationServiceImageDigest = "edfialliance/ed-fi-api-configuration-service@$($script:csDigest)"
                    ConfigurationServiceImageRevision = "5b8049c4c6854b68dfa7c9df96b1ef4cf8ffd189"
                }
            }
            $script:pubPg = & $script:newRunFolder "postgresql" $script:both $script:published
            $script:pubMs = & $script:newRunFolder "mssql" $script:both $script:published
            $script:pubText = & $script:render @($script:pubPg, $script:pubMs)
        }

        It "names the published image, its digest and source revision, and the Configuration Service image" {
            $script:pubText | Should -Match "\| DMS image digest \| ``edfialliance/ed-fi-api@$($script:digest)`` \|"
            $script:pubText | Should -Match "\| DMS image source revision \| ``9bbea26c12e9dd1040a83ce9c7e0a4976778bcfa`` \|"
            $script:pubText | Should -Match "\| Configuration Service image digest \| ``edfialliance/ed-fi-api-configuration-service@$($script:csDigest)`` \|"
            $script:pubText | Should -Match "\| Configuration Service image source revision \| ``5b8049c4c6854b68dfa7c9df96b1ef4cf8ffd189`` \|"
        }

        It "rebuilds the published command from the manifest" {
            $script:pubText | Should -Match "-DmsImageTag 8\.1\.0-beta2\.23 -ConfigurationServiceImage edfialliance/ed-fi-api-configuration-service@$($script:csDigest)"
        }
    }

    Context "outcomes that are not PASS" {
        It "reports FAIL for a criterion whose assertion failed, and not run when the scenario is absent" {
            $folder = & $script:newRunFolder "postgresql" @("Isolated") { param($c) $c.Overall["Isolated"] = $true }
            $text = & $script:render @($folder)
            (& $script:rowOf $text 2) | Should -Match "\| PASS \|"
            (& $script:rowOf $text 5) | Should -Match "\| not run \|"
            $text | Should -Match "\| FAIL \(\d+ passed, 1 failed\) \| not run \|"
        }

        It "reports FAIL, not PASS, when a required assertion was never recorded" {
            $folder = & $script:newRunFolder "postgresql" $script:both $null
            $path = Join-Path $folder "results-Isolated.json"
            $document = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -AsHashtable -Depth 20
            $document["assertions"] = @($document["assertions"] | Where-Object { $_["name"] -notlike "Run B issued Deletes*" })
            $document | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $path
            $text = & $script:render @($folder)
            (& $script:rowOf $text 4) | Should -Match "\| FAIL \|"
            $text | Should -Match "no assertion matches"
        }

        It "reports FAIL for the versions row when a version was not recorded" {
            $folder = & $script:newRunFolder "postgresql" $script:both { param($c) $c.Drop["EngineVersion"] = $true }
            (& $script:rowOf (& $script:render @($folder)) 1) | Should -Match "\| FAIL \|.*engineVersion"
        }

        It "refuses a run that used an expectation override" {
            $folder = & $script:newRunFolder "postgresql" $script:both { param($c) $c.Details["expectationOverride"] = @("PublisherExitCode") }
            { & $script:render @($folder) } | Should -Throw "*-ExpectationOverride*"
        }
    }

    Context "secrets and host paths" {
        BeforeAll {
            $script:fakeSecret = "FAKE-SECRET-VALUE-4b7f1c"
            $script:fakeConnection = "Server=localhost,1433;User Id=sa;Password=FakePassw0rd-9e2d;TrustServerCertificate=true"
            $script:fakeBearer = "Bearer eyJFAKEtoken0123456789"
            $script:fakePath = "/Users/someone/work/run"

            $versions = [pscustomobject]@{
                DmsImageName = "ed-fi-api-local"; DmsImageId = "sha256:" + ("a" * 64); DmsDiscoveryVersion = "8.1.0"
                PublisherImage = Get-InteropDefaultPublisherImage; PublisherImageDigest = "sha256:" + ("b" * 64)
                DataStandard = "Ed-Fi 5.2.0"; IdentityProvider = "self-contained"; Engine = "postgresql"; EngineVersion = "PostgreSQL 16.4"; RepositoryCommit = "0123456789ab"
            }
            $settings = @(
                [pscustomobject]@{ Name = "Harmless"; Value = $script:fakeConnection; Default = "none"; Reason = "tried $script:fakeBearer and $script:fakePath" }
                [pscustomobject]@{ Name = "ClientSecret"; Value = $script:fakeSecret; Default = "none"; Reason = "a secret-named setting" }
            )
            # A manifest the entry script could never write, to prove the record does not depend on that.
            $manifest = ConvertTo-InteropManifest -RunId "20261103T200000Z-postgresql" -Engine "postgresql" -Scenario $script:both -StartedUtc $script:stamp `
                -Versions $versions -NonDefaultSetting @() -DataStore @() -Client @()
            $manifest["nonDefaultSettings"] = $settings | ForEach-Object { [ordered]@{ name = $_.Name; value = $_.Value; default = $_.Default; reason = $_.Reason } }
            $manifest["versions"]["dataStandard"] = "Ed-Fi 5.2.0 $script:fakeConnection"
            $result = [ordered]@{ scenario = "Isolated"; engine = "postgresql"; assertions = @(); details = [ordered]@{}; overall = "PASS"; passed = 0; failed = 0; fileName = "results-Isolated.json" }
            $script:hostileRun = [pscustomobject]@{ Name = "20261103T200000Z-postgresql"; Manifest = $manifest; Results = [ordered]@{ Isolated = $result } }
        }

        It "keeps the fake secret, the fake connection string, a bearer token, and a host path out of the record" {
            $text = ConvertTo-InteropValidationRecord -Run @($script:hostileRun)
            $text | Should -Not -Match "FAKE-SECRET-VALUE"
            $text | Should -Not -Match "FakePassw0rd"
            $text | Should -Not -Match "Server=localhost"
            $text | Should -Not -Match "TrustServerCertificate"
            $text | Should -Not -Match "eyJFAKEtoken"
            $text | Should -Not -Match "/Users/someone"
            $text | Should -Match "\[redacted\]"
        }

        It "starts from inputs that really carry the secret, so the check above can fail" {
            ($script:fakeConnection + $script:fakeSecret + $script:fakeBearer + $script:fakePath) | Should -Match "Password=FakePassw0rd"
            (Protect-InteropRecordText -Text $script:fakeConnection) | Should -Not -Match "FakePassw0rd"
            (Protect-InteropRecordText -Text $script:fakeSecret -Name "ClientSecret") | Should -Be "[redacted]"
            (Protect-InteropRecordText -Text $script:fakeSecret) | Should -Be $script:fakeSecret
        }

        It "refuses to produce a record when the sanitizer is bypassed" {
            InModuleScope PublisherSnapshotInterop -Parameters @{ Run = $script:hostileRun } {
                Mock Protect-InteropRecordText { $Text }
                { ConvertTo-InteropValidationRecord -Run @($Run) } | Should -Throw "*refusing to produce it*"
            }
        }

        It "refuses a run folder whose manifest has a secret-shaped property" {
            $folder = & $script:newRunFolder "postgresql" $script:both $null
            $path = Join-Path $folder "manifest.json"
            $document = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -AsHashtable -Depth 20
            $document["clientSecret"] = $script:fakeSecret
            $document | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $path
            { Read-InteropRun -RunFolder $folder } | Should -Throw "*secret-shaped property*"
        }

        It "scans the finished text for a secret that is not in any known field" {
            { Assert-InteropRecordHasNoSecret -Text "x`nPassword=abc`n" } | Should -Throw
            { Assert-InteropRecordHasNoSecret -Text "see /Users/someone/x" } | Should -Throw
            { Assert-InteropRecordHasNoSecret -Text "clean text`n" } | Should -Not -Throw
        }
    }

    Context "Write-InteropValidationRecord" {
        It "writes a validation file named for the DMS version from the run folders and creates the output folder" {
            $folder = & $script:newRunFolder "postgresql" $script:both $null
            $out = Join-Path $script:recordRoot "out-$([guid]::NewGuid().ToString('N'))"
            $path = Write-InteropValidationRecord -RunFolder @($folder) -OutputFolder $out
            (Split-Path -Leaf $path) | Should -Be "validation-8.1.0.md"
            (Get-Content -LiteralPath $path -Raw) | Should -Be (& $script:render @($folder))
        }

        It "rebuilds the command from a reused-stack manifest" {
            $folder = & $script:newRunFolder "postgresql" @("Isolated") { param($c) $c.Setting = @(Get-InteropNonDefaultSetting -Engine postgresql -SkipStackStart -Rebuild) }
            (& $script:render @($folder)) | Should -Match "-Engine postgresql -Scenario Isolated -SkipStackStart -Rebuild\r?\n"
        }
    }
}

Describe "published-image selection" {
    BeforeAll {
        $script:csImage = "edfialliance/ed-fi-api-configuration-service@sha256:" + ("e" * 64)
    }

    It "sets the tag, the whole Configuration Service reference, and both overlays, in order" {
        $override = Get-InteropEnvironmentOverride -OverlayComposeFile "a.yml" -PublishedOverlayComposeFile "b.yml" -DmsImageTag "8.1.0-beta2.23" -ConfigurationServiceImage $script:csImage
        $override["DMS_IMAGE_TAG"] | Should -Be "8.1.0-beta2.23"
        $override["CONFIGURATION_SERVICE_DOCKER_IMAGE"] | Should -Be $script:csImage
        $override["DMS_PLUGINS_COMPOSE_FILES"] | Should -Be "a.yml;b.yml"
    }

    It "leaves a local-build run with only the one overlay and no image keys" {
        $override = Get-InteropEnvironmentOverride -OverlayComposeFile "a.yml"
        $override["DMS_PLUGINS_COMPOSE_FILES"] | Should -Be "a.yml"
        $override.Contains("DMS_IMAGE_TAG") | Should -BeFalse
        $override.Contains("CONFIGURATION_SERVICE_DOCKER_IMAGE") | Should -BeFalse
    }

    It "replaces DMS_IMAGE_TAG in place in the tracked example and appends the Configuration Service image" {
        $override = Get-InteropEnvironmentOverride -DmsImageTag "8.1.0-beta2.23" -ConfigurationServiceImage $script:csImage
        $lines = ConvertTo-InteropEnvironmentContent -BaseLine @("DMS_IMAGE_TAG=pre", "DMS_IMAGE_TAG_OTHER=x") -Override $override
        $lines | Should -Contain "DMS_IMAGE_TAG=8.1.0-beta2.23"
        $lines | Should -Contain "CONFIGURATION_SERVICE_DOCKER_IMAGE=$($script:csImage)"
        $lines | Should -Not -Contain "DMS_IMAGE_TAG=pre"
    }

    It "refuses a tag without the matching Configuration Service image" {
        { Get-InteropEnvironmentOverride -DmsImageTag "8.1.0-beta2.23" } | Should -Throw "*needs -ConfigurationServiceImage*"
    }

    It "refuses a Configuration Service image without a tag" {
        { Get-InteropEnvironmentOverride -ConfigurationServiceImage $script:csImage } | Should -Throw "*needs -DmsImageTag*"
    }

    It "refuses a malformed tag, so nothing odd reaches a compose file" {
        foreach ($tag in "8.1.0 beta", "8.1.0;x", "-x", "a/b") {
            { Assert-InteropPublishedImageSelection -DmsImageTag $tag -ConfigurationServiceImage $script:csImage } | Should -Throw "*not a valid image tag*"
        }
    }

    It "refuses a Configuration Service image from another repository, or one with no tag or digest" {
        foreach ($image in "edfialliance/ed-fi-api:8.1.0-beta2.23", "evil/ed-fi-api-configuration-service:pre", "edfialliance/ed-fi-api-configuration-service", "edfialliance/ed-fi-api-configuration-service@sha256:abc") {
            { Assert-InteropPublishedImageSelection -DmsImageTag "8.1.0-beta2.23" -ConfigurationServiceImage $image } | Should -Throw "*must be*"
        }
    }

    It "accepts a tag-only and a tag-and-digest Configuration Service reference" {
        { Assert-InteropPublishedImageSelection -DmsImageTag "8.1.0-beta2.23" -ConfigurationServiceImage "edfialliance/ed-fi-api-configuration-service:pre" } | Should -Not -Throw
        { Assert-InteropPublishedImageSelection -DmsImageTag "8.1.0-beta2.23" -ConfigurationServiceImage "edfialliance/ed-fi-api-configuration-service:pre@sha256:$('e' * 64)" } | Should -Not -Throw
    }

    It "records the pair as non-default settings only when a tag is given" {
        $settings = @(Get-InteropNonDefaultSetting -Engine postgresql -DmsImageTag "8.1.0-beta2.23" -ConfigurationServiceImage $script:csImage)
        ($settings | Where-Object Name -EQ "DmsImageTag").Value | Should -Be "8.1.0-beta2.23"
        ($settings | Where-Object Name -EQ "ConfigurationServiceImage").Value | Should -Be $script:csImage
        @(Get-InteropNonDefaultSetting -Engine postgresql | Where-Object Name -In "DmsImageTag", "ConfigurationServiceImage").Count | Should -Be 0
    }

    It "checks the Discovery version against the release line the tag names" {
        { Assert-InteropPublishedDiscoveryVersion -DmsImageTag "8.1.0-beta2.23" -DiscoveryVersion "8.1.0" } | Should -Not -Throw
        { Assert-InteropPublishedDiscoveryVersion -DmsImageTag "8.1.0-beta2.23" -DiscoveryVersion "8.1.2" } | Should -Not -Throw
        { Assert-InteropPublishedDiscoveryVersion -DmsImageTag "8.1.0-beta2.23" -DiscoveryVersion "8.0.0" } | Should -Throw "*should advertise a 8.1.x*"
        { Assert-InteropPublishedDiscoveryVersion -DmsImageTag "8.1.0-beta2.23" -DiscoveryVersion "8.10.0" } | Should -Throw "*should advertise a 8.1.x*"
        { Assert-InteropPublishedDiscoveryVersion -DmsImageTag "8.1.0-beta2.23" -DiscoveryVersion "" } | Should -Throw "*Discovery reads ''*"
        { Assert-InteropPublishedDiscoveryVersion -DmsImageTag "pre" -DiscoveryVersion "8.1.0" } | Should -Throw "*does not start with a release line*"
    }

    It "picks the registry digest by repository, not by position" {
        $other = "edfialliance/other@sha256:" + ("1" * 64)
        $mine = "edfialliance/ed-fi-api@sha256:" + ("2" * 64)
        Select-InteropRepoDigest -ImageName "edfialliance/ed-fi-api:8.1.0-beta2.23" -RepoDigest @($other, $mine) | Should -Be ("sha256:" + ("2" * 64))
        Select-InteropRepoDigest -ImageName "edfialliance/ed-fi-api@sha256:$('2' * 64)" -RepoDigest @($other, $mine) | Should -Be ("sha256:" + ("2" * 64))
        Select-InteropRepoDigest -ImageName "ed-fi-api-local" -RepoDigest @() | Should -Be ""
        Select-InteropRepoDigest -ImageName "edfialliance/ed-fi-api:8.1.0-beta2.23" -RepoDigest @($other) | Should -Be ""
    }

    It "writes the image provenance into the manifest only when the run recorded it" {
        $versions = [pscustomobject]@{
            DmsImageName = "edfialliance/ed-fi-api:8.1.0-beta2.23"; DmsImageId = "sha256:x"; DmsDiscoveryVersion = "8.1.0"
            PublisherImage = "p"; PublisherImageDigest = "d"; DataStandard = "s"; IdentityProvider = "i"; Engine = "postgresql"
        }
        $local = ConvertTo-InteropManifest -RunId "r" -Engine postgresql -Scenario @("Isolated") -StartedUtc ([datetime]::UtcNow) -Versions $versions -NonDefaultSetting @() -DataStore @() -Client @()
        $local["versions"].Contains("dmsImageDigest") | Should -BeFalse
        $versions | Add-Member -NotePropertyName ImageSource -NotePropertyValue "published"
        $versions | Add-Member -NotePropertyName DmsImageDigest -NotePropertyValue ("sha256:" + ("2" * 64))
        $versions | Add-Member -NotePropertyName ConfigurationServiceImageRevision -NotePropertyValue "5b8049c4c6854b68dfa7c9df96b1ef4cf8ffd189"
        $published = ConvertTo-InteropManifest -RunId "r" -Engine postgresql -Scenario @("Isolated") -StartedUtc ([datetime]::UtcNow) -Versions $versions -NonDefaultSetting @() -DataStore @() -Client @()
        $published["versions"]["imageSource"] | Should -Be "published"
        $published["versions"]["dmsImageDigest"] | Should -Be ("sha256:" + ("2" * 64))
        $published["versions"]["configurationServiceImageRevision"] | Should -Be "5b8049c4c6854b68dfa7c9df96b1ef4cf8ffd189"
        $published["versions"].Contains("dmsImageRevision") | Should -BeFalse
    }
}

Describe "Start-InteropStack with published images" {
    BeforeAll {
        $script:stackEnvironmentFile = Join-Path $TestDrive "published-stack.env"
        Set-Content -LiteralPath $script:stackEnvironmentFile -Value "DMS_HTTP_PORTS=8080"
        $script:csImage = "edfialliance/ed-fi-api-configuration-service@sha256:" + ("e" * 64)
    }

    BeforeEach {
        Mock -ModuleName PublisherSnapshotInterop Assert-InteropNoForeignStack {}
        Mock -ModuleName PublisherSnapshotInterop Assert-InteropPublishedImage {}
        Mock -ModuleName PublisherSnapshotInterop Invoke-InteropScriptCaptured { return [pscustomobject]@{ Failure = $null; Output = "" } }
    }

    It "starts bootstrap-published-dms.ps1, and not the local bootstrap, after making sure both images are present" {
        Start-InteropStack -Engine mssql -EnvironmentFile $script:stackEnvironmentFile -DmsImageTag "8.1.0-beta2.23" -ConfigurationServiceImage $script:csImage

        Should -Invoke -ModuleName PublisherSnapshotInterop Invoke-InteropScriptCaptured -Times 1 -Exactly -ParameterFilter { $Path -like "*bootstrap-published-dms.ps1" }
        Should -Invoke -ModuleName PublisherSnapshotInterop Invoke-InteropScriptCaptured -Times 0 -Exactly -ParameterFilter { $Path -like "*bootstrap-local-dms.ps1" }
        Should -Invoke -ModuleName PublisherSnapshotInterop Assert-InteropPublishedImage -Times 1 -Exactly -ParameterFilter { $Image -eq "edfialliance/ed-fi-api:8.1.0-beta2.23" }
        Should -Invoke -ModuleName PublisherSnapshotInterop Assert-InteropPublishedImage -Times 1 -Exactly -ParameterFilter { $Image -eq $script:csImage }
    }

    It "starts the local bootstrap when no tag is given" {
        Start-InteropStack -Engine mssql -EnvironmentFile $script:stackEnvironmentFile

        Should -Invoke -ModuleName PublisherSnapshotInterop Invoke-InteropScriptCaptured -Times 1 -Exactly -ParameterFilter { $Path -like "*bootstrap-local-dms.ps1" }
        Should -Invoke -ModuleName PublisherSnapshotInterop Assert-InteropPublishedImage -Times 0 -Exactly
    }

    It "refuses -Rebuild with published images, before anything starts" {
        { Start-InteropStack -Engine mssql -EnvironmentFile $script:stackEnvironmentFile -Rebuild -DmsImageTag "8.1.0-beta2.23" -ConfigurationServiceImage $script:csImage } | Should -Throw "*cannot be combined with -DmsImageTag*"
        Should -Invoke -ModuleName PublisherSnapshotInterop Invoke-InteropScriptCaptured -Times 0 -Exactly
    }

    It "refuses a tag without the Configuration Service image, before anything starts" {
        { Start-InteropStack -Engine mssql -EnvironmentFile $script:stackEnvironmentFile -DmsImageTag "8.1.0-beta2.23" } | Should -Throw "*needs -ConfigurationServiceImage*"
        Should -Invoke -ModuleName PublisherSnapshotInterop Invoke-InteropScriptCaptured -Times 0 -Exactly
    }

    It "names the failing script when the published bootstrap fails" {
        Mock -ModuleName PublisherSnapshotInterop Invoke-InteropScriptCaptured { return [pscustomobject]@{ Failure = "exit code 1"; Output = "boom" } }
        { Start-InteropStack -Engine postgresql -EnvironmentFile $script:stackEnvironmentFile -DmsImageTag "8.1.0-beta2.23" -ConfigurationServiceImage $script:csImage } | Should -Throw "*bootstrap-published-dms.ps1 failed*"
    }
}

Describe "Set-InteropSourceArrangement baseline publishes" {
    BeforeAll {
        $script:arrangementCalls = [System.Collections.Generic.List[object]]::new()
        $script:runArrangement = {
            $script:arrangementCalls.Clear()
            Mock -ModuleName PublisherSnapshotInterop Write-InteropStep {}
            Mock -ModuleName PublisherSnapshotInterop Invoke-InteropDmsWrite { return $null }
            Mock -ModuleName PublisherSnapshotInterop Get-InteropClassPeriod { return [pscustomobject]@{ id = [guid]::NewGuid().ToString() } }
            Mock -ModuleName PublisherSnapshotInterop Get-InteropClientToken { return "token-$($Client.Role)" }
            Mock -ModuleName PublisherSnapshotInterop Invoke-InteropPublisher {
                $null = $script:arrangementCalls.Add([pscustomobject]@{ Kind = "Publish"; Name = $RunName; Option = @($Option); EnvironmentValues = $EnvironmentValues })
                return [pscustomobject]@{ ExitCode = 0; Folder = "f" }
            }
            Mock -ModuleName PublisherSnapshotInterop Get-InteropChangeVersion { return [pscustomobject]@{ Newest = 100 } }
            Mock -ModuleName PublisherSnapshotInterop New-InteropSnapshot { return [pscustomobject]@{ ConnectionString = "x"; SnapshotName = "s"; LoginName = "l"; SourceDatabaseName = "d" } }
            Mock -ModuleName PublisherSnapshotInterop Register-InteropSnapshotDerivative { return 1 }
            Mock -ModuleName PublisherSnapshotInterop Wait-InteropSnapshotState { return 0 }
            Mock -ModuleName PublisherSnapshotInterop Test-InteropArrangementVisibility { return $null }
            Mock -ModuleName PublisherSnapshotInterop Add-InteropArrangementToManifest { return "m" }
            $reader = [pscustomobject]@{ Role = "source-reader" }
            $targetB = [pscustomobject]@{ Role = "target-b-writer" }
            $targetC = [pscustomobject]@{ Role = "target-c-writer" }
            Set-InteropSourceArrangement -DmsUrl "http://x" -CmsUrl "http://c" -AdminToken "a" -Engine mssql -EnvironmentValues @{ DMS_HTTP_PORTS = "8090" } -RunFolder "r" -RunToken "t1" `
                -SourceDataStoreId 1 -SourceReader $reader -TargetWriter $targetB -SourceWriterToken "w" -SourceReaderToken "rt" -PublisherImage "img" `
                -AdditionalBaselineTargetWriter @($targetC)
        }
    }

    It "publishes every baseline with --ignoreIsolation=true and --includeDescriptors=true" {
        $null = & $script:runArrangement

        $publishes = @($script:arrangementCalls | Where-Object Kind -EQ "Publish")
        $publishes.Count | Should -Be 2
        foreach ($publish in $publishes) {
            $publish.Option | Should -Be @("--ignoreIsolation=true", "--includeDescriptors=true")
        }
    }

    It "hands every baseline publish the stack's environment values, so the Publisher reaches DMS on the stack's port" {
        $null = & $script:runArrangement

        $publishes = @($script:arrangementCalls | Where-Object Kind -EQ "Publish")
        $publishes.Count | Should -Be 2
        foreach ($publish in $publishes) {
            $publish.EnvironmentValues["DMS_HTTP_PORTS"] | Should -Be "8090"
        }
    }
}

Describe "DMS and Configuration Service ports" {
    BeforeEach {
        $script:savedPorts = @{}
        foreach ($name in "DMS_HTTP_PORTS", "CONFIGURATION_SERVICE_ASPNETCORE_HTTP_PORTS") {
            $script:savedPorts[$name] = [Environment]::GetEnvironmentVariable($name)
            [Environment]::SetEnvironmentVariable($name, $null)
        }
    }

    AfterEach {
        foreach ($name in $script:savedPorts.Keys) {
            [Environment]::SetEnvironmentVariable($name, $script:savedPorts[$name])
        }
    }

    It "writes a DMS port set in the shell into the environment file, because compose takes the shell value over the file" {
        $env:DMS_HTTP_PORTS = "8090"

        $override = Get-InteropEnvironmentOverride -OverlayComposeFile "a.yml"

        $override["DMS_HTTP_PORTS"] | Should -Be "8090"
    }

    It "keeps the DMS port of .env.example when the shell sets none" {
        $override = Get-InteropEnvironmentOverride -OverlayComposeFile "a.yml"

        $override.Contains("DMS_HTTP_PORTS") | Should -BeFalse
    }

    It "refuses a Configuration Service port moved in the shell, because the stack's own URLs name 8081" {
        $env:CONFIGURATION_SERVICE_ASPNETCORE_HTTP_PORTS = "8091"

        { Get-InteropEnvironmentOverride -OverlayComposeFile "a.yml" } | Should -Throw "*CONFIGURATION_SERVICE_ASPNETCORE_HTTP_PORTS*8081*"
    }

    It "accepts a Configuration Service port the shell sets to its default" {
        $env:CONFIGURATION_SERVICE_ASPNETCORE_HTTP_PORTS = "8081"

        $override = Get-InteropEnvironmentOverride -OverlayComposeFile "a.yml"

        $override.Contains("CONFIGURATION_SERVICE_ASPNETCORE_HTTP_PORTS") | Should -BeFalse
    }

    It "refuses a shell DMS port that is not one port number, before the stack starts" -TestCases @(
        @{ Value = "8090;8091" }
        @{ Value = "http" }
        @{ Value = "0" }
        @{ Value = "65536" }
    ) {
        param($Value)
        $env:DMS_HTTP_PORTS = $Value

        { Get-InteropEnvironmentOverride -OverlayComposeFile "a.yml" } | Should -Throw "*DMS_HTTP_PORTS*"
    }

    It "addresses DMS on the stack's port from the host and from the Publisher's container" {
        $endpoint = Get-InteropEndpoint -EnvironmentValues @{ DMS_HTTP_PORTS = "8090"; CONFIGURATION_SERVICE_ASPNETCORE_HTTP_PORTS = "8091" }

        $endpoint.DmsUrl | Should -Be "http://localhost:8090"
        $endpoint.CmsUrl | Should -Be "http://localhost:8091"
        $endpoint.PublisherDmsUrl | Should -Be "http://ed-fi-api:8090"
    }

    It "addresses DMS on 8080 when the environment file names no port" {
        (Get-InteropEndpoint -EnvironmentValues @{}).PublisherDmsUrl | Should -Be "http://ed-fi-api:8080"
    }

    It "gives every Publisher run in the module the stack's environment values" {
        $tokens = $null
        $errors = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $script:harnessRoot "PublisherSnapshotInterop.psm1"), [ref]$tokens, [ref]$errors)
        $errors | Should -BeNullOrEmpty

        $calls = @($ast.FindAll({
                    param($node)
                    $node -is [System.Management.Automation.Language.CommandAst] -and $node.GetCommandName() -eq "Invoke-InteropPublisher"
                }, $true))
        $calls.Count | Should -Be 4
        foreach ($call in $calls) {
            $named = @($call.CommandElements | Where-Object { $_ -is [System.Management.Automation.Language.CommandParameterAst] } | ForEach-Object ParameterName)
            $named | Should -Contain "EnvironmentValues" -Because "line $($call.Extent.StartLineNumber) would otherwise address DMS on a fixed port"
        }
    }

    It "records a moved DMS port as a non-default setting" {
        $settings = @(Get-InteropNonDefaultSetting -Engine postgresql -EnvironmentValues @{ DMS_HTTP_PORTS = "8090" })

        ($settings | Where-Object Name -EQ "DMS_HTTP_PORTS").Value | Should -Be "8090"
        ($settings | Where-Object Name -EQ "DMS_HTTP_PORTS").Default | Should -Be "8080"
    }

    It "records no port setting when the stack uses the default DMS port" {
        $settings = @(Get-InteropNonDefaultSetting -Engine postgresql -EnvironmentValues @{ DMS_HTTP_PORTS = "8080" })

        @($settings | Where-Object Name -EQ "DMS_HTTP_PORTS").Count | Should -Be 0
    }
}

Describe "SQL Server memory limit" {
    It "leaves SQL Server at the stock memory limit, which several DMS databases on one instance need" {
        $override = Get-InteropEnvironmentOverride -OverlayComposeFile "a.yml"
        $override.Contains("MSSQL_MEMORY_LIMIT_MB") | Should -BeFalse
        @(Get-InteropNonDefaultSetting -Engine mssql | Where-Object Name -EQ "MSSQL_MEMORY_LIMIT_MB").Count | Should -Be 0
    }
}

Describe "New-InteropDataStore provisioning" {
    BeforeAll {
        $script:storeEnvironmentFile = Join-Path $TestDrive "store.env"
        Set-Content -LiteralPath $script:storeEnvironmentFile -Value "MSSQL_SA_PASSWORD=x"
        $script:memoryFailure = [pscustomobject]@{ Failure = "api-schema-tools ddl provision failed"; Output = "SqlException: There is insufficient system memory in resource pool 'default' to run this query.`nError Number:701,State:123,Class:17" }
        $script:ddlFailure = [pscustomobject]@{ Failure = "api-schema-tools ddl provision failed"; Output = "SqlException: Incorrect syntax near 'GO'.`nError Number:102,State:1,Class:15" }
        $script:ok = [pscustomobject]@{ Failure = $null; Output = "Provisioning complete" }
    }

    BeforeEach {
        $script:outcomes = [System.Collections.Generic.Queue[object]]::new()
        $outcomes = $script:outcomes
        Mock -ModuleName PublisherSnapshotInterop Write-InteropStep {}
        Mock -ModuleName PublisherSnapshotInterop Add-DataStore { return 7 }
        Mock -ModuleName PublisherSnapshotInterop New-E2EDataStoreConnectionStrings { return [pscustomobject]@{ RegistrationConnectionString = "c" } }
        Mock -ModuleName PublisherSnapshotInterop Invoke-InteropScriptCaptured { return $outcomes.Dequeue() }.GetNewClosure()
    }

    It "recognizes the SQL Server memory failure and nothing else" {
        Test-InteropSqlServerMemoryFailure -Text $script:memoryFailure.Output | Should -BeTrue
        Test-InteropSqlServerMemoryFailure -Text "Error Number:701,State:1" | Should -BeTrue
        Test-InteropSqlServerMemoryFailure -Text $script:ddlFailure.Output | Should -BeFalse
        Test-InteropSqlServerMemoryFailure -Text "Error Number:7012,State:1" | Should -BeFalse
        Test-InteropSqlServerMemoryFailure -Text "" | Should -BeFalse
    }

    It "provisions the schema once and returns the store" {
        $script:outcomes.Enqueue($script:ok)

        $store = New-InteropDataStore -CmsUrl "http://c" -AdminToken "a" -Engine mssql -EnvironmentFile $script:storeEnvironmentFile -RunToken "20261104T000000Z-ab12" -Role "target"

        $store.Id | Should -Be 7
        Should -Invoke -ModuleName PublisherSnapshotInterop Invoke-InteropScriptCaptured -Times 1 -Exactly
    }

    It "fails at once on a SQL Server memory failure, which a retry does not clear, and says what to free" {
        $script:outcomes.Enqueue($script:memoryFailure)
        $script:outcomes.Enqueue($script:ok)

        { New-InteropDataStore -CmsUrl "http://c" -AdminToken "a" -Engine mssql -EnvironmentFile $script:storeEnvironmentFile -RunToken "20261104T000000Z-ab12" -Role "target" } |
            Should -Throw "*provision-dms-schema.ps1 failed*SQL Server ran out of memory*MSSQL_MEMORY_LIMIT_MB*"

        Should -Invoke -ModuleName PublisherSnapshotInterop Invoke-InteropScriptCaptured -Times 1 -Exactly
    }

    It "fails at once on any other provisioning failure, without the memory hint" {
        $script:outcomes.Enqueue($script:ddlFailure)

        $thrown = { New-InteropDataStore -CmsUrl "http://c" -AdminToken "a" -Engine mssql -EnvironmentFile $script:storeEnvironmentFile -RunToken "20261104T000000Z-ab12" -Role "target" } | Should -Throw -PassThru

        $thrown.Exception.Message | Should -BeLike "*provision-dms-schema.ps1 failed*"
        $thrown.Exception.Message | Should -Not -BeLike "*ran out of memory*"
        Should -Invoke -ModuleName PublisherSnapshotInterop Invoke-InteropScriptCaptured -Times 1 -Exactly
    }

    It "does not add the SQL Server memory hint on PostgreSQL" {
        $script:outcomes.Enqueue($script:memoryFailure)

        $thrown = { New-InteropDataStore -CmsUrl "http://c" -AdminToken "a" -Engine postgresql -EnvironmentFile $script:storeEnvironmentFile -RunToken "20261104T000000Z-ab12" -Role "target" } | Should -Throw -PassThru

        $thrown.Exception.Message | Should -Not -BeLike "*ran out of memory*"
        Should -Invoke -ModuleName PublisherSnapshotInterop Invoke-InteropScriptCaptured -Times 1 -Exactly
    }
}
