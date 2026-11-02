# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

# Credential guardrails for the release and prerelease workflows. A manual run takes the workflow
# file from whatever ref it names, so the Azure Artifacts and Docker Hub publishing tokens must be
# reachable only from the jobs that publish, inside the 'publishing' environment, and only from the
# step that pushes or logs in. Every other job, including the manual prerelease dry run, reads the
# public feed anonymously. Following DmsPullRequestMssqlWorkflow.Tests.ps1, no YAML parser is
# assumed: assertions run over the raw workflow text.

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot 'WorkflowExpression.psm1') -Force
    $script:workflowRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../../.github/workflows'))

    function script:Get-WorkflowJobTable([string] $Content) {
        # Each job under jobs:, keyed by id, up to the next job or a comment introducing one. A job id
        # is any GitHub accepts: a letter or _ followed by letters, digits, - or _. Empty when the
        # text has no jobs: key the guard can read.
        $jobs = [ordered]@{}
        $jobsKey = [regex]::Match($Content, '(?m)^jobs:[ \t]*(?:#.*)?\r?$')
        if (-not $jobsKey.Success) { return $jobs }
        $body = $Content.Substring($jobsKey.Index)
        $headers = [regex]::Matches($body, '(?m)^  (?<id>[A-Za-z_][A-Za-z0-9_-]*):[ \t]*(?:#.*)?\r?$')
        for ($i = 0; $i -lt $headers.Count; $i++) {
            $end = if ($i + 1 -lt $headers.Count) { $headers[$i + 1].Index } else { $body.Length }
            $jobs[$headers[$i].Groups['id'].Value] = $body.Substring($headers[$i].Index, $end - $headers[$i].Index)
        }
        return $jobs
    }

    function script:Get-WorkflowEnv([string] $Content) {
        return [regex]::Match($Content, '(?m)^env:\r?\n(?<body>(?:  .*\r?\n)+)').Groups['body'].Value
    }

    function script:Get-StepNameAt([string] $Job, [int] $Index) {
        # The name of the step whose block contains the given position in the job text.
        $names = [regex]::Matches($Job.Substring(0, $Index), '(?m)^      - name: (?<name>.+)$')
        if ($names.Count -eq 0) { return '' }
        return $names[$names.Count - 1].Groups['name'].Value.Trim()
    }

    function script:Get-JobCondition([string] $Job) { Get-WorkflowJobCondition $Job }

    function script:Test-RefCondition {
        # Evaluates a job condition built from event_name, ref_type, and ref.
        param([string] $Expression, [string] $EventName, [string] $Ref, [string] $RefType)

        return Test-WorkflowExpression -Expression $Expression -Run @{ 'github.event_name' = $EventName; 'github.ref' = $Ref; 'github.ref_type' = $RefType }
    }

    function script:Assert-SecretReferenceInStep {
        # Every secrets.<Name> reference sits inside a step whose name matches StepPattern, in a job
        # bound to the publishing environment.
        param([hashtable] $Jobs, [string] $Secret, [string] $StepPattern, [int] $ExpectedCount)

        $found = 0
        foreach ($id in $Jobs.Keys) {
            foreach ($match in [regex]::Matches($Jobs[$id], "secrets\.$Secret\b")) {
                $found++
                $Jobs[$id] | Should -Match '(?m)^    environment: publishing\r?$' -Because "$id reads $Secret"
                Get-StepNameAt -Job $Jobs[$id] -Index $match.Index | Should -Match $StepPattern -Because "$id reads $Secret only where it publishes"
            }
        }
        $found | Should -Be $ExpectedCount
    }

    function script:Get-EnvironmentSecretViolation {
        # The repository-wide rule, as findings. Each named secret is read either by a step of a job
        # bound to Environment, or passed by name from a caller job to a reusable workflow, which
        # GitHub requires for an environment secret and which resolves to nothing outside that
        # environment. Secret names are case-insensitive, so the match is too. A reference the guard
        # cannot place in a job is a finding, so syntax it does not read fails rather than passes.
        param(
            [string] $Content,
            [string[]] $Secrets = @('AZURE_ARTIFACTS_PERSONAL_ACCESS_TOKEN', 'AZURE_ARTIFACTS_USER_NAME', 'DOCKER_HUB_TOKEN'),
            [string] $Environment = 'publishing'
        )

        $names = ($Secrets | ForEach-Object { [regex]::Escape($_) }) -join '|'
        $pattern = "(?i)secrets(?:\.(?:$names)\b|\[\s*['""](?:$names)['""]\s*\])"
        $environmentLine = "(?m)^    environment: $([regex]::Escape($Environment))\r?$"
        $violations = [Collections.Generic.List[string]]::new()
        if ($Content -match '(?i)secrets:\s*inherit') { $violations.Add('passes secrets: inherit') }
        if ($Content -match '(?i)toJSON\(\s*secrets\s*\)') { $violations.Add('reads the whole secrets context') }
        if ((Get-WorkflowEnv $Content) -match $pattern) { $violations.Add("reads a $Environment token in the workflow-wide env") }

        $total = [regex]::Matches($Content, $pattern).Count
        if ($total -eq 0) { return $violations.ToArray() }

        $jobs = Get-WorkflowJobTable $Content
        $placed = 0
        foreach ($id in $jobs.Keys) {
            $job = $jobs[$id]
            foreach ($match in [regex]::Matches($job, $pattern)) {
                $placed++
                $line = ($job.Substring(0, $match.Index) -split "`n")[-1]
                if ($job -match '(?m)^    uses: ') {
                    $callerSecrets = [regex]::Match($job, '(?ms)^    secrets:\r?\n(?:      .*\r?\n?)+')
                    if (-not ($callerSecrets.Success -and $match.Index -ge $callerSecrets.Index -and $match.Index -lt $callerSecrets.Index + $callerSecrets.Length)) {
                        $violations.Add("$id passes $($match.Value) other than through its reusable-workflow secrets")
                    }
                    continue
                }
                if ($job -notmatch $environmentLine) { $violations.Add("$id reads $($match.Value) outside the $Environment environment") }
                if ($line -notmatch '^ {10}\S') { $violations.Add("$id reads $($match.Value) other than at step level") }
                if ($job.IndexOf("`n    steps:") -lt 0 -or $job.IndexOf("`n    steps:") -gt $match.Index) { $violations.Add("$id reads $($match.Value) outside its steps") }
            }
        }
        if ($placed -ne $total) {
            $violations.Add("$($total - $placed) $Environment token reference(s) outside any job this guard can read")
        }
        return $violations.ToArray()
    }
}

