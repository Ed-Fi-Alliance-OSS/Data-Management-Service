# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot '../workflow-ref-guard.psm1') -Force
    $script:entryScript = Join-Path $PSScriptRoot '../Test-WorkflowRefInjection.ps1'
    $script:repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
    $script:root = Join-Path ([System.IO.Path]::GetTempPath()) "workflow-ref-guard-$([guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $script:root | Out-Null

    # A repository with one workflow, or one composite action, holding $Text.
    function script:New-Repository([string]$Text, [string]$RelativePath = '.github/workflows/test.yml') {
        $repo = Join-Path $script:root ([guid]::NewGuid().ToString('N'))
        $file = Join-Path $repo $RelativePath
        New-Item -ItemType Directory -Path (Split-Path $file) -Force | Out-Null
        Set-Content -LiteralPath $file -Value $Text -NoNewline
        return $repo
    }

    function script:Find-Injection([string]$Text, [string]$RelativePath = '.github/workflows/test.yml') {
        $repo = script:New-Repository $Text $RelativePath
        @(Find-WorkflowRefInjection -Path (Join-Path $repo $RelativePath) -Root $repo)
    }

    function script:Invoke-Guard([string]$Repo) {
        $printed = & pwsh -NoProfile -NonInteractive -File $script:entryScript -Root $Repo 2>&1 | ForEach-Object { "$_" }
        [pscustomobject]@{ ExitCode = $LASTEXITCODE; Printed = $printed }
    }
}

AfterAll {
    Remove-Item -LiteralPath $script:root -Recurse -Force -ErrorAction SilentlyContinue
}

