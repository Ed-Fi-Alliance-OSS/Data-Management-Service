# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

# DMS-1502: the stock-image proof has to name one published Ed-Fi API image by version-specific tag
# and digest, and a digest is meaningful only against the repository it was published under.
# published-dms.yml and published-config.yml shared DMS_IMAGE_TAG, so pinning through it would have
# asked Docker Hub for the Ed-Fi API's digest under the Configuration Service's repository. These
# assert the two seams that separate them, against what Compose actually renders rather than against
# the files' text.
#
# `docker compose config` is a client-side render: it interpolates and merges, and contacts no
# daemon and no registry. Nothing here starts, pulls or creates anything.

Describe 'Stock image pin selection' {
    BeforeAll {
        $script:composeRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
        $script:baseEnvironmentFile = Join-Path $script:composeRoot '.env.e2e'
        $script:pinOverlay = 'tests/plugin-deployment/stock-image-pin-dms.yml'

        $script:docker = (Get-Command docker -CommandType Application -ErrorAction SilentlyContinue |
                Select-Object -First 1)

        # Loudly, rather than as a skip. These cases are the only thing standing between a pin and
        # the wrong repository, and a silent skip would retire them on any host missing the CLI.
        if ($null -eq $script:docker) {
            throw 'The Docker CLI is not on PATH. These cases render Compose configuration client-side and cannot be skipped silently.'
        }

        # Renders the compose set the launcher would build, with the supplied values appended to the
        # ordinary environment file. Returns the rendered image reference per service.
        function script:Get-RenderedImage {
            param(
                [Parameter(Mandatory)] [string[]] $ComposeFile,
                [hashtable] $Values = @{}
            )

            $environmentFile = Join-Path ([IO.Path]::GetTempPath()) "dms1502-pin-$([guid]::NewGuid().ToString('N')).env"
            $lines = @(Get-Content -LiteralPath $script:baseEnvironmentFile)
            foreach ($key in $Values.Keys) {
                $lines += "$key=$($Values[$key])"
            }
            Set-Content -LiteralPath $environmentFile -Value $lines -Encoding utf8

            $arguments = @()
            foreach ($file in $ComposeFile) {
                $arguments += @('-f', $file)
            }
            $arguments += @('--env-file', $environmentFile, 'config', '--format', 'json')

            Push-Location $script:composeRoot
            try {
                $rendered = & $script:docker compose @arguments 2>$null
                $exitCode = $LASTEXITCODE
            }
            finally {
                Pop-Location
                Remove-Item -LiteralPath $environmentFile -Force -ErrorAction SilentlyContinue
            }

            if ($exitCode -ne 0) {
                throw "docker compose config failed with exit code $exitCode."
            }

            $document = ($rendered | Out-String) | ConvertFrom-Json
            $images = @{}
            foreach ($service in $document.services.PSObject.Properties) {
                $images[$service.Name] = [string]$service.Value.image
            }

            return $images
        }

        $script:publishedSet = @('postgresql.yml', 'published-dms.yml', 'published-config.yml')
        # The order the launchers build: application file, then the plugin overlays, then the
        # configuration service. The pin overlay rides the plugin hook, so it lands in the middle.
        $script:pinnedSet = @('postgresql.yml', 'published-dms.yml', $script:pinOverlay, 'published-config.yml')
    }

    Context 'the shipped default, which this change must not move' {
        It 'renders both published images from the moving tag when nothing is pinned' {
            $images = Get-RenderedImage -ComposeFile $script:publishedSet

            $images['dms'] | Should -BeExactly 'edfialliance/ed-fi-api:pre'
            $images['config'] | Should -BeExactly 'edfialliance/ed-fi-api-configuration-service:pre'
        }

        It 'still lets DMS_IMAGE_TAG select the tag for both services' {
            # The shared variable keeps working for the case it can express: one tag both
            # repositories publish under the same name.
            $images = Get-RenderedImage -ComposeFile $script:publishedSet -Values @{ DMS_IMAGE_TAG = '8.0' }

            $images['dms'] | Should -BeExactly 'edfialliance/ed-fi-api:8.0'
            $images['config'] | Should -BeExactly 'edfialliance/ed-fi-api-configuration-service:8.0'
        }
    }

    Context 'the Configuration Service seam' {
        It 'takes a whole reference from DMS_CONFIG_DOCKER_IMAGE' {
            $reference = 'edfialliance/ed-fi-api-configuration-service@sha256:' + ('c' * 64)
            $images = Get-RenderedImage -ComposeFile $script:publishedSet -Values @{ DMS_CONFIG_DOCKER_IMAGE = $reference }

            $images['config'] | Should -BeExactly $reference
        }

        It 'leaves the Ed-Fi API image alone when only the Configuration Service is pinned' {
            $reference = 'edfialliance/ed-fi-api-configuration-service@sha256:' + ('c' * 64)
            $images = Get-RenderedImage -ComposeFile $script:publishedSet -Values @{ DMS_CONFIG_DOCKER_IMAGE = $reference }

            $images['dms'] | Should -BeExactly 'edfialliance/ed-fi-api:pre'
        }

        It 'wins over DMS_IMAGE_TAG, so a pinned deployment is not silently retagged' {
            $reference = 'edfialliance/ed-fi-api-configuration-service@sha256:' + ('c' * 64)
            $images = Get-RenderedImage -ComposeFile $script:publishedSet -Values @{
                DMS_IMAGE_TAG           = '8.0'
                DMS_CONFIG_DOCKER_IMAGE = $reference
            }

            $images['config'] | Should -BeExactly $reference
            $images['dms'] | Should -BeExactly 'edfialliance/ed-fi-api:8.0'
        }
    }

    Context 'the Ed-Fi API pin overlay' {
        BeforeAll {
            $script:dmsPin = 'edfialliance/ed-fi-api:8.0.1-alpha.0.7@sha256:' + ('b' * 64)
            $script:configPin = 'edfialliance/ed-fi-api-configuration-service@sha256:' + ('c' * 64)
        }

        It 'overrides the application file it is composed after' {
            $images = Get-RenderedImage -ComposeFile $script:pinnedSet -Values @{
                DMS_STOCK_IMAGE_REFERENCE = $script:dmsPin
                DMS_CONFIG_DOCKER_IMAGE   = $script:configPin
            }

            $images['dms'] | Should -BeExactly $script:dmsPin
        }

        It 'pins the two services independently, and never sends one digest to the other repository' {
            # The defect this whole seam exists for: a shared variable would have rendered the Ed-Fi
            # API's digest under the Configuration Service's repository.
            $images = Get-RenderedImage -ComposeFile $script:pinnedSet -Values @{
                DMS_STOCK_IMAGE_REFERENCE = $script:dmsPin
                DMS_CONFIG_DOCKER_IMAGE   = $script:configPin
            }

            $images['dms'] | Should -BeExactly $script:dmsPin
            $images['config'] | Should -BeExactly $script:configPin
            $images['config'] | Should -Not -Match ('b' * 64)
            $images['dms'] | Should -Not -Match ('c' * 64)
        }

        It 'carries a version-specific tag and a digest through to the rendered reference' {
            $images = Get-RenderedImage -ComposeFile $script:pinnedSet -Values @{
                DMS_STOCK_IMAGE_REFERENCE = $script:dmsPin
                DMS_CONFIG_DOCKER_IMAGE   = $script:configPin
            }

            # Both halves, because the ticket's pin is a tag AND a digest and Compose has to accept
            # the combined reference form for that to be expressible at all.
            $images['dms'] | Should -Match ':8\.0\.1-alpha\.0\.7@sha256:'
        }

        It 'refuses to render at all when the reference is not supplied' {
            # Declared with :? like the committed recipes, so an unpinned run fails in Compose rather
            # than quietly proving something about whatever :pre happens to be today.
            { Get-RenderedImage -ComposeFile $script:pinnedSet -Values @{ DMS_CONFIG_DOCKER_IMAGE = $script:configPin } } |
                Should -Throw '*docker compose config failed*'
        }
    }
}