Describe 'on-prerelease.yml publishing credentials' {
    BeforeAll {
        $script:content = Get-Content (Join-Path $script:workflowRoot 'on-prerelease.yml') -Raw
        $script:jobs = Get-WorkflowJobTable $script:content
        $script:jobs.Count | Should -BeGreaterThan 0
        $script:dmsPublishers = @('publish-package-dms', 'publish-package-schema-tools', 'publish-package-plugins', 'publish-package-custom-validation', 'publish-package-identity', 'docker-publish-dms')
        $script:csPublishers = @('publish-package-secrets', 'publish-package-cs', 'publish-package-security-metadata-tools', 'docker-publish-cs')
    }

    It 'keeps every secret out of the workflow-wide env' {
        Get-WorkflowEnv $script:content | Should -Not -Match 'secrets\.'
    }

    It 'binds exactly the publishing jobs to the publishing environment' {
        $bound = @($script:jobs.Keys | Where-Object { $script:jobs[$_] -match '(?m)^    environment: publishing\r?$' })
        $bound | Sort-Object | Should -Be (@($script:dmsPublishers) + @($script:csPublishers) | Sort-Object)
        @([regex]::Matches($script:content, '(?m)^\s*environment:')).Count | Should -Be $bound.Count
    }

    It 'reads the Azure publishing token only in the push steps' {
        # Each push step binds it twice: the NuGet API key and the credential provider's endpoint.
        Assert-SecretReferenceInStep -Jobs $script:jobs -Secret 'AZURE_ARTIFACTS_PERSONAL_ACCESS_TOKEN' -StepPattern '^Push .+ Package to Azure Artifacts$' -ExpectedCount 16
        $script:content | Should -Not -Match 'secrets\.AZURE_ARTIFACTS_USER_NAME'
    }

    It 'reads the Docker Hub publishing token only in the Docker Hub logins' {
        Assert-SecretReferenceInStep -Jobs $script:jobs -Secret 'DOCKER_HUB_TOKEN' -StepPattern '^Log in to Docker Hub for ' -ExpectedCount 2
        $script:content | Should -Not -Match 'env\.DOCKER_HUB_TOKEN'
    }

    It 'checks and rechecks the contracts against the feed anonymously' {
        $script:content | Should -Not -Match 'FeedApiKey'
        foreach ($id in $script:jobs.Keys) {
            foreach ($match in [regex]::Matches($script:jobs[$id], 'env\.ARTIFACTS_API_KEY')) {
                Get-StepNameAt -Job $script:jobs[$id] -Index $match.Index |
                    Should -Match '^Push .+ Package to Azure Artifacts$' -Because "$id uses the token only where it pushes"
            }
        }
    }

    It 'runs the manual dry run with no publishing environment or secret' {
        # The dispatch runs these, and the SBOM and provenance jobs that need them.
        $dryRun = @($script:jobs.Keys | Where-Object { (Get-JobCondition $script:jobs[$_]) -like "github.event_name == 'workflow_dispatch' ||*" })
        $dryRun | Sort-Object | Should -Be (@(
                'pack-plugins', 'pack-custom-validation', 'pack-identity', 'pack-secrets',
                'check-plugins-contract', 'check-custom-validation-contract', 'check-identity-contract', 'check-secrets-contract'
            ) | Sort-Object)
        foreach ($id in $script:jobs.Keys | Where-Object { $_ -notin $script:dmsPublishers -and $_ -notin $script:csPublishers }) {
            $script:jobs[$id] | Should -Not -Match '(?m)^\s*environment:' -Because "$id does not publish"
            ([regex]::Matches($script:jobs[$id], 'secrets\.(?<name>[A-Z_]+)') | ForEach-Object { $_.Groups['name'].Value } | Where-Object { $_ -ne 'GITHUB_TOKEN' }) |
                Should -BeNullOrEmpty -Because "$id does not publish"
        }
    }

    It 'publishes <Product> only from <Case>: <Publishes>' -ForEach @(
        @{ Product = 'DMS'; Case = 'a dms- prerelease'; EventName = 'release'; Ref = 'refs/tags/dms-pre-8.1.0-alpha.0.12'; RefType = 'tag'; Publishes = $true }
        @{ Product = 'DMS'; Case = 'a cs- prerelease, never'; EventName = 'release'; Ref = 'refs/tags/cs-pre-8.1.0-alpha.0.12'; RefType = 'tag'; Publishes = $false }
        @{ Product = 'DMS'; Case = 'a manual dry run from a branch, never'; EventName = 'workflow_dispatch'; Ref = 'refs/heads/DMS-1647-1649'; RefType = 'branch'; Publishes = $false }
        @{ Product = 'DMS'; Case = 'a manual run from a dms- prerelease tag, never'; EventName = 'workflow_dispatch'; Ref = 'refs/tags/dms-pre-8.1.0-alpha.0.12'; RefType = 'tag'; Publishes = $false }
        @{ Product = 'DMS'; Case = 'a release whose ref is a branch named like the tag, never'; EventName = 'release'; Ref = 'refs/tags/dms-pre-8.1.0'; RefType = 'branch'; Publishes = $false }
        @{ Product = 'Configuration Service'; Case = 'a cs- prerelease'; EventName = 'release'; Ref = 'refs/tags/cs-pre-8.1.0-alpha.0.12'; RefType = 'tag'; Publishes = $true }
        @{ Product = 'Configuration Service'; Case = 'a dms- prerelease, never'; EventName = 'release'; Ref = 'refs/tags/dms-pre-8.1.0-alpha.0.12'; RefType = 'tag'; Publishes = $false }
        @{ Product = 'Configuration Service'; Case = 'a manual dry run from main, never'; EventName = 'workflow_dispatch'; Ref = 'refs/heads/main'; RefType = 'branch'; Publishes = $false }
    ) {
        $publishers = if ($Product -eq 'DMS') { $script:dmsPublishers } else { $script:csPublishers }
        foreach ($id in $publishers) {
            $condition = Get-JobCondition $script:jobs[$id]
            $condition | Should -Not -BeNullOrEmpty -Because "$id must state its own trusted-ref gate"
            Test-RefCondition -Expression $condition -EventName $EventName -Ref $Ref -RefType $RefType |
                Should -Be $Publishes -Because $id
        }
    }
}

