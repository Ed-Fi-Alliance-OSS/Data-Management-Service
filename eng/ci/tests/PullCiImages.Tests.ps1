# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

# Runtime behavior of the shipped .github/actions/pull-ci-images/Invoke-CiImagePull.ps1.
#
# The primitive is invoked in a child pwsh with `docker`, `Get-Random`, and `Start-Sleep` replaced by
# recording functions, so the retry, backoff, and failure assertions are on what the real script
# actually does, and no registry, image, or wall-clock delay is involved. A child process keeps the
# shims and the script's preference assignments out of this Pester session. Three tests here do read
# source text instead, because nothing invoked can reach what they cover: the declared action.yml
# defaults below, and the two wiring invariants in the second Describe.
#
# Two things the shipped script does are deliberately not asserted anywhere in this suite, because
# the shim pattern cannot observe either one:
#
#   - The `--` that ends Docker's option list. PowerShell forwards that token verbatim to a native
#     command but strips it from a function call, so a shim can never see it: a native
#     `echo pull -- ref` receives three arguments while a function receives two. The recorded pull
#     lines below therefore omit `--` even though the shipped command includes it - do not "fix"
#     that by removing the guard from the script. Only a real native invocation could assert it.
#   - `$PSNativeCommandUseErrorActionPreference = $false`. That preference governs native commands
#     only, so a function shim is indifferent to it and deleting the assignment leaves this suite
#     green. It is defensive rather than load-bearing on a current host, where the default is
#     already false: a host that had turned it on would promote the first failed pull to a
#     terminating error under `$ErrorActionPreference = "Stop"` and abort the run instead of
#     retrying.
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
        # Emitted on the success stream on purpose. That is the stream an accidental capture
        # (`$x = docker ...`, `| Out-Null`, `> $null`) would swallow, so asserting this line reaches the
        # harness output is what holds the script to leaving Docker's registry error in the job log.
        Write-Output "simulated registry failure for $($args[-1])"
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
    if ($env:DMS_PULL_USE_SCRIPT_DEFAULTS -eq "true") {
        # Passing nothing but the images is the only way to exercise the script's own parameter
        # defaults; every other scenario supplies them explicitly and would mask a drift.
        & $env:DMS_PULL_SCRIPT -Images $env:DMS_PULL_IMAGES
    }
    else {
        & $env:DMS_PULL_SCRIPT `
            -Images $env:DMS_PULL_IMAGES `
            -MaxAttempts ([int] $env:DMS_PULL_MAX_ATTEMPTS) `
            -InitialDelaySeconds ([int] $env:DMS_PULL_INITIAL_DELAY) `
            -MaxDelaySeconds ([int] $env:DMS_PULL_MAX_DELAY)
    }
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
                [ValidateSet("floor", "ceiling", "interior")] [string] $RandomMode = "floor",
                [switch] $UseScriptDefaults
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
            $env:DMS_PULL_USE_SCRIPT_DEFAULTS = if ($UseScriptDefaults) { "true" } else { "false" }

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

        It "keeps every wait inside [base/2, base) and rising while the base still doubles" {
            # Only while the base doubles. The cap is what breaks strict growth, and the default 60s
            # cap never binds inside four retries from a 3s base; the two capped specs below cover
            # the bands that repeat instead.
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

    Context "an attempt budget past the Int32 exponent boundary" {
        # The delay arithmetic multiplies the initial base by 2^(attempt-1). Once that product
        # exceeds Int32, computing the cap has to stay in floating point: binding Math.Min(Int32,Int32)
        # instead narrows the double and dies with a conversion error partway through the budget,
        # which silently caps attempts well below the 100 that ValidateRange advertises.
        It "spends the whole declared budget instead of dying on the delay arithmetic" {
            $result = Invoke-PullHarness -Images "registry/dms:ci" -ExitCodes "1" -MaxAttempts 40 -InitialDelaySeconds 3

            $result.Pulls | Should -HaveCount 40
            $result.Output | Should -Not -Match "Cannot convert"
            $result.Output | Should -Match "Failed to pull registry/dms:ci after 40 attempt\(s\)\."
        }

        It "reports failure rather than an arithmetic error at the top of the declared range" {
            $result = Invoke-PullHarness -Images "registry/dms:ci" -ExitCodes "1" -MaxAttempts 100 -InitialDelaySeconds 3600

            $result.Pulls | Should -HaveCount 100
            $result.Output | Should -Not -Match "Cannot convert"
            $result.Output | Should -Match "Failed to pull registry/dms:ci after 100 attempt\(s\)\."
        }

        It "holds every retry past the cap at the capped band" {
            $result = Invoke-PullHarness -Images "registry/dms:ci" -ExitCodes "1" -MaxAttempts 40 -InitialDelaySeconds 3 -RandomMode "floor"

            # Base reaches the 60s cap on retry 6 and stays there, so the half-width is fixed at
            # 30000 from that retry on. Skipping the five uncapped retries starts the slice at 6.
            @($result.Randoms | Select-Object -Skip 5 | Sort-Object -Unique) | Should -Be @("random 0 30000")
        }
    }

    Context "Docker's own output" {
        It "leaves it in the job log rather than capturing it" {
            # The registry error explaining a failure is the whole point of not capturing the pull, so
            # the shim's success-stream line has to survive into the harness output.
            $result = Invoke-PullHarness -Images "registry/dms:ci" -ExitCodes "1,0"

            $result.Output | Should -Match "simulated registry failure for registry/dms:ci"
        }

        It "keeps it for the final failed attempt too" {
            $result = Invoke-PullHarness -Images "registry/dms:ci" -ExitCodes "1" -MaxAttempts 2 -InitialDelaySeconds 1

            @([regex]::Matches($result.Output, "simulated registry failure")) | Should -HaveCount 2
        }
    }

    Context "the shipped defaults" {
        BeforeAll {
            $script:actionPath = [System.IO.Path]::GetFullPath(
                (Join-Path $PSScriptRoot "../../../.github/actions/pull-ci-images/action.yml")
            )
            $script:actionLines = @(Get-Content -LiteralPath $script:actionPath)

            function Get-DeclaredInputDefault {
                # No YAML parser is available in this lane (see DmsPullRequestMssqlWorkflow.Tests.ps1),
                # so the input's block is found by its two-space key and scanned for its default.
                param([Parameter(Mandatory)] [string] $InputName)

                $start = -1
                for ($i = 0; $i -lt $script:actionLines.Count; $i++) {
                    if ($script:actionLines[$i] -match "^  $([regex]::Escape($InputName)):\s*$") {
                        $start = $i
                        break
                    }
                }
                if ($start -lt 0) {
                    return $null
                }

                for ($j = $start + 1; $j -lt $script:actionLines.Count; $j++) {
                    if ($script:actionLines[$j] -match '^  \S') {
                        break
                    }
                    if ($script:actionLines[$j] -match '^\s+default:\s*"?([^"]+?)"?\s*$') {
                        return $Matches[1]
                    }
                }
                return $null
            }
        }

        It "declares the retry defaults the call sites rely on" {
            # Every call site passes only `images`, so these declared values are the retry policy for
            # all of them: lowering max-attempts to 1 here would disable retries fleet-wide.
            Get-DeclaredInputDefault -InputName "max-attempts" | Should -Be "5"
            Get-DeclaredInputDefault -InputName "initial-delay-seconds" | Should -Be "3"
            Get-DeclaredInputDefault -InputName "max-delay-seconds" | Should -Be "60"
        }

        It "spends five attempts when the caller supplies only images" {
            $result = Invoke-PullHarness -Images "registry/dms:ci" -ExitCodes "1" -UseScriptDefaults

            $result.Pulls | Should -HaveCount 5
            $result.Output | Should -Match "Failed to pull registry/dms:ci after 5 attempt\(s\)\."
        }

        It "starts from a three-second base when the caller supplies only images" {
            $result = Invoke-PullHarness -Images "registry/dms:ci" -ExitCodes "1" -UseScriptDefaults

            $result.Randoms[0] | Should -Be "random 0 1500"
            $result.Sleeps[0] | Should -Be 1500
        }
    }

    AfterAll {
        foreach ($name in @(
                "DMS_PULL_SCRIPT", "DMS_PULL_IMAGES", "DMS_PULL_MAX_ATTEMPTS", "DMS_PULL_INITIAL_DELAY",
                "DMS_PULL_MAX_DELAY", "DMS_PULL_EXIT_CODES", "DMS_PULL_RANDOM_MODE", "DMS_PULL_LOG",
                "DMS_PULL_USE_SCRIPT_DEFAULTS")) {
            # Remove-Item rather than assigning $null: assigning leaves a defined-but-empty variable
            # behind on some hosts, which a later spec would read as configured.
            Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue
        }
    }
}

