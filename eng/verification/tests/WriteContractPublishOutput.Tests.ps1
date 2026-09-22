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
            $ShouldPush = $true,
            [string] $Reason = "absent",
            [string] $TypeName = "EdFi.ContractPublishDecision"
        )

        return [pscustomobject]@{
            PSTypeName     = $TypeName
            ShouldPush     = $ShouldPush
            Reason         = $Reason
            PackageId      = "EdFi.Api.TestContract"
            PackageVersion = "1.0.0"
            Comparisons    = @()
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
    It "writes the two outputs for a decision received from the pipeline" {
        $run = Invoke-WithOutputFile { Get-Decision | & $script:writer }

        $run.Failure | Should -BeNullOrEmpty
        ($run.Lines -join ";") | Should -BeExactly "should-push=true;reason=absent"
        $run.Result.ShouldPush | Should -BeTrue
    }

    It "accepts the same decision as a direct argument" {
        $run = Invoke-WithOutputFile { & $script:writer -Decision (Get-Decision) }

        $run.Failure | Should -BeNullOrEmpty
        $run.Lines.Count | Should -Be 2
    }

    It "accepts a one-element array as a direct argument" {
        $run = Invoke-WithOutputFile { & $script:writer -Decision @((Get-Decision)) }

        $run.Failure | Should -BeNullOrEmpty
        $run.Lines.Count | Should -Be 2
    }

    It "writes an unchanged decision as no push" {
        $run = Invoke-WithOutputFile { Get-Decision -ShouldPush $false -Reason unchanged | & $script:writer }

        $run.Failure | Should -BeNullOrEmpty
        ($run.Lines -join ";") | Should -BeExactly "should-push=false;reason=unchanged"
    }

    It "returns the decision unchanged when GITHUB_OUTPUT is not set" {
        $previous = $env:GITHUB_OUTPUT

        try {
            $env:GITHUB_OUTPUT = ""
            $result = Get-Decision -Reason unchanged -ShouldPush $false | & $script:writer

            $result.Reason | Should -BeExactly "unchanged"
        }
        finally {
            $env:GITHUB_OUTPUT = $previous
        }
    }

    It "writes exactly one object to the success stream" {
        $objects = @(Get-Decision | & $script:writer 6>$null)

        $objects.Count | Should -Be 1
        $objects[0].PSObject.TypeNames[0] | Should -BeExactly "EdFi.ContractPublishDecision"
    }
}

Describe "Write-ContractPublishOutput refuses anything but one well-formed decision" {
    It "refuses an empty pipeline and writes nothing" {
        $run = Invoke-WithOutputFile { @() | & $script:writer }

        $run.Failure.Exception.Message | Should -BeLike "*exactly one publish decision object and received 0*"
        $run.Lines.Count | Should -Be 0
    }

    # The System.Object[] path: two decisions on one pipeline used to render as a string that was
    # neither true nor false, and the step exited green having pushed nothing.
    It "refuses two decisions and writes nothing" {
        $run = Invoke-WithOutputFile { @((Get-Decision), (Get-Decision)) | & $script:writer }

        $run.Failure.Exception.Message | Should -BeLike "*received 2*"
        $run.Lines.Count | Should -Be 0
    }

    It "refuses two decisions passed as one array argument" {
        $run = Invoke-WithOutputFile { & $script:writer -Decision @((Get-Decision), (Get-Decision)) }

        $run.Failure.Exception.Message | Should -BeLike "*received 2*"
        $run.Lines.Count | Should -Be 0
    }

    It "refuses a null decision" {
        $run = Invoke-WithOutputFile { $null | & $script:writer }

        $run.Failure.Exception.Message | Should -BeLike "*received null*"
        $run.Lines.Count | Should -Be 0
    }

    It "refuses a string" {
        $run = Invoke-WithOutputFile { "EdFi.Api.TestContract 1.0.0 is not on the feed" | & $script:writer }

        $run.Failure.Exception.Message | Should -BeLike "*EdFi.ContractPublishDecision*"
        $run.Lines.Count | Should -Be 0
    }

    It "refuses an object of another type" {
        $run = Invoke-WithOutputFile { Get-Decision -TypeName "EdFi.SomethingElse" | & $script:writer }

        $run.Failure.Exception.Message | Should -BeLike "*received 'EdFi.SomethingElse'*"
        $run.Lines.Count | Should -Be 0
    }

    It "refuses a ShouldPush that is not a boolean" {
        $run = Invoke-WithOutputFile { Get-Decision -ShouldPush "true" | & $script:writer }

        $run.Failure.Exception.Message | Should -BeLike "*ShouldPush is 'true' rather than a boolean*"
        $run.Lines.Count | Should -Be 0
    }

    It "refuses an unknown reason" {
        $run = Invoke-WithOutputFile { Get-Decision -Reason "maybe`nlater" | & $script:writer }

        $run.Failure.Exception.Message | Should -BeLike "*not one of: absent, unchanged*"
        $run.Lines.Count | Should -Be 0
    }
}