Describe 'on-release.yml publishing credentials' {
    BeforeAll {
        $script:content = Get-Content (Join-Path $script:workflowRoot 'on-release.yml') -Raw
        $script:jobs = Get-WorkflowJobTable $script:content
        $script:jobs.Count | Should -BeGreaterThan 0
        $script:publishers = @('promote-azure-artifact', 'tag-dms-image', 'tag-cs-image')
    }

    It 'keeps every secret out of the workflow-wide env' {
        Get-WorkflowEnv $script:content | Should -Not -Match 'secrets\.'
    }

    It 'binds exactly the promotion and image jobs to the publishing environment' {
        $bound = @($script:jobs.Keys | Where-Object { $script:jobs[$_] -match '(?m)^    environment: publishing\r?$' })
        $bound | Sort-Object | Should -Be ($script:publishers | Sort-Object)
        $script:jobs['delete-pre-releases'] | Should -Not -Match '(?m)^\s*environment:'
    }

    It 'reads the Azure publishing credentials only in the promote steps' {
        # Eight promote steps, each binding the user name and the token.
        Assert-SecretReferenceInStep -Jobs $script:jobs -Secret 'AZURE_ARTIFACTS_PERSONAL_ACCESS_TOKEN' -StepPattern '^Promote .+ Package$' -ExpectedCount 8
        Assert-SecretReferenceInStep -Jobs $script:jobs -Secret 'AZURE_ARTIFACTS_USER_NAME' -StepPattern '^Promote .+ Package$' -ExpectedCount 8
    }

    It 'reads the Docker Hub publishing token only in the Docker Hub logins' {
        Assert-SecretReferenceInStep -Jobs $script:jobs -Secret 'DOCKER_HUB_TOKEN' -StepPattern '^Login to Docker hub$' -ExpectedCount 2
    }

    It 'promotes and tags images only from <Case>: <Publishes>' -ForEach @(
        @{ Case = 'a stable release'; EventName = 'release'; Ref = 'refs/tags/v8.1.0'; RefType = 'tag'; Publishes = $true }
        @{ Case = 'a manual run on a v tag'; EventName = 'workflow_dispatch'; Ref = 'refs/tags/v8.1.0'; RefType = 'tag'; Publishes = $true }
        @{ Case = 'a manual run from main, never'; EventName = 'workflow_dispatch'; Ref = 'refs/heads/main'; RefType = 'branch'; Publishes = $false }
        @{ Case = 'a manual run from a branch named like a v tag, never'; EventName = 'workflow_dispatch'; Ref = 'refs/heads/v8.1.0'; RefType = 'branch'; Publishes = $false }
        @{ Case = 'a manual run from a prerelease tag, never'; EventName = 'workflow_dispatch'; Ref = 'refs/tags/dms-pre-8.1.0'; RefType = 'tag'; Publishes = $false }
    ) {
        foreach ($id in $script:publishers) {
            $condition = Get-JobCondition $script:jobs[$id]
            $condition | Should -Not -BeNullOrEmpty -Because "$id must state its own trusted-ref gate"
            Test-RefCondition -Expression $condition -EventName $EventName -Ref $Ref -RefType $RefType |
                Should -Be $Publishes -Because $id
        }
    }
}