Describe "pull-ci-images workflow wiring" {
    # Two invariants that live purely in declarative CI wiring, so no invoked-script test can reach
    # them, and whose regression would not surface as a failing pull-request lane. The rest of the
    # conversion - login order, permissions, and the `use_prebuilt_images` condition on each pull
    # step - is left to review, following eng/ci/tests/DmsPullRequestMssqlWorkflow.Tests.ps1. Be
    # clear about what that leaves exposed: `dms_image` and `config_image` are emitted
    # unconditionally, and `use_prebuilt_images` goes false only for a fork pull request or when the
    # REBUILD_DOWNSTREAM_IMAGES repository variable is set, so a dropped condition still passes on
    # an internal pull request - including the one that drops it - and breaks fork lanes only.
    BeforeAll {
        $script:githubRoot = [System.IO.Path]::GetFullPath(
            (Join-Path $PSScriptRoot "../../../.github")
        )
        $script:workflowRoot = Join-Path $script:githubRoot "workflows"

        function Get-JobBlock {
            param(
                # Not Mandatory: a mandatory string[] rejects an array holding the file's blank lines.
                [string[]] $Lines,
                [Parameter(Mandatory)] [string] $JobName
            )

            $start = -1
            for ($i = 0; $i -lt $Lines.Count; $i++) {
                if ($Lines[$i] -match "^  $([regex]::Escape($JobName)):\s*$") {
                    $start = $i
                    break
                }
            }
            if ($start -lt 0) {
                return @()
            }

            $end = $Lines.Count
            for ($j = $start + 1; $j -lt $Lines.Count; $j++) {
                if ($Lines[$j] -match '^  [A-Za-z0-9_-]+:\s*$') {
                    $end = $j
                    break
                }
            }

            return @($Lines[$start..($end - 1)])
        }
    }

    It "routes every declarative image pull through the action" {
        # A reintroduced inline `docker pull` still succeeds, so it drops the retry this action exists
        # to provide without failing anything. The whole .github tree is in scope, not just
        # workflows: a composite action under .github/actions is as good a place to hide a bare pull.
        # `docker image pull` is the same command spelled long-hand, and .yaml is the same file type
        # spelled differently - neither appears today, and neither should slip past this if it does.
        # Only YAML is scanned: Invoke-CiImagePull.ps1 is the one .ps1 under .github and its
        # `docker pull` is the call this guard exists to funnel every other site into.
        $barePulls = @(
            Get-ChildItem -LiteralPath $script:githubRoot -Recurse -File -Include *.yml, *.yaml |
                ForEach-Object {
                    $file = $_
                    Get-Content -LiteralPath $file.FullName |
                        Where-Object { $_ -match 'docker\s+(image\s+)?pull\b' } |
                        ForEach-Object { "$($file.Name): $($_.Trim())" }
                }
        )

        $barePulls | Should -HaveCount 0
    }

    It "checks out the repository before the local action in each release image-tag job" {
        # Neither job had a checkout before this action existed, and both run only on release, so a
        # dropped checkout would first appear during a release instead of on a pull request.
        $releaseLines = @(Get-Content -LiteralPath (Join-Path $script:workflowRoot "on-release.yml"))

        foreach ($jobName in @("tag-dms-image", "tag-cs-image")) {
            $block = Get-JobBlock -Lines $releaseLines -JobName $jobName
            $block | Should -Not -BeNullOrEmpty -Because "$jobName must exist"

            $checkoutIndex = -1
            $actionIndex = -1
            for ($k = 0; $k -lt $block.Count; $k++) {
                if ($checkoutIndex -lt 0 -and $block[$k] -match 'uses:\s+actions/checkout@') {
                    $checkoutIndex = $k
                }
                if ($actionIndex -lt 0 -and $block[$k] -match 'uses:\s+\./\.github/actions/pull-ci-images\s*$') {
                    $actionIndex = $k
                }
            }

            $actionIndex | Should -BeGreaterOrEqual 0 -Because "$jobName must pull through the local action"
            $checkoutIndex | Should -BeGreaterOrEqual 0 -Because "$jobName must check out the repository"
            $checkoutIndex | Should -BeLessThan $actionIndex -Because "$jobName must check out before the local action resolves"
        }
    }
}
