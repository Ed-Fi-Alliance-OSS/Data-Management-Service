# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

# The attach decision a contract's evidence job makes from its dependencies' results.
#
# Every input is the raw string a `needs` context hands a job, including the empty string a skipped
# or failed dependency leaves in its outputs, so each row below is a shape the workflow can produce.

BeforeAll {
    $script:decider = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../Test-ContractEvidenceAttachable.ps1"))

    function Invoke-Decision {
        [CmdletBinding()]
        param(
            [AllowEmptyString()][string] $Publish = "",
            [AllowEmptyString()][string] $Evidence = "",
            [AllowEmptyString()][string] $Sbom = "",
            [AllowEmptyString()][string] $Provenance = ""
        )

        return & $script:decider -PublishResult $Publish -AttachEvidence $Evidence -SbomResult $Sbom -ProvenanceResult $Provenance
    }
}

Describe "Test-ContractEvidenceAttachable" {
    It "attaches when the publisher confirmed the bytes and both evidence jobs succeeded" {
        $result = Invoke-Decision -Publish "success" -Evidence "true" -Sbom "success" -Provenance "success"

        $result.Attach | Should -BeTrue
        $result.Attach | Should -BeOfType [bool]
    }

    It "does not attach when the publisher refused or failed" {
        $result = Invoke-Decision -Publish "failure" -Evidence "" -Sbom "success" -Provenance "success"

        $result.Attach | Should -BeFalse
        $result.Reason | Should -BeLike "*publish job's result was failure*"
    }

    It "does not attach when the publisher was skipped and left no outputs" {
        $result = Invoke-Decision -Publish "skipped" -Evidence "" -Sbom "success" -Provenance "success"

        $result.Attach | Should -BeFalse
    }

    It "does not attach when the publisher was cancelled" {
        (Invoke-Decision -Publish "cancelled" -Evidence "" -Sbom "success" -Provenance "success").Attach | Should -BeFalse
    }

    It "does not attach when every input is empty" {
        $result = Invoke-Decision

        $result.Attach | Should -BeFalse
        $result.Reason | Should -BeLike "*(none)*"
    }

    It "does not attach when the publisher did not confirm the feed serves this run's bytes" {
        $result = Invoke-Decision -Publish "success" -Evidence "false" -Sbom "success" -Provenance "success"

        $result.Attach | Should -BeFalse
        $result.Reason | Should -BeLike "*did not confirm*"
    }

    # A successful publisher that produced no decision is a broken producer. Reading its silence as
    # "no" would hide a workflow defect behind a clean skip; reading it as "yes" would attach unproven
    # evidence.
    It "fails when the publisher succeeded but reported no boolean" {
        { Invoke-Decision -Publish "success" -Evidence "" -Sbom "success" -Provenance "success" } |
            Should -Throw -ExpectedMessage "*rather than true or false*"

        { Invoke-Decision -Publish "success" -Evidence "yes" -Sbom "success" -Provenance "success" } |
            Should -Throw -ExpectedMessage "*rather than true or false*"
    }

    It "reads the boolean exactly, not case-insensitively" {
        { Invoke-Decision -Publish "success" -Evidence "True" -Sbom "success" -Provenance "success" } |
            Should -Throw
    }

    It "does not attach when the SBOM job did not succeed" {
        $result = Invoke-Decision -Publish "success" -Evidence "true" -Sbom "failure" -Provenance "success"

        $result.Attach | Should -BeFalse
        $result.Reason | Should -BeLike "*SBOM job's result was failure*"
    }

    It "does not attach when the provenance job did not succeed" {
        $result = Invoke-Decision -Publish "success" -Evidence "true" -Sbom "success" -Provenance "skipped"

        $result.Attach | Should -BeFalse
        $result.Reason | Should -BeLike "*provenance job's result was skipped*"
    }

    It "writes the decision to GITHUB_OUTPUT when that file is set" {
        $output = Join-Path ([System.IO.Path]::GetTempPath()) "dms1501-attach-output-$([guid]::NewGuid().ToString('N')).txt"
        $previous = $env:GITHUB_OUTPUT

        try {
            $env:GITHUB_OUTPUT = $output
            Invoke-Decision -Publish "success" -Evidence "true" -Sbom "success" -Provenance "success" | Out-Null
            Invoke-Decision -Publish "failure" | Out-Null

            (Get-Content -LiteralPath $output) -join ";" | Should -BeExactly "attach=true;attach=false"
        }
        finally {
            $env:GITHUB_OUTPUT = $previous

            if (Test-Path -LiteralPath $output) {
                Remove-Item -LiteralPath $output -Force
            }
        }
    }

    It "writes exactly one object to the success stream" {
        $objects = @(Invoke-Decision -Publish "success" -Evidence "true" -Sbom "success" -Provenance "success" 6>$null)

        $objects.Count | Should -Be 1
        $objects[0].PSObject.TypeNames[0] | Should -BeExactly "EdFi.ContractEvidenceDecision"
    }
}
