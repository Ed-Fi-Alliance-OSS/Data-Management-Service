# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

# DMS-1502: every case here drives a decision the stock-image proof makes against the failure that
# decision exists to tell apart. The negative cases are the point. An assertion that only checks
# "something failed" passes when the feed was unreachable, when DMS started and then died, when core
# validation rejected the document before the plugin saw it, and when a write failed for a reason
# that has nothing to do with a read-only mount - and in each case it would report a proof that did
# not happen. No Docker, no registry, no daemon.

Describe 'Stock image proof decisions' {
    BeforeAll {
        Import-Module (Join-Path $PSScriptRoot 'plugin-deployment/stock-image-proof.psm1') -Force
    }

    Context 'the wrong-digest scenario: checksum failure versus anything else' {
        It 'accepts a real BusyBox checksum comparison failure' {
            $log = @(
                'Connecting to plugin-feed:8080 (172.18.0.3:8080)'
                'saving to ''/tmp/plugin.nupkg'''
                '/tmp/plugin.nupkg: FAILED'
                'sha256sum: WARNING: 1 computed checksum did NOT match'
            ) -join "`n"

            (Test-FetchFailedOnChecksum -ExitCode 1 -Log $log).Verified | Should -BeTrue
        }

        It 'refuses a transport failure, which exits non-zero without verifying anything' {
            # The package was never downloaded, so its digest was never compared. This is the case a
            # bare exit-code assertion would have reported as a successful digest proof.
            $log = @(
                'wget: bad address ''plugin-feed'''
            ) -join "`n"

            $verdict = Test-FetchFailedOnChecksum -ExitCode 1 -Log $log

            $verdict.Verified | Should -BeFalse
            $verdict.Reason | Should -Match 'never downloaded'
        }

        It 'refuses a fetch that exited zero, which means the wrong digest was accepted' {
            (Test-FetchFailedOnChecksum -ExitCode 0 -Log 'done').Verified | Should -BeFalse
        }

        It 'refuses a fetch service that never ran' {
            $verdict = Test-FetchFailedOnChecksum -ExitCode $null -Log ''

            $verdict.Verified | Should -BeFalse
            $verdict.Reason | Should -Match 'did not run'
        }

        It 'refuses a non-zero exit whose log says nothing about a checksum' {
            (Test-FetchFailedOnChecksum -ExitCode 2 -Log 'PLUGIN_NAME must be a single path segment').Verified |
                Should -BeFalse
        }
    }

    Context 'the wrong-digest scenario: never started versus started then stopped' {
        It 'accepts a container created but never started' {
            # Every fact passed explicitly, here and below: the contract is what the caller
            # established, not what a default let it omit.
            $verdict = Test-DmsNeverStarted -Status 'created' -StartedAtRaw '0001-01-01T00:00:00Z' -ExitCode 0 `
                -EnumerationSucceeded $true -ContainerLocated $true -InspectSucceeded $true

            $verdict.Verified | Should -BeTrue
        }

        It 'refuses no container at all, however reliably the absence was established' {
            # What service_completed_successfully buys is a DMS container created and never
            # started. A project that produced no DMS container is consistent with that dependency
            # working and equally with the service having been dropped or never composed, so it
            # establishes nothing.
            $verdict = Test-DmsNeverStarted -Status '' -StartedAtRaw '' -ExitCode $null `
                -EnumerationSucceeded $true -ContainerLocated $false -InspectSucceeded $true

            $verdict.Verified | Should -BeFalse
            $verdict.Reason | Should -Match 'an absent service is not the same as a service that refused to start'
        }

        It 'refuses a container it could not inspect' {
            $verdict = Test-DmsNeverStarted -Status '' -StartedAtRaw '' -ExitCode $null `
                -EnumerationSucceeded $true -ContainerLocated $true -InspectSucceeded $false

            $verdict.Verified | Should -BeFalse
            $verdict.Reason | Should -Match 'could not be inspected'
        }

        It 'refuses a successful inspect that reported no status' {
            $verdict = Test-DmsNeverStarted -Status '' -StartedAtRaw '' -ExitCode $null `
                -EnumerationSucceeded $true -ContainerLocated $true -InspectSucceeded $true

            $verdict.Verified | Should -BeFalse
            $verdict.Reason | Should -Match 'reported no status'
        }

        It 'refuses an empty status when the enumeration itself failed' {
            # "Absent" and "unreadable" look identical in an empty result, and only one of them is
            # evidence. A failed docker ps must not be reported as a container that never existed.
            $verdict = Test-DmsNeverStarted -Status '' -StartedAtRaw '' -ExitCode $null -EnumerationSucceeded $false

            $verdict.Verified | Should -BeFalse
            $verdict.Reason | Should -Match 'not evidence of absence'
        }

        It 'refuses a container that started and then exited' {
            # Much weaker than never starting: the plugin root was mounted and the host ran.
            $verdict = Test-DmsNeverStarted -Status 'exited' -StartedAtRaw '2026-09-21T10:00:00.1234567Z' -ExitCode 1 `
                -EnumerationSucceeded $true -ContainerLocated $true -InspectSucceeded $true

            $verdict.Verified | Should -BeFalse
            $verdict.Reason | Should -Match 'not the same as never starting'
        }

        It 'refuses a caller that established nothing at all' {
            # The defaults are closed, so omitting the facts is refused rather than accepted.
            (Test-DmsNeverStarted -Status 'created' -StartedAtRaw '0001-01-01T00:00:00Z' -ExitCode 0).Verified |
                Should -BeFalse
        }

        It 'refuses a container that is still running' {
            (Test-DmsNeverStarted -Status 'running' -StartedAtRaw '2026-09-21T10:00:00.1234567Z' -ExitCode $null `
                    -EnumerationSucceeded $true -ContainerLocated $true -InspectSucceeded $true).Verified |
                Should -BeFalse
        }
    }

    Context 'the misspelled-allowlist scenario' {
        BeforeAll {
            $script:expectedPath = '/app/plugins/Acme.CustomValidationProof1'

            function script:New-Status {
                param([string] $State = 'Failed', [string] $Phase = 'LoadPlugins', [string] $Summary = '')
                return [pscustomobject]@{ State = $State; Phase = $Phase; Summary = $Summary; ErrorMessage = '' }
            }
        }

        It 'accepts a failed LoadPlugins phase naming the expected path' {
            $status = New-Status -Summary "Loading plugins failed: no directory named $script:expectedPath"

            (Test-LoadPluginsFailure -StatusDocument $status -ExpectedPath $script:expectedPath -ExitCode 1).Verified |
                Should -BeTrue
        }

        It 'treats a missing status document as a failed assertion, not a fallback to logs' {
            $verdict = Test-LoadPluginsFailure -StatusDocument $null -ExpectedPath $script:expectedPath -ExitCode 1

            $verdict.Verified | Should -BeFalse
            $verdict.Reason | Should -Match 'diagnostic only'
        }

        It 'requires the PascalCase phase the host actually writes' {
            # DmsStartupPhases.LoadPlugins is the literal "LoadPlugins" and the document is
            # serialized with no naming policy, so a kebab-case value would mean something else
            # wrote it.
            $status = New-Status -Phase 'load-plugins' -Summary $script:expectedPath

            (Test-LoadPluginsFailure -StatusDocument $status -ExpectedPath $script:expectedPath -ExitCode 1).Verified |
                Should -BeFalse
        }

        It 'refuses a failure recorded against another phase' {
            $status = New-Status -Phase 'LoadDataStores' -Summary $script:expectedPath

            (Test-LoadPluginsFailure -StatusDocument $status -ExpectedPath $script:expectedPath -ExitCode 1).Verified |
                Should -BeFalse
        }

        It 'refuses a host that reached Ready' {
            $status = New-Status -State 'Ready' -Summary $script:expectedPath

            $verdict = Test-LoadPluginsFailure -StatusDocument $status -ExpectedPath $script:expectedPath -ExitCode 0

            $verdict.Verified | Should -BeFalse
            $verdict.Reason | Should -Match 'started rather than refusing'
        }

        It 'refuses a failure that does not name the expected path' {
            $status = New-Status -Summary 'Loading plugins failed.'

            (Test-LoadPluginsFailure -StatusDocument $status -ExpectedPath $script:expectedPath -ExitCode 1).Verified |
                Should -BeFalse
        }

        It 'refuses a container that exited zero despite the recorded failure' {
            $status = New-Status -Summary $script:expectedPath

            $verdict = Test-LoadPluginsFailure -StatusDocument $status -ExpectedPath $script:expectedPath -ExitCode 0

            $verdict.Verified | Should -BeFalse
            $verdict.Reason | Should -Match 'did not stop startup'
        }

        It 'refuses a missing exit code rather than treating absent evidence as an exit' {
            # This scenario asserts that DMS exits. Absent exit evidence usually means an inspect
            # failed, and a failed inspect is not a container that stopped.
            $status = New-Status -Summary $script:expectedPath

            $verdict = Test-LoadPluginsFailure -StatusDocument $status -ExpectedPath $script:expectedPath -ExitCode $null

            $verdict.Verified | Should -BeFalse
            $verdict.Reason | Should -Match 'not proof of an exit'
        }

        It 'refuses an empty expected path, which would assert nothing about the refusal' {
            $status = New-Status -Summary 'Loading plugins failed.'

            $verdict = Test-LoadPluginsFailure -StatusDocument $status -ExpectedPath '' -ExitCode 1

            $verdict.Verified | Should -BeFalse
            $verdict.Reason | Should -Match 'no expected path'
        }
    }

    Context 'the fixture 400 versus every other rejection' {
        BeforeAll {
            # The fixture's own text, and the write-path 400 shape the integration scenario asserts:
            # BOTH arms are present in every case. A path failure leaves errors empty rather than
            # absent and a document-level failure leaves validationErrors an empty object, which is
            # exactly why substring presence cannot say which arm reported what.
            $script:message = "This value is the custom-validation proof fixture's reserved rejection token."
            $script:resourceMessage = "This document carries the custom-validation proof fixture's reserved document-level rejection token."
            $script:path = '$.lastSurname'

            function script:New-PathArmBody {
                param([string] $Path = $script:path, [string] $Message = $script:message, [string[]] $Errors = @())

                return ([ordered]@{
                        detail           = "Data validation failed. See 'validationErrors' for details."
                        type             = 'urn:ed-fi:api:bad-request:data-validation-failed'
                        title            = 'Data Validation Failed'
                        status           = 400
                        correlationId    = $null
                        validationErrors = @{ $Path = @($Message) }
                        errors           = $Errors
                    } | ConvertTo-Json -Depth 6)
            }

            function script:New-ResourceArmBody {
                param([string] $Message = $script:resourceMessage)

                return ([ordered]@{
                        detail           = "The request could not be processed. See 'errors' for details."
                        type             = 'urn:ed-fi:api:bad-request'
                        title            = 'Bad Request'
                        status           = 400
                        correlationId    = $null
                        validationErrors = @{}
                        errors           = @($Message)
                    } | ConvertTo-Json -Depth 6)
            }
        }

        It 'accepts the fixture rejection on the path arm' {
            (Test-FixtureValidationFailure -StatusCode 400 -Body (New-PathArmBody) `
                    -ExpectedMessage $script:message -ExpectedPath $script:path -Arm 'Path').Verified |
                Should -BeTrue
        }

        It 'accepts the fixture rejection on the resource arm' {
            (Test-FixtureValidationFailure -StatusCode 400 -Body (New-ResourceArmBody) `
                    -ExpectedMessage $script:resourceMessage -Arm 'Resource').Verified |
                Should -BeTrue
        }

        It 'refuses a generic 400 from core validation' {
            # Reached the pipeline but not the plugin. This is the case the ticket names.
            $body = New-PathArmBody -Path '$.birthDate' -Message 'Value could not be parsed as a date.'

            $verdict = Test-FixtureValidationFailure -StatusCode 400 -Body $body `
                -ExpectedMessage $script:message -ExpectedPath $script:path

            $verdict.Verified | Should -BeFalse
            $verdict.Reason | Should -Match 'no entry for'
        }

        It 'refuses the fixture message sitting under the other arm while both arms exist' {
            # The near miss substring matching accepted: the expected path is present and carries an
            # unrelated message, and the fixture's text is in errors. Every keyword is in the body.
            $body = New-PathArmBody -Message 'An unrelated validation message.' -Errors @($script:message)

            $verdict = Test-FixtureValidationFailure -StatusCode 400 -Body $body `
                -ExpectedMessage $script:message -ExpectedPath $script:path -Arm 'Path'

            $verdict.Verified | Should -BeFalse
            $verdict.Reason | Should -Match 'exact message'
        }

        It 'refuses the fixture message at a different path while the expected path carries another' {
            $body = ([ordered]@{
                    status           = 400
                    validationErrors = @{ '$.lastSurname' = @('Another message.'); '$.firstName' = @($script:message) }
                    errors           = @()
                } | ConvertTo-Json -Depth 6)

            (Test-FixtureValidationFailure -StatusCode 400 -Body $body `
                    -ExpectedMessage $script:message -ExpectedPath $script:path).Verified |
                Should -BeFalse
        }

        It 'refuses a longer message that merely contains the expected text' {
            # Exact membership, not containment: a different message is a different rejection.
            $body = New-PathArmBody -Message "Prefix. $($script:message) Suffix."

            (Test-FixtureValidationFailure -StatusCode 400 -Body $body `
                    -ExpectedMessage $script:message -ExpectedPath $script:path).Verified |
                Should -BeFalse
        }

        It 'refuses non-JSON text even when it contains every keyword' {
            $body = "400 validationErrors errors $script:path $script:message"

            $verdict = Test-FixtureValidationFailure -StatusCode 400 -Body $body `
                -ExpectedMessage $script:message -ExpectedPath $script:path

            $verdict.Verified | Should -BeFalse
            $verdict.Reason | Should -Match 'not a JSON object'
        }

        It 'refuses a path-arm body whose validationErrors is an array rather than an object' {
            $body = '{"status":400,"validationErrors":["' + $script:message + '"],"errors":[]}'

            (Test-FixtureValidationFailure -StatusCode 400 -Body $body `
                    -ExpectedMessage $script:message -ExpectedPath $script:path).Verified |
                Should -BeFalse
        }

        It 'refuses a resource-arm body whose errors is not an array' {
            $body = '{"status":400,"validationErrors":{},"errors":"' + $script:resourceMessage + '"}'

            (Test-FixtureValidationFailure -StatusCode 400 -Body $body `
                    -ExpectedMessage $script:resourceMessage -Arm 'Resource').Verified |
                Should -BeFalse
        }

        It 'refuses an empty expected <_>, which would assert nothing' -ForEach @('message', 'path') {
            $message = if ($_ -eq 'message') { '' } else { $script:message }
            $path = if ($_ -eq 'path') { '' } else { $script:path }

            $verdict = Test-FixtureValidationFailure -StatusCode 400 -Body (New-PathArmBody) `
                -ExpectedMessage $message -ExpectedPath $path -Arm 'Path'

            $verdict.Verified | Should -BeFalse
            $verdict.Reason | Should -Match 'was supplied'
        }

        It 'refuses HTTP <_>, which means the request never reached a validator' -ForEach @(401, 403) {
            $verdict = Test-FixtureValidationFailure -StatusCode $_ -Body '' -ExpectedMessage $script:message -ExpectedPath $script:path

            $verdict.Verified | Should -BeFalse
            $verdict.Reason | Should -Match 'never reached a validator'
        }

        It 'refuses a success' {
            (Test-FixtureValidationFailure -StatusCode 201 -Body '' -ExpectedMessage $script:message -ExpectedPath $script:path).Verified |
                Should -BeFalse
        }

        It 'refuses a recorded response that never happened' {
            (Test-FixtureValidationFailure -StatusCode $null -Body '' -ExpectedMessage $script:message -ExpectedPath $script:path).Verified |
                Should -BeFalse
        }
    }

    Context 'the read-only mount, observed from inside the container' {
        BeforeAll {
            $script:readOnlyMounts = @(
                '/dev/sda1 / ext4 rw,relatime 0 0'
                '/dev/sda1 /app/plugins ext4 ro,relatime 0 0'
            ) -join "`n"
            $script:writableMounts = @(
                '/dev/sda1 /app/plugins ext4 rw,relatime 0 0'
            ) -join "`n"
        }

        It 'accepts a read-only mount and a write refused as read-only' {
            (Test-PluginMountReadOnly -MountTable $script:readOnlyMounts -MountTableExitCode 0 `
                    -WriteExitCode 1 -WriteOutput 'touch: /app/plugins/.probe: Read-only file system').Verified |
                Should -BeTrue
        }

        It 'refuses a writable mount even when the write happened to fail' {
            (Test-PluginMountReadOnly -MountTable $script:writableMounts -MountTableExitCode 0 `
                    -WriteExitCode 1 -WriteOutput 'touch: /app/plugins/.probe: Read-only file system').Verified |
                Should -BeFalse
        }

        It 'refuses a write that failed for an unrelated reason' {
            # Permission denied on a writable mount is not this control holding.
            $verdict = Test-PluginMountReadOnly -MountTable $script:readOnlyMounts -MountTableExitCode 0 `
                -WriteExitCode 1 -WriteOutput 'touch: /app/plugins/.probe: Permission denied'

            $verdict.Verified | Should -BeFalse
            $verdict.Reason | Should -Match 'other than a read-only file system'
        }

        It 'refuses a write that succeeded' {
            (Test-PluginMountReadOnly -MountTable $script:readOnlyMounts -MountTableExitCode 0 `
                    -WriteExitCode 0 -WriteOutput '').Verified |
                Should -BeFalse
        }

        It 'refuses a probe that could not run, rather than passing on its exit code' {
            # "The shell tool was missing" and "the mount is read-only" look identical from an exit
            # code alone, so an unreadable mount table is a failed assertion.
            $verdict = Test-PluginMountReadOnly -MountTable '' -MountTableExitCode 127 `
                -WriteExitCode 1 -WriteOutput 'sh: grep: not found'

            $verdict.Verified | Should -BeFalse
            $verdict.Reason | Should -Match 'not verified rather than verified'
        }

        It 'does not let a neighbouring path answer for /app/plugins' {
            $mounts = '/dev/sda1 /app/plugins-backup ext4 ro,relatime 0 0'

            (Test-PluginMountReadOnly -MountTable $mounts -MountTableExitCode 0 `
                    -WriteExitCode 1 -WriteOutput 'Read-only file system').Verified |
                Should -BeFalse
        }

        It 'reads ro as an option rather than as a substring' {
            # "relatime" contains no standalone "ro" option; a substring match would accept it.
            $mounts = '/dev/sda1 /app/plugins ext4 rw,relatime 0 0'

            (Test-PluginMountReadOnly -MountTable $mounts -MountTableExitCode 0 `
                    -WriteExitCode 1 -WriteOutput 'Read-only file system').Verified |
                Should -BeFalse
        }
    }

    Context 'the remote tag descriptor' {
        It 'reads the index digest from the JSON manifest form' {
            $output = '{"digest":"sha256:' + ('a' * 64) + '","mediaType":"application/vnd.oci.image.index.v1+json"}'

            Get-RemoteImageDigest -ImagetoolsOutput $output | Should -BeExactly ('sha256:' + ('a' * 64))
        }

        It 'reads the index digest from the default text form' {
            $output = @(
                'Name:      docker.io/edfialliance/ed-fi-api:8.0.1-alpha.0.7'
                'MediaType: application/vnd.oci.image.index.v1+json'
                "Digest:    sha256:$('b' * 64)"
            ) -join "`n"

            Get-RemoteImageDigest -ImagetoolsOutput $output | Should -BeExactly ('sha256:' + ('b' * 64))
        }

        It 'returns nothing for output it cannot read' {
            Get-RemoteImageDigest -ImagetoolsOutput 'ERROR: failed to read manifest' | Should -BeNullOrEmpty
        }

        It 'accepts a tag that resolves to the pinned digest' {
            (Test-RemoteImageDescriptor -Tag '8.0.1-alpha.0.7' -PinnedDigest ('sha256:' + ('a' * 64)) `
                    -ResolvedDigest ('sha256:' + ('a' * 64))).Verified |
                Should -BeTrue
        }

        It 'refuses a tag that has moved' {
            $verdict = Test-RemoteImageDescriptor -Tag '8.0.1-alpha.0.7' `
                -PinnedDigest ('sha256:' + ('a' * 64)) -ResolvedDigest ('sha256:' + ('b' * 64))

            $verdict.Verified | Should -BeFalse
            $verdict.Reason | Should -Match 'has moved'
        }

        It 'fails with an actionable diagnostic when Buildx is unavailable, rather than degrading' {
            $verdict = Test-RemoteImageDescriptor -Tag '8.0.1-alpha.0.7' `
                -PinnedDigest ('sha256:' + ('a' * 64)) -ResolvedDigest '' -ImagetoolsAvailable $false

            $verdict.Verified | Should -BeFalse
            $verdict.Reason | Should -Match 'setup-buildx-action'
        }

        It 'refuses an empty descriptor rather than assuming the tag is right' {
            (Test-RemoteImageDescriptor -Tag '8.0.1-alpha.0.7' -PinnedDigest ('sha256:' + ('a' * 64)) `
                    -ResolvedDigest '').Verified |
                Should -BeFalse
        }
    }

    Context 'the no-build claim, read off what actually ran' {
        It 'accepts a log of packs, publishes and pulls' {
            $commands = @(
                'docker pull edfialliance/ed-fi-api@sha256:abc'
                'dotnet publish eng/fixtures/plugins/Acme.CustomValidationProof'
                'docker compose -f postgresql.yml up -d'
            )

            (Test-BuildCommandAbsent -Command $commands).Verified | Should -BeTrue
        }

        It 'refuses <Case>' -ForEach @(
            @{ Case = 'a docker build'; Command = 'docker build -t local/ed-fi-api src/dms' }
            @{ Case = 'a buildx build'; Command = 'docker buildx build --push src/dms' }
            @{ Case = 'a compose build'; Command = 'docker compose -f local-dms.yml build dms' }
            @{ Case = "build-dms.ps1 DockerBuild"; Command = 'pwsh ./build-dms.ps1 DockerBuild -DMSVersion 9.9.9' }
        ) {
            $verdict = Test-BuildCommandAbsent -Command @('docker pull something', $Command)

            $verdict.Verified | Should -BeFalse
            $verdict.Reason | Should -Match 'image-building command'
        }

        It 'does not mistake a fixture pack for an image build' {
            # Building and packing the fixture and its contracts is permitted; building DMS is not.
            (Test-BuildCommandAbsent -Command @('dotnet pack src/plugins/EdFi.Api.Plugins')).Verified |
                Should -BeTrue
        }

        It 'accepts <Case>, where the word build is not the command' -ForEach @(
            @{ Case = 'an explicit --no-build'; Command = 'docker compose -f published-dms.yml up -d --no-build' }
            @{ Case = 'a compose file whose path contains build'; Command = 'docker compose -f eng/build/published-dms.yml up -d' }
            @{ Case = 'a compose file literally named build.yml'; Command = 'docker compose -f build.yml up -d' }
            @{ Case = 'a project directory containing build'; Command = 'docker compose --project-directory ./build up -d' }
            @{ Case = 'a pull of an image whose tag contains build'; Command = 'docker pull edfialliance/ed-fi-api:8.0.1-build.7' }
        ) {
            # --no-build is the strongest correct form of a compose up, so refusing it would refuse
            # exactly the invocation this proof wants.
            (Test-BuildCommandAbsent -Command @($Command)).Verified | Should -BeTrue
        }

        It 'still refuses <Case>' -ForEach @(
            @{ Case = 'a compose up that rebuilds'; Command = 'docker compose -f local-dms.yml up -d --build' }
            @{ Case = 'a compose build with file options first'; Command = 'docker compose -f a.yml -f b.yml build dms' }
            @{ Case = 'a buildx build with options'; Command = 'docker --context default buildx build --push src/dms' }
        ) {
            (Test-BuildCommandAbsent -Command @($Command)).Verified | Should -BeFalse
        }

        It 'answers for a compose command that carries only options' {
            # Nothing after the options, so no subcommand. This used to index past the end of the
            # token list, and it runs in the entry script's finally, where a throw would replace
            # the run's real outcome.
            (Test-BuildCommandAbsent -Command @('docker compose --help')).Verified | Should -BeTrue
            (Test-BuildCommandAbsent -Command @('docker compose -f a.yml')).Verified | Should -BeTrue
        }
    }

    Context 'redaction of evidence and command logs' {
        It 'removes a declared secret wherever it appears' {
            $text = 'dotnet nuget add source --password sekret-value-123 --name EdFi'

            $redacted = Protect-StockProofText -Text $text -Secret @('sekret-value-123')

            $redacted | Should -Not -Match 'sekret-value-123'
            $redacted | Should -Match 'REDACTED'
        }

        It 'removes a credential-shaped value nobody declared' {
            # The value nobody remembered to declare is exactly the one that leaks.
            $redacted = Protect-StockProofText -Text 'Authorization: Basic dXNlcjpwYXNz'

            $redacted | Should -Not -Match 'dXNlcjpwYXNz'
        }

        It 'removes credentials embedded in a URL' {
            $redacted = Protect-StockProofText -Text 'restoring from https://user:tokenvalue@pkgs.dev.azure.com/feed'

            $redacted | Should -Not -Match 'tokenvalue'
        }

        It 'leaves ordinary text alone' {
            $text = 'docker pull edfialliance/ed-fi-api@sha256:abc'

            Protect-StockProofText -Text $text | Should -BeExactly $text
        }

        It 'handles empty input without throwing' {
            Protect-StockProofText -Text '' | Should -BeExactly ''
        }

        It 'keeps a serialized evidence document parseable when a value carries an Authorization line' {
            # Serialized JSON escapes the newline, so the whole value is one physical line. Redacting
            # that text would run the Authorization rule past the closing quote and the members that
            # follow. Redacting the values first keeps each rule inside its own value.
            $result = [pscustomobject]@{
                primaryFailure = "GET /x`nAuthorization: Basic dXNlcjpwYXNz`nmore"
                kept           = 'kept'
                commandLog     = @('docker pull x', "Authorization: Bearer abc`ntrailing")
                nested         = [ordered]@{ header = 'Authorization: ***REDACTED***' }
            }

            $json = Protect-StockProofValue -Value $result | ConvertTo-Json -Depth 10
            $parsed = $json | ConvertFrom-Json

            $json | Should -Not -Match 'dXNlcjpwYXNz'
            $json | Should -Not -Match 'Bearer abc'
            $parsed.kept | Should -BeExactly 'kept'
            $parsed.primaryFailure | Should -BeExactly "GET /x`nAuthorization: ***REDACTED***`nmore"
            $parsed.commandLog | Should -HaveCount 2
            $parsed.commandLog[1] | Should -BeExactly "Authorization: ***REDACTED***`ntrailing"
            $parsed.nested.header | Should -BeExactly 'Authorization: ***REDACTED***'
        }

        It 'redacts a declared secret inside a nested value' {
            $redacted = Protect-StockProofValue -Value ([pscustomobject]@{ list = @('a sekret-value-123 b') }) -Secret @('sekret-value-123')

            $redacted.list[0] | Should -BeExactly 'a ***REDACTED*** b'
        }
    }

    Context 'the repositories this proof is about' {
        It 'names them rather than taking them as a parameter' {
            # Official stock acceptance is about these two, so making them configurable would only
            # create a way to prove the claim against something else.
            $repository = Get-StockProofRepository

            $repository.EdFiApi | Should -BeExactly 'edfialliance/ed-fi-api'
            $repository.ConfigurationService | Should -BeExactly 'edfialliance/ed-fi-api-configuration-service'
        }
    }
}
