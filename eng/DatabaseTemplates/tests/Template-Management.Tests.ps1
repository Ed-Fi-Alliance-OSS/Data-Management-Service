# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

Describe "Get-EducationOrganizationIdsFromSampleData" {
    BeforeAll {
        # Import with the module's own directory as the working directory, matching how the
        # template workflow invokes it. The tests reach the parser through InModuleScope.
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

    It "uses conservative relational-backend limits for postgresql" {
        InModuleScope Template-Management {
            $tuning = Get-TemplateBulkLoadTuning -DatabaseEngine postgresql
            $tuning.Count | Should -Be 4
            $tuning.MaxConcurrentConnections | Should -Be 5
            $tuning.MaxSimultaneousRequests | Should -Be 5
            $tuning.MaxBufferedTasks | Should -Be 2
            $tuning.RetryCount | Should -Be 5
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
                Mock Invoke-TemplateCatalogQuery { @() }
                Mock docker {
                    $calls.Add(@($args))
                    $global:LASTEXITCODE = 0
                }

                Invoke-DatabaseDump -DatabaseEngine postgresql -ContainerName "dms-postgresql" -DatabaseName "edfi_datamanagementservice" -DatabaseSchemas @("dms", "edfi") -BackupDirectory $backupDir -BackupFileName "test.sql"

                $calls.Count | Should -Be 1
                ($calls[0] -join '|') | Should -Be (@('exec', 'dms-postgresql', 'pg_dump', '-U', 'postgres', '--no-owner', 'edfi_datamanagementservice', '-n', 'dms', '-n', 'edfi') -join '|')

                # Schema-scoped pg_dump never emits CREATE EXTENSION, but the dumped dms.uuidv5()
                # function requires pgcrypto's digest(), so the preamble must precede the dump.
                $content = Get-Content -LiteralPath (Join-Path $backupDir "test.sql") -Raw
                $content | Should -Match ([regex]::Escape('CREATE EXTENSION IF NOT EXISTS "pgcrypto";'))
            }
        }

        It "throws immediately when pg_dump fails, and leaves no partial artifact behind" {
            InModuleScope Template-Management -Parameters @{ backupDir = $TestDrive } {
                param($backupDir)
                # pg_dump writes some output and then fails: the redirection has already produced a
                # partial file, so a missing exit-code check would report "Backup Created" over a
                # truncated dump and let manifest/packaging/attestation consume it.
                Mock Invoke-TemplateCatalogQuery { @() }
                Mock docker {
                    "-- partial dump output"
                    $global:LASTEXITCODE = 1
                }
                Mock Write-Host {}

                { Invoke-DatabaseDump -DatabaseEngine postgresql -ContainerName "dms-postgresql" -DatabaseName "edfi_datamanagementservice" -DatabaseSchemas @("dms") -BackupDirectory $backupDir -BackupFileName "failed.sql" } |
                    Should -Throw "*pg_dump of 'edfi_datamanagementservice' failed in container 'dms-postgresql' with exit code 1*"

                # No "Backup Created" banner, and no partial artifact left for a later step to pick up.
                Should -Invoke Write-Host -Times 0 -Exactly
                Test-Path -LiteralPath (Join-Path $backupDir "failed.sql") | Should -BeFalse
            }
        }

        It "produces byte-identical pg_dump arguments whether or not -DatabaseEngine is supplied" {
            InModuleScope Template-Management -Parameters @{ backupDir = $TestDrive } {
                param($backupDir)
                Mock Invoke-TemplateCatalogQuery { @() }
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

        It "appends the enqueue owner's function ownership after the --no-owner dump, so the hashed artifact names no source administrator" {
            InModuleScope Template-Management -Parameters @{ backupDir = $TestDrive } {
                param($backupDir)
                Mock Invoke-TemplateCatalogQuery {
                    @(
                        'function|edfi_dms_enqueue_owner|dms."TF_Document_EnqueueProjectionInsert"()',
                        'function|edfi_dms_enqueue_owner|dms."TF_Document_EnqueueProjectionUpdate"()'
                    )
                }
                Mock docker { "-- dump body"; $global:LASTEXITCODE = 0 }

                Invoke-DatabaseDump -DatabaseEngine postgresql -ContainerName "dms-postgresql" -DatabaseName "edfi_datamanagementservice" -DatabaseSchemas @("dms", "edfi") -BackupDirectory $backupDir -BackupFileName "portable.sql"

                # The ownership is read from the dumped schemas of the same source, as the dumping administrator.
                Should -Invoke Invoke-TemplateCatalogQuery -Times 1 -Exactly -ParameterFilter {
                    $DatabaseName -ceq "edfi_datamanagementservice" -and $ContainerName -ceq "dms-postgresql" -and
                    $Query -ceq (Get-PostgresqlTemplateOwnershipQuerySql -SchemaName @("dms", "edfi"))
                }
                $lines = @(Get-Content -LiteralPath (Join-Path $backupDir "portable.sql") | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
                $lines | Should -Be @(
                    'CREATE EXTENSION IF NOT EXISTS "pgcrypto";',
                    '-- dump body',
                    '-- Ownership the --no-owner dump does not carry (DMS template finalization).',
                    'ALTER FUNCTION dms."TF_Document_EnqueueProjectionInsert"() OWNER TO "edfi_dms_enqueue_owner";',
                    'ALTER FUNCTION dms."TF_Document_EnqueueProjectionUpdate"() OWNER TO "edfi_dms_enqueue_owner";'
                )
            }
        }

        It "refuses a source whose ownership the portable dump cannot reproduce, before pg_dump runs" {
            InModuleScope Template-Management -Parameters @{ backupDir = $TestDrive } {
                param($backupDir)
                Mock Invoke-TemplateCatalogQuery { @('relation|someone_else|dms."Document"') }
                Mock docker { $global:LASTEXITCODE = 0 }

                { Invoke-DatabaseDump -DatabaseEngine postgresql -ContainerName "dms-postgresql" -DatabaseName "edfi_datamanagementservice" -DatabaseSchemas @("dms") -BackupDirectory $backupDir -BackupFileName "refused.sql" } |
                    Should -Throw '*does not produce: relation dms."Document" (owner someone_else)*Nothing was dumped or packaged*'

                Should -Invoke docker -Times 0 -Exactly
                Test-Path -LiteralPath (Join-Path $backupDir "refused.sql") | Should -BeFalse
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

    It "registers an MSSQL connection string without a PostgreSQL password when an MSSQL build finds no existing data store" {
        InModuleScope Template-Management {
            Mock ConvertTo-PostgresCredential { throw "An MSSQL registration must not build a PostgreSQL credential." }
            Mock New-DataStoreConnectionString { return "mssql-connection-string" }
            Mock Add-DataStore { return 42 }

            $result = Add-TemplateDataStore `
                -CmsUrl "http://cms" `
                -AccessToken "cms-token" `
                -DatabaseName "template_db" `
                -DatabaseEngine "mssql" `
                -MssqlPassword "mssql-secret"

            $result | Should -Be 42
            Should -Invoke ConvertTo-PostgresCredential -Times 0 -Exactly
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
                $null -eq $PostgresCredential -and
                $PostgresDbName -eq "template_db" -and
                $DatabaseEngine -eq "mssql" -and
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
                -PostgresPassword "pg-secret"

            $result | Should -Be 84
            Should -Invoke New-DataStoreConnectionString -Times 0 -Exactly
            Should -Invoke Add-DataStore -Times 1 -Exactly -ParameterFilter {
                $CmsUrl -eq "http://cms" -and
                $AccessToken -eq "cms-token" -and
                $PostgresCredential -eq $credential -and
                $PostgresDbName -eq "template_db" -and
                $DatabaseEngine -eq "postgresql" -and
                [string]::IsNullOrWhiteSpace($ConnectionString)
            }
        }
    }

    It "rejects a PostgreSQL build without -PostgresPassword before registering anything" {
        InModuleScope Template-Management {
            Mock Add-DataStore { return 1 }

            {
                Add-TemplateDataStore `
                    -CmsUrl "http://cms" `
                    -AccessToken "cms-token" `
                    -DatabaseName "template_db" `
                    -DatabaseEngine "postgresql" `
                    -MssqlPassword "mssql-secret"
            } | Should -Throw "*-PostgresPassword is required when -DatabaseEngine is 'postgresql'*"
            Should -Invoke Add-DataStore -Times 0 -Exactly
        }
    }

    It "rejects an MSSQL build without -MssqlPassword before registering anything" {
        InModuleScope Template-Management {
            Mock Add-DataStore { return 1 }

            {
                Add-TemplateDataStore `
                    -CmsUrl "http://cms" `
                    -AccessToken "cms-token" `
                    -DatabaseName "template_db" `
                    -DatabaseEngine "mssql" `
                    -PostgresPassword "pg-secret"
            } | Should -Throw "*-MssqlPassword is required when -DatabaseEngine is 'mssql'*"
            Should -Invoke Add-DataStore -Times 0 -Exactly
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

    BeforeEach {
        # The privilege gate reads the live source; its own tests cover it.
        Mock Assert-TemplateSourcePrivilege {} -ModuleName Template-Management
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
            Mock Assert-TemplateSourceMatchesAuthoritativeInventory {}
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
            Mock Assert-TemplateSourceMatchesAuthoritativeInventory {}
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

Describe "Build-Template vendor reuse" {
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

    It "creates one vendor for a populated build and creates both applications under it" {
        # POST /v3/vendors is create-only, so a second Add-Vendor for the same company would be
        # rejected with 400 and fail every populated template build.
        InModuleScope Template-Management {
            Mock Add-CmsClient {}
            Mock Get-CmsToken { return "cms-token" }
            Mock Get-DataStore { return @() }
            Mock Resolve-DataStoreIdForTemplate { return [long]5 }
            Mock Find-CmsVendorByCompany { return $null }
            Mock Add-Vendor { return [long]17 }
            Mock Add-Application { return @{ Id = 1; Key = "key"; Secret = "secret" } }
            Mock Get-DmsToken { return "dms-token" }
            Mock Invoke-SchoolYearLoader {}
            Mock Initialize-BulkLoad { return @{} }
            Mock Get-TemplateBulkLoadTuning { return @{} }
            Mock Invoke-BulkLoad {}
            Mock Get-EducatorPreparationSampleFileName { return @() }
            Mock New-EducatorPreparationFilteredSampleDirectory { return "populated-dir" }
            Mock Get-EducationOrganizationIdsFromSampleData { return @() }
            Mock Build-TemplateNuGetPackage {}

            Build-Template `
                -TemplateType Populated `
                -DmsUrl "http://dms" `
                -CmsUrl "http://cms" `
                -MinimalSampleDataDirectory "minimal-dir" `
                -PopulatedSampleDataDirectory "populated-dir" `
                -Extension "ed-fi" `
                -ConfigFilePath "config.psd1" `
                -StandardVersion "5.2.0" `
                -PackageVersion "1.0.0"

            Should -Invoke Find-CmsVendorByCompany -Times 1 -Exactly -ParameterFilter { $Company -eq 'Demo Vendor' }
            Should -Invoke Add-Vendor -Times 1 -Exactly -ParameterFilter { $Company -eq 'Demo Vendor' }
            Should -Invoke Add-Application -Times 2 -Exactly
            Should -Invoke Add-Application -Times 1 -Exactly -ParameterFilter {
                $VendorId -eq 17 -and $ClaimSetName -eq 'BootstrapDescriptorsandEdOrgs'
            }
            Should -Invoke Add-Application -Times 1 -Exactly -ParameterFilter {
                $VendorId -eq 17 -and $ClaimSetName -eq 'EdFiSandbox'
            }
        }
    }

    It "reuses the vendor a previous build created instead of POSTing it again" {
        # A re-run against the same Configuration Service finds "Demo Vendor" already present; a
        # second POST for that company would be rejected with 400.
        InModuleScope Template-Management {
            Mock Add-CmsClient {}
            Mock Get-CmsToken { return "cms-token" }
            Mock Get-DataStore { return @() }
            Mock Resolve-DataStoreIdForTemplate { return [long]5 }
            Mock Find-CmsVendorByCompany { return [pscustomobject]@{ id = 23; company = 'Demo Vendor' } }
            Mock Add-Vendor { throw "Add-Vendor must not be called for an existing vendor." }
            Mock Add-Application { return @{ Id = 1; Key = "key"; Secret = "secret" } }
            Mock Get-DmsToken { return "dms-token" }
            Mock Invoke-SchoolYearLoader {}
            Mock Initialize-BulkLoad { return @{} }
            Mock Get-TemplateBulkLoadTuning { return @{} }
            Mock Invoke-BulkLoad {}
            Mock Get-EducatorPreparationSampleFileName { return @() }
            Mock New-EducatorPreparationFilteredSampleDirectory { return "populated-dir" }
            Mock Get-EducationOrganizationIdsFromSampleData { return @() }
            Mock Build-TemplateNuGetPackage {}

            Build-Template `
                -TemplateType Populated `
                -DmsUrl "http://dms" `
                -CmsUrl "http://cms" `
                -MinimalSampleDataDirectory "minimal-dir" `
                -PopulatedSampleDataDirectory "populated-dir" `
                -Extension "ed-fi" `
                -ConfigFilePath "config.psd1" `
                -StandardVersion "5.2.0" `
                -PackageVersion "1.0.0" `
                -ApplicationName "Second build"

            Should -Invoke Add-Vendor -Times 0 -Exactly
            Should -Invoke Add-Application -Times 2 -Exactly
            Should -Invoke Add-Application -Times 1 -Exactly -ParameterFilter {
                $VendorId -eq 23 -and $ClaimSetName -eq 'BootstrapDescriptorsandEdOrgs'
            }
            Should -Invoke Add-Application -Times 1 -Exactly -ParameterFilter {
                $VendorId -eq 23 -and $ClaimSetName -eq 'EdFiSandbox'
            }
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
                if ($query -match 'pg_event_trigger') { return @('eventtrigger|intruder_event_trigger') }
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
            # Database-level objects are read too; the source gate refuses any of them.
            $facts.DatabaseLevelObjects | Should -Be @('eventtrigger|intruder_event_trigger')

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
                if ($query -match 'sys\.symmetric_keys') { return @('symmetrickey|##MS_DatabaseMasterKey##') }
                if ($query -match 'EffectiveSchema') { return "1.0.0|$('ab' * 32)|42|$('cd' * 32)" }
                if ($query -match 'ProductVersion') { return '17.0.900.7' }
                if ($query -match 'compatibility_level') { return '170' }
                if ($query -match 'sys\.columns') { return 'DocumentCache|nvarchar(max)' }
                # The schema query also names sys.objects (built-in schemas show only when
                # non-empty), so it is told apart by what it selects.
                if ($query -match 'SELECT s\.name FROM sys\.schemas') { return @('dbo', 'dms', 'edfi', 'tracked_changes_edfi') }
                if ($query -match 'sys\.objects') {
                    return @('dms|Document|table', 'edfi|School|table', 'tracked_changes_edfi|School|table')
                }
                if ($query -match 'database_principals') { return @() }
            }

            $facts = Get-TemplateSourceCatalogFacts -DatabaseEngine mssql -DatabaseName "edfi_datamanagementservice"

            $facts.EngineVersion | Should -Be "17.0.900.7"
            $facts.DatabaseCompatibilityLevel | Should -Be 170
            $facts.DocumentJsonColumnType | Should -Be "nvarchar(max)"
            # A copied database master key travels in the .bak; the source gate refuses it.
            $facts.DatabaseLevelObjects | Should -Be @('symmetrickey|##MS_DatabaseMasterKey##')

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

    BeforeEach {
        # The privilege gate reads the live source; its own tests cover it.
        Mock Assert-TemplateSourcePrivilege {} -ModuleName Template-Management
    }

    It "refuses to dump or package when the source fails the DMS-only gate (dmscs contamination)" {
        InModuleScope Template-Management -Parameters @{ configPath = (Join-Path (Split-Path $PSScriptRoot -Parent) "MinimalTemplateSettings.psd1") } {
            param($configPath)

            $configPath | Should -Exist

            $contaminatedFacts = [pscustomobject]@{
                DatabaseLevelObjects = @()
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

    It "refuses to dump or package a source whose only contamination is a database-level copied key" {
        InModuleScope Template-Management -Parameters @{ configPath = (Join-Path (Split-Path $PSScriptRoot -Parent) "MinimalTemplateSettings.psd1") } {
            param($configPath)

            $configPath | Should -Exist

            $keyedFacts = [pscustomobject]@{
                DatabaseLevelObjects = @("symmetrickey|##MS_DatabaseMasterKey##")
                FullInventory = @{
                    schemas    = @(
                        @{ schemaName = "dbo"; objects = @() },
                        @{ schemaName = "dms"; objects = @(@{ name = "Document"; type = "table" }) },
                        @{ schemaName = "edfi"; objects = @(@{ name = "School"; type = "table" }) },
                        @{ schemaName = "tracked_changes_edfi"; objects = @(@{ name = "School"; type = "table" }) }
                    )
                    principals = @()
                }
            }

            Mock Get-TemplateSourceCatalogFacts { $keyedFacts }
            Mock Invoke-DatabaseDump {}
            Mock Write-TemplateRestoreManifest {}
            Mock Build-NuGetPackage {}

            { Build-TemplateNuGetPackage -ConfigFilePath $configPath -StandardVersion "5.2.0" -PackageVersion "1.0.0" -TemplateKind "Minimal" -DatabaseEngine mssql } |
                Should -Throw "*not a dedicated DMS datastore: it carries database-level objects the DMS schema does not create: symmetrickey|##MS_DatabaseMasterKey##*"

            Should -Invoke Invoke-DatabaseDump -Times 0 -Exactly
            Should -Invoke Write-TemplateRestoreManifest -Times 0 -Exactly
            Should -Invoke Build-NuGetPackage -Times 0 -Exactly
        }
    }

    It "does not write a manifest, pack, or attest after a failed dump" {
        InModuleScope Template-Management -Parameters @{ configPath = (Join-Path (Split-Path $PSScriptRoot -Parent) "MinimalTemplateSettings.psd1") } {
            param($configPath)

            $configPath | Should -Exist

            # A clean, gate-passing source; -DumpAllUserSchemas keeps the artifact scope valid, so
            # the dump failure below is the only thing that can stop the build.
            $cleanFacts = [pscustomobject]@{
                DatabaseLevelObjects = @()
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

            Mock Get-TemplateSourceCatalogFacts { $cleanFacts }
            # -DumpAllUserSchemas discovers the dump scope from the live database; the mock keeps
            # this test off Docker while matching the facts above.
            Mock Get-UserSchemaNames { @("dms", "edfi", "tracked_changes_edfi") }
            Mock Get-TemplateAuthoritativeObjectInventory { $cleanFacts.FullInventory }
            Mock Invoke-DatabaseDump { throw "pg_dump of 'edfi_datamanagementservice' failed in container 'dms-postgresql' with exit code 1." }
            Mock Write-TemplateRestoreManifest {}
            Mock New-DatabaseTemplateCsproj {}
            Mock Add-FileToCsProjForNuget {}
            Mock Build-NuGetPackage {}
            Mock Invoke-TemplatePackageAttestation {}

            { Build-TemplateNuGetPackage -ConfigFilePath $configPath -StandardVersion "5.2.0" -PackageVersion "1.0.0" -TemplateKind "Minimal" -DumpAllUserSchemas -SchemaFilePath "ApiSchema.json" } |
                Should -Throw "*pg_dump*failed*exit code 1*"

            # A failed or partial dump never reaches manifest, packaging, or attestation work.
            Should -Invoke Write-TemplateRestoreManifest -Times 0 -Exactly
            Should -Invoke New-DatabaseTemplateCsproj -Times 0 -Exactly
            Should -Invoke Build-NuGetPackage -Times 0 -Exactly
            Should -Invoke Invoke-TemplatePackageAttestation -Times 0 -Exactly
        }
    }

    It "refuses to build a PostgreSQL restore package whose dms-only artifact would omit the validated resource schemas" {
        InModuleScope Template-Management -Parameters @{ configPath = (Join-Path (Split-Path $PSScriptRoot -Parent) "MinimalTemplateSettings.psd1") } {
            param($configPath)

            $configPath | Should -Exist

            # A clean, gate-passing full source whose artifact scope (default dms-only dump,
            # no -DumpAllUserSchemas) would not contain the declared projects.
            $cleanFullSourceFacts = [pscustomobject]@{
                DatabaseLevelObjects = @()
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
                    DatabaseLevelObjects       = @()
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
                Mock Get-TemplateAuthoritativeObjectInventory { $cleanFacts.FullInventory }
                Mock Invoke-DatabaseDump {
                    Set-Content -LiteralPath (Join-Path $BackupDirectory $BackupFileName) -Value "fake artifact bytes"
                }
                Mock New-DatabaseTemplateCsproj {}
                Mock Add-FileToCsProjForNuget {}
                Mock Build-NuGetPackage {}

                Build-TemplateNuGetPackage -ConfigFilePath $configPath -StandardVersion "5.2.0" -PackageVersion "1.0.123" -TemplateKind "Minimal" -DumpAllUserSchemas -SchemaFilePath "ApiSchema.json"

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
                $manifest.relationalMappingVersion | Should -Be "v3"
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

Describe "Build-TemplateNuGetPackage authoritative source comparison" {
    BeforeAll {
        $script:templatesDir = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
        Push-Location $script:templatesDir
        try {
            Import-Module (Join-Path $script:templatesDir "Template-Management.psm1") -Force
        }
        finally {
            Pop-Location
        }

        # What the schema files' DDL defines: the dms schema, the generated auth companion, one
        # resource schema with its tracked_changes companion, and the empty always-present schema.
        $script:authoritativeInventory = @{
            schemas    = @(
                @{ schemaName = "auth"; objects = @(@{ name = "EducationOrganizationIdToEducationOrganizationId"; type = "table" }) },
                @{ schemaName = "dms"; objects = @(@{ name = "Document"; type = "table" }, @{ name = "EffectiveSchema"; type = "table" }) },
                @{ schemaName = "edfi"; objects = @(@{ name = "School"; type = "table" }) },
                @{ schemaName = "public"; objects = @() },
                @{ schemaName = "tracked_changes_edfi"; objects = @(@{ name = "School"; type = "table" }) }
            )
            principals = @()
        }
    }

    BeforeEach {
        # The privilege gate reads the live source; its own tests cover it.
        Mock Assert-TemplateSourcePrivilege {} -ModuleName Template-Management
    }

    It "refuses a source with <Name> in an otherwise allowed schema before any dump, manifest, package, or attestation" -ForEach @(
        @{ Name = "an extra auth.CopiedClients table"; Schema = "auth"; Extra = "CopiedClients"; Expected = "*objects the schema files do not define: auth.CopiedClients (table)*Nothing was dumped or packaged*" }
        @{ Name = "an extra dms.PrivateKeys table"; Schema = "dms"; Extra = "PrivateKeys"; Expected = "*objects the schema files do not define: dms.PrivateKeys (table)*Nothing was dumped or packaged*" }
    ) {
        InModuleScope Template-Management -Parameters @{
            configPath    = (Join-Path (Split-Path $PSScriptRoot -Parent) "MinimalTemplateSettings.psd1")
            authoritative = $script:authoritativeInventory
            schema        = $Schema
            extra         = $Extra
            expected      = $Expected
        } {
            param($configPath, $authoritative, $schema, $extra, $expected)

            $configPath | Should -Exist

            $contaminatedSchemas = @($authoritative.schemas | ForEach-Object {
                    $objects = @($_.objects)
                    if ($_.schemaName -ceq $schema) { $objects += @{ name = $extra; type = "table" } }
                    @{ schemaName = $_.schemaName; objects = $objects }
                })
            $contaminatedFacts = [pscustomobject]@{ DatabaseLevelObjects = @(); FullInventory = @{ schemas = $contaminatedSchemas; principals = @() } }

            Mock Assert-TemplateAttestationSignerKey {}
            Mock Get-TemplateSourceCatalogFacts { $contaminatedFacts }
            Mock Get-UserSchemaNames { @("auth", "dms", "edfi", "tracked_changes_edfi") }
            Mock Get-TemplateAuthoritativeObjectInventory { $authoritative }
            Mock Invoke-DatabaseDump {}
            Mock Write-TemplateRestoreManifest {}
            Mock New-DatabaseTemplateCsproj {}
            Mock Add-FileToCsProjForNuget {}
            Mock Build-NuGetPackage {}
            Mock Invoke-TemplatePackageAttestation {}

            # The DMS-only gate alone accepts this source: the extra table sits in an allowed schema.
            { Assert-DmsOnlyTemplateSource -Facts $contaminatedFacts -DatabaseEngine postgresql -DatabaseName "edfi_datamanagementservice" } | Should -Not -Throw

            {
                Build-TemplateNuGetPackage -ConfigFilePath $configPath -StandardVersion "5.2.0" -PackageVersion "1.0.0" -TemplateKind "Minimal" `
                    -DumpAllUserSchemas -SchemaFilePath @("ApiSchema.json") `
                    -AttestationSignerKeyPath "signer.pem" -AttestationProducer "local-dev"
            } | Should -Throw $expected

            Should -Invoke Get-TemplateAuthoritativeObjectInventory -Times 1 -Exactly -ParameterFilter {
                @($SchemaFilePath) -join "|" -eq "ApiSchema.json" -and $DatabaseEngine -eq "postgresql"
            }
            Should -Invoke Invoke-DatabaseDump -Times 0 -Exactly
            Should -Invoke Write-TemplateRestoreManifest -Times 0 -Exactly
            Should -Invoke New-DatabaseTemplateCsproj -Times 0 -Exactly
            Should -Invoke Build-NuGetPackage -Times 0 -Exactly
            Should -Invoke Invoke-TemplatePackageAttestation -Times 0 -Exactly
        }
    }

    It "accepts a source holding exactly the authoritative objects, including the auth and tracked_changes companions" {
        InModuleScope Template-Management -Parameters @{
            configPath    = (Join-Path (Split-Path $PSScriptRoot -Parent) "MinimalTemplateSettings.psd1")
            authoritative = $script:authoritativeInventory
        } {
            param($configPath, $authoritative)

            $configPath | Should -Exist
            $authoritative.schemas | Should -Not -BeNullOrEmpty

            Mock Get-TemplateSourceCatalogFacts { [pscustomobject]@{ DatabaseLevelObjects = @(); FullInventory = $authoritative } }
            Mock Get-UserSchemaNames { @("auth", "dms", "edfi", "tracked_changes_edfi") }
            Mock Get-TemplateAuthoritativeObjectInventory { $authoritative }
            Mock Invoke-DatabaseDump {}
            Mock Write-TemplateRestoreManifest { "restore-manifest.json" }
            Mock New-DatabaseTemplateCsproj {}
            Mock Add-FileToCsProjForNuget {}
            Mock Build-NuGetPackage {}

            Build-TemplateNuGetPackage -ConfigFilePath $configPath -StandardVersion "5.2.0" -PackageVersion "1.0.0" -TemplateKind "Minimal" `
                -DumpAllUserSchemas -SchemaFilePath @("ApiSchema.json", "ApiSchema.tpdm.json")

            Should -Invoke Get-TemplateAuthoritativeObjectInventory -Times 1 -Exactly -ParameterFilter {
                @($SchemaFilePath) -join "|" -eq "ApiSchema.json|ApiSchema.tpdm.json" -and $DatabaseEngine -eq "postgresql"
            }
            Should -Invoke Invoke-DatabaseDump -Times 1 -Exactly
            Should -Invoke Build-NuGetPackage -Times 1 -Exactly
        }
    }

    It "refuses to package without -SchemaFilePath, before deriving anything or dumping" {
        InModuleScope Template-Management -Parameters @{
            configPath    = (Join-Path (Split-Path $PSScriptRoot -Parent) "MinimalTemplateSettings.psd1")
            authoritative = $script:authoritativeInventory
        } {
            param($configPath, $authoritative)

            $configPath | Should -Exist
            $authoritative.schemas | Should -Not -BeNullOrEmpty

            Mock Get-TemplateSourceCatalogFacts { [pscustomobject]@{ DatabaseLevelObjects = @(); FullInventory = $authoritative } }
            Mock Get-UserSchemaNames { @("auth", "dms", "edfi", "tracked_changes_edfi") }
            Mock Get-TemplateAuthoritativeObjectInventory { $authoritative }
            Mock Invoke-DatabaseDump {}
            Mock Build-NuGetPackage {}

            { Build-TemplateNuGetPackage -ConfigFilePath $configPath -StandardVersion "5.2.0" -PackageVersion "1.0.0" -TemplateKind "Minimal" -DumpAllUserSchemas } |
                Should -Throw "*cannot be packaged without -SchemaFilePath*"

            Should -Invoke Get-TemplateAuthoritativeObjectInventory -Times 0 -Exactly
            Should -Invoke Invoke-DatabaseDump -Times 0 -Exactly
            Should -Invoke Build-NuGetPackage -Times 0 -Exactly
        }
    }

    It "derives the authoritative inventory through the restore consumer's own derivation" {
        # A missing schema tool fails inside Get-RestoreAuthoritativeObjectInventory's resolver,
        # which proves the producer reaches the same derivation the consumer uses.
        InModuleScope Template-Management -Parameters @{ outputRoot = $TestDrive } {
            param($outputRoot)

            $outputRoot | Should -Exist
            { Get-TemplateAuthoritativeObjectInventory -SchemaFilePath @("ApiSchema.json") -DatabaseEngine postgresql -OutputRoot $outputRoot -SchemaToolPath (Join-Path $outputRoot "absent-tool") } |
                Should -Throw "*api-schema-tools executable was not found*"
        }
    }

    It "Build-Template forwards -SchemaFilePath to the package build" {
        InModuleScope Template-Management {
            Mock Add-CmsClient {}
            Mock Get-CmsToken { return "cms-token" }
            Mock Get-DataStore { return @() }
            Mock Resolve-DataStoreIdForTemplate { return [long]5 }
            Mock Find-CmsVendorByCompany { return [pscustomobject]@{ id = 23; company = 'Demo Vendor' } }
            Mock Add-Application { return @{ Id = 1; Key = "key"; Secret = "secret" } }
            Mock Get-DmsToken { return "dms-token" }
            Mock Invoke-SchoolYearLoader {}
            Mock Initialize-BulkLoad { return @{} }
            Mock Get-TemplateBulkLoadTuning { return @{} }
            Mock Invoke-BulkLoad {}
            Mock Build-TemplateNuGetPackage {}

            Build-Template -TemplateType Minimal -DmsUrl "http://dms" -CmsUrl "http://cms" -MinimalSampleDataDirectory "minimal-dir" `
                -Extension "ed-fi" -ConfigFilePath "config.psd1" -StandardVersion "5.2.0" -PackageVersion "1.0.0" `
                -SchemaFilePath @("ApiSchema.json", "ApiSchema.tpdm.json")

            Should -Invoke Build-TemplateNuGetPackage -Times 1 -Exactly -ParameterFilter {
                @($SchemaFilePath) -join "|" -eq "ApiSchema.json|ApiSchema.tpdm.json"
            }
        }
    }
}

Describe "Build-TemplateNuGetPackage PostgreSQL privilege gate" {
    BeforeAll {
        $script:templatesDir = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
        Push-Location $script:templatesDir
        try {
            Import-Module (Join-Path $script:templatesDir "Template-Management.psm1") -Force
        }
        finally {
            Pop-Location
        }

        $script:sourceInventory = @{
            schemas    = @(
                @{ schemaName = "dms"; objects = @(@{ name = "Document"; type = "table" }) },
                @{ schemaName = "edfi"; objects = @(@{ name = "School"; type = "table" }) },
                @{ schemaName = "public"; objects = @() },
                @{ schemaName = "tracked_changes_edfi"; objects = @(@{ name = "School"; type = "table" }) }
            )
            principals = @()
        }
        # The privilege rows a source provisioned from the authoritative DDL holds (shape and
        # non-owner grants taken from a live PostgreSQL 18 provisioning of the generated DDL).
        $script:authoritativePrivilegeRows = @(
            'function|EXECUTE|edfi_dms_enqueue_owner|edfi_dms_enqueue_owner|dms."TF_Document_EnqueueProjectionInsert"()',
            'function|EXECUTE|postgres|-|dms.uuidv5(namespace_uuid uuid, name_text text)',
            'function|EXECUTE|postgres|postgres|dms.uuidv5(namespace_uuid uuid, name_text text)',
            'relation|INSERT|postgres|edfi_dms_enqueue_owner|dms."DocumentProjectionWork"',
            'relation|SELECT|postgres|edfi_dms_enqueue_owner|dms."DocumentCacheState"',
            'relation|SELECT|postgres|edfi_dms_enqueue_owner|dms."DocumentProjectionWork"',
            'relation|SELECT|postgres|postgres|dms."Document"',
            'relation|UPDATE|postgres|edfi_dms_enqueue_owner|dms."DocumentProjectionWork"',
            'schema|CREATE|pg_database_owner|pg_database_owner|public',
            'schema|USAGE|pg_database_owner|-|public',
            'schema|USAGE|postgres|edfi_dms_enqueue_owner|dms',
            'schema|USAGE|postgres|postgres|dms',
            'type|USAGE|postgres|-|dms."Status"'
        )
    }

    It "dumps a source holding only the privileges the authoritative DDL grants" {
        InModuleScope Template-Management -Parameters @{
            configPath = (Join-Path (Split-Path $PSScriptRoot -Parent) "MinimalTemplateSettings.psd1")
            inventory  = $script:sourceInventory
            rows       = $script:authoritativePrivilegeRows
        } {
            param($configPath, $inventory, $rows)

            $sourceFacts = [pscustomobject]@{ DatabaseLevelObjects = @(); FullInventory = $inventory }
            $privilegeRows = @($rows)
            Mock Get-TemplateSourceCatalogFacts { $sourceFacts }
            Mock Get-UserSchemaNames { @("dms", "edfi", "tracked_changes_edfi") }
            Mock Get-TemplateAuthoritativeObjectInventory { $sourceFacts.FullInventory }
            Mock Invoke-TemplateCatalogQuery { $privilegeRows }
            Mock Invoke-DatabaseDump {}
            Mock Write-TemplateRestoreManifest { "restore-manifest.json" }
            Mock New-DatabaseTemplateCsproj {}
            Mock Add-FileToCsProjForNuget {}
            Mock Build-NuGetPackage {}

            Build-TemplateNuGetPackage -ConfigFilePath $configPath -StandardVersion "5.2.0" -PackageVersion "1.0.0" -TemplateKind "Minimal" `
                -DumpAllUserSchemas -SchemaFilePath @("ApiSchema.json")

            Should -Invoke Invoke-TemplateCatalogQuery -Times 1 -Exactly -ParameterFilter {
                $Query -ceq (Get-PostgresqlPrivilegeQuerySql) -and $DatabaseName -ceq "edfi_datamanagementservice"
            }
            Should -Invoke Invoke-DatabaseDump -Times 1 -Exactly
        }
    }

    It "refuses a source carrying <Name> before any dump" -ForEach @(
        @{ Name = "a grant to an unrelated cluster role"; Row = 'relation|SELECT|postgres|reporting_reader|dms."Document"'; Violation = 'relation dms."Document" SELECT to reporting_reader (owner postgres)' }
        @{ Name = "a column grant to an unrelated cluster role"; Row = 'column|SELECT|postgres|reporting_reader|dms."Document"."DocumentId"'; Violation = 'column dms."Document"."DocumentId" SELECT to reporting_reader (owner postgres)' }
        @{ Name = "a table grant to PUBLIC"; Row = 'relation|SELECT|postgres|-|edfi."School"'; Violation = 'relation edfi."School" SELECT to PUBLIC (owner postgres)' }
        @{ Name = "an enqueue function still executable by PUBLIC"; Row = 'function|EXECUTE|edfi_dms_enqueue_owner|-|dms."TF_Document_EnqueueProjectionInsert"()'; Violation = 'function dms."TF_Document_EnqueueProjectionInsert"() EXECUTE to PUBLIC (owner edfi_dms_enqueue_owner)' }
        @{ Name = "an enqueue-owner grant the DDL does not make"; Row = 'relation|DELETE|postgres|edfi_dms_enqueue_owner|dms."DocumentProjectionWork"'; Violation = 'relation dms."DocumentProjectionWork" DELETE to edfi_dms_enqueue_owner (owner postgres)' }
        @{ Name = "the enqueue owner's transient CREATE on dms left in place"; Row = 'schema|CREATE|postgres|edfi_dms_enqueue_owner|dms'; Violation = 'schema dms CREATE to edfi_dms_enqueue_owner (owner postgres)' }
    ) {
        InModuleScope Template-Management -Parameters @{
            configPath = (Join-Path (Split-Path $PSScriptRoot -Parent) "MinimalTemplateSettings.psd1")
            inventory  = $script:sourceInventory
            rows       = @($script:authoritativePrivilegeRows) + @($Row)
            violation  = $Violation
        } {
            param($configPath, $inventory, $rows, $violation)

            $configPath | Should -Exist
            $sourceFacts = [pscustomobject]@{ DatabaseLevelObjects = @(); FullInventory = $inventory }
            $privilegeRows = @($rows)
            Mock Get-TemplateSourceCatalogFacts { $sourceFacts }
            Mock Get-UserSchemaNames { @("dms", "edfi", "tracked_changes_edfi") }
            Mock Get-TemplateAuthoritativeObjectInventory { $sourceFacts.FullInventory }
            Mock Invoke-TemplateCatalogQuery { $privilegeRows }
            Mock Invoke-DatabaseDump {}
            Mock Build-NuGetPackage {}

            {
                Build-TemplateNuGetPackage -ConfigFilePath $configPath -StandardVersion "5.2.0" -PackageVersion "1.0.0" -TemplateKind "Minimal" `
                    -DumpAllUserSchemas -SchemaFilePath @("ApiSchema.json")
            } | Should -Throw "Template source database 'edfi_datamanagementservice' carries privileges the authoritative DMS DDL does not grant: $violation. *Nothing was dumped or packaged.*"

            Should -Invoke Invoke-DatabaseDump -Times 0 -Exactly
            Should -Invoke Build-NuGetPackage -Times 0 -Exactly
        }
    }
}

Describe "Build-TemplateNuGetPackage SQL Server security-state gate" {
    BeforeAll {
        $script:templatesDir = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
        Push-Location $script:templatesDir
        try {
            Import-Module (Join-Path $script:templatesDir "Template-Management.psm1") -Force
        }
        finally {
            Pop-Location
        }

        $script:mssqlSourceInventory = @{
            schemas    = @(
                @{ schemaName = "dbo"; objects = @() },
                @{ schemaName = "dms"; objects = @(@{ name = "Document"; type = "table" }) },
                @{ schemaName = "edfi"; objects = @(@{ name = "School"; type = "table" }) },
                @{ schemaName = "tracked_changes_edfi"; objects = @(@{ name = "School"; type = "table" }) }
            )
            principals = @()
        }
        # A new SQL Server database's security state (read live from SQL Server 2025; one of its
        # system catalog grants stands for all of them).
        $script:newDatabaseSecurityRows = @(
            "member|16384|1|db_owner|dbo",
            "permission|0|CONNECT|GRANT|1|0|0|dbo|DATABASE",
            "permission|0|VIEW ANY COLUMN ENCRYPTION KEY DEFINITION|GRANT|0|0|0|public|DATABASE",
            "permission|0|VIEW ANY COLUMN MASTER KEY DEFINITION|GRANT|0|0|0|public|DATABASE",
            "permission|1|SELECT|GRANT|0|-385|0|public|OBJECT::sys.objects"
        )
    }

    It "backs up a source holding only a new database's security state, read from the source with its password" {
        InModuleScope Template-Management -Parameters @{
            configPath = (Join-Path (Split-Path $PSScriptRoot -Parent) "MinimalTemplateSettings.psd1")
            inventory  = $script:mssqlSourceInventory
            rows       = $script:newDatabaseSecurityRows
        } {
            param($configPath, $inventory, $rows)

            $sourceFacts = [pscustomobject]@{ DatabaseLevelObjects = @(); FullInventory = $inventory }
            $securityRows = @($rows)
            Mock Get-TemplateSourceCatalogFacts { $sourceFacts }
            Mock Get-TemplateAuthoritativeObjectInventory { $sourceFacts.FullInventory }
            Mock Invoke-TemplateCatalogQuery { $securityRows }
            Mock Invoke-DatabaseDump {}
            Mock Write-TemplateRestoreManifest { "restore-manifest.json" }
            Mock New-DatabaseTemplateCsproj {}
            Mock Add-FileToCsProjForNuget {}
            Mock Build-NuGetPackage {}

            Build-TemplateNuGetPackage -ConfigFilePath $configPath -StandardVersion "5.2.0" -PackageVersion "1.0.0" -TemplateKind "Minimal" `
                -DatabaseEngine mssql -MssqlPassword "Source_Pw1!" -SchemaFilePath @("ApiSchema.json")

            Should -Invoke Invoke-TemplateCatalogQuery -Times 1 -Exactly -ParameterFilter {
                $Query -ceq (Get-MssqlSecurityStateQuerySql) -and $DatabaseEngine -ceq "mssql" -and $ContainerName -ceq "dms-mssql" -and
                $DatabaseName -ceq "edfi_datamanagementservice" -and $MssqlPassword -ceq "Source_Pw1!"
            }
            Should -Invoke Invoke-DatabaseDump -Times 1 -Exactly
            Should -Invoke Build-NuGetPackage -Times 1 -Exactly
        }
    }

    It "refuses a source carrying <Name> before any backup, manifest, or package" -ForEach @(
        @{ Name = "CONNECT granted to guest"; Rows = @("permission|0|CONNECT|GRANT|2|0|0|guest|DATABASE"); Violation = "GRANT CONNECT ON DATABASE TO guest" }
        @{ Name = "SELECT on a DMS schema granted to public"; Rows = @("permission|3|SELECT|GRANT|0|5|0|public|SCHEMA::edfi"); Violation = "GRANT SELECT ON SCHEMA::edfi TO public" }
        @{ Name = "both grants together"; Rows = @("permission|0|CONNECT|GRANT|2|0|0|guest|DATABASE", "permission|3|SELECT|GRANT|0|5|0|public|SCHEMA::edfi"); Violation = "GRANT CONNECT ON DATABASE TO guest, GRANT SELECT ON SCHEMA::edfi TO public" }
        @{ Name = "guest as a member of db_datareader"; Rows = @("member|16390|2|db_datareader|guest"); Violation = "role db_datareader member guest" }
    ) {
        InModuleScope Template-Management -Parameters @{
            configPath = (Join-Path (Split-Path $PSScriptRoot -Parent) "MinimalTemplateSettings.psd1")
            inventory  = $script:mssqlSourceInventory
            rows       = @($script:newDatabaseSecurityRows) + $Rows
            violation  = $Violation
        } {
            param($configPath, $inventory, $rows, $violation)

            $configPath | Should -Exist
            $sourceFacts = [pscustomobject]@{ DatabaseLevelObjects = @(); FullInventory = $inventory }
            $securityRows = @($rows)
            Mock Get-TemplateSourceCatalogFacts { $sourceFacts }
            Mock Get-TemplateAuthoritativeObjectInventory { $sourceFacts.FullInventory }
            Mock Invoke-TemplateCatalogQuery { $securityRows }
            Mock Invoke-DatabaseDump {}
            Mock Write-TemplateRestoreManifest {}
            Mock New-DatabaseTemplateCsproj {}
            Mock Build-NuGetPackage {}

            {
                Build-TemplateNuGetPackage -ConfigFilePath $configPath -StandardVersion "5.2.0" -PackageVersion "1.0.0" -TemplateKind "Minimal" `
                    -DatabaseEngine mssql -SchemaFilePath @("ApiSchema.json")
            } | Should -Throw "Template source database 'edfi_datamanagementservice' carries permissions or role memberships the authoritative DMS DDL does not produce: $violation. *Nothing was dumped or packaged."

            Should -Invoke Invoke-DatabaseDump -Times 0 -Exactly
            Should -Invoke Write-TemplateRestoreManifest -Times 0 -Exactly
            Should -Invoke New-DatabaseTemplateCsproj -Times 0 -Exactly
            Should -Invoke Build-NuGetPackage -Times 0 -Exactly
        }
    }
}

# The synthetic inventories above cannot show that the catalog query DISCOVERS an object; this
# live suite runs the real query, through each side's own transport, against databases built
# from one DDL, and feeds the results to the real producer and consumer gates. OPT-IN: each
# engine row self-skips unless its DISPOSABLE fixture container is configured (the suite
# creates and drops its own generated databases there):
#   DMS_TEMPLATE_INVENTORY_PG_CONTAINER      (superuser 'postgres')
#   DMS_TEMPLATE_INVENTORY_MSSQL_CONTAINER   (+ DMS_TEMPLATE_INVENTORY_MSSQL_SA_PASSWORD)
Describe "Authoritative inventory table constraints and indexes against a live <Engine> catalog" -ForEach @(
    @{
        Engine             = "postgresql"
        Container          = $env:DMS_TEMPLATE_INVENTORY_PG_CONTAINER
        AdminDatabase      = "postgres"
        SourceHistory      = @()
        # The unique expression index has the generated UX_Descriptor_ResourceKeyId_Uri shape.
        SchemaBatches      = @(
            'CREATE SCHEMA dms;
             CREATE TABLE dms."Document" ("DocumentId" bigint PRIMARY KEY, "ResourceKeyId" smallint NOT NULL, CONSTRAINT "CK_Positive" CHECK ("DocumentId" > 0));
             CREATE TABLE dms."Alias" ("AliasId" bigint NOT NULL, "DocumentId" bigint NOT NULL REFERENCES dms."Document" ("DocumentId"), CONSTRAINT "PK_Alias" PRIMARY KEY ("AliasId"));
             CREATE INDEX "IX_Alias_DocumentId" ON dms."Alias" ("DocumentId");
             CREATE UNIQUE INDEX "UX_Document_ResourceKeyId_Uri" ON dms."Document" ("ResourceKeyId", ("DocumentId"::text || ''#''));
             CREATE TYPE dms."Status" AS ENUM (''active'');
             CREATE TYPE dms."Pair" AS ("Left" int, "Right" int);
             INSERT INTO dms."Document" VALUES (1, 1);'
        )
        # Constraint-backed indexes (Document_pkey, PK_Alias) appear once, as their constraint.
        # The standalone types appear once each; their generated array types and the tables'
        # row and array types do not appear at all.
        ExpectedAttached   = @("Document.Document_pkey|primarykey", "Document.CK_Positive|check", "Alias.PK_Alias|primarykey", "Alias.Alias_DocumentId_fkey|foreignkey",
            "Alias.IX_Alias_DocumentId|index", "Document.UX_Document_ResourceKeyId_Uri|uniqueindex", "Status|enum", "Pair|compositetype")
        Contamination      = @(
            @{ Index = 0; Name = "a NOT VALID check on an expected table"; Sql = 'ALTER TABLE dms."Document" ADD CONSTRAINT "RejectNewDocuments" CHECK ("DocumentId" < 0) NOT VALID;'; Extra = "dms.Document.RejectNewDocuments (check)" }
            @{ Index = 1; Name = "a constraint reusing another table's constraint name"; Sql = 'ALTER TABLE dms."Alias" ADD CONSTRAINT "CK_Positive" CHECK ("AliasId" < 0) NOT VALID;'; Extra = "dms.Alias.CK_Positive (check)" }
            @{ Index = 2; Name = "a missing required unique index"; Sql = 'DROP INDEX dms."UX_Document_ResourceKeyId_Uri";'; Missing = "dms.Document.UX_Document_ResourceKeyId_Uri (uniqueindex)" }
            @{ Index = 3; Name = "an unexpected index on an expected table"; Sql = 'CREATE INDEX "IX_Extra" ON dms."Document" ("ResourceKeyId");'; Extra = "dms.Document.IX_Extra (index)" }
            @{ Index = 4; Name = "a required unique index recreated without uniqueness"; Sql = 'DROP INDEX dms."UX_Document_ResourceKeyId_Uri"; CREATE INDEX "UX_Document_ResourceKeyId_Uri" ON dms."Document" ("ResourceKeyId", ("DocumentId"::text || ''#''));'; Extra = "dms.Document.UX_Document_ResourceKeyId_Uri (index)"; Missing = "dms.Document.UX_Document_ResourceKeyId_Uri (uniqueindex)" }
            @{ Index = 5; Name = "a shell type in an allowed schema"; Sql = 'CREATE TYPE dms.extra;'; Extra = "dms.extra (shelltype)" }
            # The I/O functions live in public, so the base type is the first extra reported.
            @{ Index = 6; Name = "a standalone base type in an allowed schema"; Sql = 'CREATE TYPE dms.extrabase;
                CREATE FUNCTION public.extrabase_in(cstring) RETURNS dms.extrabase AS ''int4in'' LANGUAGE internal IMMUTABLE STRICT;
                CREATE FUNCTION public.extrabase_out(dms.extrabase) RETURNS cstring AS ''int4out'' LANGUAGE internal IMMUTABLE STRICT;
                CREATE TYPE dms.extrabase (INPUT = public.extrabase_in, OUTPUT = public.extrabase_out, LIKE = int4);'; Extra = "dms.extrabase (basetype)"; ExtraAbsent = "_extrabase" }
        )
    }
    @{
        Engine             = "mssql"
        Container          = $env:DMS_TEMPLATE_INVENTORY_MSSQL_CONTAINER
        AdminDatabase      = "master"
        # Source databases get a different object-id history than the expected replay, as a
        # real source does; SQL Server derives the generated names from object ids.
        SourceHistory      = @("CREATE TABLE dbo.ShiftObjectIds (Id int); DROP TABLE dbo.ShiftObjectIds;")
        SchemaBatches      = @(
            "CREATE SCHEMA dms;",
            "CREATE TABLE dms.Document (DocumentId bigint NOT NULL CONSTRAINT PK_Document PRIMARY KEY, ResourceKeyId smallint NOT NULL CHECK (ResourceKeyId > 0), ContentVersion bigint NOT NULL DEFAULT (0));
             CREATE TABLE dms.Alias (AliasId bigint NOT NULL PRIMARY KEY, DocumentId bigint NOT NULL CONSTRAINT FK_Alias_Document REFERENCES dms.Document (DocumentId));
             CREATE INDEX IX_Alias_DocumentId ON dms.Alias (DocumentId);
             CREATE UNIQUE INDEX UX_Document_ResourceKeyId_Uri ON dms.Document (ResourceKeyId, DocumentId);
             INSERT INTO dms.Document (DocumentId, ResourceKeyId) VALUES (1, 1);"
        )
        # The unnamed check, default, and primary key are system-named: keyed by column, not name.
        # Primary key indexes appear once, as their constraint.
        ExpectedAttached   = @("Document.PK_Document|primarykey", "Document.#system(ResourceKeyId)|check", "Document.#system(ContentVersion)|default", "Alias.#system()|primarykey", "Alias.FK_Alias_Document|foreignkey",
            "Alias.IX_Alias_DocumentId|index", "Document.UX_Document_ResourceKeyId_Uri|uniqueindex")
        Contamination      = @(
            @{ Index = 0; Name = "a WITH NOCHECK check on an expected table"; Sql = "ALTER TABLE [dms].[Document] WITH NOCHECK ADD CONSTRAINT [RejectNewDocuments] CHECK ([DocumentId] < 0);"; Extra = "dms.Document.RejectNewDocuments (check)" }
            @{ Index = 1; Name = "an unnamed WITH NOCHECK check"; Sql = "ALTER TABLE [dms].[Alias] WITH NOCHECK ADD CHECK ([AliasId] < 0);"; Extra = "dms.Alias.#system(AliasId) (check)" }
            @{ Index = 2; Name = "a missing required unique index"; Sql = "DROP INDEX [UX_Document_ResourceKeyId_Uri] ON [dms].[Document];"; Missing = "dms.Document.UX_Document_ResourceKeyId_Uri (uniqueindex)" }
            @{ Index = 3; Name = "an unexpected index on an expected table"; Sql = "CREATE INDEX [IX_Extra] ON [dms].[Document] ([ResourceKeyId]);"; Extra = "dms.Document.IX_Extra (index)" }
            @{ Index = 4; Name = "a required unique index recreated without uniqueness"; Sql = "DROP INDEX [UX_Document_ResourceKeyId_Uri] ON [dms].[Document]; CREATE INDEX [UX_Document_ResourceKeyId_Uri] ON [dms].[Document] ([ResourceKeyId], [DocumentId]);"; Extra = "dms.Document.UX_Document_ResourceKeyId_Uri (index)"; Missing = "dms.Document.UX_Document_ResourceKeyId_Uri (uniqueindex)" }
        )
    }
) {
    BeforeAll {
        if ([string]::IsNullOrWhiteSpace($Container)) { return }

        # Template-RestoreCore first, forced: the producer and consumer modules import it without
        # -Force, so they would otherwise keep whatever copy this session loaded earlier.
        $script:templatesDir = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
        Import-Module (Join-Path $script:templatesDir "Template-RestoreCore.psm1") -Force
        Push-Location $script:templatesDir
        try {
            Import-Module (Join-Path $script:templatesDir "Template-Management.psm1") -Force
        }
        finally {
            Pop-Location
        }
        Import-Module (Join-Path $script:templatesDir "../docker-compose/bootstrap-restore.psm1") -Force

        $script:live = @{
            Engine    = $Engine
            Container = $Container
            Password  = $env:DMS_TEMPLATE_INVENTORY_MSSQL_SA_PASSWORD
        }

        function script:Invoke-LiveQuery {
            param([string]$Database, [string]$Query)
            Invoke-RestoreCatalogQuery -DatabaseEngine $script:live.Engine -ContainerName $script:live.Container -DatabaseName $Database `
                -MssqlPassword $script:live.Password -Query $Query -FailureMessage "Live inventory fixture query failed against '$Database'."
        }

        # Reads a database's inventory with the producer's transport or the consumer's.
        function script:Read-LiveInventory {
            param([string]$Database, [switch]$Producer)
            $runQuery = if ($Producer) {
                {
                    param($database, $query)
                    InModuleScope Template-Management -Parameters @{ live = $script:live; database = $database; query = $query } {
                        param($live, $database, $query)
                        Invoke-TemplateCatalogQuery -DatabaseEngine $live.Engine -ContainerName $live.Container -DatabaseName $database `
                            -MssqlPassword $live.Password -Query $query -FailureMessage "Producer inventory read failed against '$database'."
                    }
                }
            }
            else {
                { param($database, $query) Invoke-LiveQuery -Database $database -Query $query }
            }

            ConvertFrom-InventoryQueryRow `
                -SchemaRow (& $runQuery $Database (Get-InventorySchemaQuerySql -DatabaseEngine $script:live.Engine -Purpose InventoryEnumeration)) `
                -ObjectRow (& $runQuery $Database (Get-InventoryObjectQuerySql -DatabaseEngine $script:live.Engine))
        }

        # The real producer gate, with only its schema-file derivation replaced by the live
        # expected database's inventory.
        function script:Invoke-LiveProducerGate {
            param($SourceInventory)
            InModuleScope Template-Management -Parameters @{ engine = $script:live.Engine; expected = $script:expectedInventory; source = $SourceInventory } {
                param($engine, $expected, $source)
                $expected.schemas | Should -Not -BeNullOrEmpty
                Mock Get-TemplateAuthoritativeObjectInventory { $expected }
                Assert-TemplateSourceMatchesAuthoritativeInventory -Facts ([pscustomobject]@{ FullInventory = $source }) -DatabaseEngine $engine `
                    -DatabaseName "edfi_datamanagementservice" -SchemaFilePath @("ApiSchema.json")
            }
        }

        $prefix = "dms_inventory_probe_$([Guid]::NewGuid().ToString('N').Substring(0, 12))"
        $script:databases = [ordered]@{ expected = "${prefix}_expected"; clean = "${prefix}_clean" }
        foreach ($row in $Contamination) { $script:databases["c$($row.Index)"] = "${prefix}_c$($row.Index)" }
        # PostgreSQL only: a database whose one difference is a grant to an unrelated cluster role.
        $script:grantRole = $null
        if ($Engine -eq "postgresql") { $script:databases["grant"] = "${prefix}_grant" }
        # SQL Server only: databases whose one difference is guest/public access, or a role
        # membership of a built-in principal.
        if ($Engine -eq "mssql") {
            $script:databases["access"] = "${prefix}_access"
            $script:databases["member"] = "${prefix}_member"
        }

        foreach ($database in $script:databases.Values) {
            $createSql = if ($Engine -eq "mssql") { "CREATE DATABASE [$database];" } else { "CREATE DATABASE `"$database`";" }
            $null = Invoke-LiveQuery -Database $AdminDatabase -Query $createSql
            $history = if ($database -ceq $script:databases.expected) { @() } else { $SourceHistory }
            foreach ($batch in @($history) + @($SchemaBatches)) { $null = Invoke-LiveQuery -Database $database -Query $batch }
        }
        foreach ($row in $Contamination) { $null = Invoke-LiveQuery -Database $script:databases["c$($row.Index)"] -Query $row.Sql }
        if ($Engine -eq "postgresql") {
            $script:grantRole = "${prefix}_reader"
            $null = Invoke-LiveQuery -Database $AdminDatabase -Query "CREATE ROLE `"$($script:grantRole)`" NOLOGIN;"
            $null = Invoke-LiveQuery -Database $script:databases["grant"] -Query "GRANT SELECT ON dms.`"Document`" TO `"$($script:grantRole)`";"
        }
        if ($Engine -eq "mssql") {
            $null = Invoke-LiveQuery -Database $script:databases["access"] -Query "GRANT CONNECT TO guest; GRANT SELECT ON SCHEMA::dms TO public;"
            $null = Invoke-LiveQuery -Database $script:databases["member"] -Query "ALTER ROLE db_datareader ADD MEMBER guest;"
        }

        $script:expectedInventory = Read-LiveInventory -Database $script:databases.expected
    }

    AfterAll {
        if ([string]::IsNullOrWhiteSpace($Container) -or $null -eq $script:databases) { return }
        foreach ($database in $script:databases.Values) {
            $dropSql = if ($Engine -eq "mssql") { "DROP DATABASE IF EXISTS [$database];" } else { "DROP DATABASE IF EXISTS `"$database`" WITH (FORCE);" }
            $null = Invoke-LiveQuery -Database $AdminDatabase -Query $dropSql
        }
        if ($null -ne $script:grantRole) {
            $null = Invoke-LiveQuery -Database $AdminDatabase -Query "DROP ROLE IF EXISTS `"$($script:grantRole)`";"
        }
    }

    It "discovers the table constraints and indexes and derives the same inventory from two databases built from one DDL" -Skip:([string]::IsNullOrWhiteSpace($Container)) {
        $dmsObjects = @(@($script:expectedInventory.schemas | Where-Object { $_.schemaName -ceq "dms" })[0].objects |
                Where-Object { $_.type -notin @("table") } | ForEach-Object { "$($_.name)|$($_.type)" })
        @($dmsObjects | Sort-Object) | Should -Be @($ExpectedAttached | Sort-Object)

        (Compare-RestoreObjectInventory -ExpectedInventory $script:expectedInventory -ActualInventory (Read-LiveInventory -Database $script:databases.clean)).IsMatch |
            Should -BeTrue

        if ($Engine -eq "mssql") {
            # The keying above is load-bearing: the two databases' generated names really differ.
            $nameSql = "SET NOCOUNT ON; SELECT name FROM sys.default_constraints;"
            (Invoke-LiveQuery -Database $script:databases.expected -Query $nameSql) |
                Should -Not -Be (Invoke-LiveQuery -Database $script:databases.clean -Query $nameSql)
        }
    }

    It "accepts the clean source at the producer gate and the clean scratch at the consumer gate" -Skip:([string]::IsNullOrWhiteSpace($Container)) {
        { Invoke-LiveProducerGate -SourceInventory (Read-LiveInventory -Database $script:databases.clean -Producer) } | Should -Not -Throw

        { Assert-RestoreScratchMatchesAuthoritativeInventory -AuthoritativeInventory $script:expectedInventory `
                -ScratchInventory (Read-LiveInventory -Database $script:databases.clean) -ScratchDatabaseName $script:databases.clean } |
            Should -Not -Throw
    }

    It "reads privileges live: accepts the clean database and refuses a grant to an unrelated cluster role at both gates" -Skip:([string]::IsNullOrWhiteSpace($Container) -or $Engine -ne "postgresql") {
        # The other cluster role exists either way; only the database holding a grant to it fails.
        $producerGate = {
            param($database)
            InModuleScope Template-Management -Parameters @{ container = $script:live.Container; database = $database } {
                param($container, $database)
                Assert-TemplateSourcePrivilege -DatabaseName $database -ContainerName $container
            }
        }
        $consumerGate = {
            param($database)
            Assert-PostgresqlArtifactPrivilege -PrivilegeRow (Invoke-LiveQuery -Database $database -Query (Get-PostgresqlPrivilegeQuerySql)) `
                -SourceDescription "Restore scratch database '$database'" -FailureSuffix "Refused."
        }

        { & $producerGate $script:databases.clean } | Should -Not -Throw
        { & $consumerGate $script:databases.clean } | Should -Not -Throw

        $violation = "relation dms.`"Document`" SELECT to $($script:grantRole) (owner postgres)"
        { & $producerGate $script:databases["grant"] } | Should -Throw "*carries privileges the authoritative DMS DDL does not grant: $violation.*Nothing was dumped or packaged.*"
        { & $consumerGate $script:databases["grant"] } | Should -Throw "*carries privileges the authoritative DMS DDL does not grant: $violation.*"
    }

    It "reads security state live: accepts the clean database and refuses guest/public access and a guest role membership at both gates" -Skip:([string]::IsNullOrWhiteSpace($Container) -or $Engine -ne "mssql") {
        $producerGate = {
            param($database)
            InModuleScope Template-Management -Parameters @{ live = $script:live; database = $database } {
                param($live, $database)
                Assert-TemplateSourcePrivilege -DatabaseEngine mssql -DatabaseName $database -ContainerName $live.Container -MssqlPassword $live.Password
            }
        }
        $consumerGate = {
            param($database)
            Assert-MssqlArtifactSecurityState -SecurityRow (Invoke-LiveQuery -Database $database -Query (Get-MssqlSecurityStateQuerySql)) `
                -SourceDescription "Restore scratch database '$database'" -FailureSuffix "Refused."
        }

        { & $producerGate $script:databases.clean } | Should -Not -Throw
        { & $consumerGate $script:databases.clean } | Should -Not -Throw

        $accessViolation = "GRANT CONNECT ON DATABASE TO guest, GRANT SELECT ON SCHEMA::dms TO public"
        { & $producerGate $script:databases["access"] } | Should -Throw "*carries permissions or role memberships the authoritative DMS DDL does not produce: $accessViolation.*Nothing was dumped or packaged."
        { & $consumerGate $script:databases["access"] } | Should -Throw "*carries permissions or role memberships the authoritative DMS DDL does not produce: $accessViolation.*"

        $memberViolation = "role db_datareader member guest"
        { & $producerGate $script:databases["member"] } | Should -Throw "*carries permissions or role memberships the authoritative DMS DDL does not produce: $memberViolation.*Nothing was dumped or packaged."
        { & $consumerGate $script:databases["member"] } | Should -Throw "*carries permissions or role memberships the authoritative DMS DDL does not produce: $memberViolation.*"
    }

    It "rejects <Name> at the producer gate and at the consumer gate" -ForEach $Contamination -Skip:([string]::IsNullOrWhiteSpace($Container)) {
        $database = $script:databases["c$Index"]
        # A row names what is extra, what is missing, or both; the gates report extras first.
        $producerPattern = "*"
        $consumerPattern = "*"
        if ($_.ContainsKey("Extra")) {
            $producerPattern += "objects the schema files do not define: $($_.Extra)*"
            $consumerPattern += "objects the candidate schema does not define: $($_.Extra)*"
        }
        if ($_.ContainsKey("Missing")) {
            $producerPattern += "objects the schema files define but the source lacks: $($_.Missing)*"
            $consumerPattern += "objects the candidate schema defines but the artifact lacks: $($_.Missing)*"
        }

        { Invoke-LiveProducerGate -SourceInventory (Read-LiveInventory -Database $database -Producer) } |
            Should -Throw "$($producerPattern)Nothing was dumped or packaged*"

        { Assert-RestoreScratchMatchesAuthoritativeInventory -AuthoritativeInventory $script:expectedInventory `
                -ScratchInventory (Read-LiveInventory -Database $database) -ScratchDatabaseName $database } |
            Should -Throw $consumerPattern

        if ($_.ContainsKey("ExtraAbsent")) {
            # A type PostgreSQL generates for the extra object is not inventoried on its own.
            $absentName = $_.ExtraAbsent
            @((Read-LiveInventory -Database $database).schemas | ForEach-Object { $_.objects } | Where-Object { $_.name -ceq $absentName }) |
                Should -BeNullOrEmpty
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

    BeforeEach {
        # The privilege gate reads the live source; its own tests cover it.
        Mock Assert-TemplateSourcePrivilege {} -ModuleName Template-Management
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
                    DatabaseLevelObjects       = @()
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
                Mock Get-TemplateAuthoritativeObjectInventory { $cleanFacts.FullInventory }
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
                    -SchemaFilePath "ApiSchema.json" `
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
                    DatabaseLevelObjects       = @()
                    ArtifactInventory          = @{
                        schemas    = @(@{ schemaName = "dms"; objects = @(@{ name = "Document"; type = "table" }) }, @{ schemaName = "edfi"; objects = @(@{ name = "School"; type = "table" }) }, @{ schemaName = "tracked_changes_edfi"; objects = @(@{ name = "School"; type = "table" }) }, @{ schemaName = "public"; objects = @() })
                        principals = @()
                    }
                }

                Mock Get-TemplateSourceCatalogFacts { $cleanFacts }
                Mock Get-UserSchemaNames { @("dms", "edfi", "tracked_changes_edfi") }
                Mock Get-TemplateAuthoritativeObjectInventory { $cleanFacts.FullInventory }
                Mock Invoke-DatabaseDump {
                    Set-Content -LiteralPath (Join-Path $BackupDirectory $BackupFileName) -Value "fake artifact bytes"
                }
                Mock New-DatabaseTemplateCsproj {}
                Mock Add-FileToCsProjForNuget {}
                Mock Build-NuGetPackage {}
                Mock Build-TemplateAttestationCompanionPackage {}

                Build-TemplateNuGetPackage -ConfigFilePath $configPath -StandardVersion "5.2.0" -PackageVersion "1.0.123" -TemplateKind "Minimal" -DumpAllUserSchemas -SchemaFilePath "ApiSchema.json"

                @(Get-ChildItem -Path "." -Filter "*.attestation.json").Count | Should -Be 0
                Should -Invoke Build-TemplateAttestationCompanionPackage -Times 0 -Exactly
            }
        }
        finally {
            Pop-Location
        }
    }

    It "never attests a stale same-version package when <Name>" -ForEach @(
        @{ Name = "dotnet restore fails"; RestoreExit = 1; PackExit = 0; Expected = "*dotnet restore failed*" }
        @{ Name = "dotnet pack fails"; RestoreExit = 0; PackExit = 1; Expected = "*dotnet pack failed*" }
        @{ Name = "dotnet pack succeeds without writing the package"; RestoreExit = 0; PackExit = 0; Expected = "*did not write the template package*" }
    ) {
        $workDir = Join-Path $TestDrive ([Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $workDir -Force | Out-Null
        Push-Location $workDir
        try {
            InModuleScope Template-Management -Parameters @{
                configPath  = (Join-Path (Split-Path $PSScriptRoot -Parent) "MinimalTemplateSettings.psd1")
                workDir     = $workDir
                restoreExit = $RestoreExit
                packExit    = $PackExit
                expected    = $Expected
            } {
                param($configPath, $workDir, $restoreExit, $packExit, $expected)

                $configPath | Should -Exist
                $exitCodes = @{ restore = $restoreExit; pack = $packExit }
                $signingKey = New-TemplateAttestationSigningKey -PrivateKeyPath (Join-Path $workDir "signer.pem")
                $inventory = @{
                    schemas    = @(@{ schemaName = "dms"; objects = @(@{ name = "Document"; type = "table" }) }, @{ schemaName = "edfi"; objects = @(@{ name = "School"; type = "table" }) }, @{ schemaName = "tracked_changes_edfi"; objects = @(@{ name = "School"; type = "table" }) }, @{ schemaName = "public"; objects = @() })
                    principals = @()
                }
                $cleanFacts = [pscustomobject]@{
                    ApiSchemaFormatVersion     = "1.0.0"
                    EffectiveSchemaHash        = ("ab" * 32)
                    ResourceKeyCount           = 42
                    ResourceKeySeedHashB64     = [System.Convert]::ToBase64String([System.Convert]::FromHexString(("cd" * 32)))
                    EngineVersion              = "16.8"
                    DatabaseCompatibilityLevel = $null
                    DocumentJsonColumnType     = "jsonb"
                    FullInventory              = $inventory
                    DatabaseLevelObjects       = @()
                    ArtifactInventory          = $inventory
                }

                Mock Get-TemplateSourceCatalogFacts { $cleanFacts }
                Mock Get-UserSchemaNames { @("dms", "edfi", "tracked_changes_edfi") }
                Mock Get-TemplateAuthoritativeObjectInventory { $cleanFacts.FullInventory }
                Mock Invoke-DatabaseDump {
                    Set-Content -LiteralPath (Join-Path $BackupDirectory $BackupFileName) -Value "fake artifact bytes"
                }
                Mock New-DatabaseTemplateCsproj {}
                Mock Add-FileToCsProjForNuget {}
                Mock Invoke-TemplatePackageAttestation {}
                Mock dotnet {
                    $global:LASTEXITCODE = if (@($args)[0] -eq "restore") { $exitCodes.restore } else { $exitCodes.pack }
                }

                # An earlier run left a package with the same id and version in the workspace.
                $stalePackagePath = Join-Path $workDir "EdFi.Api.Minimal.Template.PostgreSql.5.2.0.1.0.123.nupkg"
                Set-Content -LiteralPath $stalePackagePath -Value "stale packed bytes"

                {
                    Build-TemplateNuGetPackage `
                        -ConfigFilePath $configPath `
                        -StandardVersion "5.2.0" `
                        -PackageVersion "1.0.123" `
                        -TemplateKind "Minimal" `
                        -DumpAllUserSchemas `
                        -SchemaFilePath "ApiSchema.json" `
                        -AttestationSignerKeyPath $signingKey.PrivateKeyPath `
                        -AttestationProducer "local-dev"
                } | Should -Throw $expected

                Should -Invoke Invoke-TemplatePackageAttestation -Times 0 -Exactly
                $stalePackagePath | Should -Not -Exist
                @(Get-ChildItem -Path $workDir -Filter "*.attestation.json").Count | Should -Be 0
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
