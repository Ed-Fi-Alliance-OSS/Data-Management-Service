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

Describe "Get-EducatorPreparationSampleFileNames" {
    BeforeAll {
        $script:templatesDir = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
        Push-Location $script:templatesDir
        try {
            Import-Module (Join-Path $script:templatesDir "Template-Management.psm1") -Force
        }
        finally {
            Pop-Location
        }

        function script:Invoke-EdPrepNames {
            param([string]$Directory)
            InModuleScope Template-Management -Parameters @{ dir = $Directory } {
                param($dir)
                @(Get-EducatorPreparationSampleFileNames -SourceDirectory $dir)
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

        $excluded = Invoke-EdPrepNames -Directory $script:work

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

        $excluded = Invoke-EdPrepNames -Directory $script:work

        @($excluded) | Should -Be @('PerformanceEvaluation.xml')
    }

    It "returns nothing for a sample set with no educator-prep files (e.g. DS 5.2)" {
        Set-Content -LiteralPath (Join-Path $script:work 'Student.xml') -Value '<x/>'
        Set-Content -LiteralPath (Join-Path $script:work 'EducationOrganization.xml') -Value '<x/>'

        $excluded = Invoke-EdPrepNames -Directory $script:work

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
