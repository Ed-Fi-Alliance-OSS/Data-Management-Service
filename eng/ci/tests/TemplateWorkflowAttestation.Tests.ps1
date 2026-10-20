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

BeforeAll {
    # The one signing-eligibility rule: never a pull request, and only main or a v*.*.* tag.
    # Each reusable workflow states it three times (the job's environment, the signer step's
    # SIGNING_ELIGIBLE, and the gate on the key itself), and the tests require each verbatim.
    $script:signingEligibleExpression = "github.event_name != 'pull_request' && (github.ref == 'refs/heads/main' || startsWith(github.ref, 'refs/tags/v'))"

    function script:Test-SigningEligibleExpression {
        # Evaluates the workflow expression for one event and ref by translating its few
        # operators into PowerShell, with GitHub's case-insensitive string comparison. Any token
        # outside that vocabulary survives the translation and fails to parse, so a changed rule
        # cannot pass by being ignored.
        param([string]$Expression, [string]$EventName, [string]$Ref)

        $translated = $Expression.
            Replace("github.event_name", '$EventName').
            Replace("github.ref", '$Ref').
            Replace("!=", "-ine").
            Replace("==", "-ieq").
            Replace("&&", "-and").
            Replace("||", "-or")
        $translated = [regex]::Replace($translated, "startsWith\(\`$Ref, '([^']*)'\)", '$Ref.StartsWith(''$1'', [StringComparison]::OrdinalIgnoreCase)')
        return [bool](& ([scriptblock]::Create("param(`$EventName, `$Ref) $translated")) $EventName $Ref)
    }

    function script:Get-WorkflowStepRunScript {
        # The run block of one step, cut from the workflow text with its indentation removed.
        param([string]$Content, [string]$StepName)

        $lines = $Content -split "`n"
        $stepIndex = [array]::IndexOf($lines, "      - name: $StepName")
        $stepIndex | Should -BeGreaterThan 0
        $runIndex = $stepIndex + 1
        while ($lines[$runIndex] -ne "        run: |") {
            $lines[$runIndex] | Should -Match '^ {8,}\S' -Because "the step's own keys precede its run block"
            $runIndex++
        }
        $body = foreach ($line in $lines[($runIndex + 1)..($lines.Count - 1)]) {
            if ($line.Trim().Length -gt 0 -and -not $line.StartsWith("          ")) { break }
            if ($line.Length -ge 10) { $line.Substring(10) } else { "" }
        }
        return ($body -join "`n")
    }

    function script:Get-WorkflowStepBlock {
        # One whole step (name, keys, and run block), up to the next step at the same depth,
        # without the comment block that introduces the next step.
        param([string]$Content, [string]$StepName)

        $start = $Content.IndexOf("      - name: $StepName`n")
        $start | Should -BeGreaterThan 0
        $next = [regex]::Match($Content.Substring($start + 1), "\n      - name: |\n\n  [a-z-]+:\n")
        $block = if ($next.Success) { $Content.Substring($start, $next.Index + 1) } else { $Content.Substring($start) }
        return ($block -replace '(\n(      #[^\n]*)?)+$', '')
    }

    function script:Invoke-AttestStep {
        # Runs the attest step's own script in a child pwsh (as the runner does), from a working
        # directory whose Template-Management.psm1 is a recording stub. The stub logs what the
        # signing call saw of the key file while the step held it.
        param([string]$Content, [string]$WorkDir, [hashtable]$Environment)

        $stepScript = (Get-WorkflowStepRunScript -Content $Content -StepName "Attest template package").
            Replace('${{ inputs.standard_version }}', "5.2.0").
            Replace('${{ inputs.database_engine }}', "postgresql").
            Replace('${{ steps.versions.outputs.dms-semver }}', "dms-v1.2.3")
        $stepScript | Should -Not -Match '\$\{\{'
        $stepPath = Join-Path $WorkDir "attest-step.ps1"
        Set-Content -LiteralPath $stepPath -Value ("`$ErrorActionPreference = 'stop'`n" + $stepScript)
        @(
            'function Get-TemplatePackageConfig { param($ConfigFilePath, $StandardVersion, $DatabaseEngine) Add-Content -LiteralPath (Join-Path $PSScriptRoot "stub.log") -Value "config $ConfigFilePath $StandardVersion $DatabaseEngine"; @{ Id = "EdFi.Api.Test.Template" } }'
            'function Write-TemplatePackageAttestation {'
            '    param($Config, $PackageVersion, $BackupDirectory, $AttestationSignerKeyPath, $AttestationProducer)'
            '    $mode = [Convert]::ToString([int](Get-Item -LiteralPath $AttestationSignerKeyPath).UnixFileMode, 8)'
            '    $key = [System.IO.File]::ReadAllText($AttestationSignerKeyPath)'
            '    Add-Content -LiteralPath (Join-Path $PSScriptRoot "stub.log") -Value "sign id=$($Config.Id) version=$PackageVersion dir=$BackupDirectory producer=$AttestationProducer mode=$mode key=$key"'
            '    if ($env:STUB_SIGN_FAILS -eq "true") { throw "stub signing failure" }'
            '    return "/work/pkg.nupkg.attestation.json"'
            '}'
        ) | Set-Content -LiteralPath (Join-Path $WorkDir "Template-Management.psm1")

        $saved = @{}
        foreach ($name in $Environment.Keys) {
            $saved[$name] = [System.Environment]::GetEnvironmentVariable($name)
        }
        Push-Location -LiteralPath $WorkDir
        try {
            foreach ($name in $Environment.Keys) {
                [System.Environment]::SetEnvironmentVariable($name, $Environment[$name])
            }
            $printed = @(& ([System.Diagnostics.Process]::GetCurrentProcess().MainModule.FileName) -NoProfile -NonInteractive -File $stepPath 2>&1 | ForEach-Object { "$_" })
            return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Printed = ($printed -join "`n") }
        }
        finally {
            Pop-Location
            foreach ($name in $saved.Keys) {
                if ($null -eq $saved[$name]) { Remove-Item "Env:\$name" -ErrorAction SilentlyContinue }
                else { [System.Environment]::SetEnvironmentVariable($name, $saved[$name]) }
            }
        }
    }
}

