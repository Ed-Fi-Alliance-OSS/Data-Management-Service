# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

Describe "Get-SmokeTestCredential" {
    BeforeAll {
        Import-Module "$PSScriptRoot/../modules/SmokeTest.psm1" -Force

        Mock Add-CmsClient -ModuleName SmokeTest { }
        Mock Get-CmsToken -ModuleName SmokeTest { "test-token" }
        Mock Get-DataStore -ModuleName SmokeTest { @([pscustomobject]@{ id = 1 }) }
        Mock Add-Vendor -ModuleName SmokeTest { 42 }
        Mock Add-Application -ModuleName SmokeTest { @{ Key = "test-key"; Secret = "test-secret" } }
    }

    It "defaults -EducationOrganizationIds to the TPDM-inclusive envelope (5, 6, 7, 255901, 19255901, 100000, 200000, 300000)" {
        Get-SmokeTestCredential -ConfigServiceUrl "http://localhost:8081" | Out-Null

        Should -Invoke Add-Application -ModuleName SmokeTest -Times 1 -Exactly -ParameterFilter {
            ($EducationOrganizationIds -join ',') -eq '5,6,7,255901,19255901,100000,200000,300000'
        } -Because "removing 5, 6, 7 from the default re-breaks TPDM smoke coverage with 403s on educatorPreparationProgram (whose claim defaults to RelationshipsWithEdOrgsOnly)"
    }

    It "forwards an explicit -EducationOrganizationIds without merging it with the default envelope" {
        Get-SmokeTestCredential -ConfigServiceUrl "http://localhost:8081" -EducationOrganizationIds @(255901) | Out-Null

        Should -Invoke Add-Application -ModuleName SmokeTest -Times 1 -Exactly -ParameterFilter {
            ($EducationOrganizationIds -join ',') -eq '255901'
        }
    }
}
