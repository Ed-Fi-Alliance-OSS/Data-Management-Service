# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

Describe "Get-EducationOrganizationIdsFromSampleData" {
    BeforeAll {
        # The module imports sibling modules with paths relative to its own directory, so import
        # it with that directory as the working directory (matching how the template workflow
        # invokes it). The parser under test is not exported, so it is reached via InModuleScope.
        $script:templatesDir = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
        Push-Location $script:templatesDir
        try {
            Import-Module (Join-Path $script:templatesDir "Template-Management.psm1") -Force
        }
        finally {
            Pop-Location
        }

        function script:Invoke-Parser {
            param([string]$Directory)
            InModuleScope Template-Management -Parameters @{ dir = $Directory } {
                param($dir)
                @(Get-EducationOrganizationIdsFromSampleData -SampleDataDirectory $dir)
            }
        }
    }

    BeforeEach {
        $script:work = Join-Path ([System.IO.Path]::GetTempPath()) "dms-edorg-parser-$([Guid]::NewGuid().ToString('N'))"
        New-Item -ItemType Directory -Path $script:work -Force | Out-Null
    }

    AfterEach {
        if (Test-Path -LiteralPath $script:work) {
            Remove-Item -LiteralPath $script:work -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It "returns the distinct, sorted education-organization ids across EducationOrganization*.xml files" {
        Set-Content -LiteralPath (Join-Path $script:work 'EducationOrganization.xml') -Value @'
<InterchangeEducationOrganization>
  <StateEducationAgency><StateEducationAgencyId>1</StateEducationAgencyId></StateEducationAgency>
  <LocalEducationAgency><LocalEducationAgencyId>255901</LocalEducationAgencyId></LocalEducationAgency>
  <School><SchoolId>255901001</SchoolId></School>
  <School><SchoolId>255901001</SchoolId></School>
</InterchangeEducationOrganization>
'@
        Set-Content -LiteralPath (Join-Path $script:work 'EducationOrganization-EdPrep.xml') -Value @'
<InterchangeEducationOrganization>
  <EducatorPreparationProvider><EducatorPreparationProviderId>5</EducatorPreparationProviderId></EducatorPreparationProvider>
  <School><SchoolId>50</SchoolId></School>
  <PostSecondaryInstitution><PostSecondaryInstitutionId>6</PostSecondaryInstitutionId></PostSecondaryInstitution>
</InterchangeEducationOrganization>
'@

        $ids = Invoke-Parser -Directory $script:work

        # Distinct (255901001 appears twice), sorted ascending, across both files.
        ($ids -join ',') | Should -Be '1,5,6,50,255901,255901001'
    }

    It "covers the DS 6.1 Educator Preparation Provider hierarchy folded in from TPDM" {
        # The EPP hierarchy (its own provider, schools, and post-secondary institution) is disjoint
        # from the Grand Bend district and only appears in DS 6.1; the sandbox must claim it so the
        # populated sample loads. Each EPP edorg id must be discovered.
        Set-Content -LiteralPath (Join-Path $script:work 'EducationOrganization-EdPrep.xml') -Value @'
<InterchangeEducationOrganization>
  <EducatorPreparationProvider><EducatorPreparationProviderId>5</EducatorPreparationProviderId></EducatorPreparationProvider>
  <School><SchoolId>50</SchoolId></School>
  <School><SchoolId>60</SchoolId></School>
  <School><SchoolId>70</SchoolId></School>
  <PostSecondaryInstitution><PostSecondaryInstitutionId>6</PostSecondaryInstitutionId></PostSecondaryInstitution>
</InterchangeEducationOrganization>
'@

        $ids = Invoke-Parser -Directory $script:work

        $ids | Should -Contain 5
        $ids | Should -Contain 50
        $ids | Should -Contain 60
        $ids | Should -Contain 70
        $ids | Should -Contain 6
    }

    It "ignores ids in files that are not EducationOrganization*.xml" {
        Set-Content -LiteralPath (Join-Path $script:work 'EducationOrganization.xml') -Value @'
<InterchangeEducationOrganization><School><SchoolId>255901001</SchoolId></School></InterchangeEducationOrganization>
'@
        Set-Content -LiteralPath (Join-Path $script:work 'StudentSchoolAssociation.xml') -Value @'
<Interchange><StudentSchoolAssociation><SchoolReference><SchoolId>888888</SchoolId></SchoolReference></StudentSchoolAssociation></Interchange>
'@

        $ids = Invoke-Parser -Directory $script:work

        $ids | Should -Contain 255901001
        $ids | Should -Not -Contain 888888
    }

    It "excludes non-education-organization identifiers such as ProgramId" {
        Set-Content -LiteralPath (Join-Path $script:work 'EducationOrganization.xml') -Value @'
<InterchangeEducationOrganization>
  <LocalEducationAgency><LocalEducationAgencyId>255901</LocalEducationAgencyId></LocalEducationAgency>
  <Program><ProgramId>999999</ProgramId></Program>
</InterchangeEducationOrganization>
'@

        $ids = Invoke-Parser -Directory $script:work

        $ids | Should -Contain 255901
        $ids | Should -Not -Contain 999999
    }

    It "returns no ids when the directory has no EducationOrganization sample files" {
        $ids = Invoke-Parser -Directory $script:work
        $ids.Count | Should -Be 0
    }
}

Describe "Get-EducatorPreparationSampleFileName" {
    BeforeAll {
        $script:templatesDir = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
        Push-Location $script:templatesDir
        try {
            Import-Module (Join-Path $script:templatesDir "Template-Management.psm1") -Force
        }
        finally {
            Pop-Location
        }

        function script:Invoke-EdPrepName {
            param([string]$Directory)
            InModuleScope Template-Management -Parameters @{ dir = $Directory } {
                param($dir)
                @(Get-EducatorPreparationSampleFileName -SourceDirectory $dir)
            }
        }
    }

    BeforeEach {
        $script:work = Join-Path ([System.IO.Path]::GetTempPath()) "dms-edprep-names-$([Guid]::NewGuid().ToString('N'))"
        New-Item -ItemType Directory -Path $script:work -Force | Out-Null
    }

    AfterEach {
        if (Test-Path -LiteralPath $script:work) {
            Remove-Item -LiteralPath $script:work -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It "selects the standalone epdm files and the *-EdPrep variants but keeps AssessmentMetadata-EdPrep and core files" {
        $present = @(
            'Candidate.xml', 'Path.xml', 'PerformanceEvaluation.xml', 'ProfessionalDevelopment.xml', 'RecruitmentAndStaffing.xml',
            'AssessmentMetadata-EdPrep.xml', 'EducationOrganization-EdPrep.xml', 'EducationOrgCalendar-EdPrep.xml',
            'MasterSchedule-EdPrep.xml', 'StaffAssociation-EdPrep.xml', 'StudentGradebook-EdPrep.xml', 'Survey-EdPrep.xml',
            'Student.xml', 'StudentAssessmentSample.xml', 'EducationOrganization.xml', 'SpecialEducation.xml'
        )
        foreach ($f in $present) { Set-Content -LiteralPath (Join-Path $script:work $f) -Value '<x/>' }

        $excluded = Invoke-EdPrepName -Directory $script:work

        $expected = @(
            'Candidate.xml', 'EducationOrgCalendar-EdPrep.xml', 'EducationOrganization-EdPrep.xml', 'MasterSchedule-EdPrep.xml',
            'Path.xml', 'PerformanceEvaluation.xml', 'ProfessionalDevelopment.xml', 'RecruitmentAndStaffing.xml',
            'StaffAssociation-EdPrep.xml', 'StudentGradebook-EdPrep.xml', 'Survey-EdPrep.xml'
        ) | Sort-Object
        (@($excluded) | Sort-Object) -join ',' | Should -Be ($expected -join ',')

        # AssessmentMetadata-EdPrep is namespace-authorized metadata core StudentAssessment data
        # references, so it is kept; core files are never excluded.
        $excluded | Should -Not -Contain 'AssessmentMetadata-EdPrep.xml'
        $excluded | Should -Not -Contain 'Student.xml'
        $excluded | Should -Not -Contain 'SpecialEducation.xml'
    }

    It "only returns educator-prep files that are actually present" {
        Set-Content -LiteralPath (Join-Path $script:work 'PerformanceEvaluation.xml') -Value '<x/>'
        Set-Content -LiteralPath (Join-Path $script:work 'StudentAssessmentSample.xml') -Value '<x/>'

        $excluded = Invoke-EdPrepName -Directory $script:work

        @($excluded) | Should -Be @('PerformanceEvaluation.xml')
    }

    It "returns nothing for a sample set with no educator-prep files (e.g. DS 5.2)" {
        Set-Content -LiteralPath (Join-Path $script:work 'Student.xml') -Value '<x/>'
        Set-Content -LiteralPath (Join-Path $script:work 'EducationOrganization.xml') -Value '<x/>'

        $excluded = Invoke-EdPrepName -Directory $script:work

        $excluded.Count | Should -Be 0
    }
}

Describe "New-EducatorPreparationFilteredSampleDirectory" {
    BeforeAll {
        $script:templatesDir = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
        Push-Location $script:templatesDir
        try {
            Import-Module (Join-Path $script:templatesDir "Template-Management.psm1") -Force
        }
        finally {
            Pop-Location
        }

        function script:Invoke-FilterDir {
            param([string]$Directory)
            InModuleScope Template-Management -Parameters @{ dir = $Directory } {
                param($dir)
                New-EducatorPreparationFilteredSampleDirectory -SourceDirectory $dir
            }
        }
    }

    BeforeEach {
        $script:work = Join-Path ([System.IO.Path]::GetTempPath()) "dms-edprep-dir-$([Guid]::NewGuid().ToString('N'))"
        New-Item -ItemType Directory -Path $script:work -Force | Out-Null
    }

    AfterEach {
        foreach ($d in @($script:work, $script:result)) {
            if ($d -and (Test-Path -LiteralPath $d)) {
                Remove-Item -LiteralPath $d -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
    }

    It "returns the source directory unchanged when there is nothing to exclude" {
        Set-Content -LiteralPath (Join-Path $script:work 'Student.xml') -Value '<x/>'

        $script:result = Invoke-FilterDir -Directory $script:work

        $script:result | Should -Be $script:work
    }

    It "copies to a new directory that omits educator-prep files but keeps core and AssessmentMetadata-EdPrep" {
        $present = @('Candidate.xml', 'ProfessionalDevelopment.xml', 'Survey-EdPrep.xml',
            'AssessmentMetadata-EdPrep.xml', 'Student.xml', 'StudentAssessmentSample.xml')
        foreach ($f in $present) { Set-Content -LiteralPath (Join-Path $script:work $f) -Value '<x/>' }

        $script:result = Invoke-FilterDir -Directory $script:work

        $script:result | Should -Not -Be $script:work
        $kept = @(Get-ChildItem -LiteralPath $script:result -File | Select-Object -ExpandProperty Name | Sort-Object)
        ($kept -join ',') | Should -Be (@('AssessmentMetadata-EdPrep.xml', 'Student.xml', 'StudentAssessmentSample.xml') -join ',')
    }
}

Describe "Get-BulkLoadFailureClassification" {
    BeforeAll {
        $script:templatesDir = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
        Push-Location $script:templatesDir
        try {
            Import-Module (Join-Path $script:templatesDir "Template-Management.psm1") -Force
        }
        finally {
            Pop-Location
        }

        # Synthetic BulkLoad failure lines in the shape the loader emits (' - <code> - {'). Only the
        # presence/absence of the 'unresolved-reference' marker matters to the classifier; the caller
        # pre-filters the log to 4xx/5xx lines before passing them in.
        function script:New-UnresolvedReferenceLine {
            param([int]$Id = 1)
            " - 409 - {detail: unresolved-reference; missing dependency $Id}"
        }
        function script:New-FatalFailureLine {
            param([int]$Code = 403)
            " - $Code - {detail: fatal failure (access-denied / server error)}"
        }

        # The classifier is not exported, so reach it via InModuleScope (matching the other helpers here).
        function script:Invoke-Classification {
            param([string[]]$FailureLines = @(), [switch]$AllowUnresolvedReferences, [int]$Max = 50)
            InModuleScope Template-Management -Parameters @{
                lines = [string[]]$FailureLines
                allow = [bool]$AllowUnresolvedReferences
                max   = $Max
            } {
                param($lines, $allow, $max)
                Get-BulkLoadFailureClassification -FailureLines ([string[]]$lines) -AllowUnresolvedReferences:$allow -MaxToleratedUnresolvedReferences $max
            }
        }
    }

    It "tolerates unresolved-reference-only failures within the cap" {
        $lines = @((New-UnresolvedReferenceLine 1), (New-UnresolvedReferenceLine 2), (New-UnresolvedReferenceLine 3))
        $result = Invoke-Classification -FailureLines $lines -AllowUnresolvedReferences -Max 50
        $result.Tolerated | Should -BeTrue
        $result.UnresolvedReferenceCount | Should -Be 3
        $result.FatalFailureCount | Should -Be 0
    }

    It "tolerates exactly at the cap boundary" {
        $lines = @(1..4 | ForEach-Object { New-UnresolvedReferenceLine $_ })
        $result = Invoke-Classification -FailureLines $lines -AllowUnresolvedReferences -Max 4
        $result.Tolerated | Should -BeTrue
        $result.UnresolvedReferenceCount | Should -Be 4
    }

    It "does NOT tolerate when any fatal (non-unresolved-reference) failure is present" {
        $lines = @((New-UnresolvedReferenceLine 1), (New-UnresolvedReferenceLine 2), (New-FatalFailureLine 403))
        $result = Invoke-Classification -FailureLines $lines -AllowUnresolvedReferences -Max 50
        $result.Tolerated | Should -BeFalse
        $result.FatalFailureCount | Should -Be 1
    }

    It "does NOT tolerate a fatal 5xx failure" {
        $lines = @((New-FatalFailureLine 500))
        $result = Invoke-Classification -FailureLines $lines -AllowUnresolvedReferences -Max 50
        $result.Tolerated | Should -BeFalse
        $result.FatalFailureCount | Should -Be 1
    }

    It "does NOT tolerate unresolved references over the cap" {
        $lines = @(1..5 | ForEach-Object { New-UnresolvedReferenceLine $_ })
        $result = Invoke-Classification -FailureLines $lines -AllowUnresolvedReferences -Max 4
        $result.Tolerated | Should -BeFalse
        $result.UnresolvedReferenceCount | Should -Be 5
    }

    It "does NOT tolerate when there are no parsed failures" {
        $result = Invoke-Classification -FailureLines @() -AllowUnresolvedReferences -Max 50
        $result.Tolerated | Should -BeFalse
        $result.UnresolvedReferenceCount | Should -Be 0
    }

    It "stays strict (DS 5.2) when -AllowUnresolvedReferences is not set" {
        # Even an all-unresolved-reference set must fail when tolerance was not opted into.
        $lines = @((New-UnresolvedReferenceLine 1), (New-UnresolvedReferenceLine 2))
        $result = Invoke-Classification -FailureLines $lines -Max 50
        $result.Tolerated | Should -BeFalse
    }

    It "does NOT tolerate a non-409 line even when it contains 'unresolved-reference'" {
        # Only HTTP 409 unresolved-reference is tolerable; a 500 that merely mentions the marker is fatal.
        $lines = @(" - 500 - {detail: unresolved-reference surfaced during a server error}")
        $result = Invoke-Classification -FailureLines $lines -AllowUnresolvedReferences -Max 50
        $result.Tolerated | Should -BeFalse
        $result.FatalFailureCount | Should -Be 1
        $result.UnresolvedReferenceCount | Should -Be 0
    }

    It "does NOT tolerate a 409 that is not an unresolved-reference" {
        # A 409 without the unresolved-reference marker (e.g. a duplicate-key conflict) is fatal.
        $lines = @(" - 409 - {detail: identity conflict (duplicate key)}")
        $result = Invoke-Classification -FailureLines $lines -AllowUnresolvedReferences -Max 50
        $result.Tolerated | Should -BeFalse
        $result.FatalFailureCount | Should -Be 1
        $result.UnresolvedReferenceCount | Should -Be 0
    }
}

Describe "Get-TemplateBulkLoadTuning" {
    BeforeAll {
        $script:templatesDir = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
        Push-Location $script:templatesDir
        try {
            Import-Module (Join-Path $script:templatesDir "Template-Management.psm1") -Force
        }
        finally {
            Pop-Location
        }
    }

    It "uses conservative relational-backend limits for mssql" {
        InModuleScope Template-Management {
            $tuning = Get-TemplateBulkLoadTuning -DatabaseEngine mssql

            $tuning.Count | Should -Be 4
            $tuning.MaxConcurrentConnections | Should -Be 5
            $tuning.MaxSimultaneousRequests | Should -Be 5
            $tuning.MaxBufferedTasks | Should -Be 2
            $tuning.RetryCount | Should -Be 5
        }
    }

    It "leaves postgresql on Invoke-BulkLoad's established defaults" {
        InModuleScope Template-Management {
            $tuning = Get-TemplateBulkLoadTuning -DatabaseEngine postgresql
            $tuning.Count | Should -Be 0
        }
    }
}

Describe "Invoke-DatabaseDump" {
    BeforeAll {
        # The module imports sibling modules with paths relative to its own directory, so import
        # it with that directory as the working directory (matching how the template workflow
        # invokes it). The dump function is not exported, so it is reached via InModuleScope.
        $script:templatesDir = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
        Push-Location $script:templatesDir
        try {
            Import-Module (Join-Path $script:templatesDir "Template-Management.psm1") -Force
        }
        finally {
            Pop-Location
        }
    }

    Context "when -DatabaseEngine is mssql" {
        It "runs BACKUP DATABASE ... WITH INIT, copies the .bak out, and removes the transient in-container file" {
            InModuleScope Template-Management -Parameters @{ backupDir = $TestDrive } {
                param($backupDir)
                $backupDirectory = $backupDir
                $calls = [System.Collections.Generic.List[object]]::new()
                Mock docker {
                    $calls.Add(@($args))
                    if ($args[0] -eq 'cp') {
                        New-Item -ItemType File -Path $args[-1] -Force | Out-Null
                    }
                    $global:LASTEXITCODE = 0
                }

                Invoke-DatabaseDump -DatabaseEngine mssql -ContainerName "dms-mssql" -DatabaseName "edfi_datamanagementservice" -BackupDirectory $backupDirectory -BackupFileName "test.bak" -MssqlPassword "abcdefgh1!"

                $calls.Count | Should -Be 3
                ($calls[0] -join '|') | Should -Be (@(
                        'exec', '-e', 'SQLCMDPASSWORD=abcdefgh1!', 'dms-mssql', '/opt/mssql-tools18/bin/sqlcmd', '-S', 'localhost', '-U', 'sa', '-C', '-b', '-Q',
                        "BACKUP DATABASE [edfi_datamanagementservice] TO DISK = N'/var/opt/mssql/data/test.bak' WITH INIT;"
                    ) -join '|')
                ($calls[1] -join '|') | Should -Be (@('cp', "dms-mssql:/var/opt/mssql/data/test.bak", (Join-Path $backupDir "test.bak")) -join '|')
                ($calls[2] -join '|') | Should -Be (@('exec', 'dms-mssql', 'rm', '-f', '/var/opt/mssql/data/test.bak') -join '|')
            }
        }

        It "removes a partial in-container backup when BACKUP DATABASE fails" {
            InModuleScope Template-Management -Parameters @{ backupDir = $TestDrive } {
                param($backupDir)
                $backupDirectory = $backupDir
                $calls = [System.Collections.Generic.List[object]]::new()
                Mock docker {
                    $calls.Add(@($args))
                    $global:LASTEXITCODE = if ($args -contains '/opt/mssql-tools18/bin/sqlcmd') { 1 } else { 0 }
                }

                {
                    Invoke-DatabaseDump -DatabaseEngine mssql -ContainerName "dms-mssql" -DatabaseName "edfi_datamanagementservice" -BackupDirectory $backupDirectory -BackupFileName "backup-failure.bak" -MssqlPassword "abcdefgh1!"
                } | Should -Throw "*BACKUP DATABASE*failed*"

                $calls.Count | Should -Be 2
                ($calls[-1] -join '|') | Should -Be (@('exec', 'dms-mssql', 'rm', '-f', '/var/opt/mssql/data/backup-failure.bak') -join '|')
            }
        }

        It "removes the in-container backup when docker cp fails" {
            InModuleScope Template-Management -Parameters @{ backupDir = $TestDrive } {
                param($backupDir)
                $backupDirectory = $backupDir
                $calls = [System.Collections.Generic.List[object]]::new()
                Mock docker {
                    $calls.Add(@($args))
                    $global:LASTEXITCODE = if ($args[0] -eq 'cp') { 1 } else { 0 }
                }

                {
                    Invoke-DatabaseDump -DatabaseEngine mssql -ContainerName "dms-mssql" -DatabaseName "edfi_datamanagementservice" -BackupDirectory $backupDirectory -BackupFileName "copy-failure.bak" -MssqlPassword "abcdefgh1!"
                } | Should -Throw "*Failed to copy backup*"

                $calls.Count | Should -Be 3
                ($calls[-1] -join '|') | Should -Be (@('exec', 'dms-mssql', 'rm', '-f', '/var/opt/mssql/data/copy-failure.bak') -join '|')
            }
        }

        It "does not mask a backup failure when transient cleanup also fails" {
            InModuleScope Template-Management -Parameters @{ backupDir = $TestDrive } {
                param($backupDir)
                $backupDirectory = $backupDir
                Mock docker {
                    $global:LASTEXITCODE = 1
                }

                $previousWarningPreference = $WarningPreference
                try {
                    $WarningPreference = "Stop"
                    {
                        Invoke-DatabaseDump -DatabaseEngine mssql -ContainerName "dms-mssql" -DatabaseName "edfi_datamanagementservice" -BackupDirectory $backupDirectory -BackupFileName "double-failure.bak" -MssqlPassword "abcdefgh1!"
                    } | Should -Throw "*BACKUP DATABASE*failed*"
                }
                finally {
                    $WarningPreference = $previousWarningPreference
                }
            }
        }

        It "warns without failing a successful dump when transient cleanup fails" {
            InModuleScope Template-Management -Parameters @{ backupDir = $TestDrive } {
                param($backupDir)
                $backupDirectory = $backupDir
                $calls = [System.Collections.Generic.List[object]]::new()
                Mock Write-Warning {}
                Mock docker {
                    $calls.Add(@($args))
                    if ($args[0] -eq 'cp') {
                        New-Item -ItemType File -Path $args[-1] -Force | Out-Null
                    }
                    $global:LASTEXITCODE = if ($args.Count -gt 2 -and $args[2] -eq 'rm') { 1 } else { 0 }
                }

                {
                    Invoke-DatabaseDump -DatabaseEngine mssql -ContainerName "dms-mssql" -DatabaseName "edfi_datamanagementservice" -BackupDirectory $backupDirectory -BackupFileName "cleanup-failure.bak" -MssqlPassword "abcdefgh1!"
                } | Should -Not -Throw

                Should -Invoke Write-Warning -Times 1 -Exactly -ParameterFilter {
                    $Message -like "*Could not remove transient backup*cleanup-failure.bak*"
                }
            }
        }

        It "ignores -DatabaseSchemas because a .bak is always a full-database artifact" {
            InModuleScope Template-Management -Parameters @{ backupDir = $TestDrive } {
                param($backupDir)
                $calls = [System.Collections.Generic.List[object]]::new()
                Mock docker {
                    $calls.Add(@($args))
                    if ($args[0] -eq 'cp') {
                        New-Item -ItemType File -Path $args[-1] -Force | Out-Null
                    }
                    $global:LASTEXITCODE = 0
                }

                Invoke-DatabaseDump -DatabaseEngine mssql -ContainerName "dms-mssql" -DatabaseName "edfi_datamanagementservice" -DatabaseSchemas @("dms", "edfi") -BackupDirectory $backupDir -BackupFileName "test2.bak" -MssqlPassword "abcdefgh1!"

                # BACKUP DATABASE has no per-schema equivalent to pg_dump -n, so the schema list plays no part in the command.
                ($calls[0] -join '|') | Should -Be (@(
                        'exec', '-e', 'SQLCMDPASSWORD=abcdefgh1!', 'dms-mssql', '/opt/mssql-tools18/bin/sqlcmd', '-S', 'localhost', '-U', 'sa', '-C', '-b', '-Q',
                        "BACKUP DATABASE [edfi_datamanagementservice] TO DISK = N'/var/opt/mssql/data/test2.bak' WITH INIT;"
                    ) -join '|')
            }
        }
    }

    Context "when -DatabaseEngine is postgresql (the default)" {
        It "runs pg_dump scoped to the requested schemas, in order, and precedes the dump with a pgcrypto preamble" {
            InModuleScope Template-Management -Parameters @{ backupDir = $TestDrive } {
                param($backupDir)
                $calls = [System.Collections.Generic.List[object]]::new()
                Mock docker {
                    $calls.Add(@($args))
                    $global:LASTEXITCODE = 0
                }

                Invoke-DatabaseDump -DatabaseEngine postgresql -ContainerName "dms-postgresql" -DatabaseName "edfi_datamanagementservice" -DatabaseSchemas @("dms", "edfi") -BackupDirectory $backupDir -BackupFileName "test.sql"

                $calls.Count | Should -Be 1
                ($calls[0] -join '|') | Should -Be (@('exec', 'dms-postgresql', 'pg_dump', '-U', 'postgres', 'edfi_datamanagementservice', '-n', 'dms', '-n', 'edfi') -join '|')

                # Schema-scoped pg_dump never emits CREATE EXTENSION, but the dumped dms.uuidv5()
                # function requires pgcrypto's digest(), so the preamble must precede the dump.
                $content = Get-Content -LiteralPath (Join-Path $backupDir "test.sql") -Raw
                $content | Should -Match ([regex]::Escape('CREATE EXTENSION IF NOT EXISTS "pgcrypto";'))
            }
        }

        It "produces byte-identical pg_dump arguments whether or not -DatabaseEngine is supplied" {
            InModuleScope Template-Management -Parameters @{ backupDir = $TestDrive } {
                param($backupDir)
                $explicitCalls = [System.Collections.Generic.List[object]]::new()
                Mock docker { $explicitCalls.Add(@($args)); $global:LASTEXITCODE = 0 }
                Invoke-DatabaseDump -DatabaseEngine postgresql -ContainerName "dms-postgresql" -DatabaseName "edfi_datamanagementservice" -DatabaseSchemas @("dms") -BackupDirectory $backupDir -BackupFileName "explicit.sql"

                $defaultCalls = [System.Collections.Generic.List[object]]::new()
                Mock docker { $defaultCalls.Add(@($args)); $global:LASTEXITCODE = 0 }
                Invoke-DatabaseDump -ContainerName "dms-postgresql" -DatabaseName "edfi_datamanagementservice" -DatabaseSchemas @("dms") -BackupDirectory $backupDir -BackupFileName "default.sql"

                $defaultCalls.Count | Should -Be $explicitCalls.Count
                ($defaultCalls[0] -join '|') | Should -Be ($explicitCalls[0] -join '|')
            }
        }
    }
}

Describe "Restore-TemplatePackage" {
    BeforeAll {
        $script:templatesDir = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
        Push-Location $script:templatesDir
        try {
            Import-Module (Join-Path $script:templatesDir "Template-Management.psm1") -Force
        }
        finally {
            Pop-Location
        }

        # Restore-TemplatePackage only cares that the package directory contains exactly one
        # .nupkg (a zip archive) with exactly one artifact file inside it, so a minimal package
        # is built directly rather than driving the real dotnet pack/csproj pipeline.
        function script:New-FakeTemplatePackage {
            param(
                [Parameter(Mandatory = $true)]
                [string]$Directory,

                [Parameter(Mandatory = $true)]
                [string]$ArtifactFileName,

                [string]$PackageFileName = "FakeTemplate.nupkg"
            )

            $stagingDir = Join-Path $Directory "staging"
            New-Item -ItemType Directory -Path $stagingDir -Force | Out-Null
            $artifactPath = Join-Path $stagingDir $ArtifactFileName
            Set-Content -LiteralPath $artifactPath -Value "fake artifact content"

            $zipPath = Join-Path $Directory "package.zip"
            Compress-Archive -Path $artifactPath -DestinationPath $zipPath -Force
            $nupkgPath = Join-Path $Directory $PackageFileName
            Copy-Item -LiteralPath $zipPath -Destination $nupkgPath -Force

            return $nupkgPath
        }
    }

    BeforeEach {
        $script:packageDir = Join-Path $TestDrive ([Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $script:packageDir -Force | Out-Null
    }

    Context "when -DatabaseEngine is mssql" {
        It "drops an existing target database, then restores via RESTORE DATABASE ... WITH MOVE ..., REPLACE" {
            New-FakeTemplatePackage -Directory $script:packageDir -ArtifactFileName "backup.bak" -PackageFileName "MyTemplate.nupkg" | Out-Null

            $calls = [System.Collections.Generic.List[object]]::new()
            Mock docker -ModuleName Template-Management {
                $calls.Add(@($args))
                $joined = $args -join ' '
                $global:LASTEXITCODE = 0
                if ($joined -match 'RESTORE FILELISTONLY') {
                    return @('MyDb|/var/opt/mssql/data/MyDb.mdf|D|PRIMARY', 'MyDb_log|/var/opt/mssql/data/MyDb_log.ldf|L|NULL')
                }
                if ($joined -match 'DB_ID') {
                    return '1'
                }
            }

            $result = Restore-TemplatePackage -PackageDirectory $script:packageDir -DatabaseName "testdb" -DatabaseEngine mssql -ContainerName "dms-mssql" -MssqlPassword "abcdefgh1!"

            $result | Should -Be "MyTemplate.nupkg"
            $calls.Count | Should -Be 7

            # [0] docker cp copies the extracted .bak into the container at a generated path;
            # capture that path so the later RESTORE FROM DISK clause can be pinned against it.
            $calls[0][0] | Should -Be 'cp'
            $copyDestination = $calls[0][2]
            $copyDestination | Should -Match '^dms-mssql:/var/opt/mssql/data/template-restore-.*\.bak$'
            $containerBakPath = ($copyDestination -split ':', 2)[1]

            ($calls[1] -join '|') | Should -Be (@(
                    'exec', '-e', 'SQLCMDPASSWORD=abcdefgh1!', 'dms-mssql', '/opt/mssql-tools18/bin/sqlcmd', '-S', 'localhost', '-U', 'sa', '-d', 'master', '-C', '-b', '-h', '-1', '-W', '-Q',
                    "SET NOCOUNT ON; SELECT CASE WHEN DB_ID(N'testdb') IS NOT NULL THEN 1 ELSE 0 END;"
                ) -join '|')

            ($calls[2] -join '|') | Should -Be (@(
                    'exec', '-e', 'SQLCMDPASSWORD=abcdefgh1!', 'dms-mssql', '/opt/mssql-tools18/bin/sqlcmd', '-S', 'localhost', '-U', 'sa', '-d', 'master', '-C', '-b', '-Q',
                    "ALTER DATABASE [testdb] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [testdb];"
                ) -join '|')

            ($calls[3] -join '|') | Should -Be (@(
                    'exec', '-e', 'SQLCMDPASSWORD=abcdefgh1!', 'dms-mssql', '/opt/mssql-tools18/bin/sqlcmd', '-S', 'localhost', '-U', 'sa', '-d', 'master', '-C', '-b', '-h', '-1', '-W', '-s', '|', '-Q',
                    "SET NOCOUNT ON; RESTORE FILELISTONLY FROM DISK = N'$containerBakPath';"
                ) -join '|')

            ($calls[4] -join '|') | Should -Be (@(
                    'exec', '-e', 'SQLCMDPASSWORD=abcdefgh1!', 'dms-mssql', '/opt/mssql-tools18/bin/sqlcmd', '-S', 'localhost', '-U', 'sa', '-d', 'master', '-C', '-b', '-Q',
                    "RESTORE DATABASE [testdb] FROM DISK = N'$containerBakPath' WITH MOVE N'MyDb' TO N'/var/opt/mssql/data/testdb.mdf', MOVE N'MyDb_log' TO N'/var/opt/mssql/data/testdb_log.ldf', REPLACE;"
                ) -join '|')

            $expectedReseedSql = @'
SET NOCOUNT ON;
UPDATE [dms].[DataStoreIdentity]
SET [SourceIdentity] = NEWID()
WHERE [DataStoreIdentitySingletonId] = 1;
IF @@ROWCOUNT <> 1
    THROW 50000, N'Restored database is missing the dms.DataStoreIdentity singleton row.', 1;
'@
            ($calls[5] -join '|') | Should -Be (@(
                    'exec', '-e', 'SQLCMDPASSWORD=abcdefgh1!', 'dms-mssql', '/opt/mssql-tools18/bin/sqlcmd', '-S', 'localhost', '-U', 'sa', '-d', 'testdb', '-C', '-b', '-Q',
                    $expectedReseedSql
                ) -join '|')

            # The transient in-container backup is removed once the restore succeeds.
            ($calls[6] -join '|') | Should -Be (@('exec', 'dms-mssql', 'rm', '-f', $containerBakPath) -join '|')
        }

        It "skips the DROP DATABASE step when the target database does not already exist" {
            New-FakeTemplatePackage -Directory $script:packageDir -ArtifactFileName "backup.bak" -PackageFileName "MyTemplate.nupkg" | Out-Null

            $calls = [System.Collections.Generic.List[object]]::new()
            Mock docker -ModuleName Template-Management {
                $calls.Add(@($args))
                $joined = $args -join ' '
                $global:LASTEXITCODE = 0
                if ($joined -match 'RESTORE FILELISTONLY') {
                    return @('MyDb|/var/opt/mssql/data/MyDb.mdf|D|PRIMARY', 'MyDb_log|/var/opt/mssql/data/MyDb_log.ldf|L|NULL')
                }
                if ($joined -match 'DB_ID') {
                    return '0'
                }
            }

            Restore-TemplatePackage -PackageDirectory $script:packageDir -DatabaseName "testdb" -DatabaseEngine mssql -ContainerName "dms-mssql" -MssqlPassword "abcdefgh1!" | Out-Null

            $calls.Count | Should -Be 6
            ($calls | Where-Object { ($_ -join ' ') -match 'SET SINGLE_USER' }).Count | Should -Be 0
            ($calls[2] -join ' ') | Should -Match 'RESTORE FILELISTONLY'
            ($calls[3] -join ' ') | Should -Match 'RESTORE DATABASE \[testdb\]'
            ($calls[4] -join ' ') | Should -Match 'UPDATE \[dms\]\.\[DataStoreIdentity\]'
            ($calls[5] -join ' ') | Should -Match '^exec dms-mssql rm -f /var/opt/mssql/data/template-restore-.*\.bak$'
        }

        It "removes the transient in-container backup even when the restore fails" {
            New-FakeTemplatePackage -Directory $script:packageDir -ArtifactFileName "backup.bak" -PackageFileName "MyTemplate.nupkg" | Out-Null

            $calls = [System.Collections.Generic.List[object]]::new()
            Mock docker -ModuleName Template-Management {
                $calls.Add(@($args))
                $joined = $args -join ' '
                $global:LASTEXITCODE = 0
                if ($joined -match 'RESTORE FILELISTONLY') {
                    return @('MyDb|/var/opt/mssql/data/MyDb.mdf|D|PRIMARY', 'MyDb_log|/var/opt/mssql/data/MyDb_log.ldf|L|NULL')
                }
                if ($joined -match 'DB_ID') {
                    return '0'
                }
                if ($joined -match 'RESTORE DATABASE') {
                    $global:LASTEXITCODE = 1
                }
            }

            { Restore-TemplatePackage -PackageDirectory $script:packageDir -DatabaseName "testdb" -DatabaseEngine mssql -ContainerName "dms-mssql" -MssqlPassword "abcdefgh1!" } |
                Should -Throw "*Restore of*failed*"

            # Failed restores must not accumulate GUID-named .bak files in /var/opt/mssql/data.
            ($calls[-1] -join ' ') | Should -Match '^exec dms-mssql rm -f /var/opt/mssql/data/template-restore-.*\.bak$'
        }

        It "emits one MOVE clause per file for a multi-file backup (secondary data file, extra filegroup, multiple logs)" {
            New-FakeTemplatePackage -Directory $script:packageDir -ArtifactFileName "backup.bak" -PackageFileName "MyTemplate.nupkg" | Out-Null

            $calls = [System.Collections.Generic.List[object]]::new()
            Mock docker -ModuleName Template-Management {
                $calls.Add(@($args))
                $joined = $args -join ' '
                $global:LASTEXITCODE = 0
                if ($joined -match 'RESTORE FILELISTONLY') {
                    return @(
                        'MyDb|/var/opt/mssql/data/MyDb.mdf|D|PRIMARY',
                        'MyDb2|/var/opt/mssql/data/MyDb2.ndf|D|SECONDARY',
                        'MyDb_log|/var/opt/mssql/data/MyDb_log.ldf|L|NULL',
                        'MyDb_log2|/var/opt/mssql/data/MyDb_log2.ldf|L|NULL'
                    )
                }
                if ($joined -match 'DB_ID') {
                    return '0'
                }
            }

            Restore-TemplatePackage -PackageDirectory $script:packageDir -DatabaseName "testdb" -DatabaseEngine mssql -ContainerName "dms-mssql" -MssqlPassword "abcdefgh1!" | Out-Null

            $containerBakPath = ($calls[0][2] -split ':', 2)[1]

            # The primary data file and first log keep the plain $DatabaseName-derived names (matching
            # the single-file case); the secondary data file and second log get names suffixed with
            # their own logical name so every file lands at its own deterministic path.
            ($calls[3] -join '|') | Should -Be (@(
                    'exec', '-e', 'SQLCMDPASSWORD=abcdefgh1!', 'dms-mssql', '/opt/mssql-tools18/bin/sqlcmd', '-S', 'localhost', '-U', 'sa', '-d', 'master', '-C', '-b', '-Q',
                    "RESTORE DATABASE [testdb] FROM DISK = N'$containerBakPath' WITH MOVE N'MyDb' TO N'/var/opt/mssql/data/testdb.mdf', MOVE N'MyDb2' TO N'/var/opt/mssql/data/testdb_MyDb2.ndf', MOVE N'MyDb_log' TO N'/var/opt/mssql/data/testdb_log.ldf', MOVE N'MyDb_log2' TO N'/var/opt/mssql/data/testdb_MyDb_log2.ldf', REPLACE;"
                ) -join '|')
        }

        It "throws when an additional file's logical name contains characters unsafe for a physical path" {
            New-FakeTemplatePackage -Directory $script:packageDir -ArtifactFileName "backup.bak" -PackageFileName "MyTemplate.nupkg" | Out-Null

            Mock docker -ModuleName Template-Management {
                $joined = $args -join ' '
                $global:LASTEXITCODE = 0
                if ($joined -match 'RESTORE FILELISTONLY') {
                    return @(
                        'MyDb|/var/opt/mssql/data/MyDb.mdf|D|PRIMARY',
                        "MyDb'; DROP TABLE x --|/var/opt/mssql/data/MyDb2.ndf|D|SECONDARY",
                        'MyDb_log|/var/opt/mssql/data/MyDb_log.ldf|L|NULL'
                    )
                }
                if ($joined -match 'DB_ID') {
                    return '0'
                }
            }

            { Restore-TemplatePackage -PackageDirectory $script:packageDir -DatabaseName "testdb" -DatabaseEngine mssql -ContainerName "dms-mssql" -MssqlPassword "abcdefgh1!" } |
                Should -Throw "*contains unsupported characters*"
        }
    }

    Context "when -DatabaseEngine is postgresql (the default)" {
        It "creates restore-global roles, drops and recreates the database, copies the dump in, and restores via psql -f" {
            New-FakeTemplatePackage -Directory $script:packageDir -ArtifactFileName "dump.sql" -PackageFileName "MyPgTemplate.nupkg" | Out-Null

            $calls = [System.Collections.Generic.List[object]]::new()
            Mock docker -ModuleName Template-Management {
                $calls.Add(@($args))
                $global:LASTEXITCODE = 0
            }

            $result = Restore-TemplatePackage -PackageDirectory $script:packageDir -DatabaseName "testdb" -DatabaseEngine postgresql -ContainerName "dms-postgresql"

            $result | Should -Be "MyPgTemplate.nupkg"
            $calls.Count | Should -Be 7
            ($calls[0][0..9] -join '|') | Should -Be (@('exec', 'dms-postgresql', 'psql', '-U', 'postgres', '-d', 'postgres', '-v', 'ON_ERROR_STOP=1', '-c') -join '|')
            $calls[0][10] | Should -Match 'CREATE ROLE "edfi_dms_enqueue_owner" WITH NOLOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;'
            $calls[0][10] | Should -Match 'pg_catalog.pg_auth_members memberships'
            ($calls[1] -join '|') | Should -Be (@('exec', 'dms-postgresql', 'psql', '-U', 'postgres', '-d', 'postgres', '-v', 'ON_ERROR_STOP=1', '-c', "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = 'testdb' AND pid <> pg_backend_pid();") -join '|')
            ($calls[2] -join '|') | Should -Be (@('exec', 'dms-postgresql', 'psql', '-U', 'postgres', '-d', 'postgres', '-v', 'ON_ERROR_STOP=1', '-c', 'DROP DATABASE IF EXISTS testdb;') -join '|')
            ($calls[3] -join '|') | Should -Be (@('exec', 'dms-postgresql', 'psql', '-U', 'postgres', '-d', 'postgres', '-v', 'ON_ERROR_STOP=1', '-c', 'CREATE DATABASE testdb;') -join '|')
            $calls[4][0] | Should -Be 'cp'
            ($calls[5] -join '|') | Should -Be (@('exec', 'dms-postgresql', 'psql', '-U', 'postgres', '-d', 'testdb', '-v', 'ON_ERROR_STOP=1', '-f', '/tmp/template-restore.sql') -join '|')
            ($calls[6][0..9] -join '|') | Should -Be (@('exec', 'dms-postgresql', 'psql', '-U', 'postgres', '-d', 'testdb', '-v', 'ON_ERROR_STOP=1', '-c') -join '|')
            $calls[6][10] | Should -Match 'UPDATE "dms"\."DataStoreIdentity"'
            $calls[6][10] | Should -Match 'SET "SourceIdentity" = gen_random_uuid\(\)'
            $calls[6][10] | Should -Match 'GET DIAGNOSTICS _updated_count = ROW_COUNT'
        }

        It "fails before dropping the database when restore-global role setup fails" {
            New-FakeTemplatePackage -Directory $script:packageDir -ArtifactFileName "dump.sql" -PackageFileName "MyPgTemplate.nupkg" | Out-Null

            $calls = [System.Collections.Generic.List[object]]::new()
            Mock docker -ModuleName Template-Management {
                $calls.Add(@($args))
                $global:LASTEXITCODE = 1
            }

            { Restore-TemplatePackage -PackageDirectory $script:packageDir -DatabaseName "testdb" -DatabaseEngine postgresql -ContainerName "dms-postgresql" } |
                Should -Throw "*Failed to ensure PostgreSQL role 'edfi_dms_enqueue_owner' exists*"

            $calls.Count | Should -Be 1
            ($calls[0][0..9] -join '|') | Should -Be (@('exec', 'dms-postgresql', 'psql', '-U', 'postgres', '-d', 'postgres', '-v', 'ON_ERROR_STOP=1', '-c') -join '|')
        }

        It "ignores companion attestation packages when locating the template package" {
            # After an attested build, "<Id>.Attestation.<version>.nupkg" sits beside the
            # template package; alphabetically it sorts FIRST here, so without the exclusion
            # the restore would pick the companion instead of the template.
            Set-Content -LiteralPath (Join-Path $script:packageDir "A.Template.Attestation.1.0.1.nupkg") -Value "not a template package"
            New-FakeTemplatePackage -Directory $script:packageDir -ArtifactFileName "dump.sql" -PackageFileName "MyPgTemplate.nupkg" | Out-Null

            Mock docker -ModuleName Template-Management { $global:LASTEXITCODE = 0 }

            $result = Restore-TemplatePackage -PackageDirectory $script:packageDir -DatabaseName "testdb" -DatabaseEngine postgresql -ContainerName "dms-postgresql"
            $result | Should -Be "MyPgTemplate.nupkg"
        }

        It "produces byte-identical restore arguments whether or not -DatabaseEngine is supplied" {
            $explicitDir = Join-Path $TestDrive ([Guid]::NewGuid().ToString('N'))
            New-Item -ItemType Directory -Path $explicitDir -Force | Out-Null
            New-FakeTemplatePackage -Directory $explicitDir -ArtifactFileName "dump.sql" -PackageFileName "Explicit.nupkg" | Out-Null

            $defaultDir = Join-Path $TestDrive ([Guid]::NewGuid().ToString('N'))
            New-Item -ItemType Directory -Path $defaultDir -Force | Out-Null
            New-FakeTemplatePackage -Directory $defaultDir -ArtifactFileName "dump.sql" -PackageFileName "Default.nupkg" | Out-Null

            $explicitCalls = [System.Collections.Generic.List[object]]::new()
            Mock docker -ModuleName Template-Management { $explicitCalls.Add(@($args)); $global:LASTEXITCODE = 0 }
            Restore-TemplatePackage -PackageDirectory $explicitDir -DatabaseName "paritydb" -DatabaseEngine postgresql -ContainerName "dms-postgresql" | Out-Null

            $defaultCalls = [System.Collections.Generic.List[object]]::new()
            Mock docker -ModuleName Template-Management { $defaultCalls.Add(@($args)); $global:LASTEXITCODE = 0 }
            Restore-TemplatePackage -PackageDirectory $defaultDir -DatabaseName "paritydb" -ContainerName "dms-postgresql" | Out-Null

            # Each call is extracted into its own randomly named temp directory, so normalize that
            # GUID segment out of the local-side source path before comparing call shapes.
            $normalizeGuid = { param($callArgs) @($callArgs | ForEach-Object { $_ -replace 'template-restore-[0-9a-fA-F]{32}', 'template-restore-<guid>' }) }

            $defaultCalls.Count | Should -Be $explicitCalls.Count
            for ($i = 0; $i -lt $explicitCalls.Count; $i++) {
                ((& $normalizeGuid $defaultCalls[$i]) -join '|') | Should -Be ((& $normalizeGuid $explicitCalls[$i]) -join '|')
            }
        }
    }
}

Describe "Get-UserSchemaNames" {
    BeforeAll {
        $script:templatesDir = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
        Push-Location $script:templatesDir
        try {
            Import-Module (Join-Path $script:templatesDir "Template-Management.psm1") -Force
        }
        finally {
            Pop-Location
        }
    }

    Context "when -DatabaseEngine is mssql" {
        It "queries sys.schemas, excluding the built-in schemas and the db_* fixed-role schemas" {
            $calls = [System.Collections.Generic.List[object]]::new()
            Mock docker -ModuleName Template-Management {
                $calls.Add(@($args))
                $global:LASTEXITCODE = 0
                return @('dms', 'edfi')
            }

            $schemas = Get-UserSchemaNames -DatabaseEngine mssql -ContainerName "dms-mssql" -DatabaseName "testdb" -MssqlPassword "abcdefgh1!"

            $schemas | Should -Be @('dms', 'edfi')
            $calls.Count | Should -Be 1
            ($calls[0] -join '|') | Should -Be (@(
                    'exec', '-e', 'SQLCMDPASSWORD=abcdefgh1!', 'dms-mssql', '/opt/mssql-tools18/bin/sqlcmd', '-S', 'localhost', '-U', 'sa', '-d', 'testdb', '-C', '-b', '-h', '-1', '-W', '-Q',
                    "SET NOCOUNT ON; SELECT name FROM sys.schemas WHERE name NOT IN ('dbo', 'guest', 'sys', 'INFORMATION_SCHEMA') AND name NOT LIKE 'db[_]%' ORDER BY name;"
                ) -join '|')
        }
    }

    Context "when -DatabaseEngine is postgresql (the default)" {
        It "queries pg_namespace, excluding pg_* system schemas, information_schema, and public" {
            $calls = [System.Collections.Generic.List[object]]::new()
            Mock docker -ModuleName Template-Management {
                $calls.Add(@($args))
                $global:LASTEXITCODE = 0
                return @('dms', 'edfi')
            }

            $schemas = Get-UserSchemaNames -ContainerName "dms-postgresql" -DatabaseName "testdb"

            $schemas | Should -Be @('dms', 'edfi')
            $calls.Count | Should -Be 1
            ($calls[0] -join '|') | Should -Be (@(
                    'exec', 'dms-postgresql', 'psql', '-U', 'postgres', '-d', 'testdb', '-tA', '-c',
                    "SELECT nspname FROM pg_namespace WHERE nspname !~ '^pg_' AND nspname NOT IN ('information_schema', 'public') ORDER BY nspname;"
                ) -join '|')
        }

        It "issues the identical query whether or not -DatabaseEngine is supplied" {
            $explicitCalls = [System.Collections.Generic.List[object]]::new()
            Mock docker -ModuleName Template-Management { $explicitCalls.Add(@($args)); $global:LASTEXITCODE = 0; return @('dms') }
            Get-UserSchemaNames -DatabaseEngine postgresql -ContainerName "dms-postgresql" -DatabaseName "testdb" | Out-Null

            $defaultCalls = [System.Collections.Generic.List[object]]::new()
            Mock docker -ModuleName Template-Management { $defaultCalls.Add(@($args)); $global:LASTEXITCODE = 0; return @('dms') }
            Get-UserSchemaNames -ContainerName "dms-postgresql" -DatabaseName "testdb" | Out-Null

            $defaultCalls.Count | Should -Be $explicitCalls.Count
            ($defaultCalls[0] -join '|') | Should -Be ($explicitCalls[0] -join '|')
        }
    }

    It "throws when no user schemas are discovered, regardless of engine" {
        Mock docker -ModuleName Template-Management { $global:LASTEXITCODE = 0; return @() }

        { Get-UserSchemaNames -ContainerName "dms-postgresql" -DatabaseName "testdb" } | Should -Throw "*does not appear to be provisioned*"
    }
}

Describe "Add-TemplateDataStore engine dispatch" {
    BeforeAll {
        $script:templatesDir = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
        Push-Location $script:templatesDir
        try {
            Import-Module (Join-Path $script:templatesDir "Template-Management.psm1") -Force
        }
        finally {
            Pop-Location
        }
    }

    It "registers an MSSQL connection string when an MSSQL build finds no existing data store" {
        InModuleScope Template-Management {
            $credential = [System.Management.Automation.PSCredential]::new(
                "postgres",
                [System.Security.SecureString]::new()
            )
            Mock ConvertTo-PostgresCredential { return $credential }
            Mock New-DataStoreConnectionString { return "mssql-connection-string" }
            Mock Add-DataStore { return 42 }

            $result = Add-TemplateDataStore `
                -CmsUrl "http://cms" `
                -AccessToken "cms-token" `
                -DatabaseName "template_db" `
                -DatabaseEngine "mssql" `
                -PostgresPassword "pg-secret" `
                -MssqlPassword "mssql-secret"

            $result | Should -Be 42
            Should -Invoke ConvertTo-PostgresCredential -Times 1 -Exactly -ParameterFilter {
                $UserName -eq "postgres" -and $Secret -eq "pg-secret"
            }
            Should -Invoke New-DataStoreConnectionString -Times 1 -Exactly -ParameterFilter {
                $DatabaseEngine -eq "mssql" -and
                $DbHost -eq "dms-mssql" -and
                $Port -eq 1433 -and
                $Username -eq "sa" -and
                $Password -eq "mssql-secret" -and
                $DatabaseName -eq "template_db"
            }
            Should -Invoke Add-DataStore -Times 1 -Exactly -ParameterFilter {
                $CmsUrl -eq "http://cms" -and
                $AccessToken -eq "cms-token" -and
                $PostgresCredential -eq $credential -and
                $PostgresDbName -eq "template_db" -and
                $ConnectionString -eq "mssql-connection-string"
            }
        }
    }

    It "preserves the PostgreSQL registration path when the engine is omitted" {
        InModuleScope Template-Management {
            $credential = [System.Management.Automation.PSCredential]::new(
                "postgres",
                [System.Security.SecureString]::new()
            )
            Mock ConvertTo-PostgresCredential { return $credential }
            Mock New-DataStoreConnectionString { throw "PostgreSQL registration must not build an explicit MSSQL connection string." }
            Mock Add-DataStore { return 84 }

            $result = Add-TemplateDataStore `
                -CmsUrl "http://cms" `
                -AccessToken "cms-token" `
                -DatabaseName "template_db" `
                -PostgresPassword "pg-secret" `
                -MssqlPassword "unused"

            $result | Should -Be 84
            Should -Invoke New-DataStoreConnectionString -Times 0 -Exactly
            Should -Invoke Add-DataStore -Times 1 -Exactly -ParameterFilter {
                $CmsUrl -eq "http://cms" -and
                $AccessToken -eq "cms-token" -and
                $PostgresCredential -eq $credential -and
                $PostgresDbName -eq "template_db" -and
                [string]::IsNullOrWhiteSpace($ConnectionString)
            }
        }
    }
}

Describe "Build-TemplateNuGetPackage package identity derivation" {
    BeforeAll {
        # Package identity derivation (engine token + artifact extension substitution) is internal
        # to Build-TemplateNuGetPackage, so it is exercised through the real, repo-shipped .psd1
        # settings files with the heavy externals (dump, csproj authoring, dotnet pack) mocked out.
        $script:templatesDir = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
        Push-Location $script:templatesDir
        try {
            Import-Module (Join-Path $script:templatesDir "Template-Management.psm1") -Force
        }
        finally {
            Pop-Location
        }
    }

    It "derives the <Engine> package Id/Title/Description/PackageProjectName and <Extension> DatabaseBackupName for <TemplateType>/<StandardVersion>" -ForEach @(
        @{ TemplateType = 'Minimal'; StandardVersion = '5.2.0'; Engine = 'postgresql'; EngineToken = 'PostgreSql'; Extension = 'sql' }
        @{ TemplateType = 'Minimal'; StandardVersion = '5.2.0'; Engine = 'mssql'; EngineToken = 'MsSql'; Extension = 'bak' }
        @{ TemplateType = 'Minimal'; StandardVersion = '6.1.0'; Engine = 'postgresql'; EngineToken = 'PostgreSql'; Extension = 'sql' }
        @{ TemplateType = 'Minimal'; StandardVersion = '6.1.0'; Engine = 'mssql'; EngineToken = 'MsSql'; Extension = 'bak' }
        @{ TemplateType = 'Populated'; StandardVersion = '5.2.0'; Engine = 'postgresql'; EngineToken = 'PostgreSql'; Extension = 'sql' }
        @{ TemplateType = 'Populated'; StandardVersion = '5.2.0'; Engine = 'mssql'; EngineToken = 'MsSql'; Extension = 'bak' }
        @{ TemplateType = 'Populated'; StandardVersion = '6.1.0'; Engine = 'postgresql'; EngineToken = 'PostgreSql'; Extension = 'sql' }
        @{ TemplateType = 'Populated'; StandardVersion = '6.1.0'; Engine = 'mssql'; EngineToken = 'MsSql'; Extension = 'bak' }
    ) {
        $configPath = Join-Path $script:templatesDir "${TemplateType}TemplateSettings.psd1"

        InModuleScope Template-Management -Parameters @{
            configPath      = $configPath
            templateType    = $TemplateType
            standardVersion = $StandardVersion
            engine          = $Engine
            engineToken     = $EngineToken
            extension       = $Extension
        } {
            param($configPath, $templateType, $standardVersion, $engine, $engineToken, $extension)

            Mock Invoke-DatabaseDump {}
            Mock New-DatabaseTemplateCsproj {}
            Mock Build-NuGetPackage {}
            Mock Get-TemplateSourceCatalogFacts { [pscustomobject]@{} }
            Mock Assert-DmsOnlyTemplateSource { [pscustomobject]@{ HasAuth = $false; ResourceSchemaNames = [string[]]@(); TrackedChangesProjectNames = [string[]]@(); ProjectSchemaNames = [string[]]@("edfi") } }
            Mock Write-TemplateRestoreManifest { "restore-manifest.json" }
            Mock Add-FileToCsProjForNuget {}

            Build-TemplateNuGetPackage -ConfigFilePath $configPath -StandardVersion $standardVersion -PackageVersion "1.0.0" -TemplateKind $templateType -DatabaseEngine $engine

            $expectedId = "EdFi.Api.$templateType.Template.$engineToken.$standardVersion"
            $expectedBackupName = "EdFi.Api.$templateType.Template.$engineToken.$standardVersion.$extension"
            $expectedProjectName = "EdFi.Api.$templateType.Template.$engineToken.$standardVersion.csproj"
            $expectedDescription = "EdFi Dms $templateType Template Database for $engineToken"

            Should -Invoke New-DatabaseTemplateCsproj -Times 1 -Exactly -ParameterFilter {
                $Config.Id -eq $expectedId -and
                $Config.Title -eq $expectedId -and
                $Config.Description -eq $expectedDescription -and
                $Config.DatabaseBackupName -eq $expectedBackupName -and
                $Config.PackageProjectName -eq $expectedProjectName
            }
        }
    }

    It "defaults -DatabaseEngine to postgresql, producing the PostgreSql token and .sql extension, when the parameter is omitted" {
        InModuleScope Template-Management -Parameters @{ configPath = (Join-Path $script:templatesDir "MinimalTemplateSettings.psd1") } {
            param($configPath)
            Mock Invoke-DatabaseDump {}
            Mock New-DatabaseTemplateCsproj {}
            Mock Build-NuGetPackage {}
            Mock Get-TemplateSourceCatalogFacts { [pscustomobject]@{} }
            Mock Assert-DmsOnlyTemplateSource { [pscustomobject]@{ HasAuth = $false; ResourceSchemaNames = [string[]]@(); TrackedChangesProjectNames = [string[]]@(); ProjectSchemaNames = [string[]]@("edfi") } }
            Mock Write-TemplateRestoreManifest { "restore-manifest.json" }
            Mock Add-FileToCsProjForNuget {}

            Build-TemplateNuGetPackage -ConfigFilePath $configPath -StandardVersion "5.2.0" -PackageVersion "1.0.0" -TemplateKind "Minimal"

            Should -Invoke New-DatabaseTemplateCsproj -Times 1 -Exactly -ParameterFilter {
                $Config.Id -eq 'EdFi.Api.Minimal.Template.PostgreSql.5.2.0' -and
                $Config.DatabaseBackupName -eq 'EdFi.Api.Minimal.Template.PostgreSql.5.2.0.sql'
            }

            # The dump must also see the default resolved to postgresql, not left blank/unset.
            Should -Invoke Invoke-DatabaseDump -Times 1 -Exactly -ParameterFilter { $DatabaseEngine -eq 'postgresql' }
        }
    }
}

Describe "Get-TemplateSourceCatalogFacts" {
    BeforeAll {
        $script:templatesDir = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
        Push-Location $script:templatesDir
        try {
            Import-Module (Join-Path $script:templatesDir "Template-Management.psm1") -Force
        }
        finally {
            Pop-Location
        }
    }

    It "reads the PostgreSQL manifest facts from the live catalog and scopes the artifact inventory to the dumped schemas plus public" {
        InModuleScope Template-Management {
            $calls = [System.Collections.Generic.List[object]]::new()
            Mock docker {
                $calls.Add(@($args))
                $query = [string]$args[-1]
                $global:LASTEXITCODE = 0
                if ($query -match 'EffectiveSchema') { return "1.0.0|$('ab' * 32)|42|$('cd' * 32)" }
                if ($query -match 'server_version') { return '16.8' }
                if ($query -match 'data_type') { return 'DocumentCache|jsonb' }
                if ($query -match 'relkind') {
                    return @(
                        'dms|Document|table',
                        'dms|EffectiveSchema|table',
                        'edfi|School|table',
                        'tracked_changes_edfi|School|table'
                    )
                }
                if ($query -match 'pg_namespace') { return @('dms', 'edfi', 'public', 'tracked_changes_edfi') }
            }

            $facts = Get-TemplateSourceCatalogFacts -DatabaseEngine postgresql -DatabaseName "edfi_datamanagementservice" -ArtifactSchemaName @("dms")

            $facts.ApiSchemaFormatVersion | Should -Be "1.0.0"
            $facts.EffectiveSchemaHash | Should -Be ("ab" * 32)
            $facts.ResourceKeyCount | Should -Be 42
            $facts.ResourceKeySeedHashB64 | Should -Be ([System.Convert]::ToBase64String([System.Convert]::FromHexString(("cd" * 32))))
            $facts.EngineVersion | Should -Be "16.8"
            $facts.DatabaseCompatibilityLevel | Should -BeNullOrEmpty
            $facts.DocumentJsonColumnType | Should -Be "jsonb"

            @($facts.FullInventory.schemas).Count | Should -Be 4

            # Artifact scope: dumped schema (dms) plus always-present public, no principals.
            $artifactSchemaNames = @($facts.ArtifactInventory.schemas | ForEach-Object { $_.schemaName })
            $artifactSchemaNames | Should -Be @("dms", "public")
            @($facts.ArtifactInventory.principals).Count | Should -Be 0

            # Transport shape: catalog queries go through the standard psql idiom.
            $psqlCall = @($calls | Where-Object { $_[2] -eq 'psql' } | Select-Object -First 1)[0]
            ($psqlCall[0..8] -join '|') | Should -Be (@('exec', 'dms-postgresql', 'psql', '-U', 'postgres', '-d', 'edfi_datamanagementservice', '-tA', '-c') -join '|')
        }
    }

    It "reads the MSSQL manifest facts including compatibility level and principals, with the artifact scoped to the whole database" {
        InModuleScope Template-Management {
            $calls = [System.Collections.Generic.List[object]]::new()
            Mock docker {
                $calls.Add(@($args))
                $query = [string]$args[-1]
                $global:LASTEXITCODE = 0
                if ($query -match 'EffectiveSchema') { return "1.0.0|$('ab' * 32)|42|$('cd' * 32)" }
                if ($query -match 'ProductVersion') { return '17.0.900.7' }
                if ($query -match 'compatibility_level') { return '170' }
                if ($query -match 'sys\.columns') { return 'DocumentCache|nvarchar' }
                if ($query -match 'sys\.objects') {
                    return @('dms|Document|table', 'edfi|School|table', 'tracked_changes_edfi|School|table')
                }
                if ($query -match 'sys\.schemas') { return @('dbo', 'dms', 'edfi', 'tracked_changes_edfi') }
                if ($query -match 'database_principals') { return @() }
            }

            $facts = Get-TemplateSourceCatalogFacts -DatabaseEngine mssql -DatabaseName "edfi_datamanagementservice"

            $facts.EngineVersion | Should -Be "17.0.900.7"
            $facts.DatabaseCompatibilityLevel | Should -Be 170
            $facts.DocumentJsonColumnType | Should -Be "nvarchar"

            # A .bak carries the whole database: artifact scope equals the full inventory,
            # including the always-present dbo entry and the (empty) principals list.
            $artifactSchemaNames = @($facts.ArtifactInventory.schemas | ForEach-Object { $_.schemaName })
            $artifactSchemaNames | Should -Be @('dbo', 'dms', 'edfi', 'tracked_changes_edfi')
            @($facts.ArtifactInventory.principals).Count | Should -Be 0

            # Transport shape: catalog queries go through the standard sqlcmd idiom.
            $sqlcmdCall = @($calls | Where-Object { $_[4] -eq '/opt/mssql-tools18/bin/sqlcmd' } | Select-Object -First 1)[0]
            ($sqlcmdCall[0..15] -join '|') | Should -Be (@('exec', '-e', 'SQLCMDPASSWORD=abcdefgh1!', 'dms-mssql', '/opt/mssql-tools18/bin/sqlcmd', '-S', 'localhost', '-U', 'sa', '-d', 'edfi_datamanagementservice', '-C', '-b', '-h', '-1', '-W') -join '|')
        }
    }

    It "requires the artifact schema scope for PostgreSQL" {
        InModuleScope Template-Management {
            Mock docker {
                $query = [string]$args[-1]
                $global:LASTEXITCODE = 0
                if ($query -match 'EffectiveSchema') { return "1.0.0|$('ab' * 32)|42|$('cd' * 32)" }
                if ($query -match 'server_version') { return '16.8' }
                if ($query -match 'data_type') { return 'DocumentCache|jsonb' }
                if ($query -match 'relkind') { return @('dms|Document|table') }
                if ($query -match 'pg_namespace') { return @('dms', 'public') }
            }

            { Get-TemplateSourceCatalogFacts -DatabaseEngine postgresql -DatabaseName "edfi_datamanagementservice" } |
                Should -Throw "*ArtifactSchemaName is required for postgresql*"
        }
    }
}

Describe "Build-TemplateNuGetPackage restore gate and manifest" {
    BeforeAll {
        $script:templatesDir = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
        Push-Location $script:templatesDir
        try {
            Import-Module (Join-Path $script:templatesDir "Template-Management.psm1") -Force
        }
        finally {
            Pop-Location
        }
    }

    It "refuses to dump or package when the source fails the DMS-only gate (dmscs contamination)" {
        InModuleScope Template-Management -Parameters @{ configPath = (Join-Path (Split-Path $PSScriptRoot -Parent) "MinimalTemplateSettings.psd1") } {
            param($configPath)

            $configPath | Should -Exist

            $contaminatedFacts = [pscustomobject]@{
                FullInventory = @{
                    schemas    = @(
                        @{ schemaName = "dms"; objects = @(@{ name = "Document"; type = "table" }) },
                        @{ schemaName = "dmscs"; objects = @(@{ name = "Application"; type = "table" }) },
                        @{ schemaName = "public"; objects = @() }
                    )
                    principals = @()
                }
            }

            Mock Get-TemplateSourceCatalogFacts { $contaminatedFacts }
            Mock Invoke-DatabaseDump {}
            Mock Write-TemplateRestoreManifest {}
            Mock New-DatabaseTemplateCsproj {}
            Mock Add-FileToCsProjForNuget {}
            Mock Build-NuGetPackage {}

            { Build-TemplateNuGetPackage -ConfigFilePath $configPath -StandardVersion "5.2.0" -PackageVersion "1.0.0" -TemplateKind "Minimal" } |
                Should -Throw "*not a dedicated DMS datastore*dmscs*"

            # The gate fires before any dump, manifest, or packaging work.
            Should -Invoke Invoke-DatabaseDump -Times 0 -Exactly
            Should -Invoke Write-TemplateRestoreManifest -Times 0 -Exactly
            Should -Invoke New-DatabaseTemplateCsproj -Times 0 -Exactly
            Should -Invoke Build-NuGetPackage -Times 0 -Exactly
        }
    }

    It "refuses to build a PostgreSQL restore package whose dms-only artifact would omit the validated resource schemas" {
        InModuleScope Template-Management -Parameters @{ configPath = (Join-Path (Split-Path $PSScriptRoot -Parent) "MinimalTemplateSettings.psd1") } {
            param($configPath)

            $configPath | Should -Exist

            # A clean, gate-passing full source whose artifact scope (default dms-only dump,
            # no -DumpAllUserSchemas) would not contain the declared projects.
            $cleanFullSourceFacts = [pscustomobject]@{
                FullInventory = @{
                    schemas    = @(
                        @{ schemaName = "dms"; objects = @(@{ name = "Document"; type = "table" }) },
                        @{ schemaName = "edfi"; objects = @(@{ name = "School"; type = "table" }) },
                        @{ schemaName = "tracked_changes_edfi"; objects = @(@{ name = "School"; type = "table" }) },
                        @{ schemaName = "public"; objects = @() }
                    )
                    principals = @()
                }
            }

            Mock Get-TemplateSourceCatalogFacts { $cleanFullSourceFacts }
            Mock Invoke-DatabaseDump {}
            Mock Write-TemplateRestoreManifest {}
            Mock New-DatabaseTemplateCsproj {}
            Mock Add-FileToCsProjForNuget {}
            Mock Build-NuGetPackage {}

            { Build-TemplateNuGetPackage -ConfigFilePath $configPath -StandardVersion "5.2.0" -PackageVersion "1.0.0" -TemplateKind "Minimal" } |
                Should -Throw "*would omit required DMS-owned schemas: edfi, tracked_changes_edfi*"

            # The incomplete artifact is refused before any dump or packaging work.
            Should -Invoke Invoke-DatabaseDump -Times 0 -Exactly
            Should -Invoke Write-TemplateRestoreManifest -Times 0 -Exactly
            Should -Invoke Build-NuGetPackage -Times 0 -Exactly
        }
    }

    It "writes a shape-valid restore manifest from the captured facts and the completed artifact and packages it beside the artifact" {
        $workDir = Join-Path $TestDrive ([Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $workDir -Force | Out-Null
        Push-Location $workDir
        try {
            InModuleScope Template-Management -Parameters @{ configPath = (Join-Path (Split-Path $PSScriptRoot -Parent) "MinimalTemplateSettings.psd1") } {
                param($configPath)

                $cleanFacts = [pscustomobject]@{
                    ApiSchemaFormatVersion     = "1.0.0"
                    EffectiveSchemaHash        = ("ab" * 32)
                    ResourceKeyCount           = 42
                    ResourceKeySeedHashB64     = [System.Convert]::ToBase64String([System.Convert]::FromHexString(("cd" * 32)))
                    EngineVersion              = "16.8"
                    DatabaseCompatibilityLevel = $null
                    DocumentJsonColumnType     = "jsonb"
                    FullInventory              = @{
                        schemas    = @(
                            @{ schemaName = "dms"; objects = @(@{ name = "Document"; type = "table" }, @{ name = "EffectiveSchema"; type = "table" }) },
                            @{ schemaName = "edfi"; objects = @(@{ name = "School"; type = "table" }) },
                            @{ schemaName = "tracked_changes_edfi"; objects = @(@{ name = "School"; type = "table" }) },
                            @{ schemaName = "public"; objects = @() }
                        )
                        principals = @()
                    }
                    ArtifactInventory          = @{
                        schemas    = @(
                            @{ schemaName = "dms"; objects = @(@{ name = "Document"; type = "table" }, @{ name = "EffectiveSchema"; type = "table" }) },
                            @{ schemaName = "edfi"; objects = @(@{ name = "School"; type = "table" }) },
                            @{ schemaName = "tracked_changes_edfi"; objects = @(@{ name = "School"; type = "table" }) },
                            @{ schemaName = "public"; objects = @() }
                        )
                        principals = @()
                    }
                }

                Mock Get-TemplateSourceCatalogFacts { $cleanFacts }
                Mock Get-UserSchemaNames { @("dms", "edfi", "tracked_changes_edfi") }
                Mock Invoke-DatabaseDump {
                    Set-Content -LiteralPath (Join-Path $BackupDirectory $BackupFileName) -Value "fake artifact bytes"
                }
                Mock New-DatabaseTemplateCsproj {}
                Mock Add-FileToCsProjForNuget {}
                Mock Build-NuGetPackage {}

                Build-TemplateNuGetPackage -ConfigFilePath $configPath -StandardVersion "5.2.0" -PackageVersion "1.0.123" -TemplateKind "Minimal" -DumpAllUserSchemas

                # Read-RestoreManifest shape-validates the written manifest as a consumer would.
                $manifest = Read-RestoreManifest -Path "./restore-manifest.json"
                $manifest.packageId | Should -Be "EdFi.Api.Minimal.Template.PostgreSql.5.2.0"
                $manifest.packageVersion | Should -Be "1.0.123"
                $manifest.databaseEngine | Should -Be "postgresql"
                $manifest.templateKind | Should -Be "Minimal"
                $manifest.dataStandardVersion | Should -Be "5.2.0"
                $manifest.contentProfile | Should -Be "DmsDatastoreOnly"
                @($manifest.projects) | Should -Be @("edfi")
                $manifest.effectiveSchemaHash | Should -Be ("ab" * 32)
                $manifest.relationalMappingVersion | Should -Be "v2"
                $manifest.documentJsonColumnType | Should -Be "jsonb"
                $manifest.artifactFileName | Should -Be "EdFi.Api.Minimal.Template.PostgreSql.5.2.0.sql"
                $manifest.artifactSha256 | Should -Be (Get-FileSha256Hex -Path "./EdFi.Api.Minimal.Template.PostgreSql.5.2.0.sql")

                # The manifest inventory is the artifact scope (here: the complete dumped set).
                @($manifest.inventory.schemas | ForEach-Object { $_.schemaName }) | Should -Be @("dms", "edfi", "public", "tracked_changes_edfi")

                # The manifest is added to the package csproj beside the artifact.
                Should -Invoke Add-FileToCsProjForNuget -Times 1 -Exactly -ParameterFilter {
                    @($SourceTargetPair)[0].source -like "*restore-manifest.json" -and @($SourceTargetPair)[0].target -eq "."
                }
            }
        }
        finally {
            Pop-Location
        }
    }
}

Describe "Build-TemplateNuGetPackage attestation" {
    BeforeAll {
        $script:templatesDir = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
        Push-Location $script:templatesDir
        try {
            Import-Module (Join-Path $script:templatesDir "Template-Management.psm1") -Force
        }
        finally {
            Pop-Location
        }
    }

    It "attests the exact packed bytes: sibling document verifies against the trust policy and a later repack invalidates it" {
        $workDir = Join-Path $TestDrive ([Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $workDir -Force | Out-Null
        Push-Location $workDir
        try {
            InModuleScope Template-Management -Parameters @{
                configPath = (Join-Path (Split-Path $PSScriptRoot -Parent) "MinimalTemplateSettings.psd1")
                workDir    = $workDir
            } {
                param($configPath, $workDir)

                $signingKey = New-TemplateAttestationSigningKey -PrivateKeyPath (Join-Path $workDir "signer.pem")

                $cleanFacts = [pscustomobject]@{
                    ApiSchemaFormatVersion     = "1.0.0"
                    EffectiveSchemaHash        = ("ab" * 32)
                    ResourceKeyCount           = 42
                    ResourceKeySeedHashB64     = [System.Convert]::ToBase64String([System.Convert]::FromHexString(("cd" * 32)))
                    EngineVersion              = "16.8"
                    DatabaseCompatibilityLevel = $null
                    DocumentJsonColumnType     = "jsonb"
                    FullInventory              = @{
                        schemas    = @(
                            @{ schemaName = "dms"; objects = @(@{ name = "Document"; type = "table" }) },
                            @{ schemaName = "edfi"; objects = @(@{ name = "School"; type = "table" }) },
                            @{ schemaName = "tracked_changes_edfi"; objects = @(@{ name = "School"; type = "table" }) },
                            @{ schemaName = "public"; objects = @() }
                        )
                        principals = @()
                    }
                    ArtifactInventory          = @{
                        schemas    = @(
                            @{ schemaName = "dms"; objects = @(@{ name = "Document"; type = "table" }) },
                            @{ schemaName = "edfi"; objects = @(@{ name = "School"; type = "table" }) },
                            @{ schemaName = "tracked_changes_edfi"; objects = @(@{ name = "School"; type = "table" }) },
                            @{ schemaName = "public"; objects = @() }
                        )
                        principals = @()
                    }
                }

                Mock Get-TemplateSourceCatalogFacts { $cleanFacts }
                Mock Get-UserSchemaNames { @("dms", "edfi", "tracked_changes_edfi") }
                Mock Invoke-DatabaseDump {
                    Set-Content -LiteralPath (Join-Path $BackupDirectory $BackupFileName) -Value "fake artifact bytes"
                }
                Mock New-DatabaseTemplateCsproj {}
                Mock Add-FileToCsProjForNuget {}
                Mock Build-NuGetPackage {
                    Set-Content -LiteralPath (Join-Path $BackupDirectory "$($Config.Id).$PackageVersion.nupkg") -Value "fake packed bytes"
                }
                Mock Build-TemplateAttestationCompanionPackage { "companion.nupkg" }

                Build-TemplateNuGetPackage `
                    -ConfigFilePath $configPath `
                    -StandardVersion "5.2.0" `
                    -PackageVersion "1.0.123" `
                    -TemplateKind "Minimal" `
                    -DumpAllUserSchemas `
                    -AttestationSignerKeyPath $signingKey.PrivateKeyPath `
                    -AttestationProducer "local-dev"

                $packagePath = "./EdFi.Api.Minimal.Template.PostgreSql.5.2.0.1.0.123.nupkg"
                $attestationPath = "./EdFi.Api.Minimal.Template.PostgreSql.5.2.0.1.0.123.nupkg.attestation.json"
                $attestationPath | Should -Exist

                # Verify the attestation exactly as a restore consumer would.
                $policyPath = Join-Path $workDir "template-trust-policy.json"
                [ordered]@{
                    version   = 1
                    producers = @(
                        [ordered]@{
                            name       = "local-dev"
                            provider   = "detached-attestation"
                            publicKeys = @(
                                [ordered]@{
                                    keyId            = $signingKey.KeyId
                                    algorithm        = $signingKey.Algorithm
                                    publicKeySpkiB64 = $signingKey.PublicKeySpkiB64
                                }
                            )
                        }
                    )
                } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $policyPath -Encoding utf8
                $trustPolicy = Read-TemplateTrustPolicy -TrackedPolicyPath $policyPath

                $verdict = Test-TemplateAttestation `
                    -AttestationJson (Get-Content -LiteralPath $attestationPath -Raw) `
                    -PackageSha256 (Get-FileSha256Hex -Path $packagePath) `
                    -ExpectedPackageId "EdFi.Api.Minimal.Template.PostgreSql.5.2.0" `
                    -ExpectedPackageVersion "1.0.123" `
                    -TrustPolicy $trustPolicy
                $verdict.IsTrusted | Should -BeTrue
                $verdict.Producer | Should -Be "local-dev"

                # The attestation binds the FINAL bytes: any repack/tamper invalidates it.
                Add-Content -LiteralPath $packagePath -Value "repacked"
                $staleVerdict = Test-TemplateAttestation `
                    -AttestationJson (Get-Content -LiteralPath $attestationPath -Raw) `
                    -PackageSha256 (Get-FileSha256Hex -Path $packagePath) `
                    -ExpectedPackageId "EdFi.Api.Minimal.Template.PostgreSql.5.2.0" `
                    -ExpectedPackageVersion "1.0.123" `
                    -TrustPolicy $trustPolicy
                $staleVerdict.IsTrusted | Should -BeFalse
                $staleVerdict.Reason | Should -BeLike "*does not match the resolved package bytes*"

                # The companion package is built from the exact sibling document.
                Should -Invoke Build-TemplateAttestationCompanionPackage -Times 1 -Exactly -ParameterFilter {
                    $AttestationPath -like "*.nupkg.attestation.json" -and $PackageVersion -eq "1.0.123"
                }
            }
        }
        finally {
            Pop-Location
        }
    }

    It "builds no attestation artifacts when no signer is supplied" {
        $workDir = Join-Path $TestDrive ([Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $workDir -Force | Out-Null
        Push-Location $workDir
        try {
            InModuleScope Template-Management -Parameters @{ configPath = (Join-Path (Split-Path $PSScriptRoot -Parent) "MinimalTemplateSettings.psd1") } {
                param($configPath)

                $cleanFacts = [pscustomobject]@{
                    ApiSchemaFormatVersion     = "1.0.0"
                    EffectiveSchemaHash        = ("ab" * 32)
                    ResourceKeyCount           = 42
                    ResourceKeySeedHashB64     = [System.Convert]::ToBase64String([System.Convert]::FromHexString(("cd" * 32)))
                    EngineVersion              = "16.8"
                    DatabaseCompatibilityLevel = $null
                    DocumentJsonColumnType     = "jsonb"
                    FullInventory              = @{
                        schemas    = @(@{ schemaName = "dms"; objects = @(@{ name = "Document"; type = "table" }) }, @{ schemaName = "edfi"; objects = @(@{ name = "School"; type = "table" }) }, @{ schemaName = "tracked_changes_edfi"; objects = @(@{ name = "School"; type = "table" }) }, @{ schemaName = "public"; objects = @() })
                        principals = @()
                    }
                    ArtifactInventory          = @{
                        schemas    = @(@{ schemaName = "dms"; objects = @(@{ name = "Document"; type = "table" }) }, @{ schemaName = "edfi"; objects = @(@{ name = "School"; type = "table" }) }, @{ schemaName = "tracked_changes_edfi"; objects = @(@{ name = "School"; type = "table" }) }, @{ schemaName = "public"; objects = @() })
                        principals = @()
                    }
                }

                Mock Get-TemplateSourceCatalogFacts { $cleanFacts }
                Mock Get-UserSchemaNames { @("dms", "edfi", "tracked_changes_edfi") }
                Mock Invoke-DatabaseDump {
                    Set-Content -LiteralPath (Join-Path $BackupDirectory $BackupFileName) -Value "fake artifact bytes"
                }
                Mock New-DatabaseTemplateCsproj {}
                Mock Add-FileToCsProjForNuget {}
                Mock Build-NuGetPackage {}
                Mock Build-TemplateAttestationCompanionPackage {}

                Build-TemplateNuGetPackage -ConfigFilePath $configPath -StandardVersion "5.2.0" -PackageVersion "1.0.123" -TemplateKind "Minimal" -DumpAllUserSchemas

                @(Get-ChildItem -Path "." -Filter "*.attestation.json").Count | Should -Be 0
                Should -Invoke Build-TemplateAttestationCompanionPackage -Times 0 -Exactly
            }
        }
        finally {
            Pop-Location
        }
    }

    It "fails fast on a misconfigured signer before any catalog read or dump work" {
        InModuleScope Template-Management -Parameters @{ configPath = (Join-Path (Split-Path $PSScriptRoot -Parent) "MinimalTemplateSettings.psd1") } {
            param($configPath)

            $configPath | Should -Exist

            Mock Get-TemplateSourceCatalogFacts { [pscustomobject]@{} }
            Mock Invoke-DatabaseDump {}
            Mock Build-NuGetPackage {}

            { Build-TemplateNuGetPackage -ConfigFilePath $configPath -StandardVersion "5.2.0" -PackageVersion "1.0.0" -TemplateKind "Minimal" -AttestationSignerKeyPath (Join-Path $TestDrive "somekey.pem") } |
                Should -Throw "*must be supplied together*"

            { Build-TemplateNuGetPackage -ConfigFilePath $configPath -StandardVersion "5.2.0" -PackageVersion "1.0.0" -TemplateKind "Minimal" -AttestationProducer "local-dev" } |
                Should -Throw "*must be supplied together*"

            { Build-TemplateNuGetPackage -ConfigFilePath $configPath -StandardVersion "5.2.0" -PackageVersion "1.0.0" -TemplateKind "Minimal" -AttestationSignerKeyPath (Join-Path $TestDrive "absent.pem") -AttestationProducer "local-dev" } |
                Should -Throw "*Signing key was not found*"

            # A key on the wrong curve is refused before any catalog read or dump work: the
            # ECDSA_P256_SHA256 label must never be emitted for a non-P-256 key.
            $p384 = [System.Security.Cryptography.ECDsa]::Create([System.Security.Cryptography.ECCurve+NamedCurves]::nistP384)
            try {
                $p384Pem = [System.Security.Cryptography.PemEncoding]::WriteString("PRIVATE KEY", $p384.ExportPkcs8PrivateKey())
            }
            finally {
                $p384.Dispose()
            }
            $p384KeyPath = Join-Path $TestDrive "p384-signer.pem"
            Set-Content -LiteralPath $p384KeyPath -Value $p384Pem -Encoding utf8
            { Build-TemplateNuGetPackage -ConfigFilePath $configPath -StandardVersion "5.2.0" -PackageVersion "1.0.0" -TemplateKind "Minimal" -AttestationSignerKeyPath $p384KeyPath -AttestationProducer "local-dev" } |
                Should -Throw "*not a NIST P-256 ECDSA key*"

            Should -Invoke Get-TemplateSourceCatalogFacts -Times 0 -Exactly
            Should -Invoke Invoke-DatabaseDump -Times 0 -Exactly
        }
    }
}

Describe "Build-TemplateAttestationCompanionPackage" {
    BeforeAll {
        $script:templatesDir = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
        Push-Location $script:templatesDir
        try {
            Import-Module (Join-Path $script:templatesDir "Template-Management.psm1") -Force
        }
        finally {
            Pop-Location
        }
    }

    It "packs a same-version companion containing exactly the attestation document" {
        $workDir = Join-Path $TestDrive ([Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $workDir -Force | Out-Null

        InModuleScope Template-Management -Parameters @{ workDir = $workDir } {
            param($workDir)

            $attestationPath = Join-Path $workDir "pkg.nupkg.attestation.json"
            Set-Content -LiteralPath $attestationPath -Value '{"version":1}'

            $dotnetCalls = [System.Collections.Generic.List[object]]::new()
            Mock dotnet { $dotnetCalls.Add(@($args)); $global:LASTEXITCODE = 0 }

            $companionPath = Build-TemplateAttestationCompanionPackage `
                -Config @{ Id = "EdFi.Api.Minimal.Template.PostgreSql.5.2.0"; Authors = "Ed-Fi Alliance"; ProjectUrl = "https://www.ed-fi.org"; License = "Apache-2.0" } `
                -PackageVersion "1.0.123" `
                -BackupDirectory $workDir `
                -AttestationPath $attestationPath

            $companionPath | Should -Be (Join-Path $workDir "EdFi.Api.Minimal.Template.PostgreSql.5.2.0.Attestation.1.0.123.nupkg")

            # The companion csproj lives in its own transient workspace and references only
            # the attestation document.
            $companionCsprojPath = Join-Path $workDir ".attestation-package/EdFi.Api.Minimal.Template.PostgreSql.5.2.0.Attestation.csproj"
            $companionCsprojPath | Should -Exist
            (Get-Content -LiteralPath $companionCsprojPath -Raw) | Should -BeLike "*pkg.nupkg.attestation.json*"

            $dotnetCalls.Count | Should -Be 2
            $dotnetCalls[0][0] | Should -Be "restore"
            $packArguments = @($dotnetCalls[1])
            $packArguments[0] | Should -Be "pack"
            $packArguments | Should -Contain "--output"
            # PowerShell mock binding splits "-p:X=y" tokens at the colon, so assert on the
            # joined argv text (a real dotnet invocation receives them as single arguments).
            ($packArguments -join ' ') | Should -BeLike "*NoDefaultExcludes=true*"
            ($packArguments -join ' ') | Should -BeLike "*Version=1.0.123*"
        }
    }

    It "fails when the companion pack exits non-zero" {
        $workDir = Join-Path $TestDrive ([Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $workDir -Force | Out-Null

        InModuleScope Template-Management -Parameters @{ workDir = $workDir } {
            param($workDir)

            $attestationPath = Join-Path $workDir "pkg.nupkg.attestation.json"
            Set-Content -LiteralPath $attestationPath -Value '{"version":1}'

            Mock dotnet {
                if (@($args)[0] -eq "pack") { $global:LASTEXITCODE = 1 } else { $global:LASTEXITCODE = 0 }
            }

            { Build-TemplateAttestationCompanionPackage `
                    -Config @{ Id = "EdFi.Api.Minimal.Template.PostgreSql.5.2.0"; Authors = "Ed-Fi Alliance"; ProjectUrl = "https://www.ed-fi.org"; License = "Apache-2.0" } `
                    -PackageVersion "1.0.123" `
                    -BackupDirectory $workDir `
                    -AttestationPath $attestationPath } |
                Should -Throw "*dotnet pack failed for the attestation companion package*"
        }
    }
}
