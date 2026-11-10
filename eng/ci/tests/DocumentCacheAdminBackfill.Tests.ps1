# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

# Pins the one-time backfill of EdFi.Api.DocumentCacheAdmin: the dispatch-only workflow that builds the
# commit it was dispatched from, at the version of a stable release tag whose build inputs that commit
# shares, and publishes it, and the module that guards it. This file is deleted together with the
# workflow and the module once 8.1.0 has been backfilled.

BeforeAll {
    $script:repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
}

Describe 'DocumentCacheAdminBackfill module' {
    BeforeAll {
        # The two feed modules first, so the module under test shares the instances the mocks reach.
        # No -Force, for the reason package-helpers.psm1 gives: re-importing a loaded module replaces the
        # instance other modules already hold, and a later file's mocks would then miss their calls.
        Import-Module (Join-Path $script:repoRoot 'package-helpers.psm1') -ErrorAction Stop
        Import-Module (Join-Path $script:repoRoot 'eng/verification/ContractFeed.psm1') -ErrorAction Stop
        Import-Module (Join-Path $script:repoRoot 'eng/ci/DocumentCacheAdminBackfill.psm1') -ErrorAction Stop
        $script:feed = 'https://feed.invalid/_packaging/EdFi/nuget/v3/index.json'
        $script:packageName = 'EdFi.Api.DocumentCacheAdmin'
        $script:readme = 'src/dms/clis/EdFi.DataManagementService.DocumentCacheAdmin/README.md'

        function New-BackfillPackage {
            [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Writes only a disposable package in Pester TestDrive.')]
            param([string] $Version, [string] $Id = 'EdFi.Api.DocumentCacheAdmin')
            $directory = New-Item -ItemType Directory (Join-Path $TestDrive ([guid]::NewGuid()))
            Set-Content (Join-Path $directory "$script:packageName.nuspec") "<package><metadata><id>$Id</id><version>$Version</version></metadata></package>"
            $path = "$directory.nupkg"
            [IO.Compression.ZipFile]::CreateFromDirectory($directory, $path)
            return $path
        }

        function Invoke-TestPublication {
            Publish-DocumentCacheAdminBackfill -Version $script:version -PackageFile $script:packageFile `
                -Sha256 $script:hash -ServiceIndexUrl $script:feed -ApiKey 'test-only'
        }
    }

    Context 'Assert-DocumentCacheAdminBackfillTag' {
        BeforeEach {
            $script:lookups = [Collections.Generic.List[string]]::new()
            $script:release = [pscustomobject]@{ tag_name = 'v8.1.0'; draft = $false; prerelease = $false }
            $script:getRelease = {
                param($Tag)
                $script:lookups.Add($Tag)
                return $script:release
            }
        }

        It 'accepts v8.1.0 when its GitHub release is neither a draft nor a prerelease' {
            { Assert-DocumentCacheAdminBackfillTag -ReleaseTag 'v8.1.0' -GetRelease $script:getRelease } | Should -Not -Throw
            $script:lookups | Should -Be @('v8.1.0')
        }

        It 'rejects the malformed tag <Tag> without looking up a release' -ForEach @(
            @{ Tag = 'v8.1.0-beta2' }
            @{ Tag = '8.1.0' }
            @{ Tag = 'main' }
            @{ Tag = "v8.1.0`n" }
        ) {
            { Assert-DocumentCacheAdminBackfillTag -ReleaseTag $Tag -GetRelease $script:getRelease } | Should -Throw
            $script:lookups.Count | Should -Be 0
        }

        It 'rejects a tag with no GitHub release' {
            $script:release = $null
            { Assert-DocumentCacheAdminBackfillTag -ReleaseTag 'v8.1.0' -GetRelease $script:getRelease } | Should -Throw
        }

        It 'rejects a draft release' {
            $script:release = [pscustomobject]@{ tag_name = 'v8.1.0'; draft = $true; prerelease = $false }
            { Assert-DocumentCacheAdminBackfillTag -ReleaseTag 'v8.1.0' -GetRelease $script:getRelease } | Should -Throw
        }

        It 'rejects a prerelease release' {
            $script:release = [pscustomobject]@{ tag_name = 'v8.1.0'; draft = $false; prerelease = $true }
            { Assert-DocumentCacheAdminBackfillTag -ReleaseTag 'v8.1.0' -GetRelease $script:getRelease } | Should -Throw
        }
    }

    Context 'Get-DocumentCacheAdminBackfillVersion' {
        It 'returns the release tag without its v' {
            Get-DocumentCacheAdminBackfillVersion -ReleaseTag 'v8.1.0' | Should -BeExactly '8.1.0'
        }

        It 'refuses the tag <Tag>, which is not a stable vMAJOR.MINOR.PATCH tag' -ForEach @(
            @{ Tag = 'v8.1.0-beta2' }
            @{ Tag = '8.1.0' }
            @{ Tag = 'main' }
            @{ Tag = "v8.1.0`n" }
            @{ Tag = '' }
        ) {
            { Get-DocumentCacheAdminBackfillVersion -ReleaseTag $Tag } | Should -Throw
        }
    }

    Context 'Get-DocumentCacheAdminBackfillSourceInput' {
        It 'lists every build input and excludes only the tool README' {
            Get-DocumentCacheAdminBackfillSourceInput | Should -Be @(
                'src'
                'build-dms.ps1'
                'eng/build-helpers.psm1'
                'eng/docker-compose/effective-schema-hash.psm1'
                'package-helpers.psm1'
                'eng/verification/ContractPackageComparison.psm1'
                'eng/docker-compose/dms-schema-environment.psm1'
                'eng/docker-compose/database-safety.psm1'
                'eng/schema-package-utility.psm1'
                '.config'
                '.editorconfig'
                '.gitattributes'
                'global.json'
                'Directory.Build.rsp'
                ":(exclude)$script:readme"
            )
        }

        It 'names only paths that exist in this repository, so a misspelled input cannot leave the gate blind to it' {
            # global.json and Directory.Build.rsp are read only when present, and are absent today.
            foreach ($entry in Get-DocumentCacheAdminBackfillSourceInput | Where-Object { $_ -notin 'global.json', 'Directory.Build.rsp' }) {
                $path = $entry -replace '^:\(exclude\)', ''
                Test-Path -LiteralPath (Join-Path $script:repoRoot $path) | Should -BeTrue -Because "$path is a gate input"
            }
        }
    }

    Context 'Assert-DocumentCacheAdminBackfillSource' {
        BeforeAll {
            function Invoke-FixtureGit {
                # Signing is switched off because a developer's global config may require it.
                & git -c user.name=Test -c user.email=test@example.invalid -c commit.gpgsign=false -c tag.gpgsign=false @args | Out-Null
                if ($LASTEXITCODE -ne 0) { throw "git $args failed ($LASTEXITCODE)." }
            }

            function Set-FixtureFile {
                [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Writes only a disposable repository in Pester TestDrive.')]
                param([string] $Path, [string] $Content)
                $full = Join-Path (Get-Location) $Path
                New-Item -ItemType Directory -Force (Split-Path $full -Parent) | Out-Null
                Set-Content -LiteralPath $full -Value $Content -NoNewline
            }

            function Save-FixtureChange {
                param([string[]] $Path)
                foreach ($file in $Path) { Set-FixtureFile $file "changed after the tag: $file" }
                Invoke-FixtureGit add -A
                Invoke-FixtureGit commit -q -m 'after the tag'
            }
        }

        BeforeEach {
            # A real repository: the tag commit holds source under src/, the build script, a script
            # outside the inputs and the tool README, as the release tag does.
            $script:fixture = New-Item -ItemType Directory (Join-Path $TestDrive ([guid]::NewGuid()))
            Push-Location -LiteralPath $script:fixture
            Invoke-FixtureGit init -q
            Set-FixtureFile 'src/dms/core/Lib.cs' 'class Lib { }'
            Set-FixtureFile 'build-dms.ps1' 'param()'
            Set-FixtureFile 'eng/ci/x.ps1' 'param()'
            Set-FixtureFile $script:readme '# DocumentCacheAdmin'
            Invoke-FixtureGit add -A
            Invoke-FixtureGit commit -q -m 'release'
            Invoke-FixtureGit tag v8.1.0
        }

        AfterEach {
            Pop-Location
        }

        It 'passes when the dispatch commit is the tag commit' {
            { Assert-DocumentCacheAdminBackfillSource -ReleaseTag 'v8.1.0' } | Should -Not -Throw
        }

        It 'passes when only the tool README changed after the tag' {
            Save-FixtureChange $script:readme
            { Assert-DocumentCacheAdminBackfillSource -ReleaseTag 'v8.1.0' } | Should -Not -Throw
        }

        It 'passes when only a file outside the build inputs changed after the tag' {
            Save-FixtureChange 'eng/ci/x.ps1'
            { Assert-DocumentCacheAdminBackfillSource -ReleaseTag 'v8.1.0' } | Should -Not -Throw
        }

        It 'passes for an annotated tag, peeled to its commit' {
            Invoke-FixtureGit tag -a v8.1.1 -m 'annotated'
            Save-FixtureChange 'eng/ci/x.ps1'
            { Assert-DocumentCacheAdminBackfillSource -ReleaseTag 'v8.1.1' } | Should -Not -Throw
        }

        It 'refuses a change under src and names the tag and the path' {
            Save-FixtureChange 'src/dms/core/Lib.cs'
            { Assert-DocumentCacheAdminBackfillSource -ReleaseTag 'v8.1.0' } | Should -Throw "*v8.1.0*src/dms/core/Lib.cs*"
        }

        It 'refuses a file added under src after the tag' {
            Save-FixtureChange 'src/dms/core/Added.cs'
            { Assert-DocumentCacheAdminBackfillSource -ReleaseTag 'v8.1.0' } | Should -Throw '*src/dms/core/Added.cs*'
        }

        It 'refuses a change to build-dms.ps1 and names it' {
            Save-FixtureChange 'build-dms.ps1'
            { Assert-DocumentCacheAdminBackfillSource -ReleaseTag 'v8.1.0' } | Should -Throw '*build-dms.ps1*'
        }

        It 'lists only the differing inputs, never the README or a file outside the inputs' {
            Save-FixtureChange 'src/dms/core/Lib.cs', $script:readme, 'eng/ci/x.ps1'
            $message = try { Assert-DocumentCacheAdminBackfillSource -ReleaseTag 'v8.1.0'; '' } catch { $_.Exception.Message }
            $message | Should -BeLike '*src/dms/core/Lib.cs*'
            $message | Should -Not -BeLike '*README.md*'
            $message | Should -Not -BeLike '*eng/ci/x.ps1*'
        }

        It 'compares from the repository root when run in a subdirectory' {
            Save-FixtureChange 'src/dms/core/Lib.cs'
            Push-Location -LiteralPath (Join-Path $script:fixture 'eng/ci')
            try {
                { Assert-DocumentCacheAdminBackfillSource -ReleaseTag 'v8.1.0' } | Should -Throw '*src/dms/core/Lib.cs*'
            }
            finally {
                Pop-Location
            }
        }

        It 'refuses a release tag that does not exist' {
            { Assert-DocumentCacheAdminBackfillSource -ReleaseTag 'v8.1.2' } | Should -Throw '*v8.1.2*'
        }

        It 'refuses an existing tag that is not a stable vMAJOR.MINOR.PATCH tag, though its inputs are equal' {
            Invoke-FixtureGit tag v8.2.0-beta.1
            { Assert-DocumentCacheAdminBackfillSource -ReleaseTag 'v8.2.0-beta.1' } | Should -Throw
        }

        It 'throws when git diff exits with neither 0 nor 1' {
            Mock -ModuleName DocumentCacheAdminBackfill git {
                if ($args -contains 'diff') {
                    $global:LASTEXITCODE = 128
                    return
                }
                $global:LASTEXITCODE = 0
                return $(if ($args -contains '--show-toplevel') { $script:fixture.FullName } else { '0123456789abcdef0123456789abcdef01234567' })
            }
            { Assert-DocumentCacheAdminBackfillSource -ReleaseTag 'v8.1.0' } | Should -Throw '*128*'
        }
    }

    Context 'Publish-DocumentCacheAdminBackfill' {
        BeforeEach {
            $script:version = '8.1.0'
            $script:packageFile = New-BackfillPackage $script:version
            $script:hash = (Get-FileHash $script:packageFile).Hash
            $script:published = [Collections.Generic.List[string]]::new()
            $script:pushExit = 0
            $script:pushes = [Collections.Generic.List[object]]::new()
            $script:feedBytes = [IO.File]::ReadAllBytes($script:packageFile)
            $script:downloadFails = $false
            # Mock scopes: the push runs inside DocumentCacheAdminBackfill, so dotnet is mocked there.
            # Test-PackageInView and its feed lookups run inside package-helpers, so Invoke-RestMethod
            # is mocked there, exactly as the SecurityMetadataTools publication tests do. The download
            # of a version already on the feed runs through the ContractFeed seams, so its service
            # index read and its package download are mocked in ContractFeed.
            Mock -ModuleName DocumentCacheAdminBackfill dotnet {
                $script:pushes.Add(@($args))
                $global:LASTEXITCODE = $script:pushExit
            }
            Mock -ModuleName package-helpers Invoke-RestMethod {
                if ($Uri -like '*_packaging/*') {
                    return @{ resources = @(@{ '@id' = 'https://feed.invalid/Local/flat2/'; '@type' = 'PackageBaseAddress/3.0.0' }) }
                }
                if ($Uri -like '*/Local/flat2/*') { return @{ versions = @($script:published) } }
                throw "Unexpected feed request: $Uri"
            }
            Mock -ModuleName ContractFeed Invoke-RestMethod {
                if ($Uri -like '*_packaging/*') {
                    return @{ resources = @(@{ '@id' = 'https://feed.invalid/Local/flat2/'; '@type' = 'PackageBaseAddress/3.0.0' }) }
                }
                throw "Unexpected feed request: $Uri"
            }
            Mock -ModuleName ContractFeed Invoke-WebRequest {
                if ($script:downloadFails) { throw 'Response status code does not indicate success: 500 (Internal Server Error).' }
                [IO.File]::WriteAllBytes($OutFile, $script:feedBytes)
            }
        }

        It 'pushes exactly once through dotnet nuget push when the version is absent from the Local view' {
            Invoke-TestPublication
            $script:pushes.Count | Should -Be 1
            $script:pushes[0] | Should -Be @('nuget', 'push', $script:packageFile, '--api-key', 'test-only', '--source', $script:feed)
            Should -Invoke -ModuleName ContractFeed Invoke-WebRequest -Times 0 -Exactly
        }

        It 'refuses altered bytes before any feed call' {
            $script:hash = ('0' * 64)
            { Invoke-TestPublication } | Should -Throw
            Should -Invoke -ModuleName package-helpers Invoke-RestMethod -Times 0 -Exactly
            $script:pushes.Count | Should -Be 0
        }

        It 'refuses a package whose nuspec id is not EdFi.Api.DocumentCacheAdmin' {
            $script:packageFile = New-BackfillPackage $script:version -Id 'EdFi.Api.Other'
            $script:hash = (Get-FileHash $script:packageFile).Hash
            { Invoke-TestPublication } | Should -Throw
            $script:pushes.Count | Should -Be 0
        }

        It 'refuses a package whose nuspec version differs from the requested version' {
            $script:packageFile = New-BackfillPackage '8.1.1'
            $script:hash = (Get-FileHash $script:packageFile).Hash
            { Invoke-TestPublication } | Should -Throw
            $script:pushes.Count | Should -Be 0
        }

        It 'continues without pushing when the Local view already holds these exact bytes' {
            $script:published.Add('8.1.0')
            { Invoke-TestPublication } | Should -Not -Throw
            $script:pushes.Count | Should -Be 0
            # The mock answers every view alike, so pin the view the lookup was scoped to.
            Should -Invoke -ModuleName package-helpers Invoke-RestMethod -Times 1 -ParameterFilter { $Uri -like '*/_packaging/*@Local/*' }
            # The copy is read anonymously from the Local view's flat container, at this exact version.
            Should -Invoke -ModuleName ContractFeed Invoke-RestMethod -Times 1 -Exactly -ParameterFilter {
                $Uri -ceq 'https://feed.invalid/_packaging/EdFi@Local/nuget/v3/index.json' -and -not $Headers.ContainsKey('Authorization')
            }
            Should -Invoke -ModuleName ContractFeed Invoke-WebRequest -Times 1 -Exactly -ParameterFilter {
                $Uri -ceq 'https://feed.invalid/Local/flat2/edfi.api.documentcacheadmin/8.1.0/edfi.api.documentcacheadmin.8.1.0.nupkg' -and
                -not $Headers.ContainsKey('Authorization')
            }
        }

        It 'refuses, without pushing, a version already in the Local view with different bytes' {
            $script:published.Add('8.1.0')
            $script:feedBytes = [Text.Encoding]::UTF8.GetBytes('another build of 8.1.0')
            { Invoke-TestPublication } | Should -Throw '*already published with different bytes*'
            $script:pushes.Count | Should -Be 0
        }

        It 'refuses, without pushing, when the copy already on the feed cannot be downloaded' {
            $script:published.Add('8.1.0')
            $script:downloadFails = $true
            { Invoke-TestPublication } | Should -Throw '*could not be downloaded*'
            $script:pushes.Count | Should -Be 0
        }

        It 'throws when the push exits nonzero' {
            $script:pushExit = 1
            { Invoke-TestPublication } | Should -Throw
            $script:pushes.Count | Should -Be 1
        }
    }

    It 'exports only the backfill functions, and leaves release assets to eng/ci/release-assets.psm1' {
        @((Get-Module DocumentCacheAdminBackfill).ExportedFunctions.Keys | Sort-Object) | Should -Be @(
            'Assert-DocumentCacheAdminBackfillSource'
            'Assert-DocumentCacheAdminBackfillTag'
            'Get-DocumentCacheAdminBackfillSourceInput'
            'Get-DocumentCacheAdminBackfillVersion'
            'Publish-DocumentCacheAdminBackfill'
        )
    }
}

Describe 'DocumentCacheAdmin backfill workflow wiring' {
    BeforeAll {
        $script:workflow = Get-Content (Join-Path $script:repoRoot '.github/workflows/backfill-document-cache-admin.yml') -Raw

        function script:Get-Job([string] $Id) {
            return [regex]::Match($script:workflow, "(?ms)^  $([regex]::Escape($Id)):.*?(?=^  [A-Za-z_][\w-]*:[ \t]*\r?`$|\z)").Value
        }

        function script:Get-JobNeed([string] $Job) {
            $needs = [regex]::Match($Job, '(?m)^    needs: \[(?<ids>[^\]]*)\]\s*$').Groups['ids'].Value
            return @($needs -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ } | Sort-Object)
        }

        function script:Get-JobStep([string] $Job) {
            # Each step of a job, from its '      - ' line up to the next one.
            $steps = [regex]::Match($Job, '(?m)^    steps:\r?$')
            if (-not $steps.Success) { return @() }
            return @([regex]::Split($Job.Substring($steps.Index + $steps.Length), '(?m)^(?=      - )') | Where-Object { $_ -match '\A      - ' })
        }

        function script:Find-Step([string] $Job, [string] $Text) {
            return @(Get-JobStep $Job | Where-Object { $_.Contains($Text) })
        }

        function script:Get-StepRun([string] $Step) {
            # The block scalar under the step's run: key, without its indentation.
            $run = [Collections.Generic.List[string]]::new()
            $inRun = $false
            foreach ($line in $Step -split '\r?\n') {
                if ($line -cmatch '^        run: \|[ \t]*$') { $inRun = $true; continue }
                if (-not $inRun) { continue }
                if ($line -match '^[ \t]*$') { $run.Add(''); continue }
                if (-not $line.StartsWith('          ')) { break }
                $run.Add($line.Substring(10))
            }
            return ($run -join "`n")
        }

        function script:Get-ScriptAst([string] $Script) {
            # Read with the PowerShell parser, so line breaks and backtick continuations do not matter.
            $errors = $null
            $ast = [Management.Automation.Language.Parser]::ParseInput($Script, [ref] $null, [ref] $errors)
            @($errors).Count | Should -Be 0 -Because 'the run script parses as PowerShell'
            return $ast
        }

        function script:Get-ScriptCommand([Management.Automation.Language.Ast] $Ast, [string] $Name) {
            return @($Ast.FindAll({
                        param($node)
                        $node -is [Management.Automation.Language.CommandAst] -and $node.GetCommandName() -ceq $Name
                    }, $true))
        }

        function script:Get-CommandArgument([Management.Automation.Language.CommandAst] $Command) {
            # Each named parameter of a call to the element that is its argument.
            $arguments = @{}
            $elements = $Command.CommandElements
            for ($i = 1; $i -lt $elements.Count; $i++) {
                $parameter = $elements[$i]
                if ($parameter -isnot [Management.Automation.Language.CommandParameterAst]) { continue }
                $value = $parameter.Argument
                if ($null -eq $value -and $i + 1 -lt $elements.Count -and $elements[$i + 1] -isnot [Management.Automation.Language.CommandParameterAst]) {
                    $i++
                    $value = $elements[$i]
                }
                $arguments[$parameter.ParameterName] = $value
            }
            return $arguments
        }

        function script:ConvertTo-AssetText([Management.Automation.Language.Ast] $List) {
            # Each hashtable in the -Asset argument as 'Name|Path|Comparison', with each value as written,
            # less the quotes of a constant.
            foreach ($table in $List.FindAll({ param($node) $node -is [Management.Automation.Language.HashtableAst] }, $true)) {
                $values = @{}
                foreach ($pair in $table.KeyValuePairs) {
                    $expression = if ($pair.Item2 -is [Management.Automation.Language.PipelineAst]) { $pair.Item2.GetPureExpression() }
                    $values[$pair.Item1.Extent.Text] = if ($expression -is [Management.Automation.Language.StringConstantExpressionAst]) { $expression.Value } else { $pair.Item2.Extent.Text }
                }
                @($values.Keys | Sort-Object) -join ',' | Should -BeExactly 'Comparison,Name,Path' -Because 'each asset names exactly its Name, Path and Comparison'
                "$($values['Name'])|$($values['Path'])|$($values['Comparison'])"
            }
        }

        function script:Get-VerifyRun {
            $install = @(Find-Step (Get-Job 'verify') 'dotnet tool install')
            $install.Count | Should -Be 1 -Because 'one step installs the promoted tool'
            return Get-StepRun $install[0]
        }

        $script:trustedGate = "if: `${{ inputs.publish_package && github.ref_type == 'branch' && github.ref == 'refs/heads/main' }}"
        $script:feedUrl = 'https://pkgs.dev.azure.com/ed-fi-alliance/Ed-Fi-Alliance-OSS/_packaging/EdFi/nuget/v3/index.json'
        $script:releaseViewUrl = 'https://pkgs.dev.azure.com/ed-fi-alliance/Ed-Fi-Alliance-OSS/_packaging/EdFi%40Release/nuget/v3/index.json'
    }

    It 'is dispatch-only with exactly the release_tag and publish_package inputs' {
        $events = [regex]::Match($script:workflow, '(?ms)^on:\r?\n(.*?)^\S').Groups[1].Value
        $events | Should -Match '(?m)^  workflow_dispatch:'
        $events | Should -Not -Match 'release:|push:|workflow_call:|version:'
        $inputs = [regex]::Match($events, '(?ms)^    inputs:\r?\n(.*)').Groups[1].Value
        @([regex]::Matches($inputs, '(?m)^      (?<name>[A-Za-z_]\w*):') | ForEach-Object { $_.Groups['name'].Value }) |
            Should -Be @('release_tag', 'publish_package')
        $tag = [regex]::Match($inputs, '(?m)^      release_tag:\r?\n(?<body>(?:        [^\r\n]*\r?\n)+)').Groups['body'].Value
        $tag | Should -Match '(?m)^        type: string\r?$'
        $tag | Should -Match '(?m)^        required: true\r?$'
        $publish = [regex]::Match($inputs, '(?m)^      publish_package:\r?\n(?<body>(?:        [^\r\n]*\r?\n?)+)').Groups['body'].Value
        $publish | Should -Match '(?m)^        type: boolean\r?$'
        $publish | Should -Match '(?m)^        default: false\r?$'
    }

    It 'checks out only the dispatch commit, with every tag, and builds nowhere but the repository root' {
        $build = Get-Job 'build'
        $build | Should -Not -BeNullOrEmpty
        $build | Should -Not -Match 'publish_package'
        $checkouts = @(Find-Step $build 'uses: actions/checkout@')
        $checkouts.Count | Should -Be 1
        $checkouts[0] | Should -Match '(?m)^          ref: \$\{\{ github\.sha \}\}\r?$'
        $checkouts[0] | Should -Match '(?m)^          fetch-depth: 0\r?$'
        $checkouts[0] | Should -Not -Match '(?m)^          path:'
        $script:workflow | Should -Not -Match 'working-directory:'
        $script:workflow | Should -Not -Match 'refs/tags/\$\{\{|\bsource/|cd source'
    }

    It 'validates the tag, then requires the tag''s build inputs, before any build step' {
        $build = Get-Job 'build'
        $order = @(
            'uses: actions/checkout@'
            'Assert-DocumentCacheAdminBackfillTag -ReleaseTag $env:RELEASE_TAG'
            'Get-DocumentCacheAdminBackfillSourceInput'
            'Assert-DocumentCacheAdminBackfillSource -ReleaseTag $env:RELEASE_TAG'
            'Get-DocumentCacheAdminBackfillVersion -ReleaseTag $env:RELEASE_TAG'
            './build-dms.ps1 -Command BuildAndPublish'
            './build-dms.ps1 -Command Package'
            './eng/ci/Invoke-DocumentCacheAdminPackageSmoke.ps1'
            'actions/upload-artifact'
        ) | ForEach-Object { $build.IndexOf($_, [StringComparison]::Ordinal) }
        $order | ForEach-Object { $_ | Should -BeGreaterThan -1 }
        for ($i = 1; $i -lt $order.Count; $i++) {
            $order[$i] | Should -BeGreaterThan $order[$i - 1] -Because "step $i must follow step $($i - 1)"
        }

        foreach ($call in 'Assert-DocumentCacheAdminBackfillTag', 'Assert-DocumentCacheAdminBackfillSource', 'Get-DocumentCacheAdminBackfillVersion') {
            $step = @(Find-Step $build $call)
            $step.Count | Should -Be 1 -Because "one step calls $call"
            $step[0] | Should -Match '(?m)^          RELEASE_TAG: \$\{\{ inputs\.release_tag \}\}\r?$' -Because "$call reads the tag through env"
        }
    }

    It 'stamps the version from the release tag, never from MinVer' {
        $build = Get-Job 'build'
        $build | Should -Not -Match 'Get-VersionNumber|minver|steps\.versions\.'
        $version = @(Find-Step $build 'Get-DocumentCacheAdminBackfillVersion')
        $version[0] | Should -Match '(?m)^        id: version\r?$'
        $version[0] | Should -Match '"version=\$version" >> \$env:GITHUB_OUTPUT'
        foreach ($call in './build-dms.ps1 -Command BuildAndPublish', './build-dms.ps1 -Command Package', 'Invoke-DocumentCacheAdminPackageSmoke.ps1', 'id: package') {
            $step = @(Find-Step $build $call)
            $step.Count | Should -Be 1 -Because "one step runs $call"
            $step[0] | Should -Match '(?m)^          APP_VERSION: \$\{\{ steps\.version\.outputs\.version \}\}\r?$' -Because "$call uses the tag's version"
        }
    }

    It 'builds, packs and smoke-tests with the release flags, and uploads the package from the repository root' {
        $build = Get-Job 'build'
        $buildStep = @(Find-Step $build './build-dms.ps1 -Command BuildAndPublish')[0]
        foreach ($flag in '-Configuration Release', '-DMSVersion $env:APP_VERSION', '-LockedMode') {
            $buildStep.Contains($flag) | Should -BeTrue -Because "BuildAndPublish passes $flag"
        }
        $packageStep = @(Find-Step $build './build-dms.ps1 -Command Package')[0]
        foreach ($flag in '-DMSVersion $env:APP_VERSION', '-Configuration Release', '-PackageTarget DocumentCacheAdmin', '-LockedMode') {
            $packageStep.Contains($flag) | Should -BeTrue -Because "Package passes $flag"
        }
        $smokeStep = @(Find-Step $build 'Invoke-DocumentCacheAdminPackageSmoke.ps1')[0]
        foreach ($flag in '-DMSVersion $env:APP_VERSION', '-Configuration Release', '-SkipPackageBuild') {
            $smokeStep.Contains($flag) | Should -BeTrue -Because "the smoke passes $flag"
        }
        $upload = @(Find-Step $build 'actions/upload-artifact')
        $upload.Count | Should -Be 1
        $upload[0] | Should -Match '(?m)^          name: EdFi\.Api\.DocumentCacheAdmin-NuGet\r?$'
        $upload[0] | Should -Match '(?m)^          path: EdFi\.Api\.DocumentCacheAdmin\.\$\{\{ steps\.package\.outputs\.version \}\}\.nupkg\r?$'
        foreach ($output in 'version', 'sha256', 'hash-code') {
            $build | Should -Match "(?m)^      ${output}: \`$\{\{ steps\.package\.outputs\.${output} \}\}\r?`$" -Because "build exposes $output"
        }
    }

    It 'scans the dispatch commit for the SBOM, reads contents only, and stops when a tool fails' {
        $sbom = Get-Job 'sbom-create-document-cache-admin'
        $sbom | Should -Not -BeNullOrEmpty
        Get-JobNeed $sbom | Should -Be @('build')
        $sbom | Should -Not -Match 'publish_package'
        $sbom | Should -Match '(?m)^      contents: read\r?$'
        $sbom | Should -Not -Match '(?m)^      contents: write'

        $steps = @(Get-JobStep $sbom)
        $steps[0] | Should -Match 'uses: actions/checkout@'
        $steps[0] | Should -Match '(?m)^          ref: \$\{\{ github\.sha \}\}\r?$'

        $generate = @($steps | Where-Object { $_.Contains('sbom-tool generate') })
        $generate.Count | Should -Be 1
        $text = $generate[0]
        $text | Should -Match '-bc "\./"'
        $install = $text.IndexOf('dotnet tool install', [StringComparison]::Ordinal)
        $tool = $text.IndexOf('sbom-tool generate', [StringComparison]::Ordinal)
        $copy = $text.IndexOf('Copy-Item', [StringComparison]::Ordinal)
        $install | Should -BeGreaterThan -1
        $tool | Should -BeGreaterThan $install
        $copy | Should -BeGreaterThan $tool
        $checks = @([regex]::Matches($text, 'if \(\$LASTEXITCODE -ne 0\)') | ForEach-Object Index)
        @($checks | Where-Object { $_ -gt $install -and $_ -lt $tool }).Count | Should -Be 1 -Because 'the tool install is checked'
        @($checks | Where-Object { $_ -gt $tool -and $_ -lt $copy }).Count | Should -Be 1 -Because 'sbom-tool generate is checked'
    }

    It 'always attests the exact artifact, with no publish switch' {
        $provenance = Get-Job 'provenance-create-document-cache-admin'
        $provenance | Should -Not -BeNullOrEmpty
        $provenance | Should -Match ([regex]::Escape('uses: Ed-Fi-Alliance-OSS/slsa-github-generator/.github/workflows/generator_generic_slsa3.yml@v2.0.0'))
        $provenance | Should -Match ([regex]::Escape('base64-subjects: ${{ needs.build.outputs.hash-code }}'))
        $provenance | Should -Match 'provenance-name: edfiApiDocumentCacheAdmin\.intoto\.jsonl'
        $provenance | Should -Not -Match 'publish_package'
    }

    It 'pushes and promotes at one feed, and binds the credential provider to it for the push' {
        $workflowEnv = [regex]::Match($script:workflow, '(?m)^env:\r?\n(?<body>(?:  [^\r\n]*\r?\n)+)').Groups['body'].Value
        [regex]::Match($workflowEnv, '(?m)^  ARTIFACTS_FEED_URL: "(?<url>[^"]+)"\r?$').Groups['url'].Value | Should -BeExactly $script:feedUrl
        # No job or step sets its own feed, so every -ServiceIndexUrl below reads that one value.
        @([regex]::Matches($script:workflow, '(?m)^[ \t]+ARTIFACTS_FEED_URL:')).Count | Should -Be 1
        $script:workflow | Should -Not -Match 'vars\.AZURE_ARTIFACTS_FEED_URL'

        $push = @(Find-Step (Get-Job 'publish') 'Publish-DocumentCacheAdminBackfill')
        $push.Count | Should -Be 1
        $push[0] | Should -Match 'InstallCredentialHandler'
        $push[0] | Should -Match '-ServiceIndexUrl \$env:ARTIFACTS_FEED_URL\b'
        $endpoints = [regex]::Match($push[0], '(?m)^          VSS_NUGET_EXTERNAL_FEED_ENDPOINTS: ''(?<json>[^''\r\n]+)''\r?$').Groups['json'].Value
        $endpoints | Should -Not -BeNullOrEmpty -Because 'the credential provider authenticates the push only through this variable'
        $credentials = @(($endpoints.Replace('${{ secrets.AZURE_ARTIFACTS_PERSONAL_ACCESS_TOKEN }}', 'publishing-token') | ConvertFrom-Json).endpointCredentials)
        $credentials.Count | Should -Be 1
        $credentials[0].endpoint | Should -BeExactly $script:feedUrl
        $credentials[0].password | Should -BeExactly 'publishing-token'

        $promote = @(Find-Step (Get-Job 'promote') 'Invoke-ContractPromotion')
        $promote.Count | Should -Be 1
        $promote[0] | Should -Match '-ServiceIndexUrl \$env:ARTIFACTS_FEED_URL\b'
    }

    It 'publishes, promotes and attaches with the right dependencies' {
        $publish = Get-Job 'publish'
        $publish | Should -Not -BeNullOrEmpty
        Get-JobNeed $publish | Should -Be @('build', 'provenance-create-document-cache-admin', 'sbom-create-document-cache-admin')
        $publish | Should -Match 'Publish-DocumentCacheAdminBackfill'

        $promote = Get-Job 'promote'
        $promote | Should -Not -BeNullOrEmpty
        Get-JobNeed $promote | Should -Be @('build', 'publish')
        $promote | Should -Match 'Invoke-ContractPromotion'
        $promote | Should -Match "-PackageName 'EdFi\.Api\.DocumentCacheAdmin'"
        $promote | Should -Match 'needs\.build\.outputs\.version'

        $attach = Get-Job 'attach'
        $attach | Should -Not -BeNullOrEmpty
        # promote too, so the assets reach the release only once the version is in the Release view.
        Get-JobNeed $attach | Should -Be @('build', 'promote', 'provenance-create-document-cache-admin', 'publish', 'sbom-create-document-cache-admin')
        $attach | Should -Match '(?m)^      contents: write\r?$'
    }

    It 'attaches with Publish-ReleaseAsset, after checking out the dispatch commit and downloading both assets' {
        $attach = Get-Job 'attach'
        $steps = @(Get-JobStep $attach)
        $steps[0] | Should -Match 'uses: actions/checkout@'
        $steps[0] | Should -Match '(?m)^          ref: \$\{\{ github\.sha \}\}\r?$'
        $order = @(
            'uses: actions/checkout@'
            'secure-download-artifact@'
            'name: edfiApiDocumentCacheAdmin.intoto.jsonl'
            'Publish-ReleaseAsset'
        ) | ForEach-Object { $attach.IndexOf($_, [StringComparison]::Ordinal) }
        $order | ForEach-Object { $_ | Should -BeGreaterThan -1 }
        for ($i = 1; $i -lt $order.Count; $i++) {
            $order[$i] | Should -BeGreaterThan $order[$i - 1] -Because "step $i must follow step $($i - 1)"
        }

        $step = @(Find-Step $attach 'Publish-ReleaseAsset')
        $step.Count | Should -Be 1
        $step[0] | Should -Match '(?m)^          RELEASE_TAG: \$\{\{ inputs\.release_tag \}\}\r?$'
        $step[0] | Should -Match '(?m)^          GITHUB_TOKEN: \$\{\{ secrets\.GITHUB_TOKEN \}\}\r?$'
        $run = Get-StepRun $step[0]
        $run | Should -Not -Match '\$\{\{'
        $ast = Get-ScriptAst $run
        # The permanent module, which outlives this workflow, never the backfill module.
        @(Get-ScriptCommand $ast 'Import-Module' | ForEach-Object { $_.CommandElements[1].Extent.Text }) |
            Should -Be @('./eng/ci/release-assets.psm1')
        $calls = @(Get-ScriptCommand $ast 'Publish-ReleaseAsset')
        $calls.Count | Should -Be 1
        $arguments = Get-CommandArgument $calls[0]
        $arguments['Repository'].Extent.Text | Should -BeExactly '$env:GITHUB_REPOSITORY'
        $arguments['ReleaseTag'].Extent.Text | Should -BeExactly '$env:RELEASE_TAG'
        $arguments['Token'].Extent.Text | Should -BeExactly '$env:GITHUB_TOKEN'
        @(ConvertTo-AssetText $arguments['Asset']) | Should -Be @(
            'EdFi.Api.DocumentCacheAdmin-SBOM.zip|$env:MANIFEST_FILE|ZipEntry'
            'edfiApiDocumentCacheAdmin.intoto.jsonl|edfiApiDocumentCacheAdmin.intoto.jsonl|Bytes'
        )
    }

    It 'verifies the promoted version after promote, with no environment, no secret and read-only contents' {
        $verify = Get-Job 'verify'
        $verify | Should -Not -BeNullOrEmpty
        Get-JobNeed $verify | Should -Be @('build', 'promote')
        $verify | Should -Not -Match '(?m)^\s*environment:'
        $verify | Should -Not -Match 'secrets\.'
        $permissions = [regex]::Match($verify, '(?m)^    permissions:\r?\n(?<body>(?:      [^\r\n]*\r?\n?)+)').Groups['body'].Value
        @($permissions -split '\r?\n' | Where-Object { $_ }) | Should -Be @('      contents: read')
        $setup = @(Find-Step $verify 'uses: actions/setup-dotnet@')
        $setup.Count | Should -Be 1
        $setup[0] | Should -Match "(?m)^          dotnet-version: '10\.0\.x'\r?$"
    }

    It 'installs the promoted version, read anonymously from the Release view the tool README documents, into a fresh tool path' {
        $step = @(Find-Step (Get-Job 'verify') 'dotnet tool install')[0]
        $step | Should -Match '(?m)^          TOOL_VERSION: \$\{\{ needs\.build\.outputs\.version \}\}\r?$'
        [regex]::Match($step, '(?m)^          RELEASE_VIEW_URL: "(?<url>[^"\r\n]+)"\r?$').Groups['url'].Value | Should -BeExactly $script:releaseViewUrl
        (Get-Content (Join-Path $script:repoRoot 'src/dms/clis/EdFi.DataManagementService.DocumentCacheAdmin/README.md') -Raw).Contains($script:releaseViewUrl) |
            Should -BeTrue -Because 'the tool README documents this feed'

        $run = Get-VerifyRun
        $run | Should -Not -Match '\$\{\{'
        $run | Should -Match "(?m)^\`$toolPath = Join-Path \`$env:RUNNER_TEMP '[^'\r\n]+'\r?$"
        # A private global-packages folder, so the install cannot be served from a copy on the runner.
        $run | Should -Match "(?m)^\`$env:NUGET_PACKAGES = Join-Path \`$env:RUNNER_TEMP '[^'\r\n]+'\r?$"
        $installs = @(Get-ScriptCommand (Get-ScriptAst $run) 'dotnet')
        $installs.Count | Should -Be 1
        $elements = @($installs[0].CommandElements | ForEach-Object { $_.Extent.Text })
        ($elements[0..3] -join ' ') | Should -BeExactly 'dotnet tool install EdFi.Api.DocumentCacheAdmin'
        foreach ($option in ([ordered]@{ '--tool-path' = '$toolPath'; '--version' = '$env:TOOL_VERSION'; '--source' = '$env:RELEASE_VIEW_URL' }).GetEnumerator()) {
            $at = [Array]::IndexOf($elements, $option.Key)
            $at | Should -BeGreaterThan 3 -Because "the install passes $($option.Key)"
            $elements[$at + 1] | Should -BeExactly $option.Value
        }
        # Each attempt reads the view again rather than a cached answer from the attempt before.
        $elements | Should -Contain '--no-http-cache'
        $elements | Should -Not -Contain '--global'
        $elements | Should -Not -Contain '--add-source'
    }

    It 'retries the install up to 10 times, 30 seconds apart, and fails when the last attempt fails' {
        $ast = Get-ScriptAst (Get-VerifyRun)
        $loops = @($ast.FindAll({ param($node) $node -is [Management.Automation.Language.ForStatementAst] }, $true))
        $loops.Count | Should -Be 1
        $loop = $loops[0]
        $loop.Initializer.Extent.Text | Should -BeExactly '$attempt = 1'
        $loop.Condition.Extent.Text | Should -BeExactly '$attempt -le $attempts'
        $loop.Iterator.Extent.Text | Should -BeExactly '$attempt++'
        @($ast.FindAll({ param($node) $node -is [Management.Automation.Language.AssignmentStatementAst] -and $node.Left.Extent.Text -ceq '$attempts' }, $true) |
                ForEach-Object { $_.Right.Extent.Text }) | Should -Be @('10')
        @(Get-ScriptCommand $loop.Body 'dotnet').Count | Should -Be 1 -Because 'the install runs inside the loop'

        $checks = @($loop.Body.FindAll({ param($node) $node -is [Management.Automation.Language.IfStatementAst] }, $true))
        $success = @($checks | Where-Object { $_.Clauses[0].Item1.Extent.Text -ceq '$LASTEXITCODE -eq 0' })
        $last = @($checks | Where-Object { $_.Clauses[0].Item1.Extent.Text -ceq '$attempt -eq $attempts' })
        $success.Count | Should -Be 1
        $success[0].Clauses[0].Item2.Extent.Text | Should -Match '\Abreak\z|\A\{\s*break\s*\}\z'
        $last.Count | Should -Be 1
        @($last[0].Clauses[0].Item2.FindAll({ param($node) $node -is [Management.Automation.Language.ThrowStatementAst] }, $true)).Count | Should -Be 1
        # A success on the last attempt breaks before the last-attempt check can throw.
        $success[0].Extent.StartOffset | Should -BeLessThan $last[0].Extent.StartOffset
        $sleep = @(Get-ScriptCommand $loop.Body 'Start-Sleep')
        $sleep.Count | Should -Be 1
        $sleep[0].Extent.Text | Should -BeExactly 'Start-Sleep -Seconds 30'
        $sleep[0].Extent.StartOffset | Should -BeGreaterThan $last[0].Extent.StartOffset
    }

    It 'runs dms-document-cache --help from the tool path and requires exit 0, Usage: and status' {
        $run = Get-VerifyRun
        $ast = Get-ScriptAst $run
        $help = @($ast.FindAll({
                    param($node)
                    $node -is [Management.Automation.Language.CommandAst] -and
                    $node.InvocationOperator -eq [Management.Automation.Language.TokenKind]::Ampersand -and
                    $node.CommandElements[0].Extent.Text -ceq "(Join-Path `$toolPath 'dms-document-cache')"
                }, $true))
        $help.Count | Should -Be 1
        @($help[0].CommandElements | Select-Object -Skip 1 | ForEach-Object { $_.Extent.Text }) | Should -Be @('--help')
        # After the install loop.
        $loop = @($ast.FindAll({ param($node) $node -is [Management.Automation.Language.ForStatementAst] }, $true))[0]
        $help[0].Extent.StartOffset | Should -BeGreaterThan ($loop.Extent.EndOffset - 1)

        $run | Should -Match "(?m)^\`$help = & \(Join-Path \`$toolPath 'dms-document-cache'\) --help \| Out-String\r?$"
        $run | Should -Match '(?m)^\$helpExit = \$LASTEXITCODE\r?$'
        $run | Should -Match '(?m)^if \(\$helpExit -ne 0\) \{ throw [^\r\n]+ \}\r?$'
        $run | Should -Match "(?m)^foreach \(\`$expected in 'Usage:', 'status'\) \{\r?$"
        $run | Should -Match '(?m)^    if \(-not \$help\.Contains\(\$expected\)\) \{ throw [^\r\n]+ \}\r?$'
    }

    It 'gates <Id> on the publish switch and the main branch only' -ForEach @(
        @{ Id = 'publish' }, @{ Id = 'promote' }, @{ Id = 'attach' }, @{ Id = 'verify' }
    ) {
        $job = Get-Job $Id
        $job | Should -Not -BeNullOrEmpty
        @([regex]::Matches($job, '(?m)^    if: .*$') | ForEach-Object { $_.Value.Trim() }) | Should -Be @($script:trustedGate)
    }

    It 'binds only publish and promote to the publishing environment' {
        $bound = foreach ($id in 'build', 'sbom-create-document-cache-admin', 'provenance-create-document-cache-admin', 'publish', 'promote', 'attach', 'verify') {
            if ((Get-Job $id) -match '(?m)^    environment: publishing\r?$') { $id }
        }
        @($bound) | Sort-Object | Should -Be @('promote', 'publish')
    }
}
