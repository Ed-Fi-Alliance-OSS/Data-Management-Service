# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

# Attestation guardrails for the database-template build workflows. These invariants live
# purely in declarative CI wiring - secret gating, step ordering, artifact contents, and the
# publication coupling - so no invoked-script test can reach them; a regression here would
# publish a signed template without its companion attestation package, or sign with a key
# that leaked outside RUNNER_TEMP. Following DmsPullRequestMssqlWorkflow.Tests.ps1, no YAML
# parser is assumed: assertions run over the raw workflow text.

[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidGlobalVars', '', Justification = 'The publication stubs replace dotnet and the web cmdlets as global functions that the extracted workflow script resolves; global variables carry the model feed between them and are removed in AfterEach.')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', '', Justification = 'Pester stubs intentionally keep production-compatible signatures.')]
param()

Describe "template build workflow attestation wiring (<WorkflowFile>)" -ForEach @(
    @{ WorkflowFile = "build-minimal-template.yml"; GenerateStepName = "Generate Minimal Template package"; PushStepName = "Push Minimal Template Package to Azure" }
    @{ WorkflowFile = "build-populated-template.yml"; GenerateStepName = "Generate Populated Template package"; PushStepName = "Push Populated Template Package to Azure" }
) {
    BeforeAll {
        $script:workflowPath = [System.IO.Path]::GetFullPath(
            (Join-Path $PSScriptRoot "../../../.github/workflows/$WorkflowFile"))
        $script:content = Get-Content -LiteralPath $script:workflowPath -Raw
    }

    It "declares TEMPLATE_ATTESTATION_PRIVATE_KEY as an optional workflow_call secret" {
        $secretIndex = $script:content.IndexOf("TEMPLATE_ATTESTATION_PRIVATE_KEY:")
        $secretIndex | Should -BeGreaterThan 0
        $declarationTail = $script:content.Substring($secretIndex, 600)
        $declarationTail.Contains("required: false") | Should -BeTrue
    }

    It "materializes the signing key from the secret, under RUNNER_TEMP, with a loud unattested notice on the skip path" {
        $script:content.Contains("- name: Materialize attestation signing key") | Should -BeTrue
        $script:content.Contains("id: attestation-signer") | Should -BeTrue
        $script:content.Contains('TEMPLATE_ATTESTATION_PRIVATE_KEY: ${{ secrets.TEMPLATE_ATTESTATION_PRIVATE_KEY }}') | Should -BeTrue
        $script:content.Contains('$env:RUNNER_TEMP "template-attestation-signer.pem"') | Should -BeTrue
        $script:content.Contains("will be UNATTESTED and restore consumers will refuse it (fail-closed)") | Should -BeTrue
        $script:content.Contains('"signer-available=false"') | Should -BeTrue
        $script:content.Contains('"signer-available=true"') | Should -BeTrue
    }

    It "gates the Build-Template attestation arguments on the signer output and binds the edfi-alliance-ci producer" {
        $script:content.Contains("steps.attestation-signer.outputs.signer-available }}' -eq 'true'") | Should -BeTrue
        $expectedKeyPathBinding = "AttestationSignerKeyPath = '" + '${{ steps.attestation-signer.outputs.signer-key-path }}' + "'"
        $script:content.Contains($expectedKeyPathBinding) | Should -BeTrue
        $script:content.Contains("AttestationProducer = 'edfi-alliance-ci'") | Should -BeTrue
        $script:content.Contains("@attestationArguments") | Should -BeTrue
    }

    It "orders the steps: signer before generation, key cleanup (always) after generation" {
        $signerIndex = $script:content.IndexOf("- name: Materialize attestation signing key")
        $generateIndex = $script:content.IndexOf("- name: $GenerateStepName")
        $cleanupIndex = $script:content.IndexOf("- name: Remove attestation signing key")

        $signerIndex | Should -BeGreaterThan 0
        $generateIndex | Should -BeGreaterThan $signerIndex
        $cleanupIndex | Should -BeGreaterThan $generateIndex

        $cleanupTail = $script:content.Substring($cleanupIndex, 200)
        $cleanupTail.Contains("if: always()") | Should -BeTrue
    }

    It "ships the sibling attestation document in the package artifact" {
        $script:content.Contains("/eng/DatabaseTemplates/*.nupkg.attestation.json") | Should -BeTrue
    }

    It "couples publication: refuses inconsistent artifacts and publishes companions before templates with failure-visible pushes" {
        $pushIndex = $script:content.IndexOf("- name: $PushStepName")
        $pushIndex | Should -BeGreaterThan 0
        $pushStep = $script:content.Substring($pushIndex)

        $pushStep.Contains("refusing to publish a signed template without its companion") | Should -BeTrue
        $pushStep.Contains("refusing to publish an inconsistent artifact") | Should -BeTrue

        # Companions publish FIRST: the template cannot land on the feed unless its
        # companion already has.
        $pushStep.Contains('@($companionPackages) + @($templatePackages) | ForEach-Object') | Should -BeTrue

        # Every push is individually failure-visible. Duplicate handling is exercised by the
        # publication Describe below, which runs this step's script.
        $pushStep.Contains("Package push failed for") | Should -BeTrue
        $pushStep.Contains("--skip-duplicate") | Should -BeTrue
    }

    It "couples release promotion: probes the companion, promotes it before the template when present, and tolerates only unattested-era absence" {
        $promoteIndex = $script:content.IndexOf("Template Azure Package")
        $promoteIndex | Should -BeGreaterThan 0
        $promoteStep = $script:content.Substring($promoteIndex)

        # Existence probe with strict outcome classification: 200 promotes the companion,
        # 404 is the unattested-era notice, anything else is fatal.
        $promoteStep.Contains("/nuget/packages/") | Should -BeTrue
        $promoteStep.Contains("Companion attestation package probe failed with HTTP") | Should -BeTrue
        $promoteStep.Contains("Unattested-era release") | Should -BeTrue

        # Companion promotion comes before the template promotion.
        $companionPromoteIndex = $promoteStep.IndexOf('$arguments.PackageName = $companionPackageName')
        $templatePromoteToken = '$arguments.PackageName = "' + '${{ inputs.package_name }}' + '"'
        $templatePromoteIndex = $promoteStep.IndexOf($templatePromoteToken)
        $companionPromoteIndex | Should -BeGreaterThan 0
        $templatePromoteIndex | Should -BeGreaterThan $companionPromoteIndex

        # Both promotions go through Invoke-Promote.
        ([regex]::Matches($promoteStep, [regex]::Escape("Invoke-Promote @arguments"))).Count | Should -Be 2
    }
}

