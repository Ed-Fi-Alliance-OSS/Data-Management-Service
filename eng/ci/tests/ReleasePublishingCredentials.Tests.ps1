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
    $script:workflowRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../../.github/workflows'))

    function script:Get-WorkflowJobTable([string] $Content) {
        # Each job under jobs:, keyed by id, up to the next job or a comment introducing one.
        $jobsStart = $Content.IndexOf("`njobs:`n")
        $jobsStart | Should -BeGreaterThan 0
        $body = $Content.Substring($jobsStart)
        $jobs = [ordered]@{}
        $headers = [regex]::Matches($body, '(?m)^  (?<id>[a-z0-9-]+):\r?$')
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

    function script:Get-JobCondition([string] $Job) {
        return [regex]::Match($Job, '(?m)^    if: \$\{\{ (?<expression>.+) \}\}\r?$').Groups['expression'].Value
    }

    function script:Test-RefCondition {
        # Evaluates a job condition built from event_name, ref_type, and ref by translating its
        # few operators into PowerShell with GitHub's case-insensitive comparison. Anything else
        # survives the translation and fails to parse.
        param([string] $Expression, [string] $EventName, [string] $Ref, [string] $RefType)

        $translated = $Expression.
            Replace('github.event_name', '$EventName').
            Replace('github.ref_type', '$RefType').
            Replace('github.ref', '$Ref').
            Replace('!=', '-ine').
            Replace('==', '-ieq').
            Replace('&&', '-and').
            Replace('||', '-or')
        $translated = [regex]::Replace($translated, "startsWith\(\`$Ref, '([^']*)'\)", '$Ref.StartsWith(''$1'', [StringComparison]::OrdinalIgnoreCase)')
        return [bool](& ([scriptblock]::Create("param(`$EventName, `$Ref, `$RefType) $translated")) $EventName $Ref $RefType)
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
}

Describe 'on-prerelease.yml publishing credentials' {
    BeforeAll {
        $script:content = Get-Content (Join-Path $script:workflowRoot 'on-prerelease.yml') -Raw
        $script:jobs = Get-WorkflowJobTable $script:content
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
        $content | Should -Not -Match 'secrets: inherit'
        if ($content -notmatch "`njobs:`n") { return }
        Get-WorkflowEnv $content | Should -Not -Match 'secrets\.(AZURE_ARTIFACTS_PERSONAL_ACCESS_TOKEN|AZURE_ARTIFACTS_USER_NAME|DOCKER_HUB_TOKEN)\b'

        $jobs = Get-WorkflowJobTable $content
        foreach ($id in $jobs.Keys) {
            $job = $jobs[$id]
            foreach ($match in [regex]::Matches($job, 'secrets\.(AZURE_ARTIFACTS_PERSONAL_ACCESS_TOKEN|AZURE_ARTIFACTS_USER_NAME|DOCKER_HUB_TOKEN)\b')) {
                $line = ($job.Substring(0, $match.Index) -split "`n")[-1]
                if ($job -match '(?m)^    uses: ') {
                    $callerSecrets = [regex]::Match($job, '(?ms)^    secrets:\r?\n(?:      .*\r?\n?)+')
                    ($callerSecrets.Success -and $match.Index -ge $callerSecrets.Index -and $match.Index -lt $callerSecrets.Index + $callerSecrets.Length) |
                        Should -BeTrue -Because "$id passes $($match.Value) only through its reusable-workflow secrets"
                    continue
                }
                $job | Should -Match '(?m)^    environment: publishing\r?$' -Because "$id reads $($match.Value)"
                $line | Should -Match '^ {10}\S' -Because "$id reads $($match.Value) only at step level, never in job env"
                $job.IndexOf("`n    steps:") | Should -BeLessThan $match.Index -Because "$id reads $($match.Value) inside a step"
            }
        }
    }
}
