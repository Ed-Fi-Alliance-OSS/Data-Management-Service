# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

# DMS-1554: the secret resolution harness runs only on a schedule, so the check that stops that run
# passing with no proofs executed is exercised here, on every pull request, against TRX files written
# by the test rather than by a stack.

Describe 'Secret resolution proof results' {
    BeforeAll {
        $script:repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
        Import-Module (Join-Path $PSScriptRoot 'secret-resolution/secret-resolution-proof.psm1') -Force
        $script:expected = @(Get-ExpectedSecretResolutionProof)

        function script:New-Trx {
            param([Parameter(Mandatory)] [AllowEmptyCollection()] [object[]] $Results, [switch] $NoResultsElement)

            $rows = ($Results | ForEach-Object { "    <UnitTestResult testName=`"$($_.Name)`" outcome=`"$($_.Outcome)`" />" }) -join "`n"
            $body = if ($NoResultsElement) { '' } else { "  <Results>`n$rows`n  </Results>" }
            $path = Join-Path $TestDrive "$([guid]::NewGuid().ToString('N')).trx"
            Set-Content -LiteralPath $path -Value "<?xml version=`"1.0`" encoding=`"utf-8`"?>`n<TestRun xmlns=`"http://microsoft.com/schemas/VisualStudio/TeamTest/2010`">`n$body`n</TestRun>"
            return $path
        }

        function script:Get-AllPassed {
            return @($script:expected | ForEach-Object { @{ Name = $_; Outcome = 'Passed' } })
        }
    }

    It 'passes when every expected proof executed once and passed' {
        $output = Assert-ProofsExecuted -TrxPath (New-Trx -Results (Get-AllPassed))

        $output | Should -Be "All $($script:expected.Count) SecretResolutionPlugin proofs executed and passed."
    }

    It 'fails when the run wrote no results file' {
        { Assert-ProofsExecuted -TrxPath (Join-Path $TestDrive 'absent.trx') } |
            Should -Throw '*wrote no results*'
    }

    It 'fails with its own message when the results file is empty' {
        $path = Join-Path $TestDrive 'empty.trx'
        New-Item -ItemType File -Path $path -Force | Out-Null

        { Assert-ProofsExecuted -TrxPath $path } | Should -Throw '*is empty*'
    }

    It 'fails when the results file has no Results element' {
        { Assert-ProofsExecuted -TrxPath (New-Trx -Results @() -NoResultsElement) } |
            Should -Throw '*No SecretResolutionPlugin proofs ran*'
    }

    It 'fails when the filter matched nothing' {
        { Assert-ProofsExecuted -TrxPath (New-Trx -Results @()) } |
            Should -Throw '*No SecretResolutionPlugin proofs ran*'
    }

    It 'fails when a proof was skipped, naming it' {
        $results = Get-AllPassed
        $results[2].Outcome = 'NotExecuted'

        { Assert-ProofsExecuted -TrxPath (New-Trx -Results $results) } |
            Should -Throw "*did not pass: $($script:expected[2]) (NotExecuted)*"
    }

    It 'fails when an expected proof did not run' {
        $results = @(Get-AllPassed | Select-Object -Skip 1)

        { Assert-ProofsExecuted -TrxPath (New-Trx -Results $results) } |
            Should -Throw "*proof $($script:expected[0]) did not execute exactly once*"
    }

    It 'fails when an expected proof ran twice' {
        $results = @(Get-AllPassed) + @(@{ Name = $script:expected[0]; Outcome = 'Passed' })

        { Assert-ProofsExecuted -TrxPath (New-Trx -Results $results) } |
            Should -Throw "*proof $($script:expected[0]) did not execute exactly once*"
    }

    It 'expects exactly the proofs the SecretResolutionPlugin tests declare' {
        $source = Get-Content -Raw -LiteralPath (Join-Path $script:repositoryRoot 'src/config/tests/EdFi.DmsConfigurationService.Tests.E2E/SecretResolution/SecretResolutionPluginTests.cs')
        $declared = @([regex]::Matches($source, '(?m)\[Test\b[^\]]*\]\s+public\s+(?:async\s+Task|void)\s+(It_\w+)') |
                ForEach-Object { $_.Groups[1].Value })

        $declared | Should -Not -BeNullOrEmpty
        @($declared | Sort-Object) | Should -Be @($script:expected | Sort-Object)
    }

    It 'is what the harness checks after the proofs run' {
        $harness = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'secret-resolution/Invoke-SecretResolutionE2E.ps1')

        $harness | Should -Match ([regex]::Escape('Import-Module (Join-Path $PSScriptRoot "secret-resolution-proof.psm1")'))
        $harness | Should -Match '(?s)--filter "TestCategory=SecretResolutionPlugin".*?Assert-ProofsExecuted -TrxPath \$trxPath'
        $harness | Should -Not -Match '(?m)^function Assert-ProofsExecuted'
    }
}