Describe "template publication keeps each template with its own attestation on duplicates (<WorkflowFile>)" -ForEach @(
    @{ WorkflowFile = "build-minimal-template.yml"; PushStepName = "Push Minimal Template Package to Azure" }
    @{ WorkflowFile = "build-populated-template.yml"; PushStepName = "Push Populated Template Package to Azure" }
) {
    # Runs the push step's own script. Its run block is cut from the workflow text and its
    # expressions are bound to test values; dotnet and the two feed reads are replaced by a model
    # feed keyed by package file name, where a duplicate push without --skip-duplicate fails.
    BeforeAll {
        $workflowLines = @(Get-Content -LiteralPath ([System.IO.Path]::GetFullPath(
                    (Join-Path $PSScriptRoot "../../../.github/workflows/$WorkflowFile"))))
        $stepIndex = [array]::IndexOf($workflowLines, "      - name: $PushStepName")
        $stepIndex | Should -BeGreaterThan 0
        $workflowLines[$stepIndex + 1] | Should -Be "        run: |"
        $bodyLines = [System.Collections.Generic.List[string]]::new()
        foreach ($line in $workflowLines[($stepIndex + 2)..($workflowLines.Count - 1)]) {
            if ($line.Trim().Length -gt 0 -and -not $line.StartsWith("          ")) {
                break
            }
            $bodyLines.Add($(if ($line.Length -ge 10) { $line.Substring(10) } else { "" }))
        }

        $script:packageId = "EdFi.Api.Minimal.Template.PostgreSql.5.2.0"
        $script:feedUrl = "https://feed.test/nuget/v3/index.json"
        $script:pushScript = ($bodyLines -join "`n").
            Replace('${{ env.ARTIFACTS_FEED_URL }}', $script:feedUrl).
            Replace('${{ env.ARTIFACTS_API_KEY }}', "test-key").
            Replace('${{ inputs.package_name }}', $script:packageId)
        $script:pushScript | Should -Not -Match '\$\{\{'

        $script:templateFile = "$script:packageId.1.2.3-alpha.4.nupkg"
        $script:companionFile = "$script:packageId.Attestation.1.2.3-alpha.4.nupkg"
    }

    BeforeEach {
        $script:packageDirectory = Join-Path $TestDrive "publish-$([Guid]::NewGuid().ToString('n'))"
        New-Item -ItemType Directory -Path $script:packageDirectory | Out-Null
        [System.IO.File]::WriteAllBytes((Join-Path $script:packageDirectory $script:templateFile), [byte[]](1, 1, 1))
        [System.IO.File]::WriteAllBytes((Join-Path $script:packageDirectory $script:companionFile), [byte[]](2, 2, 2))
        Set-Content -LiteralPath (Join-Path $script:packageDirectory "$script:templateFile.attestation.json") -Value "{}"

        $global:TemplatePublishFeed = @{}
        $global:TemplatePublishCalls = [System.Collections.Generic.List[string]]::new()
        $global:TemplatePublishProbeStatus = 0
        $script:savedEnvironment = @{}
        foreach ($name in @("ARTIFACTS_FEED_URL", "ARTIFACTS_API_KEY", "RUNNER_TEMP")) {
            $script:savedEnvironment[$name] = [System.Environment]::GetEnvironmentVariable($name)
        }
        $env:ARTIFACTS_FEED_URL = $script:feedUrl
        $env:ARTIFACTS_API_KEY = "test-key"
        $env:RUNNER_TEMP = (New-Item -ItemType Directory -Path (Join-Path $TestDrive "runner-temp-$([Guid]::NewGuid().ToString('n'))")).FullName

        Set-Item function:global:Invoke-RestMethod {
            param($Uri, $Headers)
            $global:TemplatePublishCalls.Add("index $Uri")
            [pscustomobject]@{ resources = @([pscustomobject]@{ '@id' = "https://feed.test/flat/"; '@type' = "PackageBaseAddress/3.0.0" }) }
        }
        Set-Item function:global:Invoke-WebRequest {
            param($Uri, $Headers, $OutFile, [switch]$SkipHttpErrorCheck, [switch]$PassThru)
            $global:TemplatePublishCalls.Add("probe $Uri")
            if ($global:TemplatePublishProbeStatus -ne 0) {
                return [pscustomobject]@{ StatusCode = $global:TemplatePublishProbeStatus }
            }
            $fileName = ([string]$Uri).Split("/")[-1]
            if (-not $global:TemplatePublishFeed.ContainsKey($fileName)) {
                return [pscustomobject]@{ StatusCode = 404 }
            }
            [System.IO.File]::WriteAllBytes($OutFile, $global:TemplatePublishFeed[$fileName])
            [pscustomobject]@{ StatusCode = 200 }
        }
        Set-Item function:global:dotnet {
            $global:TemplatePublishCalls.Add("dotnet $($args -join ' ')")
            $fileName = ([string]$args[2]).ToLowerInvariant()
            $global:LASTEXITCODE = 0
            if ($global:TemplatePublishFeed.ContainsKey($fileName)) {
                if ($args -notcontains "--skip-duplicate") {
                    $global:LASTEXITCODE = 1
                }
                return
            }
            $global:TemplatePublishFeed[$fileName] = [System.IO.File]::ReadAllBytes((Join-Path (Get-Location) $args[2]))
        }

        function script:Invoke-PushStep {
            Push-Location -LiteralPath $script:packageDirectory
            try {
                & ([scriptblock]::Create($script:pushScript))
            }
            finally {
                Pop-Location
            }
        }
    }

    AfterEach {
        foreach ($name in @("Invoke-RestMethod", "Invoke-WebRequest", "dotnet")) {
            # A scope qualifier in a Function: provider path is read as part of the name, so
            # "function:global:$name" matches nothing and the stubs would leak into later files.
            Remove-Item -LiteralPath "Function:\$name" -ErrorAction SilentlyContinue
        }
        foreach ($name in $script:savedEnvironment.Keys) {
            if ($null -eq $script:savedEnvironment[$name]) {
                Remove-Item "Env:\$name" -ErrorAction SilentlyContinue
            }
            else {
                [System.Environment]::SetEnvironmentVariable($name, $script:savedEnvironment[$name])
            }
        }
        Remove-Variable -Name TemplatePublishFeed, TemplatePublishCalls, TemplatePublishProbeStatus -Scope Global -ErrorAction SilentlyContinue
    }

    It "refuses a rebuilt retry after a partial publication left another build's companion on the feed, before any push" {
        $global:TemplatePublishFeed[$script:companionFile.ToLowerInvariant()] = [byte[]](9, 9, 9)

        { Invoke-PushStep } | Should -Throw "*already holds $script:packageId.Attestation 1.2.3-alpha.4 with different bytes*"

        @($global:TemplatePublishCalls | Where-Object { $_ -like "dotnet *" }) | Should -BeNullOrEmpty
        $global:TemplatePublishFeed.ContainsKey($script:templateFile.ToLowerInvariant()) | Should -BeFalse
    }

    It "refuses when the feed already holds another build's template, before pushing the companion" {
        $global:TemplatePublishFeed[$script:templateFile.ToLowerInvariant()] = [byte[]](9, 9, 9)

        { Invoke-PushStep } | Should -Throw "*already holds $script:packageId 1.2.3-alpha.4 with different bytes*"

        @($global:TemplatePublishCalls | Where-Object { $_ -like "dotnet *" }) | Should -BeNullOrEmpty
        $global:TemplatePublishFeed.ContainsKey($script:companionFile.ToLowerInvariant()) | Should -BeFalse
    }

    It "completes a retry of the same artifact after a partial publication by pushing only the template" {
        $global:TemplatePublishFeed[$script:companionFile.ToLowerInvariant()] = [byte[]](2, 2, 2)

        Invoke-PushStep

        @($global:TemplatePublishCalls | Where-Object { $_ -like "dotnet *" }) |
            Should -Be @("dotnet nuget push $script:templateFile --api-key test-key --source $script:feedUrl")
        $global:TemplatePublishFeed[$script:templateFile.ToLowerInvariant()] | Should -Be ([byte[]](1, 1, 1))
    }

    It "probes the flat-container address of each package, then pushes a fresh pair companion first without --skip-duplicate" {
        Invoke-PushStep

        $lowerId = $script:packageId.ToLowerInvariant()
        @($global:TemplatePublishCalls) | Should -Be @(
            "index $script:feedUrl"
            "probe https://feed.test/flat/$lowerId.attestation/1.2.3-alpha.4/$lowerId.attestation.1.2.3-alpha.4.nupkg"
            "probe https://feed.test/flat/$lowerId/1.2.3-alpha.4/$lowerId.1.2.3-alpha.4.nupkg"
            "dotnet nuget push $script:companionFile --api-key test-key --source $script:feedUrl"
            "dotnet nuget push $script:templateFile --api-key test-key --source $script:feedUrl"
        )
    }

    It "fails closed, pushing nothing, when the feed check answers neither 200 nor 404" {
        $global:TemplatePublishProbeStatus = 500

        { Invoke-PushStep } | Should -Throw "*failed with HTTP 500*"

        @($global:TemplatePublishCalls | Where-Object { $_ -like "dotnet *" }) | Should -BeNullOrEmpty
    }

    It "leaves unattested publication as it was: no feed check, and the template pushes with --skip-duplicate" {
        Remove-Item -LiteralPath (Join-Path $script:packageDirectory $script:companionFile), (Join-Path $script:packageDirectory "$script:templateFile.attestation.json")
        $global:TemplatePublishFeed[$script:templateFile.ToLowerInvariant()] = [byte[]](9, 9, 9)

        Invoke-PushStep

        @($global:TemplatePublishCalls) | Should -Be @("dotnet nuget push $script:templateFile --api-key test-key --source $script:feedUrl --skip-duplicate")
    }
}

Describe "template caller workflows pass the attestation secret through (<CallerFile>)" -ForEach @(
    @{ CallerFile = "EdFi.Api.Minimal.Template.PostgreSQL.yml" }
    @{ CallerFile = "EdFi.Api.Minimal.Template.MsSql.yml" }
    @{ CallerFile = "EdFi.Api.Populated.Template.PostgreSQL.yml" }
    @{ CallerFile = "EdFi.Api.Populated.Template.MsSql.yml" }
) {
    It "forwards TEMPLATE_ATTESTATION_PRIVATE_KEY to the reusable workflow" {
        $callerPath = [System.IO.Path]::GetFullPath(
            (Join-Path $PSScriptRoot "../../../.github/workflows/$CallerFile"))
        (Get-Content -LiteralPath $callerPath -Raw).Contains(
            'TEMPLATE_ATTESTATION_PRIVATE_KEY: ${{ secrets.TEMPLATE_ATTESTATION_PRIVATE_KEY }}') | Should -BeTrue
    }
}
