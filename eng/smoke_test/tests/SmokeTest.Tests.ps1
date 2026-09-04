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

Describe "Invoke-SmokeTestUtility" {
    BeforeAll {
        Import-Module "$PSScriptRoot/../modules/SmokeTest.psm1" -Force
    }

    It "redacts the key and secret from the echoed invocation instead of printing them in the clear" {
        $capturedOutput = $null
        $resolveFailure = $null

        try {
            Invoke-SmokeTestUtility -BaseUrl 'http://localhost:8080' -Key 'TESTKEY_abc123' -Secret 'TESTSECRET_s3cr3t' -ToolPath '/nonexistent/tool/path' -TestSet 'NonDestructiveApi' -OutVariable capturedOutput | Out-Null
        }
        catch {
            # Resolve-Path throws for this nonexistent -ToolPath, but only after the echo has
            # already written the key and secret to the success stream. -OutVariable captures
            # that echo regardless of the later terminating error, which is exactly how the
            # leak was reproduced. The error is kept so the assertions below can prove the
            # intended path was the one exercised.
            $resolveFailure = $_
        }

        $echoedText = $capturedOutput | Out-String

        $resolveFailure | Should -Not -BeNullOrEmpty -Because "this test only exercises the real leak if the run got past the echo and then died on the unresolvable tool path; a clean return would mean it is asserting against something else entirely"
        $echoedText | Should -Not -BeNullOrEmpty -Because "the echo must run before Resolve-Path fails, or this test is not exercising the real leak"
        $echoedText | Should -Not -Match 'TESTSECRET_s3cr3t' -Because "printing the raw secret to console output defeats the purpose of a machine-issued smoke-test credential that operators may paste into shared logs or terminals"
        $echoedText | Should -Not -Match 'TESTKEY_abc123' -Because "the key identifies the smoke-test application and should not be echoed in the clear alongside its secret"
        ([regex]::Matches($echoedText, '\*\*\*')).Count | Should -Be 2 -Because "both the key and the secret should be replaced with the same redaction placeholder"
    }

    It "still passes the real key and secret to dotnet, proving the redacted echo is a separate array and not an alias of the real argument list" {
        $toolRoot = Join-Path $TestDrive "tool"
        New-Item -ItemType Directory -Path (Join-Path $toolRoot "tools/net10.0/any") -Force | Out-Null
        New-Item -ItemType File -Path (Join-Path $toolRoot "tools/net10.0/any/EdFi.SmokeTest.Console.dll") -Force | Out-Null

        $calls = [System.Collections.Generic.List[object]]::new()
        Mock dotnet -ModuleName SmokeTest {
            # Pester mocks a native command with a function, and function argument
            # binding (unlike native-command binding) does not flatten the $options
            # array into $path's sibling positional argument, so $args here is
            # @($path, $options) with $options still nested one level deep. The
            # += below relies on PowerShell's array-concatenation semantics to
            # flatten that one level back out.
            $flatArgs = @()
            foreach ($item in $args) { $flatArgs += $item }
            $calls.Add($flatArgs)
        }

        Invoke-SmokeTestUtility -BaseUrl 'http://localhost:8080' -Key 'TESTKEY_abc123' -Secret 'TESTSECRET_s3cr3t' -ToolPath $toolRoot -TestSet 'NonDestructiveApi' | Out-Null

        $calls.Count | Should -Be 1 -Because "the mocked dotnet invocation should have been reached now that Resolve-Path succeeds against the staged tool tree"

        $realArgs = $calls[0] -join ' '

        $realArgs | Should -Match 'TESTKEY_abc123' -Because "if this were the same array reference as the redacted echo, the key dotnet receives would already have been overwritten with ***, and authentication would fail on every smoke-test leg"
        $realArgs | Should -Match 'TESTSECRET_s3cr3t' -Because "if this were the same array reference as the redacted echo, the secret dotnet receives would already have been overwritten with ***, and authentication would fail on every smoke-test leg"
        $realArgs | Should -Not -Match '\*\*\*' -Because "the argument list handed to dotnet must never be the redacted display copy"
    }
}
