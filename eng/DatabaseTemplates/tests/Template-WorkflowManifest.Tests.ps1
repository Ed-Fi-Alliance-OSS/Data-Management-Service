# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseDeclaredVarsMoreThanAssignments', '', Justification = 'Pester setup variables and AST-extracted production blocks share the test scope dynamically.')]
[CmdletBinding()]
param()

Describe 'Template workflow schema and verification evidence' {
    BeforeAll {
        $repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
        $workflowRoot = Join-Path $repoRoot '.github/workflows'
    }

    It 'uses exact provisioning inputs and retains verification independent of publication: <kind>' -ForEach @(
        @{ kind = 'minimal' }, @{ kind = 'populated' }
    ) {
        $workflow = Get-Content (Join-Path $workflowRoot "build-$kind-template.yml") -Raw
        $workflow | Should -Match 'provision-e2e-database.ps1[^\r\n]+-SchemaOutputDirectory "\$\{\{ github.workspace \}\}/template-evidence/schema"'
        $verification = [regex]::Match($workflow, '(?ms)^      - name: Verify template package restore\r?\n(?<body>.*?)(?=^      - name:)').Groups['body'].Value
        $verification | Should -Match 'schema/relational-model\.\$dialect\.manifest\.json'
        $verification | Should -Match ([regex]::Escape('inputs.database_engine'))
        $verification | Should -Match 'ExpectedModelManifestPath = \$modelManifest'
        $verification | Should -Match 'Assert-TemplateSchema.ps1[^\r\n]+-ExpectedModelManifestPath \$modelManifest[^\r\n]+-RenderSql'
        $verification | Should -Match 'Start-Transcript'
        $verification | Should -Match 'finally \{\s+Stop-Transcript'
        $verification | Should -Match 'producing_commit = \(git rev-parse HEAD\)'
        $verification | Should -Match 'package_sha256 = \(Get-FileHash'
        $verification | Should -Match 'manifest_sha256 = \(Get-FileHash \$modelManifest'
        $verification | Should -Not -Match 'publish_package'
        if ($kind -eq 'populated') {
            $verification | Should -Match 'inputs.verify_restore'
            $verification | Should -Match 'inputs.require_populated_data'
            $verification | Should -Match '\$verifyParams.RequirePopulatedData = \$true'
        }
        $upload = [regex]::Match($workflow, '(?ms)^      - name: Upload template schema and verification evidence\r?\n(?<body>.*?)(?=^      - name:)').Groups['body'].Value
        $upload | Should -Match 'if: always\(\)'
        $upload | Should -Match 'name: "\$\{\{ inputs.package_name \}\}-Verification"'
        $upload | Should -Match 'path: \$\{\{ github.workspace \}\}/template-evidence/'
        $upload | Should -Match 'if-no-files-found: error'
        $workflow | Should -Match '(?ms)name: Upload (Minimal|Populated) Template Package as Artifact\s+if: success\(\)'
    }

    It 'verifies both Data Standard legs with the correct provider: <kind>/<engine>' -ForEach @(
        @{ kind = 'Minimal'; engine = 'PostgreSQL'; dialect = 'postgresql' }
        @{ kind = 'Minimal'; engine = 'MsSql'; dialect = 'mssql' }
        @{ kind = 'Populated'; engine = 'PostgreSQL'; dialect = 'postgresql' }
        @{ kind = 'Populated'; engine = 'MsSql'; dialect = 'mssql' }
    ) {
        $caller = Get-Content (Join-Path $workflowRoot "EdFi.Api.$kind.Template.$engine.yml") -Raw
        $caller | Should -Match "database_engine: $dialect"
        $caller | Should -Match ([regex]::Escape('environment_file: ${{ matrix.environment_file }}'))
        foreach ($version in @('5.2.0', '6.1.0')) {
            $token = if ($engine -eq 'PostgreSQL') { 'PostgreSql' } else { 'MsSql' }
            $caller | Should -Match ([regex]::Escape("EdFi.Api.$kind.Template.$token.$version"))
        }
        $caller | Should -Match '\.env.template"'
        $caller | Should -Match '\.env.template.ds61"'
        if ($kind -eq 'Populated') {
            $caller | Should -Match '(?m)^      verify_restore: true$'
            $caller | Should -Match '(?m)^      require_populated_data: true$'
        }
    }
}

