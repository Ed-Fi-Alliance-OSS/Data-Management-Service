# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

# The conversion from a publish decision to job outputs, which used to be an inline expression in
# three workflow steps that would have rendered two objects as System.Object[] and exited green.

BeforeAll {
    $script:writer = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../Write-ContractPublishOutput.ps1"))

    function Get-Decision {
        [CmdletBinding()]
        [OutputType([pscustomobject])]
        param(
            [string] $Mode = "decide",
            $ShouldPush = $true,
            $AttachEvidence = $false,
            $PublishedBytesIdentical = $false,
            [string] $Reason = "absent",
            [string] $TypeName = "EdFi.ContractPublishDecision"
        )

        return [pscustomobject]@{
            PSTypeName              = $TypeName
            Mode                    = $Mode
            ShouldPush              = $ShouldPush
            Reason                  = $Reason
            PackageId               = "EdFi.Api.TestContract"
            PackageVersion          = "1.0.0"
            PublishedBytesIdentical = $PublishedBytesIdentical
            AttachEvidence          = $AttachEvidence
            Comparisons             = @()
        }
    }

    # Runs the helper with GITHUB_OUTPUT pointed at a fresh file and returns the lines it wrote.
    function Invoke-WithOutputFile {
        [CmdletBinding()]
        [OutputType([hashtable])]
        param([Parameter(Mandatory)][scriptblock] $Action)

        $output = Join-Path ([System.IO.Path]::GetTempPath()) "dms1501-publish-output-$([guid]::NewGuid().ToString('N')).txt"
        $previous = $env:GITHUB_OUTPUT
        $result = $null
        $failure = $null

        try {
            $env:GITHUB_OUTPUT = $output

            try {
                $result = & $Action
            }
            catch {
                $failure = $_
            }

            $lines = if (Test-Path -LiteralPath $output) { @(Get-Content -LiteralPath $output) } else { @() }
        }
        finally {
            $env:GITHUB_OUTPUT = $previous

            if (Test-Path -LiteralPath $output) {
                Remove-Item -LiteralPath $output -Force
            }
        }

        return @{ Result = $result; Failure = $failure; Lines = $lines }
    }
}

Describe "Write-ContractPublishOutput writes a validated decision" {
    It "writes the four outputs for a decide-mode decision received from the pipeline" {
        $run = Invoke-WithOutputFile { Get-Decision | & $script:writer -ExpectedMode decide }

        $run.Failure | Should -BeNullOrEmpty
        ($run.Lines -join ";") | Should -BeExactly "should-push=true;attach-evidence=false;published-bytes-identical=false;reason=absent"
        $run.Result.ShouldPush | Should -BeTrue
    }

    It "accepts the same decision as a direct argument" {
        $run = Invoke-WithOutputFile { & $script:writer -Decision (Get-Decision) -ExpectedMode decide }

        $run.Failure | Should -BeNullOrEmpty
        $run.Lines.Count | Should -Be 4
    }

    It "accepts a one-element array as a direct argument" {
        $run = Invoke-WithOutputFile { & $script:writer -Decision @((Get-Decision)) -ExpectedMode decide }

        $run.Failure | Should -BeNullOrEmpty
        $run.Lines.Count | Should -Be 4
    }

    It "writes a confirm-mode decision's attach evidence as given" {
        $run = Invoke-WithOutputFile {
            Get-Decision -Mode confirm -ShouldPush $false -AttachEvidence $true -PublishedBytesIdentical $true -Reason confirmed |
                & $script:writer -ExpectedMode confirm
        }

        $run.Failure | Should -BeNullOrEmpty
        ($run.Lines -join ";") | Should -BeExactly "should-push=false;attach-evidence=true;published-bytes-identical=true;reason=confirmed"
    }

    It "returns the decision unchanged when GITHUB_OUTPUT is not set" {
        $previous = $env:GITHUB_OUTPUT

        try {
            $env:GITHUB_OUTPUT = ""
            $result = Get-Decision -Reason unchanged -ShouldPush $false | & $script:writer -ExpectedMode decide

            $result.Reason | Should -BeExactly "unchanged"
        }
        finally {
            $env:GITHUB_OUTPUT = $previous
        }
    }

    It "writes exactly one object to the success stream" {
        $objects = @(Get-Decision | & $script:writer -ExpectedMode decide 6>$null)

        $objects.Count | Should -Be 1
        $objects[0].PSObject.TypeNames[0] | Should -BeExactly "EdFi.ContractPublishDecision"
    }
}

