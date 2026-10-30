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
        @{ Case = 'a ref in a plain run that continues on the next line'; Line = 6; Text = @'
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
          run: echo ${{ github.ref_name }} }
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
    ) {
        script:Find-Injection $Text | Should -BeNullOrEmpty
    }

    It 'refuses <case> as a run it cannot read, whatever the value' -ForEach @(
        @{ Case = 'a flow-style step with a ref'; Line = 5; Text = @'
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - { name: Prepare, run: 'echo "${{ github.ref_name }}"' }
'@ }
        @{ Case = 'a flow-style step whose run key leads'; Line = 5; Text = @'
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - { "run": "echo ${{ github.head_ref }}" }
'@ }
        @{ Case = 'a flow-style step with the safe env pattern'; Line = 5; Text = @'
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - { env: { REF: "${{ github.ref_name }}" }, run: 'echo "$REF"' }
'@ }
        @{ Case = 'a run value that is an alias'; Line = 8; Text = @'
x-script: &script |
  echo "${{ github.ref_name }}"
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - name: Prepare
        run: *script
'@ }
        @{ Case = 'a run value with a tag'; Line = 5; Text = @'
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - run: !!str echo hi
'@ }
    ) {
        $found = script:Find-Injection $Text

        $found.Count | Should -Be 1
        $found[0].Problem | Should -BeExactly 'Unsupported'
        $found[0].Line | Should -Be $Line
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
        $result.Printed -join "`n" | Should -BeLike 'No ref or tag expression in a run script across * file(s).'
    }

    It 'fails with an annotation for <case> in on-release.yml' -ForEach @(
        @{ Case = 'the original ref interpolation'; Find = 'MAJOR_TAG="$RELEASE_REF"'; Replace = 'MAJOR_TAG="${{ github.ref_name }}"' }
        @{ Case = 'the env alias pasted back'; Find = 'MAJOR_TAG="$RELEASE_REF"'; Replace = 'MAJOR_TAG="${{ env.RELEASE_REF }}"' }
    ) {
        # The regression this guard exists for, made in a copy of the real workflow.
        $workflow = Get-Content -LiteralPath (Join-Path $script:repoRoot '.github/workflows/on-release.yml') -Raw
        $workflow.Contains($Find) | Should -BeTrue
        $index = $workflow.IndexOf($Find)
        $line = ([regex]::Matches($workflow.Substring(0, $index), "`n")).Count + 1
        $repo = script:New-Repository ($workflow.Remove($index, $Find.Length).Insert($index, $Replace)) '.github/workflows/on-release.yml'

        $result = script:Invoke-Guard $repo

        $result.ExitCode | Should -Be 1
        $result.Printed | Should -Contain "::error file=.github/workflows/on-release.yml,line=$line::$($Replace.Substring($Replace.IndexOf('$'), $Replace.Length - $Replace.IndexOf('$') - 1)) is written into a run script. Pass it through env: and read it as `"`$NAME`" (bash) or `$env:NAME (pwsh)."
    }

    It 'fails with an annotation for a flow-style step' {
        $repo = script:New-Repository @'
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - { name: Prepare, run: 'echo "${{ github.ref_name }}"' }
'@

        $result = script:Invoke-Guard $repo

        $result.ExitCode | Should -Be 1
        $result.Printed | Should -Contain "::error file=.github/workflows/test.yml,line=5::'- { name: Prepare, run: 'echo `"`${{ github.ref_name }}`"' }' is a run this guard cannot read (a flow mapping, alias, anchor or tag). Write run as a block-style key with a plain, quoted or block scalar value."
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
        $job | Should -Match '"eng/ci/tests/ReleaseRefHandling\.Tests\.ps1"'
        $job | Should -Match 'FailedContainersCount -gt 0'
    }
}
