# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

Describe 'Stock image pin readiness' {
    BeforeAll {
        $script:script = Join-Path $PSScriptRoot '../Test-StockImagePinReadiness.ps1'
        $script:committedPin = Join-Path $PSScriptRoot '../../docker-compose/tests/plugin-deployment/stock-image-pin.json'

        # A pin that passes every check, so each case below can break exactly one thing and the
        # failure it asserts is the one it caused.
        function script:New-PublishedPin {
            param([hashtable] $Override = @{})

            $pin = [ordered]@{
                status               = 'published'
                edFiApi              = [ordered]@{
                    repository = 'edfialliance/ed-fi-api'
                    tag        = '8.0.1-alpha.0.7'
                    digest     = 'sha256:' + ('a' * 64)
                }
                configurationService = [ordered]@{
                    repository = 'edfialliance/ed-fi-api-configuration-service'
                    digest     = 'sha256:' + ('b' * 64)
                }
                release              = [ordered]@{
                    githubRelease     = 'dms-pre-8.0.1-alpha.0.7'
                    sourceCommit      = 'c' * 40
                    publicationRunUrl = 'https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/1'
                }
                provisioning         = [ordered]@{
                    schemaToolsPackageVersion = '8.0.1-alpha.0.7'
                    schemaToolsFeedUrl        = 'https://pkgs.dev.azure.com/ed-fi-alliance/Ed-Fi-Alliance-OSS/_packaging/EdFi/nuget/v3/index.json'
                    dataStandardVersion       = '5.2'
                    schemaPackages            = @(
                        [ordered]@{
                            name    = 'EdFi.DataStandard52.ApiSchema'
                            version = '1.0.335'
                            feedUrl = 'https://pkgs.dev.azure.com/ed-fi-alliance/Ed-Fi-Alliance-OSS/_packaging/EdFi/nuget/v3/index.json'
                        }
                    )
                }
                contracts            = [ordered]@{
                    pluginsPackageVersion          = '1.0.0'
                    customValidationPackageVersion = '1.0.0'
                    feedUrl                        = 'https://pkgs.dev.azure.com/ed-fi-alliance/Ed-Fi-Alliance-OSS/_packaging/EdFi/nuget/v3/index.json'
                }
            }

            foreach ($key in $Override.Keys) {
                # "a.b" addresses a nested field; $null removes it outright, which is how the
                # missing-section cases are built.
                $segment = $key.Split('.')
                $node = $pin
                for ($i = 0; $i -lt $segment.Count - 1; $i++) { $node = $node[$segment[$i]] }
                $leaf = $segment[-1]

                if ($null -eq $Override[$key]) { $node.Remove($leaf) } else { $node[$leaf] = $Override[$key] }
            }

            return $pin
        }

        function script:Invoke-Readiness {
            param(
                [Parameter(Mandatory)] $Pin,
                [string] $EventName = 'schedule'
            )

            $pinFile = Join-Path ([IO.Path]::GetTempPath()) "dms1502-pin-$([guid]::NewGuid().ToString('N')).json"
            if ($Pin -is [string]) {
                Set-Content -LiteralPath $pinFile -Value $Pin -Encoding utf8
            }
            else {
                Set-Content -LiteralPath $pinFile -Value ($Pin | ConvertTo-Json -Depth 6) -Encoding utf8
            }

            try {
                $written = & $script:script -PinFile $pinFile -EventName $EventName -OutputPath ''
                $result = @{}
                foreach ($line in $written) {
                    $split = $line.Split('=', 2)
                    $result[$split[0]] = $split[1]
                }
                return [pscustomobject]$result
            }
            finally {
                Remove-Item -LiteralPath $pinFile -Force -ErrorAction SilentlyContinue
            }
        }
    }

    # Filling the real pin is a required step of this ticket, so nothing here may assume the
    # committed document is still pending. These assert what stays true across that change: that
    # whichever legitimate state it is in, the validator agrees with it. The pending-specific
    # behaviour is asserted against a synthetic document below, where it belongs.
    Context 'the committed pin document' {
        BeforeAll {
            $script:committed = Get-Content -LiteralPath $script:committedPin -Raw | ConvertFrom-Json
        }

        It 'is in one of the two legitimate states' {
            $script:committed.status | Should -BeIn @('pending', 'published')
        }

        It 'validates in whichever state it is actually in' {
            if ($script:committed.status -ceq 'pending') {
                # Pending: a schedule reports not-ready without running the proof, and a run someone
                # asked for by hand fails rather than reporting success it did not earn.
                $result = & $script:script -PinFile $script:committedPin -EventName 'schedule' -OutputPath ''
                ($result | Where-Object { $_ -like 'ready=*' }) | Should -BeExactly 'ready=false'
                ($result | Where-Object { $_ -like 'reason=*' }) | Should -Match 'not evidence'

                { & $script:script -PinFile $script:committedPin -EventName 'workflow_dispatch' -OutputPath '' } |
                    Should -Throw '*still pending*'

                # The anti-fabrication property while pending: no release-specific value is guessed.
                # The two repository names are not release-specific and are legitimately present.
                $script:committed.edFiApi.tag | Should -BeNullOrEmpty
                $script:committed.edFiApi.digest | Should -BeNullOrEmpty
                $script:committed.configurationService.digest | Should -BeNullOrEmpty
                $script:committed.release.githubRelease | Should -BeNullOrEmpty
            }
            else {
                # Published: it has to satisfy every rule, in both events. No allowance is made for
                # it being the committed document rather than a synthetic one.
                foreach ($eventName in @('schedule', 'workflow_dispatch')) {
                    $result = & $script:script -PinFile $script:committedPin -EventName $eventName -OutputPath ''
                    ($result | Where-Object { $_ -like 'ready=*' }) | Should -BeExactly 'ready=true'
                }
            }
        }

        It 'names the two repositories this proof is about, in either state' {
            $script:committed.edFiApi.repository | Should -BeExactly 'edfialliance/ed-fi-api'
            $script:committed.configurationService.repository |
                Should -BeExactly 'edfialliance/ed-fi-api-configuration-service'
        }

        It 'passes this same path once its release-specific values are filled in' {
            # The committed document's own shape, published rather than retyped, so this proves the
            # file on disk can reach a valid published state through the validator it ships with.
            $filled = Get-Content -LiteralPath $script:committedPin -Raw | ConvertFrom-Json
            $filled.status = 'published'
            $filled.edFiApi.tag = '8.0.1-alpha.0.7'
            $filled.edFiApi.digest = 'sha256:' + ('a' * 64)
            $filled.configurationService.digest = 'sha256:' + ('b' * 64)
            $filled.release.githubRelease = 'dms-pre-8.0.1-alpha.0.7'
            $filled.release.sourceCommit = 'c' * 40
            $filled.release.publicationRunUrl = 'https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/1'
            $filled.provisioning.schemaToolsPackageVersion = '8.0.1-alpha.0.7'
            $filled.provisioning.schemaToolsFeedUrl = 'https://pkgs.dev.azure.com/ed-fi-alliance/Ed-Fi-Alliance-OSS/_packaging/EdFi/nuget/v3/index.json'
            $filled.provisioning.dataStandardVersion = '5.2'
            $filled.provisioning.schemaPackages = @([pscustomobject]@{
                    name    = 'EdFi.DataStandard52.ApiSchema'
                    version = '1.0.335'
                    feedUrl = 'https://pkgs.dev.azure.com/ed-fi-alliance/Ed-Fi-Alliance-OSS/_packaging/EdFi/nuget/v3/index.json'
                })
            $filled.contracts.pluginsPackageVersion = '1.0.0'
            $filled.contracts.customValidationPackageVersion = '1.0.0'
            $filled.contracts.feedUrl = 'https://pkgs.dev.azure.com/ed-fi-alliance/Ed-Fi-Alliance-OSS/_packaging/EdFi/nuget/v3/index.json'

            (Invoke-Readiness -Pin $filled -EventName 'workflow_dispatch').ready | Should -BeExactly 'true'
        }

        It 'is still rejected through this path when a value is malformed' {
            # The committed document gets no special treatment: the validator is not weakened for it.
            $broken = Get-Content -LiteralPath $script:committedPin -Raw | ConvertFrom-Json
            $broken.status = 'published'
            $broken.edFiApi.tag = 'pre'

            { Invoke-Readiness -Pin $broken -EventName 'schedule' } | Should -Throw
        }
    }

    Context 'a pending pin' {
        BeforeAll {
            # Synthetic, so the cases below keep asserting pending behaviour after the real pin is
            # filled in. Repository names are not release-specific and stay, as they do on disk.
            function script:New-PendingPin {
                param([hashtable] $Override = @{}, [string[]] $Drop = @())

                # Every release-specific field present and null, which is what pending means. An
                # absent field records nothing, which is a differently malformed document.
                $pin = [ordered]@{
                    status               = 'pending'
                    edFiApi              = [ordered]@{ repository = 'edfialliance/ed-fi-api'; tag = $null; digest = $null }
                    configurationService = [ordered]@{ repository = 'edfialliance/ed-fi-api-configuration-service'; digest = $null }
                    release              = [ordered]@{ githubRelease = $null; sourceCommit = $null; publicationRunUrl = $null }
                    provisioning         = [ordered]@{ schemaToolsPackageVersion = $null; schemaToolsFeedUrl = $null; dataStandardVersion = $null; schemaPackages = $null }
                    contracts            = [ordered]@{ pluginsPackageVersion = $null; customValidationPackageVersion = $null; feedUrl = $null }
                }

                foreach ($key in $Override.Keys) {
                    $segment = $key.Split('.')
                    $pin[$segment[0]][$segment[1]] = $Override[$key]
                }

                foreach ($key in $Drop) {
                    $segment = $key.Split('.')
                    if ($segment.Count -eq 1) { $pin.Remove($segment[0]) }
                    else { $pin[$segment[0]].Remove($segment[1]) }
                }

                return $pin
            }
        }

    Context 'an identity that would not survive a line' {
        # Every one of these values is written into a line-oriented environment file or a GitHub
        # output line, or handed to the daemon as a reference. A value carrying CR or LF adds or
        # truncates a line there, so the refusal belongs at the pin boundary rather than downstream.
        #
        # The patterns anchor with \z rather than $, because .NET's $ also matches immediately
        # before a trailing newline; the trailing-LF cases below are what prove that.

        It 'accepts the forms a real publication produces' {
            (Invoke-Readiness -Pin (New-PublishedPin) -EventName 'schedule').ready | Should -BeExactly 'true'
        }

        It 'accepts a longer real-world release and tag pair' {
            $pin = New-PublishedPin @{
                'edFiApi.tag'                            = '8.1.0-alpha.0.152'
                'release.githubRelease'                  = 'dms-pre-8.1.0-alpha.0.152'
                'provisioning.schemaToolsPackageVersion' = '8.1.0-alpha.0.152'
            }

            (Invoke-Readiness -Pin $pin -EventName 'schedule').ready | Should -BeExactly 'true'
        }

        It 'refuses an edFiApi.tag carrying <Case>' -ForEach @(
            @{ Case = 'a trailing line feed'; Value = "8.0.1-alpha.0.7`n" }
            @{ Case = 'an embedded line feed'; Value = "8.0.1-alpha.0.7`nINJECTED=1" }
            @{ Case = 'a carriage return'; Value = "8.0.1-alpha.0.7`r" }
            @{ Case = 'a leading separator'; Value = '.8.0.1-alpha.0.7' }
            @{ Case = 'a slash'; Value = '8.0.1/alpha' }
            @{ Case = 'a space'; Value = '8.0.1 alpha' }
        ) {
            { Invoke-Readiness -Pin (New-PublishedPin @{ 'edFiApi.tag' = $Value }) -EventName 'schedule' } |
                Should -Throw '*edFiApi.tag*'
        }

        It 'refuses a release.githubRelease carrying <Case>' -ForEach @(
            @{ Case = 'an embedded line feed'; Value = "dms-pre-8.0.1-alpha.0.7`nINJECTED=1" }
            @{ Case = 'a trailing line feed'; Value = "dms-pre-8.0.1-alpha.0.7`n" }
            @{ Case = 'no dms-pre prefix'; Value = 'v8.0.1-alpha.0.7' }
        ) {
            { Invoke-Readiness -Pin (New-PublishedPin @{ 'release.githubRelease' = $Value }) -EventName 'schedule' } |
                Should -Throw '*release.githubRelease*'
        }

        It 'refuses a release that is not an alpha prerelease' {
            # The version-specific tag this pin names is only produced for an alpha ref, so a
            # non-alpha release publishes no tag to pin.
            $pin = New-PublishedPin @{
                'release.githubRelease' = 'dms-pre-8.0.1'
                'edFiApi.tag'           = '8.0'
            }

            { Invoke-Readiness -Pin $pin -EventName 'schedule' } | Should -Throw '*not an alpha prerelease*'
        }

        It 'refuses a <Case> carrying a trailing line feed' -ForEach @(
            @{ Case = 'digest'; Key = 'edFiApi.digest'; Value = "sha256:$('a' * 64)`n" }
            @{ Case = 'configuration service digest'; Key = 'configurationService.digest'; Value = "sha256:$('b' * 64)`n" }
            @{ Case = 'source commit'; Key = 'release.sourceCommit'; Value = "$('c' * 40)`n" }
            @{ Case = 'schema tools version'; Key = 'provisioning.schemaToolsPackageVersion'; Value = "8.0.1-alpha.0.7`n" }
            @{ Case = 'plugins contract version'; Key = 'contracts.pluginsPackageVersion'; Value = "1.0.0`n" }
        ) {
            { Invoke-Readiness -Pin (New-PublishedPin @{ $Key = $Value }) -EventName 'schedule' } |
                Should -Throw "*$($Key.Split('.')[-1])*"
        }

        It 'refuses a data standard label that is not env-safe: <Case>' -ForEach @(
            @{ Case = 'an embedded line feed'; Value = "5.2`nINJECTED=1" }
            @{ Case = 'a space'; Value = '5.2 label' }
            @{ Case = 'an equals sign'; Value = '5.2=x' }
        ) {
            { Invoke-Readiness -Pin (New-PublishedPin @{ 'provisioning.dataStandardVersion' = $Value }) -EventName 'schedule' } |
                Should -Throw '*dataStandardVersion*'
        }

        It 'refuses a schema package name that is not a single-line id' {
            $pin = New-PublishedPin @{
                'provisioning.schemaPackages' = @(
                    [ordered]@{ name = "EdFi.Api`nX=1"; version = '1.0.335'; feedUrl = 'https://example.invalid/index.json' }
                )
            }

            { Invoke-Readiness -Pin $pin -EventName 'schedule' } | Should -Throw '*name*'
        }

        It 'refuses a feed url carrying a line feed' {
            $pin = New-PublishedPin @{
                'provisioning.schemaPackages' = @(
                    [ordered]@{ name = 'EdFi.Api'; version = '1.0.335'; feedUrl = "https://example.invalid/index.json`n" }
                )
            }

            { Invoke-Readiness -Pin $pin -EventName 'schedule' } | Should -Throw '*feedUrl*'
        }
    }

    Context 'a pending pin is every field present and exactly null' {
        It 'refuses a pending pin whose <Case> is empty rather than null' -ForEach @(
            @{ Case = 'schema package set'; Key = 'provisioning.schemaPackages'; Value = @() }
            @{ Case = 'schema package object'; Key = 'provisioning.schemaPackages'; Value = @{} }
            @{ Case = 'tag, as an empty string'; Key = 'edFiApi.tag'; Value = '' }
            @{ Case = 'tag, as whitespace'; Key = 'edFiApi.tag'; Value = '   ' }
        ) {
            # An empty array, an empty object and an empty string all cast to "", so a string test
            # reads a half-recorded pin as an untouched one and skips the proof on it.
            { Invoke-Readiness -Pin (New-PendingPin -Override @{ $Key = $Value }) -EventName 'schedule' } |
                Should -Throw '*already carries*'
        }

        It 'refuses a pending pin carrying a real value' {
            { Invoke-Readiness -Pin (New-PendingPin -Override @{ 'edFiApi.tag' = '8.0.1-alpha.0.7' }) -EventName 'schedule' } |
                Should -Throw '*already carries*'
        }

        It 'refuses a pending pin missing <Case>' -ForEach @(
            @{ Case = 'a leaf field'; Drop = 'edFiApi.tag' }
            @{ Case = 'the schema package set'; Drop = 'provisioning.schemaPackages' }
            @{ Case = 'a whole section'; Drop = 'contracts' }
        ) {
            # Absence records nothing. A pending pin says "this is not published yet" by carrying
            # every field as null, and a document that simply lacks them says something else.
            { Invoke-Readiness -Pin (New-PendingPin -Drop @($Drop)) -EventName 'schedule' } |
                Should -Throw '*does not carry*'
        }

        It 'refuses a pending pin whose repository is <Case>' -ForEach @(
            @{ Case = 'missing'; Override = @{}; Drop = @('edFiApi.repository'); Expect = '*does not carry*' }
            @{ Case = 'someone else''s'; Override = @{ 'edFiApi.repository' = 'someone/else' }; Drop = @(); Expect = '*only about*' }
        ) {
            # The two repository names are not release-specific, so a pending pin still has to name
            # the artifacts it will one day pin.
            { Invoke-Readiness -Pin (New-PendingPin -Override $Override -Drop $Drop) -EventName 'schedule' } |
                Should -Throw $Expect
        }
    }

        It 'reports not ready on a schedule, and says the skip is not evidence' {
            $result = Invoke-Readiness -Pin (New-PendingPin) -EventName 'schedule'

            $result.ready | Should -BeExactly 'false'
            $result.reason | Should -Match 'not evidence'
        }

        It 'fails a run someone asked for by hand rather than reporting success' {
            { Invoke-Readiness -Pin (New-PendingPin) -EventName 'workflow_dispatch' } |
                Should -Throw '*still pending*'
        }

        It 'writes ready and reason to the output file the workflow reads' {
            $pinFile = Join-Path ([IO.Path]::GetTempPath()) "dms1502-pin-$([guid]::NewGuid().ToString('N')).json"
            $outputPath = Join-Path ([IO.Path]::GetTempPath()) "dms1502-out-$([guid]::NewGuid().ToString('N')).txt"
            Set-Content -LiteralPath $pinFile -Value ((New-PendingPin) | ConvertTo-Json -Depth 6) -Encoding utf8

            try {
                & $script:script -PinFile $pinFile -EventName 'schedule' -OutputPath $outputPath | Out-Null

                $written = Get-Content -LiteralPath $outputPath
                $written[0] | Should -BeExactly 'ready=false'
                $written[1] | Should -Match '^reason=.+'
            }
            finally {
                Remove-Item -LiteralPath $pinFile -Force -ErrorAction SilentlyContinue
                Remove-Item -LiteralPath $outputPath -Force -ErrorAction SilentlyContinue
            }
        }
    }

    Context 'a published pin' {
        It 'is ready, and says what it pinned' {
            $result = Invoke-Readiness -Pin (New-PublishedPin)

            $result.ready | Should -BeExactly 'true'
            $result.reason | Should -Match '8\.0\.1-alpha\.0\.7'
        }

        It 'is ready on a deliberate run too' {
            (Invoke-Readiness -Pin (New-PublishedPin) -EventName 'workflow_dispatch').ready | Should -BeExactly 'true'
        }

        It 'refuses a missing <_>, in either event' -ForEach @(
            'edFiApi.tag'
            'edFiApi.digest'
            'configurationService.digest'
            'release.githubRelease'
            'release.sourceCommit'
            'release.publicationRunUrl'
            'provisioning.schemaToolsPackageVersion'
            'provisioning.schemaToolsFeedUrl'
            'provisioning.dataStandardVersion'
            'provisioning.schemaPackages'
            'contracts.pluginsPackageVersion'
            'contracts.customValidationPackageVersion'
            'contracts.feedUrl'
        ) {
            $pin = New-PublishedPin -Override @{ $_ = $null }

            { Invoke-Readiness -Pin $pin } | Should -Throw "*$_ is missing*"
            { Invoke-Readiness -Pin $pin -EventName 'workflow_dispatch' } | Should -Throw "*$_ is missing*"
        }

        It 'refuses the moving tag <_>' -ForEach @('pre', 'latest') {
            { Invoke-Readiness -Pin (New-PublishedPin -Override @{ 'edFiApi.tag' = $_ }) } |
                Should -Throw '*a moving tag*'
        }

        It 'refuses a tag that carries its own digest' {
            $tag = '8.0.1-alpha.0.7@sha256:' + ('a' * 64)

            { Invoke-Readiness -Pin (New-PublishedPin -Override @{ 'edFiApi.tag' = $tag }) } |
                Should -Throw '*carries a digest*'
        }

        It 'refuses a malformed digest: <Case>' -ForEach @(
            @{ Case = 'no algorithm'; Value = 'a' * 64 }
            @{ Case = 'too short'; Value = 'sha256:' + ('a' * 63) }
            @{ Case = 'upper case'; Value = 'sha256:' + ('A' * 64) }
            @{ Case = 'another algorithm'; Value = 'sha512:' + ('a' * 64) }
        ) {
            { Invoke-Readiness -Pin (New-PublishedPin -Override @{ 'edFiApi.digest' = $Value }) } |
                Should -Throw '*does not match*'
        }

        It 'refuses a source commit that is not a full SHA' {
            { Invoke-Readiness -Pin (New-PublishedPin -Override @{ 'release.sourceCommit' = 'abc1234' }) } |
                Should -Throw '*does not match*'
        }

        It 'refuses a publication run that is not a URL' {
            { Invoke-Readiness -Pin (New-PublishedPin -Override @{ 'release.publicationRunUrl' = 'run 1' }) } |
                Should -Throw '*does not match*'
        }

        It 'refuses <Field> naming another repository' -ForEach @(
            @{ Field = 'edFiApi.repository'; Value = 'someone/else' }
            @{ Field = 'configurationService.repository'; Value = 'someone/else' }
        ) {
            { Invoke-Readiness -Pin (New-PublishedPin -Override @{ $Field = $Value }) } |
                Should -Throw '*this proof is only about*'
        }

        It 'refuses a non-exact <Field>: <Case>' -ForEach @(
            foreach ($field in @(
                    'provisioning.schemaToolsPackageVersion'
                    'contracts.pluginsPackageVersion'
                    'contracts.customValidationPackageVersion')) {
                @{ Field = $field; Case = 'a bracketed exact version'; Value = '[1.0.0]' }
                @{ Field = $field; Case = 'a range'; Value = '[1.0.0,2.0.0)' }
                @{ Field = $field; Case = 'a wildcard'; Value = '1.0.*' }
                @{ Field = $field; Case = 'a two-part version'; Value = '1.0' }
            }
        ) {
            # The field records one version identity. A range or a wildcard names a set; the
            # bracketed form is NuGet resolution syntax rather than a version, and one value gets
            # one spelling here.
            { Invoke-Readiness -Pin (New-PublishedPin -Override @{ $Field = $Value }) } |
                Should -Throw '*does not match*'
        }

        It 'refuses a version that is not SemVer: <Case>' -ForEach @(
            @{ Case = 'leading zeros'; Value = '01.00.000' }
            @{ Case = 'an empty prerelease identifier'; Value = '1.0.0-alpha..3' }
            @{ Case = 'a leading-zero numeric prerelease identifier'; Value = '1.0.0-alpha.03' }
            @{ Case = 'a trailing dot'; Value = '1.0.0-alpha.' }
        ) {
            # NuGet normalizes "01.00.000" to "1.0.0", so accepting it would give one pin two
            # spellings. The others are not versions at all.
            { Invoke-Readiness -Pin (New-PublishedPin -Override @{ 'contracts.pluginsPackageVersion' = $Value }) } |
                Should -Throw '*does not match*'
        }

        It 'refuses <Field> that is not a well-formed https address: <Case>' -ForEach @(
            foreach ($field in @('provisioning.schemaToolsFeedUrl', 'contracts.feedUrl', 'release.publicationRunUrl')) {
                @{ Field = $field; Case = 'no host'; Value = 'https:///' }
                @{ Field = $field; Case = 'markup'; Value = 'https://a"/><evil/>' }
                @{ Field = $field; Case = 'an ampersand'; Value = 'https://feed.example.org/index.json?a=1&b=2' }
                @{ Field = $field; Case = 'plain http'; Value = 'http://feed.example.org/index.json' }
                @{ Field = $field; Case = 'a local folder'; Value = '../local-folder-feed' }
            }
        ) {
            # These are written into a generated nuget.config or handed to dotnet. A value that
            # passed here and then broke that document would fail the proof after the gate had
            # already said ready.
            { Invoke-Readiness -Pin (New-PublishedPin -Override @{ $Field = $Value }) } |
                Should -Throw '*does not match*'
        }

        It 'refuses a schema package feed carrying markup' {
            $package = [ordered]@{
                name    = 'EdFi.DataStandard52.ApiSchema'
                version = '1.0.335'
                feedUrl = 'https://a"/><evil/>'
            }

            { Invoke-Readiness -Pin (New-PublishedPin -Override @{ 'provisioning.schemaPackages' = @($package) }) } |
                Should -Throw '*not an https feed address*'
        }

        It 'refuses <Field> recorded as a one-element array' -ForEach @(
            @{ Field = 'edFiApi.digest'; Value = 'sha256:' + ('a' * 64) }
            @{ Field = 'edFiApi.tag'; Value = '8.0.1-alpha.0.7' }
            @{ Field = 'contracts.feedUrl'; Value = 'https://pkgs.dev.azure.com/ed-fi-alliance/Ed-Fi-Alliance-OSS/_packaging/EdFi/nuget/v3/index.json' }
        ) {
            # A one-element array casts to its element, so it would otherwise validate as the value
            # it contains. The field is a string, and its shape is checked like every other.
            { Invoke-Readiness -Pin (New-PublishedPin -Override @{ $Field = @($Value) }) } |
                Should -Throw '*rather than a string*'
        }

        It 'refuses a status recorded as a one-element array' {
            { Invoke-Readiness -Pin (New-PublishedPin -Override @{ status = @('published') }) } |
                Should -Throw '*status is a*rather than a string*'
        }

        It 'refuses a schema package <Field> that is not a string' -ForEach @(
            @{ Field = 'version'; Value = @('1.0.335') }
            @{ Field = 'feedUrl'; Value = @('https://pkgs.dev.azure.com/ed-fi-alliance/Ed-Fi-Alliance-OSS/_packaging/EdFi/nuget/v3/index.json') }
        ) {
            $package = [ordered]@{
                name    = 'EdFi.DataStandard52.ApiSchema'
                version = '1.0.335'
                feedUrl = 'https://pkgs.dev.azure.com/ed-fi-alliance/Ed-Fi-Alliance-OSS/_packaging/EdFi/nuget/v3/index.json'
            }
            $package[$Field] = $Value

            { Invoke-Readiness -Pin (New-PublishedPin -Override @{ 'provisioning.schemaPackages' = @($package) }) } |
                Should -Throw "*$Field is a*rather than a string*"
        }

        It 'accepts a bare exact version, which is the identity form' {
            (Invoke-Readiness -Pin (New-PublishedPin -Override @{
                        'contracts.pluginsPackageVersion' = '1.2.3'
                    })).ready | Should -BeExactly 'true'
        }

        It 'accepts an exact prerelease version, which is what a pinned alpha release carries' {
            (Invoke-Readiness -Pin (New-PublishedPin -Override @{
                        'contracts.pluginsPackageVersion' = '1.0.0-alpha.3'
                    })).ready | Should -BeExactly 'true'
        }

        It 'refuses an empty schema package set rather than letting the catalog pick' {
            { Invoke-Readiness -Pin (New-PublishedPin -Override @{ 'provisioning.schemaPackages' = @() }) } |
                Should -Throw '*schemaPackages is empty*'
        }

        It 'accepts a singleton array, which must survive being read back' {
            # PowerShell enumerates a returned array, so a one-element set is exactly where a reader
            # turns an array into its element and stops being able to tell the two apart.
            $package = [ordered]@{
                name    = 'EdFi.DataStandard52.ApiSchema'
                version = '1.0.335'
                feedUrl = 'https://pkgs.dev.azure.com/ed-fi-alliance/Ed-Fi-Alliance-OSS/_packaging/EdFi/nuget/v3/index.json'
            }

            (Invoke-Readiness -Pin (New-PublishedPin -Override @{ 'provisioning.schemaPackages' = @($package) })).ready |
                Should -BeExactly 'true'
        }

        It 'refuses a single object written where an array belongs' {
            # The harness writes this field out verbatim as SCHEMA_PACKAGES, so a shape that is not
            # an array would be validated here and rejected there.
            $package = [ordered]@{
                name    = 'EdFi.DataStandard52.ApiSchema'
                version = '1.0.335'
                feedUrl = 'https://pkgs.dev.azure.com/ed-fi-alliance/Ed-Fi-Alliance-OSS/_packaging/EdFi/nuget/v3/index.json'
            }

            { Invoke-Readiness -Pin (New-PublishedPin -Override @{ 'provisioning.schemaPackages' = $package }) } |
                Should -Throw '*rather than an array*'
        }

        It 'refuses a null entry at index <Index>, rather than dropping it' -ForEach @(
            @{ Index = 1; Order = 'after' }
            @{ Index = 0; Order = 'before' }
        ) {
            # Filtering a hole out would validate a set the deployment never receives: the whole
            # field is written to SCHEMA_PACKAGES, holes included.
            $package = [ordered]@{
                name    = 'EdFi.DataStandard52.ApiSchema'
                version = '1.0.335'
                feedUrl = 'https://pkgs.dev.azure.com/ed-fi-alliance/Ed-Fi-Alliance-OSS/_packaging/EdFi/nuget/v3/index.json'
            }
            $set = if ($Order -eq 'after') { @($package, $null) } else { @($null, $package) }

            { Invoke-Readiness -Pin (New-PublishedPin -Override @{ 'provisioning.schemaPackages' = $set }) } |
                Should -Throw "*schemaPackages``[$Index``] is null*"
        }

        It 'refuses a set of nothing but nulls' {
            { Invoke-Readiness -Pin (New-PublishedPin -Override @{ 'provisioning.schemaPackages' = @($null, $null) }) } |
                Should -Throw '*is null*'
        }

        It 'refuses a <_> where a package object belongs' -ForEach @('string', 'number') {
            $entry = if ($_ -eq 'string') { 'EdFi.DataStandard52.ApiSchema' } else { 335 }

            { Invoke-Readiness -Pin (New-PublishedPin -Override @{ 'provisioning.schemaPackages' = @($entry) }) } |
                Should -Throw '*rather than an object carrying name, version and feedUrl*'
        }

        It 'refuses a schema package missing <_>' -ForEach @('name', 'version', 'feedUrl') {
            $package = [ordered]@{
                name    = 'EdFi.DataStandard52.ApiSchema'
                version = '1.0.335'
                feedUrl = 'https://pkgs.dev.azure.com/ed-fi-alliance/Ed-Fi-Alliance-OSS/_packaging/EdFi/nuget/v3/index.json'
            }
            $package.Remove($_)

            { Invoke-Readiness -Pin (New-PublishedPin -Override @{ 'provisioning.schemaPackages' = @($package) }) } |
                Should -Throw "*is missing $_*"
        }

        It 'refuses a schema package version that is not exact: <_>' -ForEach @('1.0.*', '[1.0.335,2.0.0)', '1.0') {
            $package = [ordered]@{
                name    = 'EdFi.DataStandard52.ApiSchema'
                version = $_
                feedUrl = 'https://pkgs.dev.azure.com/ed-fi-alliance/Ed-Fi-Alliance-OSS/_packaging/EdFi/nuget/v3/index.json'
            }

            { Invoke-Readiness -Pin (New-PublishedPin -Override @{ 'provisioning.schemaPackages' = @($package) }) } |
                Should -Throw '*not an exact version*'
        }

        It 'refuses a schema package feed that is not an https address' {
            $package = [ordered]@{
                name    = 'EdFi.DataStandard52.ApiSchema'
                version = '1.0.335'
                feedUrl = '../local-folder-feed'
            }

            { Invoke-Readiness -Pin (New-PublishedPin -Override @{ 'provisioning.schemaPackages' = @($package) }) } |
                Should -Throw '*not an https feed address*'
        }

        It 'names the offending entry when more than one schema package is pinned' {
            $good = [ordered]@{
                name    = 'EdFi.DataStandard52.ApiSchema'
                version = '1.0.335'
                feedUrl = 'https://pkgs.dev.azure.com/ed-fi-alliance/Ed-Fi-Alliance-OSS/_packaging/EdFi/nuget/v3/index.json'
            }
            $bad = [ordered]@{
                name    = 'EdFi.DataStandard52.TPDM.ApiSchema'
                version = '1.0.*'
                feedUrl = $good.feedUrl
            }

            { Invoke-Readiness -Pin (New-PublishedPin -Override @{ 'provisioning.schemaPackages' = @($good, $bad) }) } |
                Should -Throw '*schemaPackages`[1`]*'
        }

        It 'refuses a tag the pinned release does not publish' {
            # Coherence, computed by the publication workflow's own rule rather than restated: this
            # release publishes 8.0.1-alpha.0.7, so 8.0.1-alpha.0.8 describes no artifact.
            { Invoke-Readiness -Pin (New-PublishedPin -Override @{ 'edFiApi.tag' = '8.0.1-alpha.0.8' }) } |
                Should -Throw '*has to be one the pinned release produced*'
        }

        It 'accepts the tag that release does publish, which is the same check passing' {
            (Invoke-Readiness -Pin (New-PublishedPin -Override @{
                        'edFiApi.tag'           = '8.1.0-alpha.0.152'
                        'release.githubRelease' = 'dms-pre-8.1.0-alpha.0.152'
                    })).ready | Should -BeExactly 'true'
        }
    }

    Context 'a document that cannot be trusted' {
        It 'refuses a pending pin that already carries a tag, rather than skipping on it' {
            # Half a publication is not "not published yet". Skipping here would hide whichever half
            # is wrong until someone eventually looked.
            $pin = New-PublishedPin
            $pin.status = 'pending'

            { Invoke-Readiness -Pin $pin } | Should -Throw "*already carries*"
        }

        It 'names the populated fields when it refuses one' {
            $pin = New-PublishedPin
            $pin.status = 'pending'

            { Invoke-Readiness -Pin $pin } | Should -Throw '*edFiApi.tag*'
        }

        It 'refuses an unrecognized status rather than treating it as pending' {
            { Invoke-Readiness -Pin (New-PublishedPin -Override @{ 'status' = 'draft' }) } |
                Should -Throw "*has to be 'pending' or 'published'*"
        }

        It 'refuses a document that is not JSON' {
            { Invoke-Readiness -Pin 'this is not json {' } | Should -Throw '*not valid JSON*'
        }

        It 'refuses a pin file that does not exist' {
            { & $script:script -PinFile (Join-Path ([IO.Path]::GetTempPath()) 'no-such-pin.json') -EventName 'schedule' -OutputPath '' } |
                Should -Throw '*does not exist*'
        }
    }

    Context 'the output mechanism on a published pin' {
        It 'writes ready and the pinned reference to the output file the workflow reads' {
            $pinFile = Join-Path ([IO.Path]::GetTempPath()) "dms1502-pin-$([guid]::NewGuid().ToString('N')).json"
            $outputPath = Join-Path ([IO.Path]::GetTempPath()) "dms1502-out-$([guid]::NewGuid().ToString('N')).txt"
            Set-Content -LiteralPath $pinFile -Value ((New-PublishedPin) | ConvertTo-Json -Depth 6) -Encoding utf8

            try {
                & $script:script -PinFile $pinFile -EventName 'schedule' -OutputPath $outputPath | Out-Null

                $written = Get-Content -LiteralPath $outputPath
                $written[0] | Should -BeExactly 'ready=true'
                $written[1] | Should -Match '^reason=.*8\.0\.1-alpha\.0\.7'
            }
            finally {
                Remove-Item -LiteralPath $pinFile -Force -ErrorAction SilentlyContinue
                Remove-Item -LiteralPath $outputPath -Force -ErrorAction SilentlyContinue
            }
        }
    }
}