Describe 'Provisioning emits the exact ordered schema manifest' {
    BeforeAll {
        $provisionPath = Join-Path $PSScriptRoot '../../docker-compose/provision-e2e-database.ps1'
        $tokens = $null
        $errors = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseFile($provisionPath, [ref]$tokens, [ref]$errors)
        if ($errors.Count) { throw $errors[0] }
        # Exercise the actual emit block without resetting a database or downloading schemas.
        $emitBlock = $ast.Find({ param($node)
            $node -is [System.Management.Automation.Language.IfStatementAst] -and
                $node.Extent.Text.StartsWith('if (-not [string]::IsNullOrWhiteSpace($SchemaOutputDirectory))')
        }, $true)
        $emitScript = [scriptblock]::Create($emitBlock.Extent.Text)
        $provisionText = Get-Content $provisionPath -Raw
        function dotnet { }
    }

    BeforeEach {
        $SchemaOutputDirectory = Join-Path $TestDrive ([Guid]::NewGuid().ToString('N'))
        $schemaFiles = @(Join-Path $TestDrive 'core.json'; Join-Path $TestDrive 'tpdm.json')
        Set-Content $schemaFiles[0] 'core input'
        Set-Content $schemaFiles[1] 'extension input'
        $Configuration = 'Release'
        $schemaToolsProject = 'SchemaTools.csproj'
        $script:PrebuiltToolDotnetRunArgs = @('--no-build', '--no-restore')
        Mock dotnet {
            $script:capturedArguments = @($args)
            New-Item -ItemType Directory $SchemaOutputDirectory -Force | Out-Null
            $global:LASTEXITCODE = 0
        }
    }

    It 'passes core then extensions to emit and provision for <dialect>' -ForEach @(
        @{ dialect = 'pgsql' }, @{ dialect = 'mssql' }
    ) {
        $schemaToolsDialect = $dialect
        . $emitScript
        $script:capturedArguments | Should -Be @(
            'run', '--no-launch-profile', '--no-build', '--no-restore',
            '--configuration', 'Release', '--project', 'SchemaTools.csproj', '--',
            'ddl', 'emit', '--schema', $schemaFiles[0], $schemaFiles[1],
            '--dialect', $dialect, '--output', $SchemaOutputDirectory
        )
        $inputs = Get-Content (Join-Path $SchemaOutputDirectory 'schema-inputs.json') -Raw | ConvertFrom-Json
        $inputs.path | Should -Be $schemaFiles
        $inputs.sha256 | Should -Be @($schemaFiles | ForEach-Object { (Get-FileHash $_ -Algorithm SHA256).Hash.ToLowerInvariant() })
        $provisionText | Should -Match '(?s)"provision",\s+"--schema"\s+\) \+ \$schemaFiles \+ @\('
        $provisionText | Should -Match '(?s)"--dialect",\s+\$schemaToolsDialect,\s+"--create-database"'
        Should -Invoke dotnet -Exactly 1
    }

    It 'stops if manifest emission fails' {
        $schemaToolsDialect = 'pgsql'
        Mock dotnet { $global:LASTEXITCODE = 1 }
        { . $emitScript } | Should -Throw '*manifest emission failed*'
        Test-Path (Join-Path $SchemaOutputDirectory 'schema-inputs.json') | Should -BeFalse
    }

    It 'leaves ordinary E2E provisioning unchanged without an evidence directory' {
        $SchemaOutputDirectory = ''
        . $emitScript
        Should -Invoke dotnet -Exactly 0
    }
}