Describe 'build-sdk-package.yml publishing credentials' {
    BeforeAll {
        $script:content = Get-Content (Join-Path $script:workflowRoot 'build-sdk-package.yml') -Raw
        $script:jobs = Get-WorkflowJobTable $script:content
        $script:jobs.Count | Should -BeGreaterThan 0
    }

    It 'keeps every secret out of the workflow-wide env and declares the Azure secrets optional' {
        Get-WorkflowEnv $script:content | Should -Not -Match 'secrets\.'
        $script:content | Should -Match '(?m)^      AZURE_ARTIFACTS_PERSONAL_ACCESS_TOKEN:\r?\n        required: false\r?$'
    }

    It 'binds only the push and promote jobs to the publishing environment, and reads the token only in their steps' {
        $bound = @($script:jobs.Keys | Where-Object { $script:jobs[$_] -match '(?m)^    environment: publishing\r?$' })
        $bound | Sort-Object | Should -Be @('promote-azure-package', 'publish-sdk-package')
        Assert-SecretReferenceInStep -Jobs $script:jobs -Secret 'AZURE_ARTIFACTS_PERSONAL_ACCESS_TOKEN' -StepPattern '^(Push SDK Package to Azure Artifacts|Promote SDK Azure Package)$' -ExpectedCount 3
        Assert-SecretReferenceInStep -Jobs $script:jobs -Secret 'AZURE_ARTIFACTS_USER_NAME' -StepPattern '^Promote SDK Azure Package$' -ExpectedCount 1
    }

    It 'pushes only from <Case>: <Publishes>' -ForEach @(
        @{ Case = 'a version tag push'; EventName = 'push'; Ref = 'refs/tags/v8.1.0'; RefType = 'tag'; Publishes = $true }
        @{ Case = 'a manual run on main'; EventName = 'workflow_dispatch'; Ref = 'refs/heads/main'; RefType = 'branch'; Publishes = $true }
        @{ Case = 'a manual run on a feature branch, never'; EventName = 'workflow_dispatch'; Ref = 'refs/heads/DMS-1647-1649'; RefType = 'branch'; Publishes = $false }
        @{ Case = 'a manual run on a branch named like a v tag, never'; EventName = 'workflow_dispatch'; Ref = 'refs/heads/v8.1.0'; RefType = 'branch'; Publishes = $false }
    ) {
        # inputs.publish_package is the caller's opt-in, held true here; the ref gate is what is tested.
        $condition = (Get-JobCondition $script:jobs['publish-sdk-package']).Replace('inputs.publish_package && ', '')
        Test-RefCondition -Expression $condition -EventName $EventName -Ref $Ref -RefType $RefType | Should -Be $Publishes
    }
}

