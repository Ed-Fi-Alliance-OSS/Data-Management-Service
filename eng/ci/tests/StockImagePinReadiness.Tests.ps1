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

    Context 'the committed pin, which ships with nothing filled in' {
        It 'reports not ready on a schedule, and says the skip is not evidence' {
            $result = & $script:script -PinFile $script:committedPin -EventName 'schedule' -OutputPath ''

            ($result | Where-Object { $_ -like 'ready=*' }) | Should -BeExactly 'ready=false'
            ($result | Where-Object { $_ -like 'reason=*' }) | Should -Match 'not evidence'
        }

        It 'fails a run someone asked for by hand rather than reporting success' {
            { & $script:script -PinFile $script:committedPin -EventName 'workflow_dispatch' -OutputPath '' } |
                Should -Throw '*still pending*'
        }

        It 'ships no fabricated tag or digest' {
            $pin = Get-Content -LiteralPath $script:committedPin -Raw | ConvertFrom-Json

            $pin.status | Should -BeExactly 'pending'
            $pin.edFiApi.tag | Should -BeNullOrEmpty
            $pin.edFiApi.digest | Should -BeNullOrEmpty
            $pin.configurationService.digest | Should -BeNullOrEmpty
            $pin.release.githubRelease | Should -BeNullOrEmpty
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

    Context 'the output mechanism' {
        It 'writes ready and reason to the output file the workflow reads' {
            $outputPath = Join-Path ([IO.Path]::GetTempPath()) "dms1502-out-$([guid]::NewGuid().ToString('N')).txt"

            try {
                & $script:script -PinFile $script:committedPin -EventName 'schedule' -OutputPath $outputPath | Out-Null

                $written = Get-Content -LiteralPath $outputPath
                $written[0] | Should -BeExactly 'ready=false'
                $written[1] | Should -Match '^reason=.+'
            }
            finally {
                Remove-Item -LiteralPath $outputPath -Force -ErrorAction SilentlyContinue
            }
        }
    }
}