Describe 'Reusable workflow verification execution' {
    BeforeAll {
        $workflowRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../../.github/workflows'))
        function docker { }
        function git { }
    }

    It 'passes the exact manifest and persists correlated evidence: <kind>/<engine>/<standard>' -ForEach @(
        @{ kind = 'minimal'; engine = 'postgresql'; standard = '5.2.0' }
        @{ kind = 'minimal'; engine = 'postgresql'; standard = '6.1.0' }
        @{ kind = 'minimal'; engine = 'mssql'; standard = '5.2.0' }
        @{ kind = 'minimal'; engine = 'mssql'; standard = '6.1.0' }
        @{ kind = 'populated'; engine = 'postgresql'; standard = '5.2.0' }
        @{ kind = 'populated'; engine = 'postgresql'; standard = '6.1.0' }
        @{ kind = 'populated'; engine = 'mssql'; standard = '5.2.0' }
        @{ kind = 'populated'; engine = 'mssql'; standard = '6.1.0' }
    ) {
        $work = Join-Path $TestDrive "$kind-$engine-$standard"
        $schemaDir = Join-Path $work 'template-evidence/schema'
        New-Item -ItemType Directory $schemaDir -Force | Out-Null
        $dialect = if ($engine -eq 'mssql') { 'mssql' } else { 'pgsql' }
        $expectedManifest = Join-Path $schemaDir "relational-model.$dialect.manifest.json"
        Set-Content $expectedManifest '{"expected":"manifest"}'
        Set-Content (Join-Path $work 'template.nupkg') 'package bytes'
        @'
param($ExpectedModelManifestPath, $DatabaseEngine, [switch]$RenderSql)
if (-not $RenderSql) { throw 'Expected render-only preflight.' }
"-- Assertions from $ExpectedModelManifestPath for $DatabaseEngine"
'@ | Set-Content (Join-Path $work 'Assert-TemplateSchema.ps1')
        @'
param($SourceDatabaseName, $DatabaseEngine, $ExpectedModelManifestPath, [switch]$RequirePopulatedData)
@{
    manifest = $ExpectedModelManifestPath
    engine = $DatabaseEngine
    populated = [bool]$RequirePopulatedData
} | ConvertTo-Json | Set-Content './received.json'
if (Test-Path './fail-verification') { throw 'Injected catalog failure' }
Write-Host 'Mock verification completed.'
'@ | Set-Content (Join-Path $work 'verify-template-restore.ps1')
        Mock docker { $global:LASTEXITCODE = 0; 'engine:current sha256:image' }
        Mock git { 'producing-commit' }
        Mock Start-Transcript { }
        Mock Stop-Transcript { }

        $workflow = Get-Content (Join-Path $workflowRoot "build-$kind-template.yml") -Raw
        $run = [regex]::Match($workflow, '(?ms)^      - name: Verify template package restore\r?\n.*?        run: \|\r?\n(?<run>.*?)^        working-directory:').Groups['run'].Value
        $run = [regex]::Replace($run, '(?m)^          ', '')
        $replacements = @{
            '${{ github.workspace }}' = $work
            '${{ inputs.database_engine }}' = $engine
            '${{ inputs.standard_version }}' = $standard
            '${{ inputs.environment_file }}' = $(if ($standard -eq '5.2.0') { './.env.template' } else { './.env.template.ds61' })
            '${{ inputs.package_name }}' = "package.$kind.$engine.$standard"
            '${{ inputs.require_populated_data }}' = 'true'
            '${{ steps.versions.outputs.dms-semver }}' = 'dms-v8.1.0-test.1'
            '${{ github.server_url }}' = 'https://github.com'
            '${{ github.repository }}' = 'owner/repo'
            '${{ github.run_id }}' = '123'
            '${{ github.run_attempt }}' = '1'
        }
        foreach ($entry in $replacements.GetEnumerator()) { $run = $run.Replace($entry.Key, $entry.Value) }
        $run | Should -Not -Match '\$\{\{'
        Push-Location $work
        try {
            & ([scriptblock]::Create($run))
            $received = Get-Content './received.json' -Raw | ConvertFrom-Json
            $received.manifest | Should -Be $expectedManifest
            $received.engine | Should -Be $engine
            $received.populated | Should -Be ($kind -eq 'populated')
            $evidence = Get-Content './template-evidence/verification.json' -Raw | ConvertFrom-Json
            $evidence.verification | Should -Be 'passed'
            $evidence.producing_commit | Should -Be 'producing-commit'
            $evidence.package_sha256 | Should -Be (Get-FileHash './template.nupkg' -Algorithm SHA256).Hash.ToLowerInvariant()
            $evidence.manifest_sha256 | Should -Be (Get-FileHash $expectedManifest -Algorithm SHA256).Hash.ToLowerInvariant()
            $evidence.package_version | Should -Be '8.1.0-test.1'
            $evidence.standard_version | Should -Be $standard
            $evidence.engine_image | Should -Be 'engine:current sha256:image'
            $evidence.require_populated_data | Should -Be ($kind -eq 'populated')
            Get-Content './template-evidence/catalog-assertions.sql' -Raw | Should -Match ([regex]::Escape($expectedManifest))
            Should -Invoke Start-Transcript -Exactly 1 -ParameterFilter { $Path -eq "$work/template-evidence/verification.log" }
            Should -Invoke Stop-Transcript -Exactly 1

            # A later failed gate must never leave the earlier success marker in retained evidence.
            Set-Content './fail-verification' 'fail'
            { & ([scriptblock]::Create($run)) } | Should -Throw '*Injected catalog failure*'
            (Get-Content './template-evidence/verification.json' -Raw | ConvertFrom-Json).verification | Should -Be 'failed'
            Should -Invoke Stop-Transcript -Exactly 2
        }
        finally { Pop-Location }
    }
}
