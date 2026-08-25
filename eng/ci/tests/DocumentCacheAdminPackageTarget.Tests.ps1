# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

Describe "DocumentCacheAdmin package target" {
    BeforeAll {
        $script:repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../.."))
        $script:buildScriptPath = Join-Path $script:repoRoot "build-dms.ps1"

        $parseErrors = $null
        $script:buildScriptAst = [System.Management.Automation.Language.Parser]::ParseFile(
            $script:buildScriptPath, [ref]$null, [ref]$parseErrors
        )

        if (@($parseErrors).Count -gt 0) {
            throw "Failed to parse '$script:buildScriptPath': $(@($parseErrors)[0].Message)"
        }

        function Get-ScriptFunctionAst {
            param(
                [string]
                $FunctionName
            )

            $functionAst = $script:buildScriptAst.FindAll(
                { param($node)
                    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
                    $node.Name -eq $FunctionName },
                $true
            ) | Select-Object -First 1

            if ($null -eq $functionAst) {
                throw "Function '$FunctionName' was not found in '$script:buildScriptPath'."
            }

            return $functionAst
        }

        function Get-InvokedCommandName {
            param(
                [System.Management.Automation.Language.Ast]
                $FunctionAst
            )

            return @(
                $FunctionAst.FindAll(
                    { param($node) $node -is [System.Management.Automation.Language.CommandAst] },
                    $true
                ) |
                    ForEach-Object { $_.GetCommandName() } |
                    Where-Object { $_ }
            )
        }

        function Get-BuildPackageSwitchCommandMap {
            $switchAst = (Get-ScriptFunctionAst -FunctionName "BuildPackage").FindAll(
                { param($node) $node -is [System.Management.Automation.Language.SwitchStatementAst] },
                $true
            ) | Select-Object -First 1

            if ($null -eq $switchAst) {
                throw "BuildPackage does not contain a switch statement."
            }

            $commandsByTarget = @{}

            foreach ($clause in $switchAst.Clauses) {
                $label = $clause.Item1.Value
                $commandsByTarget[$label] = @(
                    $clause.Item2.FindAll(
                        { param($node) $node -is [System.Management.Automation.Language.CommandAst] },
                        $true
                    ) |
                        ForEach-Object { $_.GetCommandName() } |
                        Where-Object { $_ }
                )
            }

            return $commandsByTarget
        }
    }

    It "recognizes DocumentCacheAdmin as a public PackageTarget value" {
        $packageTargetParameter = $script:buildScriptAst.ParamBlock.Parameters |
            Where-Object { $_.Name.VariablePath.UserPath -eq "PackageTarget" } |
            Select-Object -First 1

        $packageTargetParameter | Should -Not -BeNullOrEmpty

        $validateSet = $packageTargetParameter.Attributes |
            Where-Object { $_.TypeName.Name -eq "ValidateSet" } |
            Select-Object -First 1

        $validateSet | Should -Not -BeNullOrEmpty

        $targetValues = @($validateSet.PositionalArguments | ForEach-Object { $_.Value })
        $targetValues | Should -Be @("All", "Api", "SchemaTools", "CustomValidation", "DocumentCacheAdmin")
    }

    It "dispatches PackageTarget All to every package builder" {
        $commandsByTarget = Get-BuildPackageSwitchCommandMap

        $commandsByTarget["All"] | Should -Be @(
            "BuildApiPackage",
            "BuildSchemaToolsPackage",
            "BuildCustomValidationPackage",
            "BuildDocumentCacheAdminPackage"
        )
    }

    It "dispatches each single package target to exactly its package builder" {
        $commandsByTarget = Get-BuildPackageSwitchCommandMap

        $commandsByTarget["Api"] | Should -Be @("BuildApiPackage")
        $commandsByTarget["SchemaTools"] | Should -Be @("BuildSchemaToolsPackage")
        $commandsByTarget["CustomValidation"] | Should -Be @("BuildCustomValidationPackage")
        $commandsByTarget["DocumentCacheAdmin"] | Should -Be @("BuildDocumentCacheAdminPackage")
    }

    It "preserves the existing API package builder behavior" {
        $builder = Get-ScriptFunctionAst -FunctionName "BuildApiPackage"
        $builderCommands = Get-InvokedCommandName -FunctionAst $builder

        $builderCommands | Should -Contain "RunNuGetPack"
        $builderCommands | Should -Not -Contain "DotNetClean"
        $builderCommands | Should -Not -Contain "Restore"
        $builderCommands | Should -Not -Contain "Compile"
        $builderCommands | Should -Not -Contain "PublishApi"
        $builderCommands | Should -Not -Contain "PublishCliApiDownloader"
        $builder.Extent.Text | Should -Not -BeLike '*$schemaDownloaderProjectName/publish*'
    }

    It "preserves the existing SchemaTools no-build package builder behavior" {
        $builder = Get-ScriptFunctionAst -FunctionName "BuildSchemaToolsPackage"

        $builder.Extent.Text | Should -Not -BeLike '*dotnet restore*'
        $builder.Extent.Text | Should -BeLike '*dotnet pack $projectPath*'
        $builder.Extent.Text | Should -BeLike '*--no-build*'
        $builder.Extent.Text | Should -BeLike '*--no-restore*'
    }

    It "packs the DocumentCacheAdmin tool project as EdFi.Api.DocumentCacheAdmin" {
        $builder = Get-ScriptFunctionAst -FunctionName "BuildDocumentCacheAdminPackage"
        $builderCommands = Get-InvokedCommandName -FunctionAst $builder

        $builderCommands | Should -Contain "dotnet"
        $builder.Extent.Text | Should -BeLike '*$clisRoot/$documentCacheAdminProjectName/$documentCacheAdminProjectName.csproj*'
        $builder.Extent.Text | Should -BeLike '*$PSScriptRoot/$documentCacheAdminPackageName.$DMSVersion.nupkg*'
        $builder.Extent.Text | Should -BeLike '*dotnet restore $projectPath*'
        $builder.Extent.Text | Should -BeLike '*dotnet pack $projectPath*'
        $builder.Extent.Text | Should -BeLike '*-p:PackageVersion=$DMSVersion*'
    }

    It "passes the caller-selected package file through the existing Push command" {
        $pushPackage = Get-ScriptFunctionAst -FunctionName "PushPackage"

        $pushPackage.Extent.Text | Should -BeLike '*dotnet nuget push $PackageFile --api-key $NuGetApiKey --source $EdFiNuGetFeed*'
    }
}
