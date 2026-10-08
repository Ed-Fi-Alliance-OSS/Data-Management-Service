# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

# Pins the wiring that publishes the EdFi.Api.Identity contract: the prerelease jobs, their gates
# and ordering against the DMS packages, the release promotion and the DMS pull request steps.
# Workflows are read as text, the way ConfigSecretsContractVersioning.Tests.ps1 reads them, so no
# YAML module is needed in the lane. Each identity job is compared with its CustomValidation
# sibling, so the assertion keeps meaning if the sibling's gate changes.
#
# Set IDENTITY_PUBLICATION_WORKFLOWS_DIRECTORY to a directory holding copies of on-prerelease.yml,
# on-release.yml and on-dms-pullrequest.yml to point the test at them, for example to prove it
# fails when a needs entry is removed.

Describe "EdFi.Api.Identity publication wiring" {
    BeforeAll {
        $repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../.."))
        $workflows = $env:IDENTITY_PUBLICATION_WORKFLOWS_DIRECTORY
        if ([string]::IsNullOrWhiteSpace($workflows)) {
            $workflows = Join-Path $repoRoot ".github/workflows"
        }

        $prerelease = Get-Content -LiteralPath (Join-Path $workflows "on-prerelease.yml") -Raw
        $release = Get-Content -LiteralPath (Join-Path $workflows "on-release.yml") -Raw
        $pullRequest = Get-Content -LiteralPath (Join-Path $workflows "on-dms-pullrequest.yml") -Raw

        function Get-WorkflowJob {
            param([Parameter(Mandatory)][string] $Workflow, [Parameter(Mandatory)][string] $Name)

            $job = [regex]::Match($Workflow, "(?ms)^  $([regex]::Escape($Name)):\r?\n.*?(?=^  \S|\z)").Value
            if ([string]::IsNullOrEmpty($job)) {
                throw "Job $Name was not found."
            }
            return $job
        }

        function Get-WorkflowStep {
            param([Parameter(Mandatory)][string] $Workflow, [Parameter(Mandatory)][string] $Name)

            $step = [regex]::Match(
                $Workflow,
                "(?ms)^      - name: $([regex]::Escape($Name))\r?\n.*?(?=^      - name: |^      # |^  \S|\z)"
            ).Value
            if ([string]::IsNullOrEmpty($step)) {
                throw "Step $Name was not found."
            }
            return $step
        }

        function Get-JobIf {
            param([Parameter(Mandatory)][string] $Job)

            $match = [regex]::Match($Job, '(?m)^    if: (.+?)\s*$')
            if (-not $match.Success) { return $null }
            return $match.Groups[1].Value
        }

        # Both the inline form (needs: a) and the list form, ignoring comment lines.
        function Get-JobNeed {
            param([Parameter(Mandatory)][string] $Job)

            $inline = [regex]::Match($Job, '(?m)^    needs:[ \t]+(\S.*?)\s*$')
            if ($inline.Success) { return @($inline.Groups[1].Value) }

            $list = [regex]::Match($Job, '(?m)^    needs:[ \t]*\r?\n((?:      - .+\r?\n?|\s*#.*\r?\n?)+)')
            if (-not $list.Success) { return @() }
            return @(
                $list.Groups[1].Value -split '\r?\n' |
                    Where-Object { $_ -match '^      - (\S+)' } |
                    ForEach-Object { $Matches[1] }
            )
        }

        $script:jobs = @{}
        foreach ($name in @(
            "pack-identity", "sbom-create-identity", "provenance-create-identity",
            "check-identity-contract", "publish-package-identity",
            "pack-custom-validation", "sbom-create-custom-validation", "provenance-create-custom-validation",
            "check-custom-validation-contract", "publish-package-custom-validation",
            "pack-dms", "pack-schema-tools", "publish-package-dms", "publish-package-schema-tools"
        )) {
            $script:jobs[$name] = Get-WorkflowJob -Workflow $prerelease -Name $name
        }
        $script:Needs = ${function:Get-JobNeed}
        $script:If = ${function:Get-JobIf}

        $script:promote = Get-WorkflowStep -Workflow $release -Name "Promote EdFi.Api.Identity Package"
        $script:prCreate = Get-WorkflowStep -Workflow $pullRequest -Name "Create Identity NuGet Package"
        $script:prAssert = Get-WorkflowStep -Workflow $pullRequest -Name "Assert Identity Package Contents"
        $script:prConsumer = Get-WorkflowStep -Workflow $pullRequest -Name "Compile Identity Scratch Consumer"
        $script:pullRequestWorkflow = $pullRequest
    }

    Context "The identity jobs and their gates" {
        It "gates <identity> exactly as <sibling> is gated" -ForEach @(
            @{ Identity = "pack-identity"; Sibling = "pack-custom-validation" }
            @{ Identity = "check-identity-contract"; Sibling = "check-custom-validation-contract" }
            @{ Identity = "publish-package-identity"; Sibling = "publish-package-custom-validation" }
        ) {
            $identityIf = & $script:If -Job $script:jobs[$Identity]
            $siblingIf = & $script:If -Job $script:jobs[$Sibling]

            $siblingIf | Should -Not -BeNullOrEmpty
            $identityIf | Should -BeExactly $siblingIf
        }

        It "runs the pack and the check on a dispatch and on a dms- release" {
            $gate = "\`${{ github.event_name == 'workflow_dispatch' \|\| startsWith\(github\.event\.release\.tag_name, 'dms-'\) }}"
            (& $script:If -Job $script:jobs["pack-identity"]) | Should -Match $gate
            (& $script:If -Job $script:jobs["check-identity-contract"]) | Should -Match $gate
        }

        It "needs the same upstream jobs as <sibling> does, by name" -ForEach @(
            @{ Identity = "sbom-create-identity"; Sibling = "sbom-create-custom-validation" }
            @{ Identity = "provenance-create-identity"; Sibling = "provenance-create-custom-validation" }
            @{ Identity = "check-identity-contract"; Sibling = "check-custom-validation-contract" }
            @{ Identity = "publish-package-identity"; Sibling = "publish-package-custom-validation" }
        ) {
            $expected = @(& $script:Needs -Job $script:jobs[$Sibling]) | ForEach-Object { $_ -replace 'custom-validation', 'identity' }
            $actual = @(& $script:Needs -Job $script:jobs[$Identity])

            $expected | Should -Not -BeNullOrEmpty
            $actual | Sort-Object | Should -Be ($expected | Sort-Object)
        }

        It "skips the publish job on a dispatch" {
            (& $script:If -Job $script:jobs["publish-package-identity"]) |
                Should -BeExactly "`${{ github.event_name != 'workflow_dispatch' }}"
        }

        It "keeps the publish job in its own queued concurrency group" {
            $script:jobs["publish-package-identity"] |
                Should -Match '(?ms)^    concurrency:\r?\n      group: publish-package-identity\r?\n      cancel-in-progress: false$'
        }
    }

    Context "What the jobs run" {
        It "asserts the package contents in pack-identity" {
            $script:jobs["pack-identity"] | Should -Match 'Assert-IdentityPackage\.ps1'
        }

        It "packs the Identity target in pack-identity" {
            $script:jobs["pack-identity"] | Should -Match '-PackageTarget Identity'
        }

        It "provenance subjects come from pack-identity's hash" {
            $script:jobs["provenance-create-identity"] |
                Should -Match 'base64-subjects: \$\{\{ needs\.pack-identity\.outputs\.hash-code \}\}'
        }

        It "runs the feed decision, the filtered wire baseline tests and the wire gate in check-identity-contract" {
            $check = $script:jobs["check-identity-contract"]
            $decide = $check.IndexOf("Invoke-ContractPublishCheck.ps1")
            $tests = $check.IndexOf('--filter "FullyQualifiedName~IdentityWireBaselineTests"')
            $gate = $check.IndexOf("Invoke-IdentityWireContractGate.ps1")

            $decide | Should -BeGreaterThan -1
            $tests | Should -BeGreaterThan -1
            $gate | Should -BeGreaterThan -1
            $gate | Should -BeGreaterThan $tests
        }

        It "accepts any number of wire baseline tests, provided none failed and every one passed" {
            $check = $script:jobs["check-identity-contract"]

            # A pinned count breaks the prerelease lane when a test is added to the class, although
            # the pull request lane passed. The counts are compared with each other instead.
            $check | Should -Not -Match '\[int\]\$(passed|total)\.Groups\[1\]\.Value -ne \d'
            $check | Should -Match '\[int\]\$failed\.Groups\[1\]\.Value -ne 0'
            $check | Should -Match '\[int\]\$passed\.Groups\[1\]\.Value -lt 1'
            $check | Should -Match '\[int\]\$total\.Groups\[1\]\.Value -ne \[int\]\$passed\.Groups\[1\]\.Value'
        }

        It "uploads the served wire document from check-identity-contract for the publish job" {
            $check = $script:jobs["check-identity-contract"]
            $check | Should -Match '(?ms)uses: actions/upload-artifact@\S+.*?name: "\$\{\{ env\.IDENTITY_PACKAGE_NAME \}\}-WireDocument"\r?\n\s+path: \$\{\{ steps\.served-document\.outputs\.served-document-path \}\}'
        }

        It "consults the feed by package id before pushing in publish-package-identity" {
            $publish = $script:jobs["publish-package-identity"]
            $publish | Should -Match 'Invoke-ContractPublishCheck\.ps1'
            $publish | Should -Match '-PackageId "\$\{\{ env\.IDENTITY_PACKAGE_NAME \}\}"'
        }

        It "reruns the wire gate inside publish-package-identity after the recheck and before the push" {
            $publish = $script:jobs["publish-package-identity"]
            $recheck = $publish.IndexOf("Invoke-ContractPublishCheck.ps1")
            $gate = $publish.IndexOf("Invoke-IdentityWireContractGate.ps1")
            $push = $publish.IndexOf("- name: Push Identity Package to Azure Artifacts")

            $publish | Should -Match '(?ms)uses: actions/download-artifact@\S+.*?name: \$\{\{ env\.IDENTITY_PACKAGE_NAME \}\}-WireDocument'
            $recheck | Should -BeGreaterThan -1
            $gate | Should -BeGreaterThan $recheck
            $push | Should -BeGreaterThan $gate
        }

        It "runs the publish job's wire gate whether or not the package is pushed" {
            # A concurrent run that published the same version first turns this run's package
            # decision into a skip; its served document must still match what that run published.
            $step = Get-WorkflowStep -Workflow $script:jobs["publish-package-identity"] -Name "Recheck the Served Identity Wire Document Immediately Before Pushing"

            $step | Should -Match 'Invoke-IdentityWireContractGate\.ps1'
            $step | Should -Match '-PackedPackageFile'
            $step | Should -Not -Match '(?m)^        if:'
        }

        It "checks out the full history in <job>, where the gate reads the baseline at the anchor commit" -ForEach @(
            @{ Job = "check-identity-contract" }
            @{ Job = "publish-package-identity" }
        ) {
            $script:jobs[$Job] | Should -Match '(?ms)uses: actions/checkout@\S+[^\r\n]*\r?\n\s+with:\r?\n\s+fetch-depth: 0'
        }
    }

    Context "Ordering against the DMS packages" {
        It "<job> needs check-identity-contract" -ForEach @(
            @{ Job = "pack-dms" }
            @{ Job = "pack-schema-tools" }
        ) {
            @(& $script:Needs -Job $script:jobs[$Job]) | Should -Contain "check-identity-contract"
        }

        It "<job> needs publish-package-identity" -ForEach @(
            @{ Job = "publish-package-dms" }
            @{ Job = "publish-package-schema-tools" }
        ) {
            @(& $script:Needs -Job $script:jobs[$Job]) | Should -Contain "publish-package-identity"
        }
    }

    Context "Release promotion" {
        It "promotes EdFi.Api.Identity at the version its csproj declares" {
            $script:promote | Should -Match '-PackageName "EdFi\.Api\.Identity"'
            $script:promote | Should -Match '-Version \(Get-IdentityContractVersion\)'
        }

        It "promotes only on a final release tag" {
            $script:promote | Should -Match "if: \$\{\{ github\.ref_type == 'tag' && startsWith\(github\.ref_name, 'v'\) \}\}"
        }
    }

    Context "The DMS pull request lane" {
        It "packs the identity contract on its own version" {
            $script:prCreate | Should -Match '-PackageTarget Identity'
            $script:prCreate | Should -Not -Match '-DMSVersion \S'
        }

        It "asserts the identity package contents" {
            $script:prAssert | Should -Match 'Assert-IdentityPackage\.ps1'
        }

        It "compiles the identity consumer against the local package and against the published one" {
            $script:prConsumer | Should -Match 'Invoke-IdentityConsumerCheck\.ps1'
            $script:prConsumer | Should -Match '-NuGetPackagesDirectory'
            $script:prConsumer | Should -Match '-PublishedServiceIndexUrl'
            $script:prConsumer | Should -Match '-PublishedNuGetPackagesDirectory'
        }

        It "runs pack, assert and consumer in that order" {
            $create = $script:pullRequestWorkflow.IndexOf("- name: Create Identity NuGet Package")
            $assert = $script:pullRequestWorkflow.IndexOf("- name: Assert Identity Package Contents")
            $consumer = $script:pullRequestWorkflow.IndexOf("- name: Compile Identity Scratch Consumer")

            $create | Should -BeGreaterThan -1
            $assert | Should -BeGreaterThan $create
            $consumer | Should -BeGreaterThan $assert
        }

        It "lists this file in the Pester allowlist, because a file under eng/ci/tests runs in no lane otherwise" {
            $script:pullRequestWorkflow | Should -Match '(?m)^\s+"eng/ci/tests/IdentityContractPublication\.Tests\.ps1",?$'
        }
    }
}
