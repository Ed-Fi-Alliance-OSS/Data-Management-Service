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

        # Every push is individually failure-visible.
        $pushStep.Contains("Package push failed for") | Should -BeTrue
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