Describe "template build workflow attestation wiring (<WorkflowFile>)" -ForEach @(
    @{ WorkflowFile = "build-minimal-template.yml"; GenerateStepName = "Generate Minimal Template package"; PushStepName = "Push Minimal Template Package to Azure"; SettingsFile = "MinimalTemplateSettings.psd1" }
    @{ WorkflowFile = "build-populated-template.yml"; GenerateStepName = "Generate Populated Template package"; PushStepName = "Push Populated Template Package to Azure"; SettingsFile = "PopulatedTemplateSettings.psd1" }
) {
    BeforeAll {
        $script:workflowPath = [System.IO.Path]::GetFullPath(
            (Join-Path $PSScriptRoot "../../../.github/workflows/$WorkflowFile"))
        $script:content = Get-Content -LiteralPath $script:workflowPath -Raw
    }

    It "declares TEMPLATE_ATTESTATION_PRIVATE_KEY as an optional workflow_call secret" {
        $secretIndex = $script:content.IndexOf("TEMPLATE_ATTESTATION_PRIVATE_KEY:")
        $secretIndex | Should -BeGreaterThan 0
        # The declaration's first key after its comment block.
        $requiredIndex = $script:content.IndexOf("required:", $secretIndex)
        $script:content.Substring($requiredIndex, "required: false".Length) | Should -Be "required: false"
        $script:content.IndexOf("outputs:", $secretIndex) | Should -BeGreaterThan $requiredIndex
    }

    It "gives the signing secret to the attest step alone, gated on the eligibility rule" {
        $jobStart = $script:content.IndexOf("`n  build-template:`n")
        $jobEnd = $script:content.IndexOf("`n  generate-package-sbom:`n")
        $job = $script:content.Substring($jobStart, $jobEnd - $jobStart)
        ([regex]::Matches($job, [regex]::Escape("secrets.TEMPLATE_ATTESTATION_PRIVATE_KEY"))).Count | Should -Be 1

        $attestStep = Get-WorkflowStepBlock -Content $script:content -StepName "Attest template package"
        $attestStep.Contains("id: attestation-signer") | Should -BeTrue
        $attestStep.Contains("TEMPLATE_ATTESTATION_PRIVATE_KEY: `${{ ($script:signingEligibleExpression) && secrets.TEMPLATE_ATTESTATION_PRIVATE_KEY || '' }}") | Should -BeTrue
        $attestStep.Contains('$env:RUNNER_TEMP "template-attestation-signer.pem"') | Should -BeTrue
        $attestStep.Contains("-AttestationProducer 'edfi-alliance-ci'") | Should -BeTrue
        $attestStep.Contains("will be UNATTESTED and restore consumers will refuse it (fail-closed)") | Should -BeTrue
    }

    It "states that a ref-name match is not the signing boundary and names the required environment protections" {
        $secretIndex = $script:content.IndexOf("      TEMPLATE_ATTESTATION_PRIVATE_KEY:")
        $secretComment = $script:content.Substring($secretIndex, $script:content.IndexOf("required: false", $secretIndex) - $secretIndex)
        foreach ($required in @(
                "ONLY as a secret of the 'template-attestation-signing' environment"
                "does not show that its commit was"
                "required reviewers (self-review prevented)"
                "deployment refs"
                "enforced tag ruleset"
                "create, update, or delete release tags"
                "verifying that its commit is approved"
                "only after the environment's"
            )) {
            ($secretComment -replace "\n\s+#", "") | Should -BeLike "*$($required -replace '\s+', ' ')*"
        }
        $jobComment = $script:content.Substring($script:content.IndexOf("    # Signing is requested only"), 700)
        ($jobComment -replace "\n\s+#", "") | Should -BeLike "*matching a ref name is not enough to authorize signing*"
    }

    It "binds only build-template, and only on a signing-eligible run, to the protected signing environment" {
        $environmentLines = @($script:content -split "`n" | Where-Object { $_ -match '^\s*environment:' })
        $environmentLines | Should -Be @("    environment: `${{ ($script:signingEligibleExpression) && 'template-attestation-signing' || '' }}")

        $jobStart = $script:content.IndexOf("`n  build-template:`n")
        $jobEnd = $script:content.IndexOf("`n  generate-package-sbom:`n")
        $environmentIndex = $script:content.IndexOf($environmentLines[0])
        $environmentIndex | Should -BeGreaterThan $jobStart
        $environmentIndex | Should -BeLessThan $jobEnd
        # The key reaches the job only through that environment: the step gates on the same rule.
        $script:content.Contains("SIGNING_ELIGIBLE: `${{ $script:signingEligibleExpression }}") | Should -BeTrue
    }

    It "decides signing eligibility for <Case>" -ForEach @(
        @{ Case = "a pull request (merge ref)"; EventName = "pull_request"; Ref = "refs/pull/1185/merge"; Eligible = $false }
        @{ Case = "a pull request, even one whose ref reads as main"; EventName = "pull_request"; Ref = "refs/heads/main"; Eligible = $false }
        @{ Case = "a push to main"; EventName = "push"; Ref = "refs/heads/main"; Eligible = $true }
        @{ Case = "a release tag push"; EventName = "push"; Ref = "refs/tags/v8.1.0"; Eligible = $true }
        @{ Case = "a manual dispatch from main"; EventName = "workflow_dispatch"; Ref = "refs/heads/main"; Eligible = $true }
        @{ Case = "a manual dispatch from a feature branch"; EventName = "workflow_dispatch"; Ref = "refs/heads/DMS-1271"; Eligible = $false }
        @{ Case = "a push to a branch named like main"; EventName = "push"; Ref = "refs/heads/main-backport"; Eligible = $false }
    ) {
        Test-SigningEligibleExpression -Expression $script:signingEligibleExpression -EventName $EventName -Ref $Ref | Should -Be $Eligible
    }

    It "the attest step neither writes a key nor signs on a skip path, and says so loudly (<Case>)" -ForEach @(
        @{ Case = "ineligible run, key present"; Eligible = "false"; Key = "-----BEGIN PRIVATE KEY-----"; Notice = "*Only main and v*.*.* tag runs (never pull requests) sign template packages*" }
        @{ Case = "eligible run, key missing"; Eligible = "true"; Key = ""; Notice = "*TEMPLATE_ATTESTATION_PRIVATE_KEY is not configured*" }
    ) {
        $workDir = (New-Item -ItemType Directory -Path (Join-Path $TestDrive "attest-$([Guid]::NewGuid().ToString('n'))")).FullName
        $outputFile = Join-Path $workDir "github-output"

        $result = Invoke-AttestStep -Content $script:content -WorkDir $workDir -Environment @{
            SIGNING_ELIGIBLE = $Eligible; TEMPLATE_ATTESTATION_PRIVATE_KEY = $Key; RUNNER_TEMP = $workDir; GITHUB_OUTPUT = $outputFile; STUB_SIGN_FAILS = $null
        }

        $result.ExitCode | Should -Be 0
        $result.Printed | Should -BeLike $Notice
        @(Get-Content -LiteralPath $outputFile) | Should -Be @("attested=false")
        Test-Path -LiteralPath (Join-Path $workDir "stub.log") | Should -BeFalse -Because "a skip path loads no signing helper"
        Test-Path -LiteralPath (Join-Path $workDir "template-attestation-signer.pem") | Should -BeFalse
    }

    # The owner-only creation mode is a Unix file mode, so these cases run where the job does:
    # on Linux (the CI Pester lane is ubuntu-latest).
    It "the attest step creates the key 0600, signs the packed package, and removes the key (<Case>)" -Skip:$IsWindows -ForEach @(
        @{ Case = "signing succeeds"; SignFails = "false" }
        @{ Case = "signing fails"; SignFails = "true" }
    ) {
        $workDir = (New-Item -ItemType Directory -Path (Join-Path $TestDrive "attest-$([Guid]::NewGuid().ToString('n'))")).FullName
        $outputFile = Join-Path $workDir "github-output"
        $key = "-----BEGIN PRIVATE KEY-----test-material-----END PRIVATE KEY-----"

        $result = Invoke-AttestStep -Content $script:content -WorkDir $workDir -Environment @{
            SIGNING_ELIGIBLE = "true"; TEMPLATE_ATTESTATION_PRIVATE_KEY = $key; RUNNER_TEMP = $workDir; GITHUB_OUTPUT = $outputFile; STUB_SIGN_FAILS = $SignFails
        }

        @(Get-Content -LiteralPath (Join-Path $workDir "stub.log")) | Should -Be @(
            "config ./$SettingsFile 5.2.0 postgresql"
            "sign id=EdFi.Api.Test.Template version=1.2.3 dir=./ producer=edfi-alliance-ci mode=600 key=$key"
        )
        Test-Path -LiteralPath (Join-Path $workDir "template-attestation-signer.pem") | Should -BeFalse -Because "the key is removed in finally"
        if ($SignFails -eq "true") {
            $result.ExitCode | Should -Not -Be 0
            $result.Printed | Should -BeLike "*stub signing failure*"
            Test-Path -LiteralPath $outputFile | Should -BeFalse -Because "a failed signature reports no attestation"
        }
        else {
            $result.ExitCode | Should -Be 0
            @(Get-Content -LiteralPath $outputFile) | Should -Be @("attested=true", "attestation-path=/work/pkg.nupkg.attestation.json")
        }
    }

    It "generates the template without signing credentials and builds the companion without them" {
        $generateStep = Get-WorkflowStepBlock -Content $script:content -StepName $GenerateStepName
        $generateStep | Should -Not -BeLike "*TEMPLATE_ATTESTATION*"
        $generateStep | Should -Not -BeLike "*Attestation*"

        $companionStep = Get-WorkflowStepBlock -Content $script:content -StepName "Build attestation companion package"
        $companionStep.Contains("if: `${{ steps.attestation-signer.outputs.attested == 'true' }}") | Should -BeTrue
        $companionStep.Contains("-AttestationPath '`${{ steps.attestation-signer.outputs.attestation-path }}'") | Should -BeTrue
        $companionStep.Contains("Build-TemplateAttestationCompanionPackage") | Should -BeTrue
        $companionStep | Should -Not -BeLike "*TEMPLATE_ATTESTATION_PRIVATE_KEY*"
        $companionStep | Should -Not -BeLike "*secrets.*"
        $companionStep | Should -Not -BeLike "*template-attestation-signer.pem*"
    }

    It "orders the steps: generation, then attestation, then key cleanup (always), then the companion" {
        $generateIndex = $script:content.IndexOf("- name: $GenerateStepName")
        $attestIndex = $script:content.IndexOf("- name: Attest template package")
        $cleanupIndex = $script:content.IndexOf("- name: Remove attestation signing key")
        $companionIndex = $script:content.IndexOf("- name: Build attestation companion package")

        $generateIndex | Should -BeGreaterThan 0
        $attestIndex | Should -BeGreaterThan $generateIndex
        $cleanupIndex | Should -BeGreaterThan $attestIndex
        $companionIndex | Should -BeGreaterThan $cleanupIndex

        (Get-WorkflowStepBlock -Content $script:content -StepName "Remove attestation signing key").Contains("if: always()") | Should -BeTrue
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
    # Unattested publication keeps each workflow's historical duplicate behavior: Minimal pushed
    # through build-dms.ps1 Push, which never passed --skip-duplicate, so a duplicate fails;
    # Populated always passed --skip-duplicate.
    @{ WorkflowFile = "build-minimal-template.yml"; PushStepName = "Push Minimal Template Package to Azure"; UnattestedPushArguments = ""; UnattestedDuplicateFails = $true }
    @{ WorkflowFile = "build-populated-template.yml"; PushStepName = "Push Populated Template Package to Azure"; UnattestedPushArguments = " --skip-duplicate"; UnattestedDuplicateFails = $false }
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

    It "publishes an unattested template with no feed check and the workflow's historical duplicate handling" {
        Remove-Item -LiteralPath (Join-Path $script:packageDirectory $script:companionFile), (Join-Path $script:packageDirectory "$script:templateFile.attestation.json")

        Invoke-PushStep

        @($global:TemplatePublishCalls) | Should -Be @("dotnet nuget push $script:templateFile --api-key test-key --source $($script:feedUrl)$UnattestedPushArguments")
        $global:TemplatePublishFeed[$script:templateFile.ToLowerInvariant()] | Should -Be ([byte[]](1, 1, 1))
    }

    It "on an unattested duplicate, <WorkflowFile> fails the push: <UnattestedDuplicateFails>" {
        Remove-Item -LiteralPath (Join-Path $script:packageDirectory $script:companionFile), (Join-Path $script:packageDirectory "$script:templateFile.attestation.json")
        $global:TemplatePublishFeed[$script:templateFile.ToLowerInvariant()] = [byte[]](9, 9, 9)

        if ($UnattestedDuplicateFails) {
            { Invoke-PushStep } | Should -Throw "Package push failed for '$script:templateFile' with exit code 1."
        }
        else {
            Invoke-PushStep
        }

        @($global:TemplatePublishCalls) | Should -Be @("dotnet nuget push $script:templateFile --api-key test-key --source $($script:feedUrl)$UnattestedPushArguments")
        $global:TemplatePublishFeed[$script:templateFile.ToLowerInvariant()] | Should -Be ([byte[]](9, 9, 9))
    }
}

Describe "template caller workflows pass the attestation secret through (<CallerFile>)" -ForEach @(
    @{ CallerFile = "EdFi.Api.Minimal.Template.PostgreSQL.yml" }
    @{ CallerFile = "EdFi.Api.Minimal.Template.MsSql.yml" }
    @{ CallerFile = "EdFi.Api.Populated.Template.PostgreSQL.yml" }
    @{ CallerFile = "EdFi.Api.Populated.Template.MsSql.yml" }
) {
    BeforeAll {
        $script:callerContent = Get-Content -LiteralPath ([System.IO.Path]::GetFullPath(
                (Join-Path $PSScriptRoot "../../../.github/workflows/$CallerFile"))) -Raw
    }

    It "forwards TEMPLATE_ATTESTATION_PRIVATE_KEY to the reusable workflow" {
        # Passing it by name is what lets the called job receive the ENVIRONMENT secret: the
        # called job's environment takes precedence, and a secret that exists only in the
        # environment can still be passed by name.
        $script:callerContent.Contains(
            'TEMPLATE_ATTESTATION_PRIVATE_KEY: ${{ secrets.TEMPLATE_ATTESTATION_PRIVATE_KEY }}') | Should -BeTrue
    }

    It "leaves the environment to the reusable workflow and still builds pull requests, unsigned" {
        # A job that calls a reusable workflow cannot set an environment; the called
        # build-template job does, and only on an eligible run.
        @($script:callerContent -split "`n" | Where-Object { $_ -match '^\s*environment:' }) | Should -BeNullOrEmpty
        $script:callerContent | Should -Match '(?m)^  pull_request:$'
        $script:callerContent.Contains("uses: ./.github/workflows/build-") | Should -BeTrue
        # Pull request builds stay unpublished; that they are also unsigned is the reusable
        # workflow's eligibility rule, pinned in the wiring Describe above.
        $script:callerContent.Contains("publish_package: `${{ github.event_name != 'pull_request' && (github.event_name != 'workflow_dispatch' || inputs.publish_package == true) }}") | Should -BeTrue
    }
}
