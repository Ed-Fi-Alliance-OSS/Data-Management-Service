# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

# Static contract for the MANUAL restore smoke (Invoke-BootstrapRestoreSmoke.ps1), mirroring
# the BootstrapDockerSmoke.Tests.ps1 idiom: the live script is never run by CI, but its
# surface and fail-closed invariants are pinned here so a syntax error or a weakened
# assertion becomes a PR failure instead of a silently broken manual tool. Everything below
# is raw-text/AST inspection - no Docker.

[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseSingularNouns', '', Justification = 'Test helper intentionally mirrors the established Get-DeclaredScriptParameters name used by the sibling smoke contract suites.')]
param()

Describe "Invoke-BootstrapRestoreSmoke static contract" {
    BeforeAll {
        $script:smokeScriptPath = [System.IO.Path]::GetFullPath(
            (Join-Path $PSScriptRoot "Invoke-BootstrapRestoreSmoke.ps1")
        )

        function script:Get-DeclaredScriptParameters {
            param([string]$Path)

            $tokens = $null
            $errors = $null
            $ast = [System.Management.Automation.Language.Parser]::ParseFile($Path, [ref]$tokens, [ref]$errors)
            if ($errors.Count -gt 0) {
                throw "Failed to parse ${Path}: $($errors[0])"
            }

            return @(
                $ast.ParamBlock.Parameters |
                    ForEach-Object { $_.Name.VariablePath.UserPath } |
                    Select-Object -Unique
            )
        }

        $script:smokeContent = Get-Content -LiteralPath $script:smokeScriptPath -Raw
    }

    Context "Parameter surface" {
        It "declares the smoke parameters" {
            $params = Get-DeclaredScriptParameters -Path $script:smokeScriptPath
            foreach ($expected in @("EnvironmentFile", "DatabaseEngine", "Leg", "PackageVersion", "StandardVersion", "DataStandardVersion", "Wrapper", "SkipSourceSeed", "ResultsPath", "SkipTeardown", "ConfirmForeignStackRemoval", "ExploratoryPackage")) {
                $params | Should -Contain $expected
            }
        }

        It "defaults the leg matrix to the core set including every failure leg" {
            foreach ($legName in @("package-directory", "separate-config", "directory-feed", "tampered-package", "contaminated-package", "running-stack")) {
                $script:smokeContent | Should -Match ([regex]::Escape('"' + $legName + '"'))
            }
        }
    }

    Context "Fail-closed invariants" {
        It "registers an ephemeral dev-trust producer instead of any trust bypass" {
            $script:smokeContent.Contains("new-template-dev-trust.ps1") | Should -BeTrue
            # No bypass surface exists in the restore branch and none may be invented here.
            $script:smokeContent | Should -Not -Match '(?i)skip.?attestation|no.?trust|unsigned'
        }

        It "removes exactly the ephemeral producer from the local overlay in the finally block" {
            $script:smokeContent.Contains('$overlay.producers = @($overlay.producers | Where-Object { [string]$_.name -ne $script:SmokeProducerName })') | Should -BeTrue
        }

        It "the tampered-package leg proves the refusal happened before any Docker activity" {
            # The project label follows -Wrapper (dms-local or dms-published).
            $script:smokeContent.Contains('label=com.docker.compose.project=$($script:WrapperProfile.ComposeProject)') | Should -BeTrue
            $script:smokeContent.Contains("Tampered-package refusal happened AFTER Docker activity") | Should -BeTrue
        }

        It "the contaminated-package leg is PostgreSQL-only and asserts target absence, no generated databases, and no committed workspace" {
            $script:smokeContent.Contains("contaminated-package leg is PostgreSQL-only") | Should -BeTrue
            $script:smokeContent.Contains("exists after a failed scratch validation on a fresh volume") | Should -BeTrue
            $script:smokeContent.Contains("Generated restore databases remain after the failure") | Should -BeTrue
            $script:smokeContent.Contains("An active .bootstrap workspace exists after a pre-commit failure") | Should -BeTrue
            # The refusal must come from the DMS-only gate naming the injected schema.
            $script:smokeContent.Contains("smoke_intruder") | Should -BeTrue
        }

        It "the running-stack leg requires the stop proof's own refusal" {
            $script:smokeContent.Contains("still has running containers") | Should -BeTrue
        }

        It "the separate-config leg proves the dedicated CMS database survives via a pre-planted marker" {
            $script:smokeContent.Contains("restore_smoke_marker") | Should -BeTrue
            $script:smokeContent.Contains("edfi_configurationservice") | Should -BeTrue
        }

        It "the directory-feed leg drives feed resolution through the env keys, not -PackageDirectory" {
            $script:smokeContent.Contains("DATABASE_TEMPLATE_FEED_URL=") | Should -BeTrue
            $script:smokeContent.Contains("DATABASE_TEMPLATE_NUGET_VERSION=") | Should -BeTrue
        }

        It "tears down and cleans transient state in the finally block" {
            $script:smokeContent | Should -Match '(?s)finally \{.*Invoke-SmokeTeardown.*Remove-Item -LiteralPath \$script:WorkDirectory'
        }
    }

    Context "Foreign-stack preflight and provenance" {
        It "inventories foreign stacks and refuses before authorizing the first teardown" {
            $inventoryIndex = $script:smokeContent.IndexOf('$inventory = Get-RestoreSmokeForeignStackInventory')
            $assertIndex = $script:smokeContent.IndexOf('Assert-RestoreSmokeNoForeignStack -Inventory $inventory')
            $authorizeIndex = $script:smokeContent.IndexOf('$script:TeardownAuthorized = $true')
            $workDirectoryIndex = $script:smokeContent.IndexOf('$script:WorkDirectory = Join-Path')
            $firstTeardownCallIndex = $script:smokeContent.IndexOf('Invoke-SmokeTeardown -WrapperProfile (Get-RestoreSmokeWrapperProfile')

            $inventoryIndex | Should -BeGreaterThan 0
            $assertIndex | Should -BeGreaterThan $inventoryIndex
            $authorizeIndex | Should -BeGreaterThan $assertIndex
            $workDirectoryIndex | Should -BeGreaterThan $authorizeIndex
            $firstTeardownCallIndex | Should -BeGreaterThan $authorizeIndex
        }

        It "gates every teardown, including the failure teardown in the finally block, on the preflight authorization" {
            $script:smokeContent.Contains('if (-not $script:TeardownAuthorized) {') | Should -BeTrue
            $script:smokeContent | Should -Match '(?s)finally \{\s*if \(-not \$SkipTeardown -and \$exitCode -ne 0\) \{\s*if \(\$script:TeardownAuthorized\) \{'
        }

        It "runs the wrapper and teardown scripts selected by -Wrapper, never a hard-wired local script" {
            $script:smokeContent.Contains('$($script:WrapperProfile.BootstrapScriptName)') | Should -BeTrue
            $script:smokeContent.Contains('$($WrapperProfile.TeardownScriptName)') | Should -BeTrue
            $script:smokeContent | Should -Not -Match '&\s*"\$script:DockerComposeRoot/(start|bootstrap)-local-dms\.ps1"'
            $script:smokeContent.Contains('Get-RestoreSmokeWrapperArgumentSet') | Should -BeTrue
        }

        It "builds SchemaTools and the images in-run and writes the provenance classification to the results" {
            $script:smokeContent.Contains('Invoke-RestoreSmokeSchemaToolBuild') | Should -BeTrue
            $script:smokeContent.Contains('Invoke-RestoreSmokeImageBuild') | Should -BeTrue
            $script:smokeContent.Contains('Get-RestoreSmokeResultClassification -Provenance $script:Provenance') | Should -BeTrue
            $script:smokeContent | Should -Match 'Classification\s*=\s*\$classification'
        }
    }

    Context "Served-data API probe" {
        BeforeAll {
            function script:Get-SmokeFunctionBody {
                param([string]$Name)

                $tokens = $null
                $errors = $null
                $ast = [System.Management.Automation.Language.Parser]::ParseFile($script:smokeScriptPath, [ref]$tokens, [ref]$errors)
                $definitions = @($ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $true) | Where-Object { $_.Name -eq $Name })
                $definitions.Count | Should -Be 1
                return $definitions[0].Body.Extent.Text
            }
        }

        It "reads served data through the DMS API in every post-restore assertion, recording the result" {
            $body = Get-SmokeFunctionBody -Name "Assert-RestoredDatastore"

            $body.Contains('Resolve-RestoreSmokeApiEndpoint -EnvironmentFile $EnvironmentFile') | Should -BeTrue
            $body | Should -Match '(?s)Test-RestoreSmokeApiRead\s+`\s+-Session \$script:ApiSession\s+`.*-RequirePopulatedData:\$RequirePopulatedData\s+`\s+-SchemaOnly:\$SkipSourceSeed'
            $body.Contains('$script:Provenance.ApiReads.Add($apiRead)') | Should -BeTrue
        }

        It "tags every post-restore assertion with its own restore and the directory it restored from, inside that restore's step" {
            $tokens = $null
            $errors = $null
            $ast = [System.Management.Automation.Language.Parser]::ParseFile($script:smokeScriptPath, [ref]$tokens, [ref]$errors)
            $calls = @($ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.CommandAst] }, $true) | Where-Object { $_.GetCommandName() -eq "Assert-RestoredDatastore" })

            # The step each restore runs in: the leg's own step, and the repeat step for the
            # repeated restore of package-directory.
            $stepNames = @{
                "package-directory#1" = "leg-package-directory"
                "package-directory#2" = "leg-package-directory-repeat"
                "separate-config#1"   = "leg-separate-config"
                "directory-feed#1"    = "leg-directory-feed"
                "extension-selection#1" = "leg-extension-selection"
                "populated#1"         = "leg-populated"
            }
            $restores = foreach ($call in $calls) {
                $elements = @($call.CommandElements)
                $parameterIndex = @(0..($elements.Count - 1) | Where-Object { $elements[$_] -is [System.Management.Automation.Language.CommandParameterAst] -and $elements[$_].ParameterName -eq "RestoreExecution" })
                $parameterIndex.Count | Should -Be 1
                $argumentText = $elements[$parameterIndex[0] + 1].Extent.Text
                $argumentText -match '^\(Get-RestoreSmokeRestoreExecutionId -Leg "(?<leg>[a-z-]+)"( -Ordinal (?<ordinal>\d+))?\)$' | Should -BeTrue -Because $argumentText
                $ordinal = if ($Matches.ContainsKey("ordinal")) { $Matches["ordinal"] } else { "1" }
                $restore = "$($Matches['leg'])#$ordinal"

                $directoryIndex = @(0..($elements.Count - 1) | Where-Object { $elements[$_] -is [System.Management.Automation.Language.CommandParameterAst] -and $elements[$_].ParameterName -eq "PackageDirectory" })
                $directoryIndex.Count | Should -Be 1 -Because "$restore must name the directory it restored from"
                $directoryText = $elements[$directoryIndex[0] + 1].Extent.Text

                # The enclosing step is the restore's own step, and the restore there used that directory.
                $parent = $call.Parent
                while ($null -ne $parent -and -not ($parent -is [System.Management.Automation.Language.CommandAst] -and $parent.GetCommandName() -eq "Invoke-SmokeStep")) {
                    $parent = $parent.Parent
                }
                $parent | Should -Not -BeNullOrEmpty
                $parent.Extent.Text | Should -Match ('^Invoke-SmokeStep -Name "' + [regex]::Escape($stepNames[$restore]) + '"')
                if ($restore -eq "directory-feed#1") {
                    $parent.Extent.Text.Contains("DATABASE_TEMPLATE_FEED_URL=$directoryText") | Should -BeTrue
                }
                else {
                    $parent.Extent.Text | Should -Match ('PackageDirectory\s+=\s+' + [regex]::Escape($directoryText) + '\s')
                }
                $restore
            }

            @($restores) | Should -Be @("package-directory#1", "package-directory#2", "separate-config#1", "directory-feed#1", "extension-selection#1", "populated#1")
            $script:smokeContent.Contains('Assert-RestoredDatastore -RestoreExecution (Get-RestoreSmokeRestoreExecutionId -Leg "directory-feed") -PackageDirectory $packageDirectoryPath -EnvironmentFile $feedEnvironmentFile') | Should -BeTrue
            (Get-SmokeFunctionBody -Name "Assert-RestoredDatastore").Contains('$apiRead | Add-Member -NotePropertyName RestoreExecution -NotePropertyValue $RestoreExecution') | Should -BeTrue
        }

        It "discards the cached DMS token before <function> recreates the stack" -ForEach @(
            @{ Function = "Invoke-SmokeTeardown"; Invocation = '& "$script:DockerComposeRoot/$($WrapperProfile.TeardownScriptName)"' }
            @{ Function = "Invoke-RestoreWrapper"; Invocation = '& "$script:DockerComposeRoot/$($script:WrapperProfile.BootstrapScriptName)"' }
        ) {
            $body = Get-SmokeFunctionBody -Name $Function
            $resetIndex = $body.IndexOf('Reset-RestoreSmokeApiSession -Session $script:ApiSession')
            $invocationIndex = $body.IndexOf($Invocation)

            $resetIndex | Should -BeGreaterThan 0
            $invocationIndex | Should -BeGreaterThan $resetIndex
        }

        It "restores the same package twice in package-directory, around a -KeepVolumes stop and nothing else" {
            $tokens = $null
            $errors = $null
            $ast = [System.Management.Automation.Language.Parser]::ParseFile($script:smokeScriptPath, [ref]$tokens, [ref]$errors)
            $legBlocks = @($ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.IfStatementAst] -and $node.Clauses[0].Item1.Extent.Text -eq '$legSet.Contains("package-directory")' }, $true))
            $legBlocks.Count | Should -Be 1
            $steps = @($legBlocks[0].Clauses[0].Item2.Statements | ForEach-Object { $_.PipelineElements[0] })
            @($steps | ForEach-Object { $_.GetCommandName() }) | Should -Be @("Invoke-SmokeStep", "Invoke-SmokeStep", "Invoke-SmokeStep", "Invoke-SmokeStep")
            @($steps | ForEach-Object { $_.CommandElements[2].Value }) | Should -Be @("leg-package-directory", "leg-package-directory-stop", "leg-package-directory-repeat", "leg-package-directory-teardown")

            $bodies = @($steps | ForEach-Object { $_.CommandElements[4].ScriptBlock.EndBlock })
            $bodies[1].Extent.Text.Trim() | Should -BeExactly "Invoke-SmokeTeardown -KeepVolumes"
            $bodies[3].Extent.Text.Trim() | Should -BeExactly "Invoke-SmokeTeardown"

            # Each restore step runs exactly one wrapper restore (which discards the DMS token) and
            # one assertion, and no teardown; both restores pass identical arguments.
            $restoreArguments = foreach ($index in @(0, 2)) {
                $commands = @($bodies[$index].FindAll({ param($node) $node -is [System.Management.Automation.Language.CommandAst] }, $true) | ForEach-Object { $_.GetCommandName() })
                @($commands | Where-Object { $_ -eq "Invoke-RestoreWrapper" }).Count | Should -Be 1
                @($commands | Where-Object { $_ -eq "Assert-RestoredDatastore" }).Count | Should -Be 1
                @($commands | Where-Object { $_ -eq "Invoke-SmokeTeardown" }) | Should -BeNullOrEmpty
                $wrapper = @($bodies[$index].FindAll({ param($node) $node -is [System.Management.Automation.Language.CommandAst] -and $node.GetCommandName() -eq "Invoke-RestoreWrapper" }, $true))[0]
                $wrapper.CommandElements[2].Extent.Text
            }
            $restoreArguments[0] | Should -BeExactly $restoreArguments[1]
            $restoreArguments[0] | Should -Match 'RestoreTemplate\s+=\s+"Minimal"'
            $restoreArguments[0] | Should -Match 'PackageDirectory\s+=\s+\$packageDirectoryPath'
        }

        It "proves the SourceIdentity in every post-restore assertion, before the API read, and records it under the restore" {
            $assertBody = Get-SmokeFunctionBody -Name "Assert-RestoredDatastore"
            $identityIndex = $assertBody.IndexOf('Assert-RestoredSourceIdentity -RestoreExecution $RestoreExecution -PackageFixture $fixture.Name -PackageDirectory $PackageDirectory')
            $apiIndex = $assertBody.IndexOf('Test-RestoreSmokeApiRead')
            $identityIndex | Should -BeGreaterThan 0
            $apiIndex | Should -BeGreaterThan $identityIndex

            $identityBody = Get-SmokeFunctionBody -Name "Assert-RestoredSourceIdentity"
            $identityBody | Should -Match '(?s)\$script:Provenance\.RestoredIdentities\.Add\(\[pscustomobject\]@\{\s+RestoreExecution = \$RestoreExecution\s+PackageFixture\s+= \$fixture\.Name\s+TemplateKind\s+= \$fixture\.TemplateKind.*PackageSha256\s+= \$packageSha256.*Rows\s+= \$targetRead\.Rows'
            $recordIndex = $identityBody.IndexOf('$script:Provenance.RestoredIdentities.Add(')
            $inspectionIndex = $identityBody.IndexOf('Invoke-RestoreSmokePackageInspection')
            $defectIndex = $identityBody.IndexOf('Get-RestoreSmokeRestoredIdentityDefect -Row $targetRead.Rows -PackageIdentity $packageInspection.Identity -EarlierRestore $earlierRestores')
            $recordIndex | Should -BeGreaterThan 0
            $inspectionIndex | Should -BeGreaterThan $recordIndex
            $defectIndex | Should -BeGreaterThan $inspectionIndex
            # Earlier restores are taken before this restore's record is added.
            $identityBody.IndexOf('$earlierRestores = $script:Provenance.RestoredIdentities.ToArray()') | Should -BeLessThan $recordIndex
            # The inspected identity must equal the capture bound to the same package hash.
            $identityBody.Contains('$bindings[0].BeforeBackup.Identity -cne $packageInspection.Identity') | Should -BeTrue
        }

        It "binds the source's SourceIdentity around the producer build, per package fixture" {
            $buildBody = Get-SmokeFunctionBody -Name "Build-SmokeSourceAndPackage"
            $buildBody | Should -Match '(?s)Invoke-RestoreSmokeIdentityBoundPackageBuild\s+`.*-BindingList \$script:Provenance\.SourceIdentityBindings\s+`\s+-PackageList \$script:Provenance\.Packages\s+`\s+-BuildPackage \{.*Build-TemplateNuGetPackage'
            $buildBody.Contains('$packageDirectory = Get-SmokePackageDirectory -PackageFixture $fixture.Name') | Should -BeTrue
            $buildBody.Contains('-PackageFixture $fixture.Name `') | Should -BeTrue
            $buildBody.Contains('$sourceEnvironmentFile = Get-SmokeSelectionEnvironmentFile -Selection $fixture.Selection') | Should -BeTrue
            $buildBody.Contains('$sourceArgs = @{ EnvironmentFile = $sourceEnvironmentFile;') | Should -BeTrue
            (Get-SmokeFunctionBody -Name "Get-SmokePackageDirectory").Contains('(Get-RestoreSmokePackageFixture -Name $PackageFixture).DirectoryName') | Should -BeTrue
            $script:smokeContent | Should -Not -Match 'SourceIdentityReason'
        }

        It "never issues or requests the reseed (<file>)" -ForEach @(
            @{ File = "Invoke-BootstrapRestoreSmoke.ps1" }
            @{ File = "RestoreSmokeProbes.psm1" }
        ) {
            $content = Get-Content -LiteralPath (Join-Path $PSScriptRoot $File) -Raw
            foreach ($forbidden in @("Get-SourceIdentityReseedSql", "Invoke-RestoredDataStoreIdentitySourceIdentityReseed", "gen_random_uuid", "NEWID(")) {
                $content.Contains($forbidden) | Should -BeFalse -Because "$File must not contain $forbidden"
            }
        }

        It "never sets the CMS per-client bearer-token limit (<file>)" -ForEach @(
            @{ File = "Invoke-BootstrapRestoreSmoke.ps1" }
            @{ File = "RestoreSmokeProbes.psm1" }
        ) {
            $content = Get-Content -LiteralPath (Join-Path $PSScriptRoot $File) -Raw
            $content.Contains("DMS_CONFIG_IDENTITY_BEARER_TOKEN_PER_CLIENT_LIMIT") | Should -BeFalse
        }
    }

    Context "Extension selection" {
        BeforeAll {
            $tokens = $null
            $errors = $null
            $script:smokeAst = [System.Management.Automation.Language.Parser]::ParseFile($script:smokeScriptPath, [ref]$tokens, [ref]$errors)

            function script:Get-SmokeFunctionText {
                param([string]$Name)

                $definitions = @($script:smokeAst.FindAll({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $true) | Where-Object { $_.Name -eq $Name })
                $definitions.Count | Should -Be 1
                return $definitions[0].Body.Extent.Text
            }

            function script:Get-LegStep {
                # The Invoke-SmokeStep commands directly inside a leg's if block, in order.
                param([string]$Leg)

                $condition = '$legSet.Contains("' + $Leg + '")'
                $legBlocks = @($script:smokeAst.FindAll({ param($node) $node -is [System.Management.Automation.Language.IfStatementAst] -and $node.Clauses[0].Item1.Extent.Text -eq $condition }, $true))
                $legBlocks.Count | Should -Be 1
                return [pscustomobject]@{
                    Text  = $legBlocks[0].Clauses[0].Item2.Extent.Text
                    Steps = @($legBlocks[0].Clauses[0].Item2.Statements | Where-Object { $_ -is [System.Management.Automation.Language.PipelineAst] } | ForEach-Object { $_.PipelineElements[0] } | Where-Object { $_ -is [System.Management.Automation.Language.CommandAst] -and $_.GetCommandName() -eq "Invoke-SmokeStep" })
                }
            }
        }

        It "keeps extension-selection out of the default leg set and checks the leg selection before any Docker call" {
            $legParameter = @($script:smokeAst.ParamBlock.Parameters | Where-Object { $_.Name.VariablePath.UserPath -eq "Leg" })[0]
            $legParameter.DefaultValue.Extent.Text | Should -Not -Match 'extension-selection'
            $legParameter.Attributes[0].Extent.Text | Should -Match '"extension-selection"'

            $preflight = @($script:smokeAst.FindAll({ param($node) $node -is [System.Management.Automation.Language.CommandAst] -and $node.GetCommandName() -eq "Invoke-SmokeStep" -and $node.CommandElements[2].Value -eq "preflight" }, $true))
            $preflight.Count | Should -Be 1
            $body = $preflight[0].CommandElements[4].ScriptBlock.EndBlock.Extent.Text
            $selectionIndex = $body.IndexOf('Assert-RestoreSmokeLegSelection -Leg $Leg -Wrapper $Wrapper -DataStandardVersionSupplied $script:DataStandardVersionSupplied')
            $selectionIndex | Should -BeGreaterThan -1
            $body.IndexOf('docker info') | Should -BeGreaterThan $selectionIndex
        }

        It "derives the core-only env from the image env and builds its own fixture before the refusal and the restore" {
            $leg = Get-LegStep -Leg "extension-selection"
            @($leg.Steps | ForEach-Object { $_.CommandElements[2].Value }) | Should -Be @("write-core-only-environment", "leg-extension-selection-mismatch", "leg-extension-selection", "leg-extension-selection-teardown")
            $leg.Text.Contains('Write-RestoreSmokeCoreOnlyEnvironmentFile -BaseEnvironmentFile $script:ResolvedEnvironmentFile -TargetPath $coreOnlyEnvironmentFile') | Should -BeTrue
            $leg.Text.Contains('$script:Provenance.SelectionEnvironments.Add($selectionEnvironment)') | Should -BeTrue
            $buildIndex = $leg.Text.IndexOf('Build-SmokeSourceAndPackage -PackageFixture "core-only-minimal"')
            $buildIndex | Should -BeGreaterThan $leg.Text.IndexOf('"write-core-only-environment"')
            $leg.Text.IndexOf('"leg-extension-selection-mismatch"') | Should -BeGreaterThan $buildIndex
            $leg.Text.Contains('$coreOnlyPackageDirectory = Get-SmokePackageDirectory -PackageFixture "core-only-minimal"') | Should -BeTrue
        }

        It "refuses the default package under the core-only env, and restores the core-only package under it" {
            $leg = Get-LegStep -Leg "extension-selection"
            $mismatch = $leg.Steps[1].CommandElements[4].ScriptBlock.EndBlock.Extent.Text.Trim()
            $mismatch | Should -BeExactly 'Assert-SmokeSelectionRefusal -PackageDirectory $packageDirectoryPath -EnvironmentFile (Get-SmokeSelectionEnvironmentFile -Selection "core-only")'
            $script:smokeContent.Contains('$packageDirectoryPath = Get-SmokePackageDirectory -PackageFixture "default-minimal"') | Should -BeTrue

            $restore = $leg.Steps[2].CommandElements[4].ScriptBlock.EndBlock
            $commands = @($restore.FindAll({ param($node) $node -is [System.Management.Automation.Language.CommandAst] }, $true) | ForEach-Object { $_.GetCommandName() })
            @($commands | Where-Object { $_ -eq "Invoke-RestoreWrapper" }).Count | Should -Be 1
            @($commands | Where-Object { $_ -eq "Assert-RestoredDatastore" }).Count | Should -Be 1
            @($commands | Where-Object { $_ -eq "Invoke-SmokeTeardown" }) | Should -BeNullOrEmpty
            $restore.Extent.Text | Should -Match 'EnvironmentFile\s+=\s+\(Get-SmokeSelectionEnvironmentFile -Selection "core-only"\)'
            $restore.Extent.Text | Should -Match 'RestoreTemplate\s+=\s+"Minimal"'
            $restore.Extent.Text | Should -Match 'PackageDirectory\s+=\s+\$coreOnlyPackageDirectory\s'
            $restore.Extent.Text | Should -Match '-EnvironmentFile \(Get-SmokeSelectionEnvironmentFile -Selection "core-only"\)'
            $leg.Steps[3].CommandElements[4].ScriptBlock.EndBlock.Extent.Text.Trim() | Should -BeExactly "Invoke-SmokeTeardown"
        }

        It "records the refusal and the state around it before the refused restore is judged" {
            $body = Get-SmokeFunctionText -Name "Assert-SmokeSelectionRefusal"
            $recordIndex = $body.IndexOf('$script:Provenance.SelectionRefusals.Add($refusal)')
            $beforeIndex = $body.IndexOf('$refusal.Before = Get-RestoreSmokeRefusalState @stateArguments')
            $wrapperIndex = $body.IndexOf('Invoke-RestoreWrapper -Arguments')
            $afterIndex = $body.IndexOf('$refusal.After = Get-RestoreSmokeRefusalState @stateArguments')
            $judgeIndex = $body.IndexOf('Get-RestoreSmokeSelectionRefusalDefect -Refusal $refusal')
            $recordIndex | Should -BeGreaterThan 0
            $beforeIndex | Should -BeGreaterThan $recordIndex
            $wrapperIndex | Should -BeGreaterThan $beforeIndex
            $afterIndex | Should -BeGreaterThan $wrapperIndex
            $judgeIndex | Should -BeGreaterThan $afterIndex
            $body | Should -Match 'RestoreTemplate\s+=\s+"Minimal"'
            $body.Contains('$stateArguments = @{ ComposeProject = $script:WrapperProfile.ComposeProject; BootstrapRoot = $script:BootstrapRoot; RestoreWorkspaceRoot = $script:RestoreWorkspaceRoot }') | Should -BeTrue
        }

        It "proves a non-default selection in the post-restore assertion, after the SourceIdentity and before the API read" {
            $assertBody = Get-SmokeFunctionText -Name "Assert-RestoredDatastore"
            $assertBody.Contains('$fixture = Get-RestoreSmokeRestoreExecutionFixture -RestoreExecution $RestoreExecution') | Should -BeTrue
            $identityIndex = $assertBody.IndexOf('Assert-RestoredSourceIdentity -RestoreExecution')
            $selectionIndex = $assertBody.IndexOf('Assert-RestoredSelection -RestoreExecution $RestoreExecution -PackageFixture $fixture.Name -PackageDirectory $PackageDirectory')
            $apiIndex = $assertBody.IndexOf('Test-RestoreSmokeApiRead')
            $selectionIndex | Should -BeGreaterThan $identityIndex
            $apiIndex | Should -BeGreaterThan $selectionIndex
            $assertBody | Should -Match '(?s)if \(\$fixture\.Selection -ne "default"\) \{\s+Assert-RestoredSelection'

            $selectionBody = Get-SmokeFunctionText -Name "Assert-RestoredSelection"
            $recordIndex = $selectionBody.IndexOf('$script:Provenance.SelectionProofs.Add($proof)')
            $judgeIndex = $selectionBody.IndexOf('Get-RestoreSmokeSelectionDefect -Proof $proof')
            $recordIndex | Should -BeGreaterThan 0
            $judgeIndex | Should -BeGreaterThan $recordIndex
            $selectionBody.Contains('Read-RestoreSmokeWorkspaceSelection -BootstrapRoot $script:BootstrapRoot') | Should -BeTrue
            $selectionBody.Contains('Get-RestoreSmokeCatalogSelection -DatabaseEngine $DatabaseEngine -DatabaseName $script:TargetDatabaseName') | Should -BeTrue
        }
    }

    Context "Staged-selection evidence" {
        BeforeAll {
            function script:Get-StagedSelectionFunctionBody {
                param([string]$Name)

                $tokens = $null
                $errors = $null
                $ast = [System.Management.Automation.Language.Parser]::ParseFile($script:smokeScriptPath, [ref]$tokens, [ref]$errors)
                $definitions = @($ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $true) | Where-Object { $_.Name -eq $Name })
                $definitions.Count | Should -Be 1
                return $definitions[0].Body.Extent.Text
            }
        }

        It "never reads a derived env file, which a restore does not leave" {
            $script:smokeContent | Should -Not -Match '\.env\.derived'
            $script:smokeContent.Contains("EffectiveSchemaPackage") | Should -BeFalse
        }

        It "records the stack start before the wrapper runs" {
            $body = Get-StagedSelectionFunctionBody -Name "Invoke-RestoreWrapper"
            $recordIndex = $body.IndexOf('$script:CurrentStackStart = New-RestoreSmokeStackStart')
            $addIndex = $body.IndexOf('$script:Provenance.StackStarts.Add($script:CurrentStackStart)')
            $invocationIndex = $body.IndexOf('& "$script:DockerComposeRoot/$($script:WrapperProfile.BootstrapScriptName)"')

            $recordIndex | Should -BeGreaterThan 0
            $addIndex | Should -BeGreaterThan $recordIndex
            $invocationIndex | Should -BeGreaterThan $addIndex
        }

        It "unbinds the stack start before every teardown" {
            $body = Get-StagedSelectionFunctionBody -Name "Invoke-SmokeTeardown"
            $clearIndex = $body.IndexOf('$script:CurrentStackStart = $null')

            $clearIndex | Should -BeGreaterThan 0
            $body.IndexOf('& "$script:DockerComposeRoot/$($WrapperProfile.TeardownScriptName)"') | Should -BeGreaterThan $clearIndex
        }

        It "reads each observation's staged selection from the workspace and binds it to the current stack start" {
            $body = Get-StagedSelectionFunctionBody -Name "Add-SmokeStackObservation"

            $body.Contains('Read-RestoreSmokeStagedSelection -BootstrapRoot $script:BootstrapRoot') | Should -BeTrue
            $body.Contains('$stackStart = $script:CurrentStackStart.StackStart') | Should -BeTrue
            $body.Contains('-NotePropertyName StackStart -NotePropertyValue $stackStart') | Should -BeTrue
        }
    }

    Context "In-run image provenance" {
        BeforeAll {
            $script:repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../.."))
            $script:probesModulePath = Join-Path $PSScriptRoot "RestoreSmokeProbes.psm1"

            function script:Get-ScriptAst {
                param([string]$Path)

                $tokens = $null
                $errors = $null
                $ast = [System.Management.Automation.Language.Parser]::ParseFile($Path, [ref]$tokens, [ref]$errors)
                if ($errors.Count -gt 0) {
                    throw "Failed to parse ${Path}: $($errors[0])"
                }
                return $ast
            }
        }

        It "never runs a build script's DockerBuild, docker tag, or a forced image removal (<file>)" -ForEach @(
            @{ File = "Invoke-BootstrapRestoreSmoke.ps1" }
            @{ File = "RestoreSmokeProbes.psm1" }
        ) {
            $ast = Get-ScriptAst -Path (Join-Path $PSScriptRoot $File)
            $commands = @($ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.CommandAst] }, $true))
            $commands.Count | Should -BeGreaterThan 0
            foreach ($command in $commands) {
                $elements = @($command.CommandElements | ForEach-Object { $_.Extent.Text })
                $elements | Should -Not -Contain "DockerBuild"
                ($elements -join " ") | Should -Not -Match 'build-(dms|config)\.ps1'
                if ($command.GetCommandName() -eq "docker") {
                    $elements[1] | Should -Not -BeIn @("tag", "rmi", "prune", "system")
                    $elements | Should -Not -Contain "-f"
                    $elements | Should -Not -Contain "--force"
                }
            }
        }

        It "builds <key> exactly as <script> DockerBuild does: source directory, Dockerfile, contexts, and the VERSION default" -ForEach @(
            @{ Key = "Dms"; Script = "build-dms.ps1"; VersionParameter = "DMSVersion"; SourceDirectory = "src/dms" }
            @{ Key = "Config"; Script = "build-config.ps1"; VersionParameter = "DmsCSVersion"; SourceDirectory = "src/config" }
        ) {
            # Drift guard: if a build script changes how it builds the image, the smoke's own
            # command (Get-RestoreSmokeImageBuildPlan) must change with it.
            $ast = Get-ScriptAst -Path (Join-Path $script:repoRoot $Script)
            $versionParameterAst = @($ast.ParamBlock.Parameters | Where-Object { $_.Name.VariablePath.UserPath -eq $VersionParameter })
            $versionParameterAst.Count | Should -Be 1
            $versionParameterAst[0].DefaultValue | Should -BeOfType [System.Management.Automation.Language.StringConstantExpressionAst]
            $versionDefault = $versionParameterAst[0].DefaultValue.Value
            $versionDefault | Should -Match '^\d+\.\d+\.\d+$'

            $dockerBuild = @($ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq "DockerBuild" }, $true))
            $dockerBuild.Count | Should -Be 1
            $body = $dockerBuild[0].Body.Extent.Text
            $body | Should -Match ('Push-Location\s+' + [regex]::Escape("$SourceDirectory/"))
            $body | Should -Match 'docker buildx build\b[^\r\n]*-f Dockerfile \. --build-context parentdir=\.\./'
            $body | Should -Match ('"VERSION=\$' + $VersionParameter + '"')

            Import-Module $script:probesModulePath -Force
            $plan = @(Get-RestoreSmokeImageBuildPlan -RepoRoot $TestDrive -WorkDirectory $TestDrive -RunId "0123456789ab" -Wrapper local | Where-Object { $_.Key -eq $Key })
            $plan.Count | Should -Be 1
            $plan[0].SourceDirectory | Should -Be $SourceDirectory
            ($plan[0].Arguments -join " ") | Should -BeLike "buildx build --load --iidfile * -f Dockerfile . --build-context parentdir=../ --build-arg VERSION=$versionDefault"
        }

        It "records the image ledger before the build step and removes only owned run tags after the failure teardown" {
            $script:smokeContent | Should -Match 'Images\s+=\s+\(New-RestoreSmokeImageLedger\)'
            $script:smokeContent.Contains('Register-RestoreSmokeImageTagOwnership -Plan $imagePlan -Ledger $images') | Should -BeTrue
            $script:smokeContent | Should -Match '(?s)finally \{.*Invoke-SmokeTeardown.*Remove-RestoreSmokeOwnedImageTag -Ledger \$script:Provenance\.Images -Keep:\$SkipTeardown.*Remove-Item -LiteralPath \$script:WorkDirectory'
        }
    }
}
