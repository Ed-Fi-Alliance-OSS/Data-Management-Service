# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

# Two things are pinned here, and they are different claims.
#
# The first is the live one: docs/OPERATIONS.md embeds the two committed Compose overlays verbatim.
# Those files are the artifact an operator composes with -f and the end-to-end tiers run them as
# committed, so the document is the copy and this is what stops the copy drifting. A chapter edited
# without its file, or a file edited without its chapter, fails that case.
#
# The second is that the verifier itself actually detects drift. A guard that passes on a document
# it never really compared is worse than no guard, so every failure mode gets a fixture that must
# fail: absent marker, repeated marker, absent fence, unterminated fence, a marker naming nothing,
# changed content, and whitespace changed on an interior line. All of those run against purpose-built
# fixtures under the temp directory; none of them mutates a tracked file.

BeforeAll {
    $script:repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../.."))
    $script:verifier = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../Assert-DocumentEmbeds.ps1"))

    $script:operationsDocument = Join-Path $script:repositoryRoot "docs/OPERATIONS.md"
    $script:requiredEmbeds = @(
        "eng/docker-compose/plugins-dms.yml",
        "eng/docker-compose/plugins-fetch-dms.yml"
    )

    $script:fixtureRoot = Join-Path ([System.IO.Path]::GetTempPath()) "dms1500-embed-tests-$([guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $script:fixtureRoot -Force | Out-Null

    # The content every fixture embeds. It carries an interior blank line and a comment so that the
    # whitespace case below changes something an eye would skip over.
    $script:fixtureFileContent = @(
        "services:",
        "  example:",
        "    image: alpine",
        "",
        "    # a comment"
    ) -join "`n"

    # SupportsShouldProcess because the name carries a state-changing verb and PSScriptAnalyzer's
    # PSUseShouldProcessForStateChangingFunctions requires it. The name is worth keeping: this
    # genuinely creates a fixture on disk.
    function New-EmbedFixture {
        [CmdletBinding(SupportsShouldProcess)]
        param(
            [Parameter(Mandatory)][string] $Name,
            [Parameter(Mandatory)][string] $Document,
            [string] $FileContent = $script:fixtureFileContent,
            [switch] $OmitFile
        )

        $root = Join-Path $script:fixtureRoot "$Name-$([guid]::NewGuid().ToString('N'))"

        if (-not $PSCmdlet.ShouldProcess($root, "Create embed fixture")) {
            return $null
        }

        New-Item -ItemType Directory -Path (Join-Path $root "overlays") -Force | Out-Null

        if (-not $OmitFile) {
            [System.IO.File]::WriteAllText((Join-Path $root "overlays/example.yml"), $FileContent + "`n")
        }

        $documentPath = Join-Path $root "DOCUMENT.md"
        [System.IO.File]::WriteAllText($documentPath, $Document)

        return [pscustomobject]@{
            Root = $root
            Document = $documentPath
        }
    }

    # The shape every fixture starts from: one marker, one fenced block, content matching the file.
    function Get-WellFormedDocument {
        [CmdletBinding()]
        param([string] $Content = $script:fixtureFileContent)

        return @(
            "# Fixture",
            "",
            "<!-- embed: overlays/example.yml -->",
            '```yaml',
            $Content,
            '```',
            ""
        ) -join "`n"
    }
}

AfterAll {
    if (Test-Path -LiteralPath $script:fixtureRoot) {
        Remove-Item -LiteralPath $script:fixtureRoot -Recurse -Force
    }
}

Describe "Assert-DocumentEmbeds against the committed operations chapter" {
    It "admits docs/OPERATIONS.md with both plugin overlays embedded verbatim" {
        $output = & $script:verifier `
            -DocumentPath $script:operationsDocument `
            -RequiredEmbed $script:requiredEmbeds

        $output | Should -BeLike "*2 embedded block(s) match their files*"
    }

    It "names both overlays as required, so dropping a recipe cannot pass" {
        # Guards the test above rather than the verifier: a required list that silently lost an entry
        # would keep passing while the chapter lost a recipe.
        $script:requiredEmbeds | Should -Contain "eng/docker-compose/plugins-dms.yml"
        $script:requiredEmbeds | Should -Contain "eng/docker-compose/plugins-fetch-dms.yml"
    }
}

