# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseDeclaredVarsMoreThanAssignments', '', Justification = 'Variables supply the dynamic scope of executable blocks extracted from the scripts under test.')]
param()

# DMS-1502: the committed plugin acquisition overlays describe a deployment rather than a build, so
# they have to reach a stack running a published image as well as one running a locally built image.
# One resolver serves both launchers; these tests drive that resolver and the real statements each
# launcher carries, taken from the committed scripts through the AST rather than retyped here.

Describe 'Plugin compose file resolution' {
    BeforeAll {
        $script:composeRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
        Import-Module (Join-Path $script:composeRoot 'env-utility.psm1') -Force

        $script:launcher = @('start-local-dms.ps1', 'start-published-dms.ps1')

        function script:Get-ScriptAst([string]$Name) {
            return [Management.Automation.Language.Parser]::ParseFile(
                (Join-Path $script:composeRoot $Name), [ref]$null, [ref]$null)
        }

        # The foreach that appends the resolved overlays, taken from the committed launcher.
        function script:Get-PluginForeachAst([string]$Name) {
            $node = (Get-ScriptAst $Name).FindAll({
                    param($n) $n -is [Management.Automation.Language.ForEachStatementAst] -and
                    $n.Extent.Text.Contains('Resolve-PluginComposeFile')
                }, $true) | Select-Object -First 1

            if ($null -eq $node) { throw "$Name carries no plugin compose-file foreach." }

            return $node
        }

        # The compose-set block, which is the one gated on $databaseOnlyStartup that builds $files.
        function script:Get-ComposeSetIfAst([string]$Name) {
            $node = (Get-ScriptAst $Name).FindAll({
                    param($n) $n -is [Management.Automation.Language.IfStatementAst] -and
                    $n.Extent.Text.StartsWith('if (-not $databaseOnlyStartup)') -and
                    $n.Extent.Text.Contains('Resolve-PluginComposeFile')
                }, $true) | Select-Object -First 1

            if ($null -eq $node) { throw "$Name carries no compose-set block holding the plugin hook." }

            return $node
        }

        function script:Get-DatabaseOnlyStartupBlock([string]$Name) {
            $node = (Get-ScriptAst $Name).FindAll({
                    param($n) $n -is [Management.Automation.Language.AssignmentStatementAst] -and
                    $n.Left.Extent.Text -eq '$databaseOnlyStartup'
                }, $true) | Select-Object -First 1

            if ($null -eq $node) { throw "$Name does not assign `$databaseOnlyStartup." }

            return [scriptblock]::Create($node.Extent.Text)
        }

        # The launcher's foreach reads $PSScriptRoot, which is bound to the file a scriptblock came
        # from rather than looked up as a variable, so a block created from a string would see
        # $null. Writing the committed text to a file in a scratch directory and dot-sourcing that
        # file gives it a real script root, which is also what lets the relative-resolution case run
        # against overlay files the test owns instead of against the repository's.
        function script:New-LauncherBlockFile([string]$Name, [string]$Directory) {
            $path = Join-Path $Directory 'plugin-hook-under-test.ps1'
            Set-Content -LiteralPath $path -Value (Get-PluginForeachAst $Name).Extent.Text -Encoding utf8
            return $path
        }

        function script:New-ScratchDirectory {
            $path = Join-Path ([IO.Path]::GetTempPath()) "dms1502-$([guid]::NewGuid().ToString('N'))"
            New-Item -ItemType Directory -Path $path -Force | Out-Null
            return $path
        }

        function docker { }
    }

    Context 'the resolver' {
        It 'returns nothing when the key is <Case>' -ForEach @(
            @{ Case = 'absent'; Values = @{} }
            @{ Case = 'empty'; Values = @{ DMS_PLUGINS_COMPOSE_FILES = '' } }
            @{ Case = 'whitespace'; Values = @{ DMS_PLUGINS_COMPOSE_FILES = "  `t " } }
        ) {
            @(Resolve-PluginComposeFile -EnvValues $Values -ScriptRoot $script:composeRoot) |
                Should -HaveCount 0
        }

        It 'resolves a relative entry against the script root it was given' {
            $scratch = New-ScratchDirectory
            try {
                Set-Content -LiteralPath (Join-Path $scratch 'one.yml') -Value 'services: {}'

                @(Resolve-PluginComposeFile `
                        -EnvValues @{ DMS_PLUGINS_COMPOSE_FILES = 'one.yml' } `
                        -ScriptRoot $scratch) |
                    Should -Be @((Join-Path $scratch 'one.yml'))
            }
            finally { Remove-Item -LiteralPath $scratch -Recurse -Force }
        }

        It 'takes a rooted entry as written rather than joining it to the script root' {
            $scratch = New-ScratchDirectory
            try {
                $rooted = Join-Path $scratch 'rooted.yml'
                Set-Content -LiteralPath $rooted -Value 'services: {}'

                @(Resolve-PluginComposeFile `
                        -EnvValues @{ DMS_PLUGINS_COMPOSE_FILES = $rooted } `
                        -ScriptRoot 'C:\a-different-root-entirely') |
                    Should -Be @($rooted)
            }
            finally { Remove-Item -LiteralPath $scratch -Recurse -Force }
        }

        It 'keeps the order written rather than the order on disk' {
            $scratch = New-ScratchDirectory
            try {
                foreach ($name in @('alpha.yml', 'beta.yml')) {
                    Set-Content -LiteralPath (Join-Path $scratch $name) -Value 'services: {}'
                }

                # Written second-then-first, so an implementation that sorted or enumerated the
                # directory would produce the other order and fail here.
                @(Resolve-PluginComposeFile `
                        -EnvValues @{ DMS_PLUGINS_COMPOSE_FILES = 'beta.yml;alpha.yml' } `
                        -ScriptRoot $scratch) |
                    Should -Be @((Join-Path $scratch 'beta.yml'), (Join-Path $scratch 'alpha.yml'))
            }
            finally { Remove-Item -LiteralPath $scratch -Recurse -Force }
        }

        It 'trims surrounding whitespace around an entry' {
            $scratch = New-ScratchDirectory
            try {
                Set-Content -LiteralPath (Join-Path $scratch 'one.yml') -Value 'services: {}'

                @(Resolve-PluginComposeFile `
                        -EnvValues @{ DMS_PLUGINS_COMPOSE_FILES = ' one.yml ' } `
                        -ScriptRoot $scratch) |
                    Should -Be @((Join-Path $scratch 'one.yml'))
            }
            finally { Remove-Item -LiteralPath $scratch -Recurse -Force }
        }

        It 'refuses a file that does not exist, naming the resolved path' {
            { Resolve-PluginComposeFile `
                    -EnvValues @{ DMS_PLUGINS_COMPOSE_FILES = 'tests/plugin-deployment/does-not-exist.yml' } `
                    -ScriptRoot $script:composeRoot } |
                Should -Throw 'DMS_PLUGINS_COMPOSE_FILES does not identify a compose file: *does-not-exist.yml'
        }

        It 'refuses a directory, because a compose file is a leaf' {
            { Resolve-PluginComposeFile `
                    -EnvValues @{ DMS_PLUGINS_COMPOSE_FILES = 'tests' } `
                    -ScriptRoot $script:composeRoot } |
                Should -Throw 'DMS_PLUGINS_COMPOSE_FILES does not identify a compose file: *'
        }

        It 'refuses <Case> rather than silently dropping it' -ForEach @(
            @{ Case = 'a trailing separator'; Value = 'plugins-dms.yml;' }
            @{ Case = 'a leading separator'; Value = ';plugins-dms.yml' }
            @{ Case = 'a doubled separator'; Value = 'plugins-dms.yml;;plugins-fetch-dms.yml' }
            @{ Case = 'a whitespace-only entry'; Value = 'plugins-dms.yml; ' }
        ) {
            { Resolve-PluginComposeFile `
                    -EnvValues @{ DMS_PLUGINS_COMPOSE_FILES = $Value } `
                    -ScriptRoot $script:composeRoot } |
                Should -Throw "DMS_PLUGINS_COMPOSE_FILES contains an empty entry: '$Value'"
        }

        It 'refuses the whole list on a bad entry rather than returning the good ones' {
            # The refusal has to be total. A caller that received the first file and then a throw
            # would have a half-built compose set to reason about.
            $scratch = New-ScratchDirectory
            try {
                Set-Content -LiteralPath (Join-Path $scratch 'good.yml') -Value 'services: {}'
                $resolved = $null

                { $resolved = Resolve-PluginComposeFile `
                        -EnvValues @{ DMS_PLUGINS_COMPOSE_FILES = 'good.yml;missing.yml' } `
                        -ScriptRoot $scratch } | Should -Throw

                $resolved | Should -BeNullOrEmpty
            }
            finally { Remove-Item -LiteralPath $scratch -Recurse -Force }
        }

        It 'returns the paths themselves rather than an array nested in one element' {
            # The callers append the result to a compose argument vector. A result wrapped one level
            # too deep survives @() as a single Object[] and reaches Docker as one unusable argument.
            $scratch = New-ScratchDirectory
            try {
                foreach ($name in @('one.yml', 'two.yml')) {
                    Set-Content -LiteralPath (Join-Path $scratch $name) -Value 'services: {}'
                }

                foreach ($value in @('one.yml', 'one.yml;two.yml')) {
                    $resolved = @(Resolve-PluginComposeFile `
                            -EnvValues @{ DMS_PLUGINS_COMPOSE_FILES = $value } `
                            -ScriptRoot $scratch)

                    $resolved.Count | Should -Be $value.Split(';').Count
                    foreach ($entry in $resolved) {
                        $entry | Should -BeOfType [string]
                    }
                }
            }
            finally { Remove-Item -LiteralPath $scratch -Recurse -Force }
        }
    }

    Context 'the committed hook in <_>' -ForEach @('start-local-dms.ps1', 'start-published-dms.ps1') {
        BeforeEach {
            $script:calls = [Collections.Generic.List[string]]::new()
            Mock docker {
                $script:calls.Add((@($args | ForEach-Object { $_ }) -join ' '))
                $global:LASTEXITCODE = 0
            }
        }

        It 'appends every resolved overlay in the order written, after what is already there' {
            $scratch = New-ScratchDirectory
            try {
                foreach ($name in @('acquire.yml', 'allowed.yml')) {
                    Set-Content -LiteralPath (Join-Path $scratch $name) -Value 'services: {}'
                }

                $hook = New-LauncherBlockFile $_ $scratch
                $files = @('-f', 'the-application-file.yml')
                $envValues = @{ DMS_PLUGINS_COMPOSE_FILES = 'acquire.yml;allowed.yml' }

                . $hook

                $files | Should -Be @(
                    '-f', 'the-application-file.yml'
                    '-f', (Join-Path $scratch 'acquire.yml')
                    '-f', (Join-Path $scratch 'allowed.yml')
                )
                $script:calls.Count | Should -Be 0
            }
            finally { Remove-Item -LiteralPath $scratch -Recurse -Force }
        }

        It 'resolves a relative entry against its own directory rather than the caller''s' {
            # The scratch directory stands in for the launcher's directory, so a hook that resolved
            # against the working directory or a literal would not find this file at all.
            $scratch = New-ScratchDirectory
            try {
                Set-Content -LiteralPath (Join-Path $scratch 'beside-the-launcher.yml') -Value 'services: {}'

                $hook = New-LauncherBlockFile $_ $scratch
                $files = @()
                $envValues = @{ DMS_PLUGINS_COMPOSE_FILES = 'beside-the-launcher.yml' }

                . $hook

                $files | Should -Be @('-f', (Join-Path $scratch 'beside-the-launcher.yml'))
            }
            finally { Remove-Item -LiteralPath $scratch -Recurse -Force }
        }

        It 'leaves the compose set exactly as it found it when no overlay is configured' {
            $scratch = New-ScratchDirectory
            try {
                $hook = New-LauncherBlockFile $_ $scratch
                $files = @('-f', 'postgresql.yml', '-f', 'the-application-file.yml')
                $envValues = @{}

                . $hook

                $files | Should -Be @('-f', 'postgresql.yml', '-f', 'the-application-file.yml')
                $script:calls.Count | Should -Be 0
            }
            finally { Remove-Item -LiteralPath $scratch -Recurse -Force }
        }

        It 'fails on a missing overlay without touching Docker or the compose set' {
            $scratch = New-ScratchDirectory
            try {
                $hook = New-LauncherBlockFile $_ $scratch
                $files = @('-f', 'the-application-file.yml')
                $envValues = @{ DMS_PLUGINS_COMPOSE_FILES = 'nope.yml' }

                { . $hook } | Should -Throw 'DMS_PLUGINS_COMPOSE_FILES does not identify a compose file: *nope.yml'

                $files | Should -Be @('-f', 'the-application-file.yml')
                $script:calls.Count | Should -Be 0
            }
            finally { Remove-Item -LiteralPath $scratch -Recurse -Force }
        }

        It 'fails on an empty entry without touching Docker or the compose set' {
            $scratch = New-ScratchDirectory
            try {
                Set-Content -LiteralPath (Join-Path $scratch 'acquire.yml') -Value 'services: {}'

                $hook = New-LauncherBlockFile $_ $scratch
                $files = @('-f', 'the-application-file.yml')
                $envValues = @{ DMS_PLUGINS_COMPOSE_FILES = 'acquire.yml;' }

                { . $hook } | Should -Throw "DMS_PLUGINS_COMPOSE_FILES contains an empty entry: 'acquire.yml;'"

                $files | Should -Be @('-f', 'the-application-file.yml')
                $script:calls.Count | Should -Be 0
            }
            finally { Remove-Item -LiteralPath $scratch -Recurse -Force }
        }

        It 'sits inside the compose-set block, so a database-only startup never reaches it' {
            # Containment rather than a second execution: the block is entered only when
            # $databaseOnlyStartup is false, and the case below fixes what that value is.
            $hookAst = Get-PluginForeachAst $_
            $composeSet = Get-ComposeSetIfAst $_

            $hookAst.Extent.StartOffset | Should -BeGreaterThan $composeSet.Extent.StartOffset
            $hookAst.Extent.EndOffset | Should -BeLessThan $composeSet.Extent.EndOffset
        }

        It 'is reached on a teardown even with -DbOnly, so down covers the overlays'' services' {
            # fetch-plugins, plugin-feed and the plugins volume are declared by the overlays alone.
            # A down that left them out would leak them.
            $block = Get-DatabaseOnlyStartupBlock $_
            $DbOnly = $true
            $d = $true
            $databaseOnlyStartup = $null

            . $block

            $databaseOnlyStartup | Should -BeFalse
        }

        It 'is skipped for a database-only startup' {
            $block = Get-DatabaseOnlyStartupBlock $_
            $DbOnly = $true
            $d = $false
            $databaseOnlyStartup = $null

            . $block

            $databaseOnlyStartup | Should -BeTrue
        }

        It 'validates before the script reaches its first Docker invocation' {
            # Position, measured from the parsed script: the whole compose set is built and every
            # overlay validated before anything is handed to Docker, so a typo fails while the stack
            # is untouched rather than halfway through a recreate.
            $composeSet = Get-ComposeSetIfAst $_
            $firstDocker = (Get-ScriptAst $_).FindAll({
                    param($n) $n -is [Management.Automation.Language.CommandAst] -and
                    $n.GetCommandName() -eq 'docker'
                }, $true) | Sort-Object { $_.Extent.StartOffset } | Select-Object -First 1

            $firstDocker | Should -Not -BeNullOrEmpty
            $composeSet.Extent.EndOffset | Should -BeLessThan $firstDocker.Extent.StartOffset
        }
    }

    # The block-level cases above run the committed statements but not the hundreds of lines of
    # environment resolution ahead of them, so on their own they cannot say that nothing earlier in
    # the script has already mutated something. These run the launcher itself, as a process, with the
    # ordinary environment file, and are the cases that make "before Docker side effects" a measured
    # claim rather than a positional one. They are deliberately only the refusals: a run that got
    # past validation would start a stack.
    Context 'the whole launcher, run as a process' {
        BeforeAll {
            $script:e2eEnvironmentFile = Join-Path $script:composeRoot '.env.e2e'

            # The real CLI, resolved past the no-op `docker` function the block-level cases mock.
            # These cases have to observe the actual container set, so a stub would make the
            # comparison below prove nothing at all.
            $script:dockerCli = (Get-Command docker -CommandType Application -ErrorAction SilentlyContinue |
                    Select-Object -First 1)

            function script:Invoke-Launcher {
                param(
                    [Parameter(Mandatory)] [string] $Name,
                    [Parameter(Mandatory)] [string] $PluginComposeFiles
                )

                $environmentFile = Join-Path ([IO.Path]::GetTempPath()) "dms1502-$([guid]::NewGuid().ToString('N')).env"
                $lines = @(Get-Content -LiteralPath $script:e2eEnvironmentFile)
                $lines += "DMS_PLUGINS_COMPOSE_FILES=$PluginComposeFiles"
                Set-Content -LiteralPath $environmentFile -Value $lines -Encoding utf8

                try {
                    if ($null -eq $script:dockerCli) {
                        throw 'The Docker CLI is not on PATH, so this test cannot tell whether the launcher created a container. It fails rather than skipping, because a silent skip would retire the only measured evidence that validation precedes every side effect.'
                    }

                    $before = @(& $script:dockerCli ps -a --format '{{.Names}}') | Sort-Object
                    if ($LASTEXITCODE -ne 0) {
                        throw 'docker ps failed, so this test cannot tell whether the launcher created a container.'
                    }

                    $output = & pwsh -NoProfile -File (Join-Path $script:composeRoot $Name) -EnvironmentFile $environmentFile 2>&1
                    $exitCode = $LASTEXITCODE
                    $after = @(& $script:dockerCli ps -a --format '{{.Names}}') | Sort-Object

                    return [pscustomobject]@{
                        ExitCode         = $exitCode
                        Output           = ($output | Out-String)
                        ContainersBefore = $before
                        ContainersAfter  = $after
                    }
                }
                finally {
                    Remove-Item -LiteralPath $environmentFile -Force -ErrorAction SilentlyContinue
                }
            }
        }

        It '<Name> refuses <Case> before creating any container' -ForEach @(
            foreach ($name in @('start-local-dms.ps1', 'start-published-dms.ps1')) {
                @{
                    Name    = $name
                    Case    = 'an overlay that does not exist'
                    Value   = 'tests/plugin-deployment/does-not-exist.yml'
                    Message = 'DMS_PLUGINS_COMPOSE_FILES does not identify a compose file'
                }
                @{
                    Name    = $name
                    Case    = 'an empty entry'
                    Value   = 'plugins-dms.yml;'
                    Message = 'DMS_PLUGINS_COMPOSE_FILES contains an empty entry'
                }
            }
        ) {
            $run = Invoke-Launcher -Name $Name -PluginComposeFiles $Value

            $run.ExitCode | Should -Not -Be 0
            # The message is the launcher's own rule, not a raw Docker or Compose error surfacing
            # after something was already attempted.
            $run.Output | Should -Match ([regex]::Escape($Message))
            $run.Output | Should -Not -Match 'Using plugin Docker Compose file'
            # The whole container set, not a name filter: a launcher that had got as far as Compose
            # would leave something behind, and this notices whatever it is.
            Compare-Object $run.ContainersBefore $run.ContainersAfter | Should -BeNullOrEmpty
        }
    }

    Context 'parity between the two launchers' {
        It 'gives both launchers the same hook text, so neither can drift' {
            (Get-PluginForeachAst 'start-local-dms.ps1').Extent.Text |
                Should -BeExactly (Get-PluginForeachAst 'start-published-dms.ps1').Extent.Text
        }

        It 'places the hook after the application file and before the configuration service in both' {
            # Overlay precedence is positional. The published launcher's hook has to sit where the
            # local launcher's does, or the two stacks would compose the same files differently.
            foreach ($name in $script:launcher) {
                $body = (Get-ComposeSetIfAst $name).Extent.Text
                $application = [regex]::Match($body, '"(local-dms\.yml|published-dms\.yml)"').Index
                $hook = $body.IndexOf('Resolve-PluginComposeFile')
                $config = [regex]::Match($body, '"(local-config\.yml|published-config\.yml)"').Index

                $application | Should -BeGreaterThan -1 -Because "$name composes an application file"
                $hook | Should -BeGreaterThan $application -Because "$name composes plugins after the application file"
                $config | Should -BeGreaterThan $hook -Because "$name composes the configuration service after the plugins"
            }
        }
    }
}
