# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

# Pins eng/ci/release-assets.psm1, which attaches files to a GitHub release so that a retry completes
# a partial attach instead of failing on an asset an earlier attempt already uploaded, and the release
# job that attaches through it. No YAML parser is assumed: the workflow is read as lines, and a step's
# run script is read with the PowerShell parser, so line breaks and backtick continuations in a call
# do not change what is checked.

BeforeAll {
    $script:repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
}

Describe 'Publish-ReleaseAsset' {
    BeforeAll {
        # No -Force, for the reason package-helpers.psm1 gives: re-importing a loaded module replaces the
        # instance other modules already hold, and a later file's mocks would then miss their calls.
        Import-Module (Join-Path $script:repoRoot 'eng/ci/release-assets.psm1') -ErrorAction Stop
        $script:repository = 'Ed-Fi-Alliance-OSS/Data-Management-Service'
        $script:sbomName = 'EdFi.Api.DocumentCacheAdmin-SBOM.zip'
        $script:provenanceName = 'edfiApiDocumentCacheAdmin.intoto.jsonl'

        function New-SbomZip {
            # Built with ZipFile rather than Compress-Archive, uncompressed, so its bytes differ from
            # what the function would build from the same manifest.
            [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Writes only a disposable zip in Pester TestDrive.')]
            param([string] $Content, [string] $EntryName = 'manifest.spdx.json')
            $path = Join-Path $TestDrive "$([guid]::NewGuid()).zip"
            $zip = [IO.Compression.ZipFile]::Open($path, [IO.Compression.ZipArchiveMode]::Create)
            try {
                $entry = $zip.CreateEntry($EntryName, [IO.Compression.CompressionLevel]::NoCompression)
                $writer = [IO.StreamWriter]::new($entry.Open())
                try { $writer.Write($Content) }
                finally { $writer.Dispose() }
            }
            finally { $zip.Dispose() }
            return $path
        }

        function New-AssetFile {
            [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Writes only a disposable file in Pester TestDrive.')]
            param([string] $Content)
            $path = Join-Path $TestDrive ([guid]::NewGuid())
            [IO.File]::WriteAllText($path, $Content)
            return $path
        }

        function Get-UploadedManifest([byte[]] $Zip) {
            $archive = [IO.Compression.ZipArchive]::new([IO.MemoryStream]::new($Zip))
            try {
                @($archive.Entries).Count | Should -Be 1
                $reader = [IO.StreamReader]::new($archive.GetEntry('manifest.spdx.json').Open())
                try { return $reader.ReadToEnd() }
                finally { $reader.Dispose() }
            }
            finally { $archive.Dispose() }
        }

        function Invoke-TestAttach {
            param([string] $ReleaseTag = 'v8.1.0', [object[]] $Asset = $script:assets)
            Publish-ReleaseAsset -Repository $script:repository -ReleaseTag $ReleaseTag -Token 'test-only' -Asset $Asset `
                -GetRelease $script:getRelease -DownloadAsset $script:downloadAsset -UploadAsset $script:uploadAsset `
                -DeleteAsset $script:deleteAsset
        }
    }

    BeforeEach {
        $script:manifestText = '{"spdxVersion":"SPDX-2.2","name":"EdFi.Api.DocumentCacheAdmin 8.1.0"}'
        $script:provenanceText = '{"payloadType":"application/vnd.in-toto+json"}'
        $directory = New-Item -ItemType Directory (Join-Path $TestDrive ([guid]::NewGuid()))
        $script:manifest = Join-Path $directory 'manifest.spdx.json'
        [IO.File]::WriteAllText($script:manifest, $script:manifestText)
        $script:provenance = Join-Path $directory $script:provenanceName
        [IO.File]::WriteAllText($script:provenance, $script:provenanceText)
        $script:assets = @(
            @{ Name = $script:sbomName; Path = $script:manifest; Comparison = 'ZipEntry' }
            @{ Name = $script:provenanceName; Path = $script:provenance; Comparison = 'Bytes' }
        )

        # Asset name to the file holding its bytes on the release, and to the state GitHub reports for
        # it when that is not uploaded. A state of 'none' leaves the property out.
        $script:existing = [ordered]@{}
        $script:states = @{}
        $script:lookups = [Collections.Generic.List[object]]::new()
        $script:downloads = [Collections.Generic.List[object]]::new()
        $script:uploads = [Collections.Generic.List[object]]::new()
        $script:deletes = [Collections.Generic.List[object]]::new()
        # Each delete and upload, in the order they were made.
        $script:changes = [Collections.Generic.List[string]]::new()
        $script:release = 'present'
        $script:getRelease = {
            param($Repository, $Tag, $Token)
            $script:lookups.Add([pscustomobject]@{ Repository = $Repository; Tag = $Tag; Token = $Token })
            if ($script:release -ne 'present') { return $null }
            $id = 100
            return [pscustomobject]@{
                id     = 42
                assets = @($script:existing.Keys | ForEach-Object {
                        $id++
                        $asset = [ordered]@{ id = $id; name = $_; url = "https://api.invalid/assets/$id" }
                        $state = if ($script:states.ContainsKey($_)) { $script:states[$_] } else { 'uploaded' }
                        if ($state -ne 'none') { $asset.state = $state }
                        [pscustomobject] $asset
                    })
            }
        }
        $script:downloadAsset = {
            param($Asset, $Destination, $Token)
            $script:downloads.Add([pscustomobject]@{ Name = $Asset.name; Token = $Token })
            Copy-Item -LiteralPath $script:existing[$Asset.name] -Destination $Destination
        }
        $script:uploadAsset = {
            param($Repository, $Release, $Name, $Path, $Token)
            $script:uploads.Add([pscustomobject]@{ Repository = $Repository; ReleaseId = $Release.id; Name = $Name; Bytes = [IO.File]::ReadAllBytes($Path); Token = $Token })
            $script:changes.Add("upload $Name")
        }
        $script:deleteAsset = {
            param($Repository, $Asset, $Token)
            $script:deletes.Add([pscustomobject]@{ Repository = $Repository; Id = $Asset.id; Name = $Asset.name; Token = $Token })
            $script:changes.Add("delete $($Asset.name)")
        }
    }

    It 'uploads both assets when the release has neither, the SBOM zipped with the manifest as its only entry' {
        Invoke-TestAttach
        @($script:lookups.Tag) | Should -Be @('v8.1.0')
        $script:downloads.Count | Should -Be 0
        @($script:uploads.Name) | Should -Be @($script:sbomName, $script:provenanceName)
        @($script:uploads.ReleaseId) | Should -Be @(42, 42)
        Get-UploadedManifest $script:uploads[0].Bytes | Should -BeExactly $script:manifestText
        [Text.Encoding]::UTF8.GetString($script:uploads[1].Bytes) | Should -BeExactly $script:provenanceText
    }

    It 'passes the repository and the token to every request' {
        $script:existing[$script:sbomName] = New-SbomZip $script:manifestText
        Invoke-TestAttach
        @($script:lookups.Repository) | Should -Be @($script:repository)
        @($script:lookups.Token) | Should -Be @('test-only')
        @($script:downloads.Token) | Should -Be @('test-only')
        @($script:uploads.Repository) | Should -Be @($script:repository)
        @($script:uploads.Token) | Should -Be @('test-only')
    }

    It 'uploads nothing when both assets already hold this content, though the SBOM zip bytes differ' {
        $script:existing[$script:sbomName] = New-SbomZip $script:manifestText
        $script:existing[$script:provenanceName] = New-AssetFile $script:provenanceText
        { Invoke-TestAttach } | Should -Not -Throw
        @($script:downloads.Name) | Should -Be @($script:sbomName, $script:provenanceName)
        $script:uploads.Count | Should -Be 0
    }

    It 'uploads only the provenance when the SBOM is already present with this content' {
        $script:existing[$script:sbomName] = New-SbomZip $script:manifestText
        Invoke-TestAttach
        @($script:uploads.Name) | Should -Be @($script:provenanceName)
        [Text.Encoding]::UTF8.GetString($script:uploads[0].Bytes) | Should -BeExactly $script:provenanceText
    }

    It 'uploads only the SBOM when the provenance is already present with this content' {
        $script:existing[$script:provenanceName] = New-AssetFile $script:provenanceText
        Invoke-TestAttach
        @($script:uploads.Name) | Should -Be @($script:sbomName)
        Get-UploadedManifest $script:uploads[0].Bytes | Should -BeExactly $script:manifestText
    }

    It 'refuses, uploading nothing, an SBOM whose manifest differs' {
        $script:existing[$script:sbomName] = New-SbomZip '{"spdxVersion":"SPDX-2.2","name":"another build"}'
        { Invoke-TestAttach } | Should -Throw "*$script:sbomName*different*"
        $script:uploads.Count | Should -Be 0
    }

    It 'refuses, uploading nothing, an SBOM zip whose only entry is not the manifest' {
        $script:existing[$script:sbomName] = New-SbomZip $script:manifestText -EntryName 'other.json'
        { Invoke-TestAttach } | Should -Throw "*$script:sbomName*different*"
        $script:uploads.Count | Should -Be 0
    }

    It 'refuses, uploading nothing, an SBOM asset that is not a zip' {
        $script:existing[$script:sbomName] = New-AssetFile $script:manifestText
        { Invoke-TestAttach } | Should -Throw "*$script:sbomName*different*"
        $script:uploads.Count | Should -Be 0
    }

    It 'refuses, uploading nothing, a provenance whose bytes differ, though the SBOM is missing' {
        $script:existing[$script:provenanceName] = New-AssetFile '{"payloadType":"another"}'
        { Invoke-TestAttach } | Should -Throw "*$script:provenanceName*different*"
        $script:uploads.Count | Should -Be 0
    }

    It 'deletes and uploads again, without downloading it, an asset an interrupted upload left in state <State>' -ForEach @(
        # GitHub documents that a failed upload can leave an empty starter asset, and its schema lists
        # open beside uploaded.
        @{ State = 'starter' }
        @{ State = 'open' }
    ) {
        $script:existing[$script:sbomName] = New-AssetFile ''
        $script:states[$script:sbomName] = $State
        $script:existing[$script:provenanceName] = New-AssetFile $script:provenanceText
        Invoke-TestAttach
        @($script:downloads.Name) | Should -Be @($script:provenanceName)
        @($script:deletes.Name) | Should -Be @($script:sbomName)
        @($script:deletes.Id) | Should -Be @(101)
        @($script:deletes.Repository) | Should -Be @($script:repository)
        @($script:deletes.Token) | Should -Be @('test-only')
        @($script:changes) | Should -Be @("delete $script:sbomName", "upload $script:sbomName")
        Get-UploadedManifest $script:uploads[0].Bytes | Should -BeExactly $script:manifestText
    }

    It 'refuses, deleting and uploading nothing, when another asset differs though one is unfinished' {
        $script:existing[$script:sbomName] = New-AssetFile ''
        $script:states[$script:sbomName] = 'starter'
        $script:existing[$script:provenanceName] = New-AssetFile '{"payloadType":"another"}'
        { Invoke-TestAttach } | Should -Throw "*$script:provenanceName*different*"
        $script:changes.Count | Should -Be 0
    }

    It 'refuses, deleting nothing, an empty asset whose state is <Case>, because only the state marks an upload unfinished' -ForEach @(
        @{ Case = 'uploaded'; State = 'uploaded' }
        @{ Case = 'not reported'; State = 'none' }
    ) {
        $script:existing[$script:provenanceName] = New-AssetFile ''
        $script:states[$script:provenanceName] = $State
        { Invoke-TestAttach } | Should -Throw "*$script:provenanceName*different*"
        $script:changes.Count | Should -Be 0
    }

    It 'deletes nothing under -WhatIf' {
        $script:existing[$script:sbomName] = New-AssetFile ''
        $script:states[$script:sbomName] = 'starter'
        Publish-ReleaseAsset -Repository $script:repository -ReleaseTag 'v8.1.0' -Token 'test-only' -Asset $script:assets `
            -GetRelease $script:getRelease -DownloadAsset $script:downloadAsset -UploadAsset $script:uploadAsset `
            -DeleteAsset $script:deleteAsset -WhatIf
        $script:changes.Count | Should -Be 0
    }

    It 'refuses a tag with no GitHub release' {
        $script:release = 'absent'
        { Invoke-TestAttach } | Should -Throw '*v8.1.0*no GitHub release*'
        $script:downloads.Count | Should -Be 0
        $script:uploads.Count | Should -Be 0
    }

    It 'refuses the tag <Tag>, which is not a v version, before looking up a release' -ForEach @(
        @{ Tag = 'dms-pre-8.1.0' }
        @{ Tag = 'v8.1.0$(touch pwned)' }
        @{ Tag = "v8.1.0`n" }
        @{ Tag = '' }
    ) {
        { Invoke-TestAttach -ReleaseTag $Tag } | Should -Throw '*is not a v version*'
        $script:lookups.Count | Should -Be 0
    }

    It 'accepts the prerelease v tag of a tagged prerelease build' {
        Invoke-TestAttach -ReleaseTag 'v8.1.0-beta.0.1'
        @($script:lookups.Tag) | Should -Be @('v8.1.0-beta.0.1')
        $script:uploads.Count | Should -Be 2
    }

    It 'refuses, before looking up a release, <Case>' -ForEach @(
        @{ Case = 'an asset whose file does not exist'; Change = { param($a) $a[1].Path = Join-Path $TestDrive 'missing.jsonl' }; Message = '*edfiApiDocumentCacheAdmin.intoto.jsonl*no file*' }
        @{ Case = 'an asset with no name'; Change = { param($a) $a[1].Name = '' }; Message = '*Name*' }
        @{ Case = 'an unknown comparison'; Change = { param($a) $a[1].Comparison = 'Hash' }; Message = '*Bytes or ZipEntry*' }
        @{ Case = 'two assets with one name'; Change = { param($a) $a[1].Name = $a[0].Name }; Message = '*more than once*' }
    ) {
        & $Change $script:assets
        { Invoke-TestAttach } | Should -Throw $Message
        $script:lookups.Count | Should -Be 0
    }
}

Describe 'on-prerelease.yml attaches the DocumentCacheAdmin release assets with Publish-ReleaseAsset' {
    BeforeAll {
        $script:lines = (Get-Content (Join-Path $script:repoRoot '.github/workflows/on-prerelease.yml') -Raw) -split '\r?\n'

        function script:Get-JobText([string] $Id) {
            # The job's lines, from its header up to the next line indented two spaces.
            $start = -1
            for ($i = 0; $i -lt $script:lines.Count; $i++) {
                if ($script:lines[$i] -cmatch "^  $([regex]::Escape($Id)):[ \t]*(?:#.*)?$") { $start = $i; break }
            }
            if ($start -lt 0) { return '' }
            $end = $start + 1
            while ($end -lt $script:lines.Count -and $script:lines[$end] -notmatch '^  \S') { $end++ }
            return ($script:lines[$start..($end - 1)] -join "`n")
        }

        function script:Get-JobStep([string] $Job) {
            # Each step of a job, from its '      - ' line up to the next one.
            $lines = $Job -split "`n"
            $steps = [Collections.Generic.List[string]]::new()
            $inSteps = $false
            foreach ($line in $lines) {
                if ($line -cmatch '^    steps:[ \t]*$') { $inSteps = $true; continue }
                if (-not $inSteps) { continue }
                if ($line -match '^    \S') { break }
                if ($line.StartsWith('      - ')) { $steps.Add($line) }
                elseif ($steps.Count -gt 0) { $steps[$steps.Count - 1] += "`n$line" }
            }
            return $steps.ToArray()
        }

        function script:Get-StepRun([string] $Step) {
            # The block scalar under the step's run: key, without its indentation.
            $lines = $Step -split "`n"
            $run = [Collections.Generic.List[string]]::new()
            $inRun = $false
            foreach ($line in $lines) {
                if ($line -cmatch '^        run: \|[ \t]*$') { $inRun = $true; continue }
                if (-not $inRun) { continue }
                if ($line -match '^[ \t]*$') { $run.Add(''); continue }
                if (-not $line.StartsWith('          ')) { break }
                $run.Add($line.Substring(10))
            }
            return ($run -join "`n")
        }

        function script:Get-ScriptCommand([string] $Script, [string] $Name) {
            $errors = $null
            $ast = [Management.Automation.Language.Parser]::ParseInput($Script, [ref] $null, [ref] $errors)
            @($errors).Count | Should -Be 0 -Because 'the run script parses as PowerShell'
            return @($ast.FindAll({
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

        function script:Find-StepIndex([string] $Pattern) {
            # The index of the first step whose text matches the pattern, or -1.
            for ($i = 0; $i -lt $script:steps.Count; $i++) {
                if ($script:steps[$i] -match $Pattern) { return $i }
            }
            return -1
        }

        $script:attach = Get-JobText 'attach-document-cache-admin-artifacts-to-release'
        $script:steps = @(Get-JobStep $script:attach)
        $script:attachSteps = @($script:steps | Where-Object { $_.Contains('Publish-ReleaseAsset') })
    }

    It 'checks out the release commit as its first step, so the checkout cannot clean away the downloaded assets' {
        $script:attach | Should -Not -BeNullOrEmpty
        $script:steps[0] | Should -Match '^      - uses: actions/checkout@[0-9a-f]{40} # v'
        $script:steps[0] | Should -Match '(?m)^          ref: \$\{\{ github\.sha \}\}$'
    }

    It 'downloads the SBOM and the provenance after the checkout and attaches them last' {
        $order = @(
            Find-StepIndex '(?m)^      - uses: actions/checkout@'
            Find-StepIndex '(?m)^        uses: Ed-Fi-Alliance-OSS/slsa-github-generator/\.github/actions/secure-download-artifact@\S+$'
            Find-StepIndex '(?m)^          name: edfiApiDocumentCacheAdmin\.intoto\.jsonl$'
            Find-StepIndex '(?m)^\s*Publish-ReleaseAsset\b'
        )
        $order | ForEach-Object { $_ | Should -BeGreaterThan -1 }
        for ($i = 1; $i -lt $order.Count; $i++) {
            $order[$i] | Should -BeGreaterThan $order[$i - 1] -Because "step $i must follow step $($i - 1)"
        }
        $order[-1] | Should -Be ($script:steps.Count - 1)
    }

    It 'imports eng/ci/release-assets.psm1 and attaches both assets with one Publish-ReleaseAsset call' {
        $script:attachSteps.Count | Should -Be 1
        $run = Get-StepRun $script:attachSteps[0]
        $import = @(Get-ScriptCommand $run 'Import-Module')
        @($import | ForEach-Object { $_.CommandElements[1].Extent.Text }) | Should -Be @('./eng/ci/release-assets.psm1')
        $calls = @(Get-ScriptCommand $run 'Publish-ReleaseAsset')
        $calls.Count | Should -Be 1
        $import[0].Extent.StartOffset | Should -BeLessThan $calls[0].Extent.StartOffset

        $arguments = Get-CommandArgument $calls[0]
        $arguments['Repository'].Extent.Text | Should -BeExactly '$env:GITHUB_REPOSITORY'
        $arguments['ReleaseTag'].Extent.Text | Should -BeExactly '$env:RELEASE_VERSION'
        $arguments['Token'].Extent.Text | Should -BeExactly '$env:GITHUB_TOKEN'
        @(ConvertTo-AssetText $arguments['Asset']) | Should -Be @(
            'EdFi.Api.DocumentCacheAdmin-SBOM.zip|$env:MANIFEST_FILE|ZipEntry'
            'edfiApiDocumentCacheAdmin.intoto.jsonl|edfiApiDocumentCacheAdmin.intoto.jsonl|Bytes'
        )
    }

    It 'reads the release tag and the token from env, and interpolates nothing into the script' {
        $step = $script:attachSteps[0]
        $step | Should -Match '(?m)^        shell: pwsh$'
        $step | Should -Match '(?m)^          RELEASE_VERSION: \$\{\{ steps\.prepare-version\.outputs\.semver \}\}$'
        $step | Should -Match '(?m)^          GITHUB_TOKEN: \$\{\{ secrets\.GITHUB_TOKEN \}\}$'
        Get-StepRun $step | Should -Not -Match '\$\{\{'
    }

    It 'uploads nothing itself, so every asset goes through the existence check' {
        foreach ($direct in 'uploads.github.com', 'Invoke-RestMethod', 'Invoke-WebRequest', 'Compress-Archive', 'gh release upload', 'curl ') {
            $script:attach.Contains($direct) | Should -BeFalse -Because "the job must not call $direct itself"
        }
    }

    It 'can write release assets and nothing else' {
        $permissions = [regex]::Match($script:attach, '(?m)^    permissions:\n(?<body>(?:      [^\n]*\n?)+)').Groups['body'].Value
        @($permissions -split '\n' | Where-Object { $_ }) | Should -Be @('      contents: write')
    }
}
