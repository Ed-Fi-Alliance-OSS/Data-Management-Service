# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

# Runtime behavior of the shipped .github/actions/pull-ci-images/Invoke-CiImagePull.ps1.
#
# The primitive is invoked in a child pwsh with `docker`, `Get-Random`, and `Start-Sleep` replaced by
# recording functions, so every assertion is on what the real script actually does rather than on its
# source text, and no registry, image, or wall-clock delay is involved. A child process keeps the
# shims and the script's preference assignments out of this Pester session.
#
# One argument is deliberately not asserted here. The script passes `--` to end Docker's option list,
# and PowerShell forwards that token verbatim to a native command but strips it from a function call,
# so a shim can never observe it: `& echo.exe pull -- ref` receives three arguments while a function
# receives two. The recorded pull lines below therefore omit `--` even though the shipped command
# includes it - do not "fix" that by removing the guard from the script. The real native command line
# is covered by the one-time invalid-image exercise against real Docker instead.
#
# Randomness is injected rather than sampled: the shim records the bounds the script asks for and
# returns a caller-selected value from them. That makes the jitter contract - an expanding
# [base/2, base) window per retry, capped - exactly assertable, where sampling real randomness could
# only ever be probabilistic and would eventually fail a CI run by chance.

Describe "Invoke-CiImagePull retry behavior" {
    BeforeAll {
        $script:scriptPath = [System.IO.Path]::GetFullPath(
            (Join-Path $PSScriptRoot "../../../.github/actions/pull-ci-images/Invoke-CiImagePull.ps1")
        )

        $script:harnessPath = Join-Path $TestDrive "invoke-pull-harness.ps1"
        Set-Content -LiteralPath $script:harnessPath -Encoding utf8 -Value @'
# Global rather than script scope: a shim is called from inside the script under test, so `$script:`
# would resolve against that script's scope and trip its Set-StrictMode on an unset variable.
$global:dockerCallIndex = 0

function Write-HarnessRecord {
    param([Parameter(Mandatory)] [string] $Line)
    Add-Content -LiteralPath $env:DMS_PULL_LOG -Value $Line
}

function docker {
    $global:dockerCallIndex++
    Write-HarnessRecord "docker $(($args | ForEach-Object { $_ }) -join ' ')"

    # One exit code per invocation; the final value repeats so a scenario need only describe its
    # leading attempts.
    $codes = @($env:DMS_PULL_EXIT_CODES -split ',' | ForEach-Object { [int] $_.Trim() })
    $index = [Math]::Min($global:dockerCallIndex, $codes.Count) - 1
    $global:LASTEXITCODE = $codes[$index]

    if ($global:LASTEXITCODE -ne 0) {
        Write-HarnessRecord "docker-error simulated registry failure"
    }
}

function Get-Random {
    param([int] $Minimum, [int] $Maximum)
    Write-HarnessRecord "random $Minimum $Maximum"

    switch ($env:DMS_PULL_RANDOM_MODE) {
        "ceiling" { return $Maximum - 1 }
        "interior" { return [int] (($Minimum + $Maximum) / 2) }
        default { return $Minimum }
    }
}

function Start-Sleep {
    param([int] $Milliseconds, [double] $Seconds)
    Write-HarnessRecord "sleep $Milliseconds"
}

$ErrorActionPreference = "Stop"

# The exit code must reflect whether the script under test failed. Left to the default preference, a
# terminating error would be reported and the harness would still exit 0, making every failure
# assertion vacuous.
try {
    & $env:DMS_PULL_SCRIPT `
        -Images $env:DMS_PULL_IMAGES `
        -MaxAttempts ([int] $env:DMS_PULL_MAX_ATTEMPTS) `
        -InitialDelaySeconds ([int] $env:DMS_PULL_INITIAL_DELAY) `
        -MaxDelaySeconds ([int] $env:DMS_PULL_MAX_DELAY)
}
catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}

exit 0
'@

        function Invoke-PullHarness {
            # Runs the real script under the shims and returns its combined output, exit code, and the
            # ordered shim records. Inputs travel by environment variable so a multi-line images value
            # never has to survive command-line quoting.
            param(
                [Parameter(Mandatory)] [AllowEmptyString()] [string] $Images,
                [string] $ExitCodes = "0",
                [int] $MaxAttempts = 5,
                [int] $InitialDelaySeconds = 3,
                [int] $MaxDelaySeconds = 60,
                [ValidateSet("floor", "ceiling", "interior")] [string] $RandomMode = "floor"
            )

            $logPath = Join-Path $TestDrive ("records-" + [Guid]::NewGuid().ToString("N") + ".log")
            Set-Content -LiteralPath $logPath -Value @() -Encoding utf8

            $env:DMS_PULL_SCRIPT = $script:scriptPath
            $env:DMS_PULL_IMAGES = $Images
            $env:DMS_PULL_MAX_ATTEMPTS = "$MaxAttempts"
            $env:DMS_PULL_INITIAL_DELAY = "$InitialDelaySeconds"
            $env:DMS_PULL_MAX_DELAY = "$MaxDelaySeconds"
            $env:DMS_PULL_EXIT_CODES = $ExitCodes
            $env:DMS_PULL_RANDOM_MODE = $RandomMode
            $env:DMS_PULL_LOG = $logPath

            $output = & pwsh -NoProfile -File $script:harnessPath 2>&1 | Out-String
            $exitCode = $LASTEXITCODE

            $records = @(
                if (Test-Path -LiteralPath $logPath) {
                    Get-Content -LiteralPath $logPath | Where-Object { $_.Length -gt 0 }
                }
            )

            return [pscustomobject]@{
                Output   = $output
                ExitCode = $exitCode
                Records  = $records
                Pulls    = @($records | Where-Object { $_ -like "docker *" })
                Randoms  = @($records | Where-Object { $_ -like "random *" })
                Sleeps   = @($records | Where-Object { $_ -like "sleep *" } | ForEach-Object { [int] ($_ -split " ")[1] })
            }
        }
    }

    Context "the shipped script exists where the action expects it" {
        It "resolves the script path" {
            Test-Path -LiteralPath $script:scriptPath | Should -BeTrue
        }
    }

    Context "a registry that answers on the first attempt" {
        BeforeAll {
            $script:happy = Invoke-PullHarness -Images "registry/dms:ci`nregistry/config:ci" -ExitCodes "0"
        }

        It "pulls each image once, in the order given" {
            $script:happy.Pulls | Should -HaveCount 2
            $script:happy.Pulls[0] | Should -Be "docker pull registry/dms:ci"
            $script:happy.Pulls[1] | Should -Be "docker pull registry/config:ci"
        }

        It "never waits and never draws a delay" {
            $script:happy.Sleeps | Should -HaveCount 0
            $script:happy.Randoms | Should -HaveCount 0
        }

        It "succeeds" {
            $script:happy.ExitCode | Should -Be 0
        }

        It "reports the attempt and the maximum for the successful pull" {
            $script:happy.Output | Should -Match "Pulling registry/dms:ci \(attempt 1 of 5\)\."
            $script:happy.Output | Should -Match "Pulled registry/dms:ci on attempt 1 of 5\."
        }
    }

    Context "a registry that fails twice and then answers" {
        BeforeAll {
            $script:recovered = Invoke-PullHarness -Images "registry/dms:ci" -ExitCodes "1,1,0"
        }

        It "retries until the pull succeeds" {
            $script:recovered.Pulls | Should -HaveCount 3
        }

        It "waits once per failed attempt and not after the successful one" {
            $script:recovered.Sleeps | Should -HaveCount 2
            $script:recovered.Randoms | Should -HaveCount 2
        }

        It "succeeds" {
            $script:recovered.ExitCode | Should -Be 0
        }

        It "keeps every failed attempt visible with its exit code and attempt position" {
            $script:recovered.Output | Should -Match "docker pull failed for registry/dms:ci with exit code 1 on attempt 1 of 5\."
            $script:recovered.Output | Should -Match "docker pull failed for registry/dms:ci with exit code 1 on attempt 2 of 5\."
        }

        It "names the attempt it is waiting for" {
            $script:recovered.Output | Should -Match "before attempt 2 of 5 for registry/dms:ci\."
            $script:recovered.Output | Should -Match "before attempt 3 of 5 for registry/dms:ci\."
        }
    }

    Context "the jittered backoff envelope" {
        BeforeAll {
            $script:floor = Invoke-PullHarness -Images "registry/dms:ci" -ExitCodes "1" -RandomMode "floor"
            $script:ceiling = Invoke-PullHarness -Images "registry/dms:ci" -ExitCodes "1" -RandomMode "ceiling"
            $script:interior = Invoke-PullHarness -Images "registry/dms:ci" -ExitCodes "1" -RandomMode "interior"
        }

        It "draws randomness once per retry over a doubling range" {
            # Half the base per attempt: 3s, 6s, 12s, 24s bases at the default 3-second initial base.
            $script:floor.Randoms | Should -HaveCount 4
            $script:floor.Randoms[0] | Should -Be "random 0 1500"
            $script:floor.Randoms[1] | Should -Be "random 0 3000"
            $script:floor.Randoms[2] | Should -Be "random 0 6000"
            $script:floor.Randoms[3] | Should -Be "random 0 12000"
        }

        It "waits the band floor when randomness returns its minimum" {
            $script:floor.Sleeps | Should -Be @(1500, 3000, 6000, 12000)
        }

        It "waits just under the band ceiling when randomness returns its maximum" {
            $script:ceiling.Sleeps | Should -Be @(2999, 5999, 11999, 23999)
        }

        It "waits inside the band for an interior draw" {
            $script:interior.Sleeps | Should -Be @(2250, 4500, 9000, 18000)
        }

        It "keeps every band strictly increasing and inside [base/2, base)" {
            foreach ($run in @($script:floor, $script:ceiling, $script:interior)) {
                $delays = $run.Sleeps
                $delays | Should -HaveCount 4

                for ($index = 0; $index -lt 4; $index++) {
                    $baseMilliseconds = 3000 * [Math]::Pow(2, $index)
                    $delays[$index] | Should -BeGreaterOrEqual ($baseMilliseconds / 2)
                    $delays[$index] | Should -BeLessThan $baseMilliseconds
                }

                for ($index = 1; $index -lt 4; $index++) {
                    $delays[$index] | Should -BeGreaterThan $delays[$index - 1]
                }
            }
        }

        It "stops growing the base at the cap" {
            $capped = Invoke-PullHarness -Images "registry/dms:ci" -ExitCodes "1" -MaxDelaySeconds 6 -RandomMode "floor"
            $capped.Randoms | Should -Be @("random 0 1500", "random 0 3000", "random 0 3000", "random 0 3000")
            $capped.Sleeps | Should -Be @(1500, 3000, 3000, 3000)
        }
    }

    Context "a registry that never answers" {
        BeforeAll {
            $script:exhausted = Invoke-PullHarness -Images "registry/dms:ci" -ExitCodes "1" -MaxAttempts 3 -InitialDelaySeconds 1
        }

        It "attempts exactly the configured maximum" {
            $script:exhausted.Pulls | Should -HaveCount 3
        }

        It "does not wait after the final attempt" {
            $script:exhausted.Sleeps | Should -HaveCount 2
        }

        It "fails the step" {
            $script:exhausted.ExitCode | Should -Not -Be 0
        }

        It "names the image and the attempt count in the failure" {
            $script:exhausted.Output | Should -Match "Failed to pull registry/dms:ci after 3 attempt\(s\)\."
        }
    }

    Context "a failing image ahead of a healthy one" {
        It "gives each image its own attempt budget" {
            $result = Invoke-PullHarness -Images "registry/dms:ci`nregistry/config:ci" -ExitCodes "1,0,0"

            $result.ExitCode | Should -Be 0
            $result.Pulls | Should -Be @(
                "docker pull registry/dms:ci",
                "docker pull registry/dms:ci",
                "docker pull registry/config:ci"
            )
            $result.Sleeps | Should -HaveCount 1
        }

        It "restarts the backoff for the next image" {
            # Both images fail once: the second image's retry must draw the first band again rather
            # than continuing the first image's escalation.
            $result = Invoke-PullHarness -Images "registry/dms:ci`nregistry/config:ci" -ExitCodes "1,0,1,0"

            $result.ExitCode | Should -Be 0
            $result.Randoms | Should -Be @("random 0 1500", "random 0 1500")
        }

        It "stops at the first image that exhausts its budget" {
            $result = Invoke-PullHarness -Images "registry/dms:ci`nregistry/config:ci" -ExitCodes "1" -MaxAttempts 2 -InitialDelaySeconds 1

            $result.ExitCode | Should -Not -Be 0
            $result.Pulls | Should -HaveCount 2
            $result.Output | Should -Match "Failed to pull registry/dms:ci after 2 attempt\(s\)\."
            $result.Output | Should -Not -Match "registry/config:ci"
        }
    }

    Context "the images input" {
        It "ignores blank and whitespace-only lines" {
            $result = Invoke-PullHarness -Images "`n  registry/dms:ci  `n`n   `nregistry/config:ci`n"

            $result.ExitCode | Should -Be 0
            $result.Pulls | Should -Be @(
                "docker pull registry/dms:ci",
                "docker pull registry/config:ci"
            )
        }

        It "accepts CRLF separators" {
            $result = Invoke-PullHarness -Images "registry/dms:ci`r`nregistry/config:ci`r`n"

            $result.ExitCode | Should -Be 0
            $result.Pulls | Should -HaveCount 2
        }

        It "pulls exactly once for a single-line value" {
            $result = Invoke-PullHarness -Images "edfialliance/ed-fi-data-management-service:dms-pre-1.0.0"

            $result.ExitCode | Should -Be 0
            $result.Pulls | Should -Be @("docker pull edfialliance/ed-fi-data-management-service:dms-pre-1.0.0")
        }

        It "fails without invoking Docker when no image survives parsing" {
            $result = Invoke-PullHarness -Images "`n   `n`n"

            $result.ExitCode | Should -Not -Be 0
            $result.Pulls | Should -HaveCount 0
            $result.Output | Should -Match "No images were supplied to pull\."
        }

        It "fails without invoking Docker when the value is empty" {
            $result = Invoke-PullHarness -Images ""

            $result.ExitCode | Should -Not -Be 0
            $result.Pulls | Should -HaveCount 0
        }
    }
}