Describe 'every workflow keeps the publishing tokens behind the publishing environment' {
    # The repository-wide rule the per-workflow tests above apply in detail. A publishing token is
    # read either by a step of a job bound to the 'publishing' environment, or passed by name from a
    # caller job to a reusable workflow, which GitHub requires for an environment secret and which
    # resolves to nothing outside that environment.
    BeforeDiscovery {
        $script:workflowFiles = @(Get-ChildItem -LiteralPath ([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../../.github/workflows'))) -Filter '*.yml' |
                ForEach-Object { @{ File = $_.Name } })
    }

    It '<File>' -ForEach $script:workflowFiles {
        $content = Get-Content (Join-Path $script:workflowRoot $File) -Raw
        Get-EnvironmentSecretViolation $content | Should -BeNullOrEmpty
    }

    It '<File> keeps EDFI_BUILD_AGENT_PAT in the release-automation environment' -ForEach $script:workflowFiles {
        # The build-agent token creates prerelease tags and is the identity the tag ruleset lets past
        # its restrictions, so a branch must not be able to read it either.
        $content = Get-Content (Join-Path $script:workflowRoot $File) -Raw
        Get-EnvironmentSecretViolation $content -Secrets 'EDFI_BUILD_AGENT_PAT' -Environment 'release-automation' |
            Should -BeNullOrEmpty
    }

    It 'binds exactly the build-agent token jobs to the release-automation environment' {
        $bound = foreach ($file in Get-ChildItem -LiteralPath $script:workflowRoot -Filter '*.yml') {
            $jobs = Get-WorkflowJobTable (Get-Content -LiteralPath $file.FullName -Raw)
            foreach ($id in $jobs.Keys) {
                if ($jobs[$id] -match '(?m)^\s*environment:\s*release-automation\b') { "$($file.Name):$id" }
            }
        }
        @($bound) | Sort-Object | Should -Be @(
            'on-config-merge-or-tag.yml:create-pre-releases',
            'on-dms-merge-or-tag.yml:create-pre-releases',
            'scheduled-merge-queue-enqueue.yml:enqueue'
        )
    }
}

Describe 'the repository-wide publishing token rule' {
    # Fixtures for Get-EnvironmentSecretViolation, so a workflow the guard cannot read fails it.
    BeforeAll {
        function script:New-Workflow([string] $Jobs) {
            return "name: Fixture`non: workflow_dispatch`n`npermissions: read-all`n`njobs:`n$Jobs"
        }
        $script:publishStep = @"
    runs-on: ubuntu-latest
    steps:
      - name: Log in
        uses: docker/login-action@v3
        with:
          password: `${{ secrets.DOCKER_HUB_TOKEN }}
"@
    }

    It 'flags an unprotected job whose id is <Case>' -ForEach @(
        @{ Case = 'publish_package, with an underscore'; Id = 'publish_package' }
        @{ Case = 'Publish, with a capital'; Id = 'Publish' }
        @{ Case = '_publish, starting with an underscore'; Id = '_publish' }
        @{ Case = 'publish followed by a comment'; Id = 'publish: # pushes the image' }
    ) {
        $header = if ($Id.Contains(':')) { "  $Id" } else { "  ${Id}:" }
        $violations = Get-EnvironmentSecretViolation (New-Workflow "$header`n$script:publishStep")
        $violations | Should -Contain "$(($Id -split ':')[0]) reads secrets.DOCKER_HUB_TOKEN outside the publishing environment"
    }

    It 'accepts the same job, with an underscore in its id, bound to the publishing environment' {
        Get-EnvironmentSecretViolation (New-Workflow "  publish_package:`n    environment: publishing`n$script:publishStep") |
            Should -BeNullOrEmpty
    }

    It 'flags a secret name written in lower case' {
        $step = $script:publishStep.Replace('secrets.DOCKER_HUB_TOKEN', 'secrets.docker_hub_token')
        Get-EnvironmentSecretViolation (New-Workflow "  publish:`n$step") |
            Should -Contain 'publish reads secrets.docker_hub_token outside the publishing environment'
    }

    It 'flags a secret read with index syntax' {
        $step = $script:publishStep.Replace('secrets.DOCKER_HUB_TOKEN', "secrets['DOCKER_HUB_TOKEN']")
        Get-EnvironmentSecretViolation (New-Workflow "  publish:`n$step") |
            Should -Contain "publish reads secrets['DOCKER_HUB_TOKEN'] outside the publishing environment"
    }

    It 'flags a reference it cannot place in a job, <Case>' -ForEach @(
        @{ Case = 'a quoted job id'; Jobs = "  'publish':`n" }
        @{ Case = 'jobs indented four spaces'; Jobs = "    publish:`n" }
    ) {
        $step = if ($Case -eq 'a quoted job id') { $script:publishStep } else { ($script:publishStep -split "`n" | ForEach-Object { "  $_" }) -join "`n" }
        Get-EnvironmentSecretViolation (New-Workflow "$Jobs$step") |
            Should -Contain '1 publishing token reference(s) outside any job this guard can read'
    }

    It 'flags the build-agent token read in the workflow-wide env, outside release-automation' {
        $content = "name: Fixture`non: push`n`nenv:`n  TOKEN: `${{ secrets.EDFI_BUILD_AGENT_PAT }}`n`njobs:`n  tag:`n    environment: release-automation`n    runs-on: ubuntu-latest`n    steps:`n      - run: echo tag`n"
        $violations = Get-EnvironmentSecretViolation $content -Secrets 'EDFI_BUILD_AGENT_PAT' -Environment 'release-automation'
        $violations | Should -Contain 'reads a release-automation token in the workflow-wide env'
        $violations | Should -Contain '1 release-automation token reference(s) outside any job this guard can read'
    }

    It 'flags the build-agent token read by a job bound to publishing instead of release-automation' {
        $step = $script:publishStep.Replace('secrets.DOCKER_HUB_TOKEN', 'secrets.EDFI_BUILD_AGENT_PAT')
        Get-EnvironmentSecretViolation (New-Workflow "  tag:`n    environment: publishing`n$step") -Secrets 'EDFI_BUILD_AGENT_PAT' -Environment 'release-automation' |
            Should -Contain 'tag reads secrets.EDFI_BUILD_AGENT_PAT outside the release-automation environment'
    }

    It 'flags secrets: inherit and the whole secrets context' {
        $violations = Get-EnvironmentSecretViolation (New-Workflow "  build:`n    uses: ./.github/workflows/build.yml`n    secrets: inherit`n  dump:`n    runs-on: ubuntu-latest`n    steps:`n      - run: echo `"`${{ toJSON(secrets) }}`"`n")
        $violations | Should -Contain 'passes secrets: inherit'
        $violations | Should -Contain 'reads the whole secrets context'
    }
}
