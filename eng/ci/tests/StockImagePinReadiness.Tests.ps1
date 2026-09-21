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
            $filled.provisioning.dataStandardVersion = '5.2'
            $filled.provisioning.schemaPackages = @([pscustomobject]@{
                    name    = 'EdFi.DataStandard52.ApiSchema'
                    version = '1.0.335'
                    feedUrl = 'https://pkgs.dev.azure.com/ed-fi-alliance/Ed-Fi-Alliance-OSS/_packaging/EdFi/nuget/v3/index.json'
                })
            $filled.contracts.pluginsPackageVersion = '1.0.0'
            $filled.contracts.customValidationPackageVersion = '1.0.0'

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
                return [ordered]@{
                    status               = 'pending'
                    edFiApi              = [ordered]@{ repository = 'edfialliance/ed-fi-api'; tag = $null; digest = $null }
                    configurationService = [ordered]@{ repository = 'edfialliance/ed-fi-api-configuration-service'; digest = $null }
                    release              = [ordered]@{ githubRelease = $null; sourceCommit = $null; publicationRunUrl = $null }
                    provisioning         = [ordered]@{ schemaToolsPackageVersion = $null; dataStandardVersion = $null }
                    contracts            = [ordered]@{ pluginsPackageVersion = $null; customValidationPackageVersion = $null }
                }
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
            'provisioning.dataStandardVersion'
            'provisioning.schemaPackages'
            'contracts.pluginsPackageVersion'
            'contracts.customValidationPackageVersion'
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
            # NuGet reads a bare version as a floor, so every one of these would let a restore
            # resolve a package this pin does not name.
            { Invoke-Readiness -Pin (New-PublishedPin -Override @{ $Field = $Value }) } |
                Should -Throw '*does not match*'
        }

        It 'accepts an exact prerelease version, which is what a pinned alpha release carries' {
            (Invoke-Readiness -Pin (New-PublishedPin -Override @{
                        'contracts.pluginsPackageVersion' = '1.0.0-alpha.3'
                    })).ready | Should -BeExactly 'true'
        }

        It 'refuses an empty schema package set rather than letting the catalog pick' {
            { Invoke-Readiness -Pin (New-PublishedPin -Override @{ 'provisioning.schemaPackages' = @() }) } |
                Should -Throw '*schemaPackages*'
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