Describe "Write-ContractPublishOutput refuses anything but one well-formed decision" {
    It "refuses an empty pipeline and writes nothing" {
        $run = Invoke-WithOutputFile { @() | & $script:writer -ExpectedMode decide }

        $run.Failure.Exception.Message | Should -BeLike "*exactly one publish decision object and received 0*"
        $run.Lines.Count | Should -Be 0
    }

    # The System.Object[] path: two decisions on one pipeline used to render as a string that was
    # neither true nor false, and the step exited green having pushed nothing.
    It "refuses two decisions and writes nothing" {
        $run = Invoke-WithOutputFile { @((Get-Decision), (Get-Decision)) | & $script:writer -ExpectedMode decide }

        $run.Failure.Exception.Message | Should -BeLike "*received 2*"
        $run.Lines.Count | Should -Be 0
    }

    It "refuses two decisions passed as one array argument" {
        $run = Invoke-WithOutputFile { & $script:writer -Decision @((Get-Decision), (Get-Decision)) -ExpectedMode decide }

        $run.Failure.Exception.Message | Should -BeLike "*received 2*"
        $run.Lines.Count | Should -Be 0
    }

    It "refuses a null decision" {
        $run = Invoke-WithOutputFile { $null | & $script:writer -ExpectedMode decide }

        $run.Failure.Exception.Message | Should -BeLike "*received null*"
        $run.Lines.Count | Should -Be 0
    }

    It "refuses a string" {
        $run = Invoke-WithOutputFile { "EdFi.Api.TestContract 1.0.0 is not on the feed" | & $script:writer -ExpectedMode decide }

        $run.Failure.Exception.Message | Should -BeLike "*EdFi.ContractPublishDecision*"
        $run.Lines.Count | Should -Be 0
    }

    It "refuses an object of another type" {
        $run = Invoke-WithOutputFile { Get-Decision -TypeName "EdFi.SomethingElse" | & $script:writer -ExpectedMode decide }

        $run.Failure.Exception.Message | Should -BeLike "*received 'EdFi.SomethingElse'*"
        $run.Lines.Count | Should -Be 0
    }

    # A decide-mode object carries AttachEvidence false by construction, but a confirm step that
    # accepted one would report a push intent as a publication fact if that ever changed.
    It "refuses a decide-mode decision in a confirm step" {
        $run = Invoke-WithOutputFile { Get-Decision -Mode decide | & $script:writer -ExpectedMode confirm }

        $run.Failure.Exception.Message | Should -BeLike "*from confirm mode and received one from 'decide'*"
        $run.Lines.Count | Should -Be 0
    }

    It "refuses a confirm-mode decision in a decide step" {
        $run = Invoke-WithOutputFile { Get-Decision -Mode confirm -Reason confirmed | & $script:writer -ExpectedMode decide }

        $run.Failure.Exception.Message | Should -BeLike "*from decide mode and received one from 'confirm'*"
        $run.Lines.Count | Should -Be 0
    }

    It "refuses a ShouldPush that is not a boolean" {
        $run = Invoke-WithOutputFile { Get-Decision -ShouldPush "true" | & $script:writer -ExpectedMode decide }

        $run.Failure.Exception.Message | Should -BeLike "*ShouldPush is 'true' rather than a boolean*"
        $run.Lines.Count | Should -Be 0
    }

    It "refuses an AttachEvidence that is not a boolean" {
        $run = Invoke-WithOutputFile { Get-Decision -Mode confirm -Reason confirmed -AttachEvidence $null | & $script:writer -ExpectedMode confirm }

        $run.Failure.Exception.Message | Should -BeLike "*AttachEvidence is '' rather than a boolean*"
        $run.Lines.Count | Should -Be 0
    }

    It "refuses a PublishedBytesIdentical that is not a boolean" {
        $run = Invoke-WithOutputFile { Get-Decision -PublishedBytesIdentical 1 | & $script:writer -ExpectedMode decide }

        $run.Failure.Exception.Message | Should -BeLike "*PublishedBytesIdentical is '1' rather than a boolean*"
        $run.Lines.Count | Should -Be 0
    }

    It "refuses an unknown reason" {
        $run = Invoke-WithOutputFile { Get-Decision -Reason "maybe`nlater" | & $script:writer -ExpectedMode decide }

        $run.Failure.Exception.Message | Should -BeLike "*not one of: absent, unchanged, confirmed*"
        $run.Lines.Count | Should -Be 0
    }
}
