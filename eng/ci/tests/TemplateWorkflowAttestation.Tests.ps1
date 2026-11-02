# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

# Attestation guardrails for the database-template build workflows. These invariants live
# purely in declarative CI wiring - secret gating, job isolation, artifact handoffs, and the
# publication coupling - so no invoked-script test can reach them; a regression here would
# publish a signed template without its companion attestation package, or sign with a key
# that leaked outside RUNNER_TEMP. Following DmsPullRequestMssqlWorkflow.Tests.ps1, no YAML
# parser is assumed: assertions run over the raw workflow text.

[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidGlobalVars', '', Justification = 'The publication stubs replace dotnet and the web cmdlets as global functions that the extracted workflow script resolves; global variables carry the model feed between them and are removed in AfterEach.')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', '', Justification = 'Pester stubs intentionally keep production-compatible signatures.')]
param()

BeforeAll {
    # The one signing-eligibility rule: never a pull request, and only main or a v*.*.* tag.
    # Each reusable workflow states it twice (the signing job's gate and the build job's
    # unattested-build notice), and the tests require each verbatim.
    $script:signingEligibleExpression = "github.event_name != 'pull_request' && (github.ref == 'refs/heads/main' || startsWith(github.ref, 'refs/tags/v'))"
    # The non-secret configuration gate the signing job also requires.
    $script:signingEnabledExpression = "vars.TEMPLATE_ATTESTATION_SIGNING_ENABLED == 'true'"

    function script:Test-SigningEligibleExpression {
        # Evaluates the workflow expression for one event, ref, and signing-enabled variable by
        # translating its few operators into PowerShell, with GitHub's case-insensitive string
        # comparison. Any token outside that vocabulary survives the translation and fails to
        # parse, so a changed rule cannot pass by being ignored.
        param([string]$Expression, [string]$EventName, [string]$Ref, [string]$SigningEnabled = "")

        $translated = $Expression.
            Replace("vars.TEMPLATE_ATTESTATION_SIGNING_ENABLED", '$SigningEnabled').
            Replace("github.event_name", '$EventName').
            Replace("github.ref", '$Ref').
            Replace("!=", "-ine").
            Replace("==", "-ieq").
            Replace("&&", "-and").
            Replace("||", "-or")
        $translated = [regex]::Replace($translated, "startsWith\(\`$Ref, '([^']*)'\)", '$Ref.StartsWith(''$1'', [StringComparison]::OrdinalIgnoreCase)')
        return [bool](& ([scriptblock]::Create("param(`$EventName, `$Ref, `$SigningEnabled) $translated")) $EventName $Ref $SigningEnabled)
    }

    function script:Test-PublishCondition {
        # Evaluates the publish job's condition for one set of upstream job results, the same
        # way: needs.<job>.result becomes that job's result string, and anything unrecognized
        # fails to parse.
        param([string]$Expression, [bool]$PublishPackage, [hashtable]$Results)

        $translated = $Expression.Replace("!cancelled()", '$true').Replace("inputs.publish_package", '$PublishPackage')
        $translated = [regex]::Replace($translated, "needs\.([a-z-]+)\.result", { param($match) "'" + $Results[$match.Groups[1].Value] + "'" })
        $translated = $translated.Replace("==", "-ieq").Replace("&&", "-and").Replace("||", "-or")
        return [bool](& ([scriptblock]::Create("param(`$PublishPackage) $translated")) $PublishPackage)
    }

    function script:Get-WorkflowJobBlock {
        # One whole job, up to the next job, without the comment block that introduces the next job.
        param([string]$Content, [string]$JobName)

        $start = $Content.IndexOf("`n  ${JobName}:`n")
        $start | Should -BeGreaterThan 0
        $next = [regex]::Match($Content.Substring($start + 1), "\n  [a-z-]+:\n")
        $block = if ($next.Success) { $Content.Substring($start + 1, $next.Index) } else { $Content.Substring($start + 1) }
        return ($block -replace '(\n(  #[^\n]*)?)+$', '')
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
        $next = [regex]::Match($Content.Substring($start + 1), "\n      - name: |\n\n  [a-z-]+:\n|\n\n  #")
        $block = if ($next.Success) { $Content.Substring($start, $next.Index + 1) } else { $Content.Substring($start) }
        return ($block -replace '(\n(      #[^\n]*)?)+$', '')
    }

    function script:Invoke-WorkflowStep {
        # Runs one step's own script in a child pwsh (as the runner does) from -WorkDir, with
        # its workflow expressions bound to test values.
        param([string]$Content, [string]$StepName, [string]$WorkDir, [hashtable]$Environment)

        $stepScript = (Get-WorkflowStepRunScript -Content $Content -StepName $StepName).
            Replace('${{ inputs.standard_version }}', "5.2.0").
            Replace('${{ inputs.database_engine }}', "postgresql").
            Replace('${{ needs.build-template.outputs.package-version }}', "1.2.3")
        $stepScript | Should -Not -Match '\$\{\{'
        $stepPath = Join-Path $WorkDir "step-$([Guid]::NewGuid().ToString('n')).ps1"
        # A failure prints its raw message: the error view wraps long messages at the console
        # width, with ANSI codes between the pieces, so a wildcard match on it is unreliable.
        Set-Content -LiteralPath $stepPath -Value (
            "`$ErrorActionPreference = 'stop'`ntry {`n$stepScript`n}`ncatch {`n[Console]::Out.WriteLine(`"STEP FAILED: `$(`$_.Exception.Message)`")`nexit 1`n}")

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

    function script:New-AttestStepWorkDir {
        # A working directory for the attest step: the downloaded template package (as the
        # artifact download leaves it) and a Template-Management.psm1 recording stub. The stub
        # logs what the signing call saw of the key file while the step held it, and writes a
        # real attestation document whose payload binds the package hash it read; STUB_* knobs
        # make it fail, bind other bytes, or change the package while attesting.
        $workDir = (New-Item -ItemType Directory -Path (Join-Path $TestDrive "attest-$([Guid]::NewGuid().ToString('n'))")).FullName
        [System.IO.File]::WriteAllBytes((Join-Path $workDir "EdFi.Api.Test.Template.1.2.3.nupkg"), [byte[]](7, 7, 7, 7))
        @(
            'function Get-TemplatePackageConfig { param($ConfigFilePath, $StandardVersion, $DatabaseEngine) Add-Content -LiteralPath (Join-Path $PSScriptRoot "stub.log") -Value "config $ConfigFilePath $StandardVersion $DatabaseEngine"; @{ Id = "EdFi.Api.Test.Template" } }'
            'function Write-TemplatePackageAttestation {'
            '    param($Config, $PackageVersion, $BackupDirectory, $AttestationSignerKeyPath, $AttestationProducer)'
            '    $mode = [Convert]::ToString([int](Get-Item -LiteralPath $AttestationSignerKeyPath).UnixFileMode, 8)'
            '    $key = [System.IO.File]::ReadAllText($AttestationSignerKeyPath)'
            '    Add-Content -LiteralPath (Join-Path $PSScriptRoot "stub.log") -Value "sign id=$($Config.Id) version=$PackageVersion dir=$BackupDirectory producer=$AttestationProducer mode=$mode key=$key"'
            '    if ($env:STUB_SIGN_FAILS -eq "true") { throw "stub signing failure" }'
            '    $packagePath = Join-Path $BackupDirectory "$($Config.Id).$PackageVersion.nupkg"'
            '    $boundSha256 = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash.ToLowerInvariant()'
            '    if ($env:STUB_BIND_OTHER_BYTES -eq "true") { $boundSha256 = "0" * 64 }'
            '    if ($env:STUB_CHANGE_PACKAGE -eq "true") { [System.IO.File]::AppendAllText($packagePath, "x") }'
            '    $payload = [System.Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes((@{ packageSha256 = $boundSha256 } | ConvertTo-Json -Compress)))'
            '    $attestationPath = Join-Path (Resolve-Path -LiteralPath $BackupDirectory).ProviderPath "$($Config.Id).$PackageVersion.nupkg.attestation.json"'
            '    Set-Content -LiteralPath $attestationPath -Value (@{ payloadB64 = $payload } | ConvertTo-Json)'
            '    return $attestationPath'
            '}'
        ) | Set-Content -LiteralPath (Join-Path $workDir "Template-Management.psm1")
        return $workDir
    }

    function script:Get-AttestStepEnvironment {
        param([string]$WorkDir, [string]$Key, [hashtable]$Overrides = @{})

        $package = Join-Path $WorkDir "EdFi.Api.Test.Template.1.2.3.nupkg"
        $environment = @{
            TEMPLATE_ATTESTATION_PRIVATE_KEY = $Key
            EXPECTED_PACKAGE_SHA256          = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant()
            RUNNER_TEMP                      = $WorkDir
            STUB_SIGN_FAILS                  = $null
            STUB_BIND_OTHER_BYTES            = $null
            STUB_CHANGE_PACKAGE              = $null
        }
        foreach ($name in $Overrides.Keys) { $environment[$name] = $Overrides[$name] }
        return $environment
    }
}

Describe "template build workflow attestation wiring (<WorkflowFile>)" -ForEach @(
    @{ WorkflowFile = "build-minimal-template.yml"; Kind = "Minimal"; GenerateStepName = "Generate Minimal Template package"; PushStepName = "Push Minimal Template Package to Azure"; SettingsFile = "MinimalTemplateSettings.psd1" }
    @{ WorkflowFile = "build-populated-template.yml"; Kind = "Populated"; GenerateStepName = "Generate Populated Template package"; PushStepName = "Push Populated Template Package to Azure"; SettingsFile = "PopulatedTemplateSettings.psd1" }
) {
    BeforeAll {
        $script:workflowPath = [System.IO.Path]::GetFullPath(
            (Join-Path $PSScriptRoot "../../../.github/workflows/$WorkflowFile"))
        $script:content = Get-Content -LiteralPath $script:workflowPath -Raw
        $script:buildJob = Get-WorkflowJobBlock -Content $script:content -JobName "build-template"
        $script:signJob = Get-WorkflowJobBlock -Content $script:content -JobName "sign-template-package"
        $script:companionJob = Get-WorkflowJobBlock -Content $script:content -JobName "build-attestation-companion"
        $script:publishJob = Get-WorkflowJobBlock -Content $script:content -JobName "publish-template-package"
    }

    It "declares TEMPLATE_ATTESTATION_PRIVATE_KEY as an optional workflow_call secret" {
        $secretIndex = $script:content.IndexOf("TEMPLATE_ATTESTATION_PRIVATE_KEY:")
        $secretIndex | Should -BeGreaterThan 0
        # The declaration's first key after its comment block.
        $requiredIndex = $script:content.IndexOf("required:", $secretIndex)
        $script:content.Substring($requiredIndex, "required: false".Length) | Should -Be "required: false"
        $script:content.IndexOf("outputs:", $secretIndex) | Should -BeGreaterThan $requiredIndex
    }

    It "gives the signing secret to the attest step of sign-template-package alone" {
        ([regex]::Matches($script:content, [regex]::Escape("secrets.TEMPLATE_ATTESTATION_PRIVATE_KEY"))).Count | Should -Be 1

        $attestStep = Get-WorkflowStepBlock -Content $script:content -StepName "Attest template package"
        $script:signJob.Contains($attestStep) | Should -BeTrue -Because "the attest step belongs to the signing job"
        $attestStep.Contains('TEMPLATE_ATTESTATION_PRIVATE_KEY: ${{ secrets.TEMPLATE_ATTESTATION_PRIVATE_KEY }}') | Should -BeTrue
        $attestStep.Contains('$env:RUNNER_TEMP "template-attestation-signer.pem"') | Should -BeTrue
        $attestStep.Contains("-AttestationProducer 'edfi-alliance-ci'") | Should -BeTrue

        foreach ($job in @($script:buildJob, $script:companionJob, $script:publishJob)) {
            $job | Should -Not -BeLike "*TEMPLATE_ATTESTATION_PRIVATE_KEY*"
            $job | Should -Not -BeLike "*template-attestation-signer.pem*"
        }
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
        $jobCommentStart = $script:content.IndexOf("  # Signing is requested only")
        $jobComment = $script:content.Substring($jobCommentStart, $script:content.IndexOf("`n  sign-template-package:`n") - $jobCommentStart)
        ($jobComment -replace "\n\s+#", "") | Should -BeLike "*Matching a ref name is not enough to authorize signing*"
        ($jobComment -replace "\n\s+#", "") | Should -BeLike "*Approval applies to this whole job, so it runs on a fresh runner*"
    }

    It "binds only sign-template-package, gated on eligibility and the signing-enabled variable, to the protected environment" {
        $environmentLines = @($script:content -split "`n" | Where-Object { $_ -match '^\s*environment:' })
        $environmentLines | Should -Be @("    environment: template-attestation-signing")
        $script:signJob.Contains($environmentLines[0]) | Should -BeTrue

        $script:signJob.Contains("`n    needs: build-template`n") | Should -BeTrue
        $script:signJob.Contains("`n    if: `${{ $script:signingEligibleExpression && $script:signingEnabledExpression }}`n") | Should -BeTrue
        # A job without an environment has no path to the environment secret.
        $script:buildJob | Should -Not -Match '(?m)^\s*environment:'
        $script:companionJob | Should -Not -Match '(?m)^\s*environment:'
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

    It "runs the protected signing job only for <Case>" -ForEach @(
        @{ Case = "an eligible run with signing enabled"; EventName = "push"; Ref = "refs/heads/main"; SigningEnabled = "true"; Runs = $true }
        @{ Case = "an eligible tag run with signing enabled"; EventName = "push"; Ref = "refs/tags/v8.1.0"; SigningEnabled = "true"; Runs = $true }
        @{ Case = "an eligible run with the variable unset, never"; EventName = "push"; Ref = "refs/heads/main"; SigningEnabled = ""; Runs = $false }
        @{ Case = "an eligible run with the variable not 'true', never"; EventName = "push"; Ref = "refs/heads/main"; SigningEnabled = "false"; Runs = $false }
        @{ Case = "a pull request with signing enabled, never"; EventName = "pull_request"; Ref = "refs/heads/main"; SigningEnabled = "true"; Runs = $false }
        @{ Case = "a feature-branch dispatch with signing enabled, never"; EventName = "workflow_dispatch"; Ref = "refs/heads/DMS-1271"; SigningEnabled = "true"; Runs = $false }
    ) {
        $jobCondition = [regex]::Match($script:signJob, '(?m)^    if: \$\{\{ (?<expression>.+) \}\}$').Groups["expression"].Value
        $jobCondition | Should -Not -BeNullOrEmpty
        Test-SigningEligibleExpression -Expression $jobCondition -EventName $EventName -Ref $Ref -SigningEnabled $SigningEnabled | Should -Be $Runs
    }

    It "says loudly when a build will not be signed (<Case>)" -ForEach @(
        @{ Case = "ineligible run"; Eligible = "false"; Enabled = "true"; Notice = "*::notice title=Unattested template build::Only main and v*.*.* tag runs (never pull requests) sign template packages*UNATTESTED*" }
        @{ Case = "signing not enabled"; Eligible = "true"; Enabled = "false"; Notice = "*::notice title=Unattested template build::The TEMPLATE_ATTESTATION_SIGNING_ENABLED repository variable is not 'true'*UNATTESTED*" }
        @{ Case = "signing requested"; Eligible = "true"; Enabled = "true"; Notice = "sign-template-package attests this template package after the build, on a separate runner." }
    ) {
        $planStep = Get-WorkflowStepBlock -Content $script:content -StepName "Report template signing plan"
        $script:buildJob.Contains($planStep) | Should -BeTrue
        $planStep.Contains("SIGNING_ELIGIBLE: `${{ $script:signingEligibleExpression }}") | Should -BeTrue
        $planStep.Contains("SIGNING_ENABLED: `${{ $script:signingEnabledExpression }}") | Should -BeTrue

        $workDir = (New-Item -ItemType Directory -Path (Join-Path $TestDrive "plan-$([Guid]::NewGuid().ToString('n'))")).FullName
        $result = Invoke-WorkflowStep -Content $script:content -StepName "Report template signing plan" -WorkDir $workDir -Environment @{ SIGNING_ELIGIBLE = $Eligible; SIGNING_ENABLED = $Enabled }

        $result.ExitCode | Should -Be 0
        $result.Printed | Should -BeLike $Notice
    }

    It "fails an enabled signing run that has no key, before loading the signer or writing a key" {
        $workDir = New-AttestStepWorkDir

        $result = Invoke-WorkflowStep -Content $script:content -StepName "Attest template package" -WorkDir $workDir -Environment (Get-AttestStepEnvironment -WorkDir $workDir -Key "")

        $result.ExitCode | Should -Not -Be 0
        $result.Printed | Should -BeLike "*TEMPLATE_ATTESTATION_PRIVATE_KEY is not configured*refusing to continue without signing*"
        Test-Path -LiteralPath (Join-Path $workDir "stub.log") | Should -BeFalse -Because "a run that cannot sign loads no signing helper"
        Test-Path -LiteralPath (Join-Path $workDir "template-attestation-signer.pem") | Should -BeFalse
    }

    It "refuses to sign a downloaded package whose bytes differ from build-template's, before writing a key" {
        $workDir = New-AttestStepWorkDir

        $result = Invoke-WorkflowStep -Content $script:content -StepName "Attest template package" -WorkDir $workDir `
            -Environment (Get-AttestStepEnvironment -WorkDir $workDir -Key "-----BEGIN PRIVATE KEY-----" -Overrides @{ EXPECTED_PACKAGE_SHA256 = ("ab" * 32) })

        $result.ExitCode | Should -Not -Be 0
        $result.Printed | Should -BeLike "*does not match the bytes build-template packed; refusing to sign it*"
        @(Get-Content -LiteralPath (Join-Path $workDir "stub.log")) | Should -Be @("config ./$SettingsFile 5.2.0 postgresql") -Because "nothing is signed"
        Test-Path -LiteralPath (Join-Path $workDir "template-attestation-signer.pem") | Should -BeFalse
        Test-Path -LiteralPath (Join-Path $workDir "EdFi.Api.Test.Template.1.2.3.nupkg.attestation.json") | Should -BeFalse
    }

    # The owner-only creation mode is a Unix file mode, so these cases run where the job does:
    # on Linux (the CI Pester lane is ubuntu-latest).
    It "the attest step creates the key 0600, attests the unchanged package bytes, and removes the key (<Case>)" -Skip:$IsWindows -ForEach @(
        @{ Case = "signing succeeds"; Overrides = @{}; Failure = $null }
        @{ Case = "signing fails"; Overrides = @{ STUB_SIGN_FAILS = "true" }; Failure = "*stub signing failure*" }
        @{ Case = "the package changes while it is attested"; Overrides = @{ STUB_CHANGE_PACKAGE = "true" }; Failure = "*The template package changed while it was attested*" }
        @{ Case = "the attestation binds other bytes"; Overrides = @{ STUB_BIND_OTHER_BYTES = "true" }; Failure = "*The attestation does not bind the template package bytes build-template packed*" }
    ) {
        $workDir = New-AttestStepWorkDir
        $package = Join-Path $workDir "EdFi.Api.Test.Template.1.2.3.nupkg"
        $packageSha256Before = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant()
        $key = "-----BEGIN PRIVATE KEY-----test-material-----END PRIVATE KEY-----"

        $result = Invoke-WorkflowStep -Content $script:content -StepName "Attest template package" -WorkDir $workDir `
            -Environment (Get-AttestStepEnvironment -WorkDir $workDir -Key $key -Overrides $Overrides)

        @(Get-Content -LiteralPath (Join-Path $workDir "stub.log")) | Should -Be @(
            "config ./$SettingsFile 5.2.0 postgresql"
            "sign id=EdFi.Api.Test.Template version=1.2.3 dir=./ producer=edfi-alliance-ci mode=600 key=$key"
        )
        Test-Path -LiteralPath (Join-Path $workDir "template-attestation-signer.pem") | Should -BeFalse -Because "the key is removed in finally"
        if ($null -ne $Failure) {
            $result.ExitCode | Should -Not -Be 0
            $result.Printed | Should -BeLike $Failure
        }
        else {
            $result.ExitCode | Should -Be 0
            (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant() | Should -Be $packageSha256Before -Because "attestation never alters the built package"
            $attestation = Get-Content -LiteralPath "$package.attestation.json" -Raw | ConvertFrom-Json
            ([System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String($attestation.payloadB64)) | ConvertFrom-Json).packageSha256 |
                Should -Be $packageSha256Before -Because "the attestation binds the exact bytes build-template packed"
        }
    }

    It "builds and verifies the template without signing, and records the exact package for the signer" {
        $generateStep = Get-WorkflowStepBlock -Content $script:content -StepName $GenerateStepName
        $generateStep | Should -Not -BeLike "*TEMPLATE_ATTESTATION*"
        $generateStep | Should -Not -BeLike "*Attestation*"
        $script:buildJob | Should -Not -BeLike "*Write-TemplatePackageAttestation*"
        $script:buildJob | Should -Not -BeLike "*Build-TemplateAttestationCompanionPackage*"

        $script:buildJob.Contains('package-sha256: ${{ steps.package-identity.outputs.package-sha256 }}') | Should -BeTrue
        $script:buildJob.Contains('package-version: ${{ steps.package-identity.outputs.package-version }}') | Should -BeTrue
        $identityStep = Get-WorkflowStepBlock -Content $script:content -StepName "Record template package identity"
        $identityStep.Contains("(Get-FileHash -LiteralPath `$packages[0].FullName -Algorithm SHA256).Hash.ToLowerInvariant()") | Should -BeTrue
        $script:buildJob.IndexOf("- name: Record template package identity") |
            Should -BeLessThan $script:buildJob.IndexOf("- name: Upload $Kind Template Package as Artifact") -Because "the recorded hash is of the uploaded bytes"
    }

    It "runs nothing on the key-bearing runner but checkout, the package download, attestation, key cleanup, and the document upload" {
        @([regex]::Matches($script:signJob, '(?m)^      - name: (?<name>.+)$') | ForEach-Object { $_.Groups["name"].Value }) | Should -Be @(
            "Checkout the repo"
            "Get $Kind Template Package Artifact"
            "Attest template package"
            "Remove attestation signing key"
            "Upload template attestation document"
        )
        foreach ($forbidden in @("dotnet", "setup-dotnet", "Build-Template ", "Build-TemplateAttestationCompanionPackage", "docker", "psql", "sqlcmd", "start-local-dms", "provision-")) {
            $script:signJob.Contains($forbidden) | Should -BeFalse -Because "the signing runner must not execute '$forbidden'"
        }
        $script:signJob.Contains("`n    permissions:`n      contents: read`n") | Should -BeTrue
        $script:signJob.Contains("name: `${{ inputs.package_name }}-NuGet") | Should -BeTrue -Because "it attests the package artifact build-template uploaded"
        (Get-WorkflowStepBlock -Content $script:content -StepName "Remove attestation signing key").Contains("if: always()") | Should -BeTrue
    }

    It "builds the companion on its own unprivileged runner from the attestation document" {
        $script:companionJob.Contains("`n    needs:`n      - build-template`n      - sign-template-package`n") | Should -BeTrue
        $script:companionJob | Should -Not -BeLike "*secrets.*"
        $script:companionJob.Contains("name: `${{ inputs.package_name }}-AttestationDocument") | Should -BeTrue
        $companionStep = Get-WorkflowStepBlock -Content $script:content -StepName "Build attestation companion package"
        $script:companionJob.Contains($companionStep) | Should -BeTrue
        $companionStep.Contains("Build-TemplateAttestationCompanionPackage") | Should -BeTrue
    }

    It "hands the attestation document, never a repacked template, from signing to the companion and to publication" {
        $script:buildJob.Contains("/eng/DatabaseTemplates/*.nupkg.attestation.json") | Should -BeFalse -Because "build-template never holds an attestation"
        $signUpload = Get-WorkflowStepBlock -Content $script:content -StepName "Upload template attestation document"
        $signUpload.Contains('name: "${{ inputs.package_name }}-AttestationDocument"') | Should -BeTrue
        $signUpload.Contains("path: `${{ github.workspace }}/eng/DatabaseTemplates/*.nupkg.attestation.json`n") | Should -BeTrue
        $companionUpload = Get-WorkflowStepBlock -Content $script:content -StepName "Upload attestation companion package"
        $companionUpload.Contains('name: "${{ inputs.package_name }}-Attestation"') | Should -BeTrue
        $companionUpload.Contains("/eng/DatabaseTemplates/*.nupkg.attestation.json") | Should -BeTrue

        $script:publishJob.Contains("name: `${{ inputs.package_name }}-NuGet") | Should -BeTrue
        $attestationDownload = Get-WorkflowStepBlock -Content $script:content -StepName "Get $Kind Template Attestation Artifact"
        $attestationDownload.Contains("if: `${{ needs.build-attestation-companion.result == 'success' }}") | Should -BeTrue
        $attestationDownload.EndsWith("name: `${{ inputs.package_name }}-Attestation") | Should -BeTrue
    }

    It "publishes for <Case>: <Publishes>" -ForEach @(
        @{ Case = "an unsigned run (signing skipped by its gate)"; PublishPackage = $true; Sign = "skipped"; Companion = "skipped"; Build = "success"; Publishes = $true }
        @{ Case = "a signed run with its companion"; PublishPackage = $true; Sign = "success"; Companion = "success"; Build = "success"; Publishes = $true }
        @{ Case = "a failed signing run"; PublishPackage = $true; Sign = "failure"; Companion = "skipped"; Build = "success"; Publishes = $false }
        @{ Case = "a cancelled signing run"; PublishPackage = $true; Sign = "cancelled"; Companion = "skipped"; Build = "success"; Publishes = $false }
        @{ Case = "a signed run whose companion failed"; PublishPackage = $true; Sign = "success"; Companion = "failure"; Build = "success"; Publishes = $false }
        @{ Case = "a signed run without a companion result"; PublishPackage = $true; Sign = "success"; Companion = "skipped"; Build = "success"; Publishes = $false }
        @{ Case = "a failed build"; PublishPackage = $true; Sign = "skipped"; Companion = "skipped"; Build = "failure"; Publishes = $false }
        @{ Case = "a run that does not publish"; PublishPackage = $false; Sign = "success"; Companion = "success"; Build = "success"; Publishes = $false }
    ) {
        foreach ($need in @("build-template", "generate-package-sbom", "generate-package-provenance", "sign-template-package", "build-attestation-companion")) {
            $script:publishJob.Contains("`n      - $need`n") | Should -BeTrue -Because "publication waits for $need"
        }
        $publishCondition = [regex]::Match($script:publishJob, '(?m)^    if: \$\{\{ (?<expression>.+) \}\}$').Groups["expression"].Value
        $publishCondition | Should -Not -BeNullOrEmpty

        Test-PublishCondition -Expression $publishCondition -PublishPackage $PublishPackage -Results @{
            "build-template"              = $Build
            "generate-package-sbom"       = $(if ($Build -eq "success") { "success" } else { "skipped" })
            "generate-package-provenance" = $(if ($Build -eq "success") { "success" } else { "skipped" })
            "sign-template-package"       = $Sign
            "build-attestation-companion" = $Companion
        } | Should -Be $Publishes
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
        # sign-template-package job does, and only on an eligible run with signing enabled.
        @($script:callerContent -split "`n" | Where-Object { $_ -match '^\s*environment:' }) | Should -BeNullOrEmpty
        $script:callerContent | Should -Match '(?m)^  pull_request:$'
        $script:callerContent.Contains("uses: ./.github/workflows/build-") | Should -BeTrue
        # Pull request builds stay unpublished; that they are also unsigned is the reusable
        # workflow's eligibility rule, pinned in the wiring Describe above.
        $script:callerContent.Contains("publish_package: `${{ github.event_name != 'pull_request' && (github.event_name != 'workflow_dispatch' || inputs.publish_package == true) }}") | Should -BeTrue
    }
}