Describe 'Find-WorkflowRefInjection' {
    It 'finds <case>' -ForEach @(
        @{ Case = 'a ref in a single-line run'; Line = 5; Text = @'
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - run: echo ${{ github.ref_name }}
'@ }
        @{ Case = 'a ref in a quoted single-line run'; Line = 5; Text = @'
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - run: "echo ${{ github.head_ref }}"
'@ }
        @{ Case = 'a ref in a plain run that continues on the next line, on the line the run starts'; Line = 5; Text = @'
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - run: echo
          ${{ github.ref }}
'@ }
        @{ Case = 'a tag name in a literal block'; Line = 8; Text = @'
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - name: Prepare
        shell: bash
        run: |
          TAG_NAME="${{ github.event.release.tag_name }}"
'@ }
        @{ Case = 'a ref in a folded, stripped block'; Line = 8; Text = @'
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - name: Prepare
        shell: pwsh
        run: >-
          Write-Output "${{ github.ref_name }}"
'@ }
        @{ Case = 'a ref in a block with an indentation indicator and a comment'; Line = 7; Text = @'
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - name: Prepare
        run: |2 # keep
          echo ${{ github.base_ref }}
'@ }
        @{ Case = 'an expression that spans lines'; Line = 8; Text = @'
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - name: Prepare
        run: |
          echo one
          echo "${{
            github.ref_name
          }}"
'@ }
        @{ Case = 'bracket notation'; Line = 6; Text = @'
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - run: |
          echo "${{ github['ref_name'] }}"
'@ }
        @{ Case = 'a pull request head ref inside a function call'; Line = 6; Text = @'
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - run: |
          echo "${{ format('{0}', github.event.pull_request.head.ref) }}"
'@ }
        @{ Case = 'a workflow_run head branch in upper case'; Line = 6; Text = @'
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - run: |
          echo "${{ GitHub.Event.Workflow_Run.Head_Branch }}"
'@ }
        @{ Case = 'a ref in a shell comment, which GitHub still substitutes'; Line = 6; Text = @'
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - run: |
          # building ${{ github.ref_name }}
'@ }
        @{ Case = 'a ref under a double-quoted run key'; Line = 6; Text = @'
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - "run": |
          echo "${{ github.ref_name }}"
'@ }
        @{ Case = 'a ref under a single-quoted run key on one line'; Line = 6; Text = @'
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - name: Prepare
        'run': echo ${{ github.event.release.tag_name }}
'@ }
        @{ Case = 'a ref under a run key on its own line of a multi-line flow mapping'; Line = 7; Text = @'
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - {
          name: Prepare,
          run: 'echo ${{ github.ref_name }}' }
'@ }
        @{ Case = 'a workflow env alias pasted back into a script'; Line = 10; Text = @'
env:
  REF: ${{ github.ref_name }}
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - name: Prepare
        shell: bash
        run: |
          REF="${{ env.REF }}"
'@ }
        @{ Case = 'a step env alias in bracket form'; Line = 8; Text = @'
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - env:
          TAG: ${{ github.event.release.tag_name }}
        run: |
          echo "${{ env['TAG'] }}"
'@ }
        @{ Case = 'a ref under a run key with space before the colon'; Line = 6; Text = @'
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - run : |
          echo "${{ github.ref_name }}"
'@ }
        @{ Case = 'a ref in a job called run'; Line = 5; Text = @'
jobs:
  run:
    runs-on: ubuntu-latest
    steps:
      - run: echo ${{ github.ref_name }}
'@ }
        @{ Case = 'bracket notation that YAML single-quote decoding produces'; Line = 5; Text = @'
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - run: 'echo "${{ github[''ref_name''] }}"'
'@ }
        @{ Case = 'bracket notation that a YAML double-quote escape produces'; Line = 5; Text = @'
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - run: "echo ${{ github[\x27ref_name\x27] }}"
'@ }
        @{ Case = 'a ref after braces in a string inside the expression'; Line = 6; Text = @'
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - run: |
          echo "${{ format('{{{0}}}', github.ref_name) }}"
'@ }
        @{ Case = 'a ref after a closing pair inside a string'; Line = 6; Text = @'
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - run: |
          echo "${{ format('}} {0}', github.ref_name) }}"
'@ }
        @{ Case = 'an env alias whose quoted value decodes to bracket notation'; Line = 8; Text = @'
env:
  REF: '${{ github[''ref_name''] }}'
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - run: |
          echo "${{ env.REF }}"
'@ }
        @{ Case = 'a ref in a flow-style step'; Line = 5; Text = @'
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - { name: Prepare, run: 'echo "${{ github.ref_name }}"' }
'@ }
        @{ Case = 'a ref in a flow-style step whose run key leads'; Line = 5; Text = @'
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - { "run": "echo ${{ github.head_ref }}" }
'@ }
        @{ Case = 'a ref in a run value that is an alias, where the anchor writes it'; Line = 2; Text = @'
x-script: &script |
  echo "${{ github.ref_name }}"
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - name: Prepare
        run: *script
      - name: Again
        run: *script
'@ }
        @{ Case = 'a ref in a run value with a tag'; Line = 5; Text = @'
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - run: !!str echo ${{ github.ref_name }}
'@ }
        @{ Case = 'a ref under an explicit run key'; Line = 6; Text = @'
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - ? run
        : echo ${{ github.ref_name }}
'@ }
    ) {
        $found = script:Find-Injection $Text

        $found.Count | Should -Be 1
        $found[0].Problem | Should -BeExactly 'Ref'
        $found[0].File | Should -BeExactly '.github/workflows/test.yml'
        $found[0].Line | Should -Be $Line
    }

    It 'allows <case>' -ForEach @(
        @{ Case = 'the env pattern, read as data in bash and pwsh'; Text = @'
env:
  REF: ${{ github.ref_name }}
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - name: Prepare
        env:
          TAG_NAME: ${{ github.event.release.tag_name }}
        run: |
          SEMVER="v${TAG_NAME#dms-pre-}"
          echo "$REF"
      - shell: pwsh
        run: Write-Output $env:REF
'@ }
        @{ Case = 'refs in if:, with:, concurrency and the job name'; Text = @'
concurrency:
  group: ${{ github.workflow }}-${{ github.ref }}
jobs:
  build:
    name: Build ${{ github.ref_name }}
    if: ${{ github.ref_type == 'tag' && startsWith(github.ref_name, 'v') }}
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
        with:
          ref: ${{ github.event.pull_request.head.ref }}
      - if: github.ref == 'refs/heads/main'
        run: echo main
'@ }
        @{ Case = 'expressions that are not ref names'; Text = @'
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - run: |
          echo "${{ github.ref_type }} ${{ github.repository }} ${{ inputs.ref }} ${{ steps.versions.outputs.dms-semver }} ${{ env.CONFIGURATION }}"
'@ }
        @{ Case = 'a defaults run mapping'; Text = @'
defaults:
  run:
    shell: pwsh
    working-directory: ${{ github.workspace }}
jobs: {}
'@ }
        @{ Case = 'a run key outside a step'; Text = @'
x-notes:
  run: echo ${{ github.ref_name }}
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
        with:
          run: ${{ github.ref_name }}
'@ }
        @{ Case = 'the env pattern in flow style'; Text = @'
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - { env: { REF: "${{ github.ref_name }}" }, run: 'echo "$REF"' }
      - env: { TOKEN: "${{ secrets.API_TOKEN }}" }
        run: echo "$TOKEN"
'@ }
    ) {
        script:Find-Injection $Text | Should -BeNullOrEmpty
    }

    It 'finds a secret in <case>' -ForEach @(
        @{ Case = 'a literal block'; Line = 8; Text = @'
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - name: Attach
        shell: pwsh
        run: |
          $token = "${{ secrets.GITHUB_TOKEN }}"
'@ }
        @{ Case = 'bracket notation in a single-line run'; Line = 5; Text = @'
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - run: curl -H "Authorization ${{ secrets['API_TOKEN'] }}" https://example.invalid
'@ }
        @{ Case = 'the whole context wrapped in a function across lines'; Line = 7; Text = @'
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - run: |
          echo one
          echo '${{
            toJSON(secrets)
          }}'
'@ }
        @{ Case = 'a workflow env alias'; Line = 9; Text = @'
env:
  ARTIFACTS_API_KEY: ${{ secrets.FEED_TOKEN }}
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - shell: pwsh
        run: |
          dotnet nuget push x.nupkg --api-key "${{ env.ARTIFACTS_API_KEY }}"
'@ }
        @{ Case = 'a job env alias'; Line = 9; Text = @'
jobs:
  build:
    runs-on: ubuntu-latest
    env:
      TOKEN: ${{ secrets.BUILD_AGENT_PAT }}
    steps:
      - shell: pwsh
        run: |
          $headers = @{ Authorization = "Bearer ${{ env.TOKEN }}" }
'@ }
        @{ Case = 'a step env alias in bracket form'; Line = 9; Text = @'
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - shell: pwsh
        env:
          ARTIFACTS_USERNAME: ${{ secrets.FEED_USER }}
        run: |
          Write-Output "${{ env['ARTIFACTS_USERNAME'] }}"
'@ }
        @{ Case = 'an alias whose value embeds a secret in other text'; Line = 8; Text = @'
env:
  FEED_ENDPOINTS: '{"endpointCredentials": [{"endpoint": "https://example.invalid","password": "${{ secrets.FEED_TOKEN }}"}]}'
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - run: |
          echo '${{ env.FEED_ENDPOINTS }}'
'@ }
        @{ Case = 'an alias whose secret is on a continuation line'; Line = 10; Text = @'
env:
  FEED_ENDPOINTS: >-
    {"endpointCredentials": [{"endpoint": "https://example.invalid",
    "password": "${{ secrets.FEED_TOKEN }}"}]}
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - run: |
          echo '${{ env.FEED_ENDPOINTS }}'
'@ }
        @{ Case = 'an alias of an alias'; Line = 11; Text = @'
env:
  FEED_TOKEN: ${{ secrets.FEED_TOKEN }}
jobs:
  build:
    runs-on: ubuntu-latest
    env:
      API_KEY: ${{ env.FEED_TOKEN }}
    steps:
      - shell: pwsh
        run: |
          Write-Output "${{ env.API_KEY }}"
'@ }
        @{ Case = 'an alias whose secret follows braces in a string'; Line = 7; Text = @'
env:
  TOKEN: ${{ format('{{{0}}}', secrets.API_TOKEN) }}
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - run: echo "${{ env.TOKEN }}"
'@ }
        @{ Case = 'an alias under an env key with space before the colon'; Line = 7; Text = @'
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - env :
          TOKEN: ${{ secrets.API_TOKEN }}
        run: echo "${{ env.TOKEN }}"
'@ }
        @{ Case = 'a flow env mapping'; Line = 6; Text = @'
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - env: { TOKEN: "${{ secrets.API_TOKEN }}" }
        run: echo "${{ env.TOKEN }}"
'@ }
        @{ Case = 'an env that is an alias'; Line = 9; Text = @'
x-credentials: &credentials
  TOKEN: ${{ secrets.API_TOKEN }}
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - shell: pwsh
        'env': *credentials
        run: Write-Output "${{ env.TOKEN }}"
'@ }
        @{ Case = 'an env entry whose value is an alias'; Line = 8; Text = @'
x-token: &token ${{ secrets.API_TOKEN }}
jobs:
  build:
    runs-on: ubuntu-latest
    env:
      TOKEN: *token
    steps:
      - run: echo "${{ env.TOKEN }}"
'@ }
        @{ Case = 'an alias in a job called with'; Line = 7; Text = @'
jobs:
  with:
    runs-on: ubuntu-latest
    env:
      TOKEN: ${{ secrets.API_TOKEN }}
    steps:
      - run: echo "${{ env.TOKEN }}"
'@ }
    ) {
        $found = script:Find-Injection $Text

        $found.Count | Should -Be 1
        $found[0].Problem | Should -BeExactly 'Secret'
        $found[0].Line | Should -Be $Line
    }

    It 'allows <case>' -ForEach @(
        @{ Case = 'secrets bound in env at every level and read as data'; Text = @'
env:
  ARTIFACTS_API_KEY: ${{ secrets.FEED_TOKEN }}
  FEED_ENDPOINTS: '{"endpointCredentials": [{"password": "${{ secrets.FEED_TOKEN }}"}]}'
jobs:
  build:
    runs-on: ubuntu-latest
    env:
      TOKEN: ${{ secrets.BUILD_AGENT_PAT }}
    steps:
      - shell: pwsh
        env:
          GITHUB_TOKEN: ${{ secrets.GITHUB_TOKEN }}
          ARTIFACTS_USERNAME: ${{ secrets.FEED_USER }}
        run: |
          $token = $env:GITHUB_TOKEN
          Publish -Username $env:ARTIFACTS_USERNAME -ApiKey $env:ARTIFACTS_API_KEY -Token "Bearer $env:TOKEN"
      - shell: bash
        env:
          API_KEY: ${{ secrets.FEED_TOKEN }}
        run: dotnet nuget push x.nupkg --api-key "$API_KEY"
'@ }
        @{ Case = 'secrets in with: and a reusable workflow secrets: mapping'; Text = @'
jobs:
  login:
    runs-on: ubuntu-latest
    steps:
      - uses: docker/login-action@v3
        with:
          password: ${{ secrets.DOCKER_HUB_TOKEN }}
  call:
    uses: ./.github/workflows/build.yml
    secrets:
      FEED_URL: ${{ secrets.FEED_TOKEN }}
'@ }
        @{ Case = 'env and other expressions that are not secrets'; Text = @'
env:
  ARTIFACTS_FEED_URL: "https://example.invalid/index.json"
  SECRETS_PACKAGE_NAME: "EdFi.Api.Secrets"
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - shell: pwsh
        run: |
          Push -Source "${{ env.ARTIFACTS_FEED_URL }}" -Package "${{ env.SECRETS_PACKAGE_NAME }}.${{ needs.pack-secrets.outputs.contract-version }}.nupkg" -Flag "${{ inputs.secrets }}"
'@ }
    ) {
        script:Find-Injection $Text | Should -BeNullOrEmpty
    }

    It 'refuses an env built by an expression from secrets, whatever it sets' {
        $found = script:Find-Injection @'
jobs:
  build:
    runs-on: ubuntu-latest
    env: ${{ fromJSON(format('{{"TOKEN":"{0}"}}', secrets.API_TOKEN)) }}
    steps:
      - run: echo "${{ env.TOKEN }}"
'@

        $found.Count | Should -Be 1
        $found[0].Problem | Should -BeExactly 'Unsupported'
        $found[0].Line | Should -Be 4
    }

    It 'refuses a merge key <case>, which YamlDotNet does not expand' -ForEach @(
        @{ Case = 'in env'; Line = 9; Text = @'
x-credentials: &credentials
  TOKEN: ${{ secrets.API_TOKEN }}
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - shell: pwsh
        env:
          <<: *credentials
        run: Write-Output "${{ env.TOKEN }}"
'@ }
        @{ Case = 'in a step'; Line = 7; Text = @'
x-step: &step
  run: echo ${{ github.ref_name }}
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - <<: *step
'@ }
        @{ Case = 'under jobs'; Line = 7; Text = @'
x-jobs: &common
  build:
    runs-on: ubuntu-latest
    steps:
      - run: echo ${{ github.ref_name }}
jobs:
  <<: *common
'@ }
        @{ Case = 'in a composite action step'; Line = 6; Text = @'
x-step: &step
  run: echo ${{ github.ref_name }}
runs:
  using: composite
  steps:
    - <<: *step
      shell: bash
'@ }
    ) {
        $found = script:Find-Injection $Text

        $found.Count | Should -Be 1
        $found[0].Problem | Should -BeExactly 'MergeKey'
        $found[0].Line | Should -Be $Line
    }

    It 'reads a quoted << key as an ordinary key' {
        script:Find-Injection @'
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - uses: example/deploy@v1
        with:
          '<<': not a merge
'@ | Should -BeNullOrEmpty
    }

    It 'still reads <case>' -ForEach @(
        @{ Case = 'an anchored env with a trailing comment'; Line = 8; Text = @'
jobs:
  build:
    runs-on: ubuntu-latest
    env: &credentials # publishing
      TOKEN: ${{ secrets.API_TOKEN }}
    steps:
      - run: |
          echo "${{ env.TOKEN }}"
'@ }
    ) {
        $found = script:Find-Injection $Text

        $found.Count | Should -Be 1
        $found[0].Problem | Should -BeExactly 'Secret'
        $found[0].Line | Should -Be $Line
    }

    It 'allows an action input named env, whatever its value' {
        script:Find-Injection @'
jobs:
  deploy:
    runs-on: ubuntu-latest
    steps:
      - uses: example/deploy@v1
        with:
          env: ${{ secrets.DEPLOY_ENV }}
      - uses: example/deploy@v1
        "with":
          env: { TOKEN: "${{ secrets.API_TOKEN }}" }
      - uses: example/deploy@v1
        with:
          env: production
'@ | Should -BeNullOrEmpty
    }

    It 'does not read env keys out of another key''s block scalar' {
        script:Find-Injection @'
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - uses: slackapi/slack-github-action@v2
        with:
          payload: |
            env: { "token": "${{ secrets.SLACK_TOKEN }}" }
      - run: |
          cat <<'YAML' > config.yml
          env: { TOKEN: placeholder }
          YAML
'@ | Should -BeNullOrEmpty
    }

    It 'allows the safe env pattern under quoted run keys' {
        script:Find-Injection @'
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - "env":
          TAG_NAME: ${{ github.event.release.tag_name }}
        "run": |
          echo "$TAG_NAME"
      - 'run': echo "$TAG_NAME"
'@ | Should -BeNullOrEmpty
    }

    It 'does not read run keys out of another key''s block scalar' {
        script:Find-Injection @'
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - uses: slackapi/slack-github-action@v2
        with:
          payload: |
            { "text": "done", "run": "${{ github.ref }}" }
'@ | Should -BeNullOrEmpty
    }

    It 'refuses a YamlDotNet package that does not have the pinned hash' {
        $guard = Get-Module workflow-ref-guard
        $pinned = & $guard { $script:YamlDotNetSha512 }
        try {
            & $guard { $script:YamlDotNet = $null; $script:YamlDotNetSha512 = '00' }

            { & $guard { Get-YamlDotNet } } | Should -Throw '*does not have the pinned SHA-512*'
        }
        finally {
            & $guard { param($Hash) $script:YamlDotNet = $null; $script:YamlDotNetSha512 = $Hash } $pinned
        }
    }

    It 'scans composite actions as well' {
        $found = script:Find-Injection @'
runs:
  using: composite
  steps:
    - shell: bash
      run: echo "${{ github.ref_name }}"
'@ '.github/actions/example/action.yml'

        $found.Count | Should -Be 1
        $found[0].Line | Should -Be 5
    }
}

Describe 'Test-WorkflowRefInjection' {
    It 'passes on this repository' {
        $result = script:Invoke-Guard $script:repoRoot

        $result.ExitCode | Should -Be 0 -Because ($result.Printed -join "`n")
        $result.Printed -join "`n" | Should -BeLike 'No ref, tag or secret expression in a run script across * file(s).'
    }

    It 'fails with an annotation for <case> in <file>' -ForEach @(
        @{ Case = 'the original ref interpolation'; File = 'on-release.yml'; Find = 'MAJOR_TAG="$RELEASE_REF"'; Replace = 'MAJOR_TAG="${{ github.ref_name }}"' }
        @{ Case = 'the env alias pasted back'; File = 'on-release.yml'; Find = 'MAJOR_TAG="$RELEASE_REF"'; Replace = 'MAJOR_TAG="${{ env.RELEASE_REF }}"' }
        @{ Case = 'the original GITHUB_TOKEN interpolation'; File = 'on-release.yml'; Find = '$token = $env:GITHUB_TOKEN'; Replace = '$token = "${{ secrets.GITHUB_TOKEN }}"' }
        @{ Case = 'the original promotion API key interpolation'; File = 'on-release.yml'; Find = '-ApiKey $env:ARTIFACTS_API_KEY'; Replace = '-ApiKey "${{ env.ARTIFACTS_API_KEY }}"' }
        @{ Case = 'the original feed API key interpolation'; File = 'on-prerelease.yml'; Find = 'NuGetApiKey = $env:ARTIFACTS_API_KEY'; Replace = 'NuGetApiKey = "${{ env.ARTIFACTS_API_KEY }}"' }
        @{ Case = 'the original template push key interpolation'; File = 'build-minimal-template.yml'; Find = '--api-key $env:ARTIFACTS_API_KEY'; Replace = '--api-key "${{ env.ARTIFACTS_API_KEY }}"' }
        @{ Case = 'the original release token interpolation'; File = 'on-dms-merge-or-tag.yml'; Find = '"Bearer $env:TOKEN"'; Replace = '"Bearer ${{ env.TOKEN }}"' }
    ) {
        # The regression this guard exists for, made in a copy of the real workflow.
        $workflow = Get-Content -LiteralPath (Join-Path $script:repoRoot ".github/workflows/$File") -Raw
        $workflow.Contains($Find) | Should -BeTrue
        $index = $workflow.IndexOf($Find)
        $line = ([regex]::Matches($workflow.Substring(0, $index), "`n")).Count + 1
        $repo = script:New-Repository ($workflow.Remove($index, $Find.Length).Insert($index, $Replace)) ".github/workflows/$File"
        $expression = [regex]::Match($Replace, '\$\{\{.*?\}\}').Value

        $result = script:Invoke-Guard $repo

        $result.ExitCode | Should -Be 1
        $result.Printed | Should -Contain "::error file=.github/workflows/$File,line=$line::$expression is written into a run script. Pass it through env: and read it as `"`$NAME`" (bash) or `$env:NAME (pwsh)."
    }

    It 'fails with an annotation for an env built by an expression' {
        $repo = script:New-Repository @'
jobs:
  build:
    runs-on: ubuntu-latest
    env: ${{ fromJSON(secrets.ENV_JSON) }}
    steps:
      - run: echo "$TOKEN"
'@

        $result = script:Invoke-Guard $repo

        $result.ExitCode | Should -Be 1
        $result.Printed | Should -Contain "::error file=.github/workflows/test.yml,line=4::'`${{ fromJSON(secrets.ENV_JSON) }}' builds the whole env from a ref or secret, so this guard cannot see which names it sets. Write env as a mapping of names to values."
    }

    It 'fails with an annotation for a merge key' {
        $repo = script:New-Repository @'
x-step: &step
  run: echo hi
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - <<: *step
'@

        $result = script:Invoke-Guard $repo

        $result.ExitCode | Should -Be 1
        $result.Printed | Should -Contain '::error file=.github/workflows/test.yml,line=7::A merge key (<<) can bring in a run or env this guard does not read. Write the mapping out in full.'
    }

    It 'fails with an annotation for a file that is not YAML' {
        $repo = script:New-Repository @'
jobs:
  build:
    steps: [
      run: echo hi
'@

        $result = script:Invoke-Guard $repo

        $result.ExitCode | Should -Be 1
        $errors = @($result.Printed | Where-Object { $_ -like '::error *' })
        $errors.Count | Should -Be 1
        $errors[0] | Should -BeLike '::error file=.github/workflows/test.yml,line=5::This file is not YAML GitHub can read: *'
    }

    It 'scans a new .yaml workflow' {
        $repo = script:New-Repository @'
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - run: echo ${{ github.head_ref }}
'@ '.github/workflows/new.yaml'

        (script:Invoke-Guard $repo).ExitCode | Should -Be 1
    }
}

Describe 'Workflow ref guard wiring' {
    BeforeAll {
        $script:analysis = (Get-Content -LiteralPath (Join-Path $script:repoRoot '.github/workflows/powershell-analysis.yml') -Raw) -replace "`r`n", "`n"
    }

    It 'runs the PowerShell workflow for workflow and action changes on <event>' -ForEach @(
        @{ Event = 'push' }
        @{ Event = 'pull_request' }
    ) {
        $trigger = [regex]::Match($script:analysis, "(?ms)^  ${Event}:\n.*?(?=^  \S|^\S)").Value

        $trigger | Should -Match '(?m)^      - "\.github/workflows/\*\*"$'
        $trigger | Should -Match '(?m)^      - "\.github/actions/\*\*"$'
        $trigger | Should -Match '(?m)^      - "\*\*/\*\.ps1"$'
        $trigger | Should -Match '(?m)^      - "\*\*/\*\.psm1"$'
    }

    It 'runs the guard and its tests read-only, failing the job on any test failure' {
        $job = [regex]::Match($script:analysis, '(?ms)^  workflow-ref-guard:\n.*?(?=^  \S|\z)').Value

        $job | Should -Match '(?m)^    permissions:\n      contents: read$'
        $job | Should -Not -Match 'secrets\.'
        $job | Should -Match 'persist-credentials: false'
        $job | Should -Match '(?m)^        run: \./eng/ci/Test-WorkflowRefInjection\.ps1$'
        $job | Should -Match '"eng/ci/tests/WorkflowRefGuard\.Tests\.ps1"'
        $job | Should -Match 'FailedContainersCount -gt 0'
    }

    It 'runs the release ref tests once, in the bootstrap job that the merge queue also runs' {
        $pullRequest = (Get-Content -LiteralPath (Join-Path $script:repoRoot '.github/workflows/on-dms-pullrequest.yml') -Raw) -replace "`r`n", "`n"
        $bootstrap = [regex]::Match($pullRequest, '(?ms)^  run-bootstrap-pester-tests:\n.*?(?=^  \S|\z)').Value

        $pullRequest | Should -Match '(?m)^  merge_group:$'
        $bootstrap | Should -Match '"eng/ci/tests/ReleaseRefHandling\.Tests\.ps1"'
        $script:analysis | Should -Not -Match 'ReleaseRefHandling'
    }
}