Describe "Assert-DocumentEmbeds drift detection" {
    It "refuses a document whose block no longer matches its file" {
        $fixture = New-EmbedFixture -Name "drift" -Document (
            Get-WellFormedDocument -Content ($script:fixtureFileContent -replace "image: alpine", "image: busybox")
        )

        {
            & $script:verifier -DocumentPath $fixture.Document -RequiredEmbed @("overlays/example.yml") -RepositoryRoot $fixture.Root
        } | Should -Throw -ExpectedMessage "*no longer embeds 'overlays/example.yml' verbatim*"
    }

    It "reports where the first difference is" {
        $fixture = New-EmbedFixture -Name "drift-location" -Document (
            Get-WellFormedDocument -Content ($script:fixtureFileContent -replace "image: alpine", "image: busybox")
        )

        {
            & $script:verifier -DocumentPath $fixture.Document -RequiredEmbed @("overlays/example.yml") -RepositoryRoot $fixture.Root
        } | Should -Throw -ExpectedMessage "*line 3 of the block*"
    }

    It "refuses whitespace added to an interior line, which a per-line trim would hide" {
        $drifted = ($script:fixtureFileContent -split "`n" | ForEach-Object {
                if ($_ -eq "    image: alpine") { "$_   " } else { $_ }
            }) -join "`n"

        $fixture = New-EmbedFixture -Name "interior-whitespace" -Document (Get-WellFormedDocument -Content $drifted)

        {
            & $script:verifier -DocumentPath $fixture.Document -RequiredEmbed @("overlays/example.yml") -RepositoryRoot $fixture.Root
        } | Should -Throw -ExpectedMessage "*no longer embeds*"
    }

    It "admits a trailing blank line inside the fence, which is an artifact of the container" {
        $fixture = New-EmbedFixture -Name "trailing-blank" -Document (
            Get-WellFormedDocument -Content "$script:fixtureFileContent`n"
        )

        {
            & $script:verifier -DocumentPath $fixture.Document -RequiredEmbed @("overlays/example.yml") -RepositoryRoot $fixture.Root
        } | Should -Not -Throw
    }
}

Describe "Assert-DocumentEmbeds structural failures" {
    It "refuses a document that embeds a required file nowhere" {
        $fixture = New-EmbedFixture -Name "absent-marker" -Document "# Fixture`n`nNothing is embedded here.`n"

        {
            & $script:verifier -DocumentPath $fixture.Document -RequiredEmbed @("overlays/example.yml") -RepositoryRoot $fixture.Root
        } | Should -Throw -ExpectedMessage "*carries no '<!-- embed: overlays/example.yml -->' marker*"
    }

    It "refuses a document that embeds one file twice" {
        $document = (Get-WellFormedDocument) + (Get-WellFormedDocument)
        $fixture = New-EmbedFixture -Name "duplicate-marker" -Document $document

        {
            & $script:verifier -DocumentPath $fixture.Document -RequiredEmbed @("overlays/example.yml") -RepositoryRoot $fixture.Root
        } | Should -Throw -ExpectedMessage "*carries 2 '<!-- embed: overlays/example.yml -->' markers*"
    }

    It "refuses a marker with no fenced block after it" {
        $document = @(
            "# Fixture",
            "",
            "<!-- embed: overlays/example.yml -->",
            "",
            "Some prose where the block should be.",
            ""
        ) -join "`n"

        $fixture = New-EmbedFixture -Name "absent-fence" -Document $document

        {
            & $script:verifier -DocumentPath $fixture.Document -RequiredEmbed @("overlays/example.yml") -RepositoryRoot $fixture.Root
        } | Should -Throw -ExpectedMessage "*no fenced block opens on the next line*"
    }

    It "refuses a fenced block that is never closed" {
        $document = @(
            "# Fixture",
            "",
            "<!-- embed: overlays/example.yml -->",
            '```yaml',
            $script:fixtureFileContent,
            ""
        ) -join "`n"

        $fixture = New-EmbedFixture -Name "unterminated-fence" -Document $document

        {
            & $script:verifier -DocumentPath $fixture.Document -RequiredEmbed @("overlays/example.yml") -RepositoryRoot $fixture.Root
        } | Should -Throw -ExpectedMessage "*never closed*"
    }

    It "refuses a marker naming a file that does not exist" {
        $fixture = New-EmbedFixture -Name "absent-file" -Document (Get-WellFormedDocument) -OmitFile

        {
            & $script:verifier -DocumentPath $fixture.Document -RequiredEmbed @("overlays/example.yml") -RepositoryRoot $fixture.Root
        } | Should -Throw -ExpectedMessage "*which does not exist under*"
    }

    It "checks a marker that is embedded but not required, so an extra block cannot rot" {
        # The -join is parenthesized deliberately. Without it the operator binds to the whole
        # expression, string concatenation flattens the array with spaces, and the appended block
        # collapses onto one line that carries no marker at all.
        $document = (Get-WellFormedDocument) + (
            @(
                "",
                "<!-- embed: overlays/second.yml -->",
                '```yaml',
                "services: {}",
                '```',
                ""
            ) -join "`n"
        )

        $fixture = New-EmbedFixture -Name "extra-marker" -Document $document
        [System.IO.File]::WriteAllText((Join-Path $fixture.Root "overlays/second.yml"), "services: { }`n")

        {
            & $script:verifier -DocumentPath $fixture.Document -RequiredEmbed @("overlays/example.yml") -RepositoryRoot $fixture.Root
        } | Should -Throw -ExpectedMessage "*no longer embeds 'overlays/second.yml' verbatim*"
    }

    It "refuses a document with no markers at all" {
        $fixture = New-EmbedFixture -Name "no-markers" -Document "# Fixture`n`nProse only.`n"

        {
            & $script:verifier -DocumentPath $fixture.Document -RequiredEmbed @() -RepositoryRoot $fixture.Root
        } | Should -Throw -ExpectedMessage "*carries no embed markers at all*"
    }
}
