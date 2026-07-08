# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', '', Justification = 'Pester stubs intentionally keep production-compatible signatures.')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseSingularNouns', '', Justification = 'Pester stubs intentionally shadow production plural-noun helpers.')]
param()

Describe "DMS-1154 Invoke-BootstrapDockerSmoke static contract" {
    BeforeAll {
        $script:smokeScriptPath = [System.IO.Path]::GetFullPath(
            (Join-Path $PSScriptRoot "Invoke-BootstrapDockerSmoke.ps1")
        )

        function script:Get-DeclaredScriptParameters {
            param([string]$Path)

            $tokens = $null
            $errors = $null
            $ast = [System.Management.Automation.Language.Parser]::ParseFile($Path, [ref]$tokens, [ref]$errors)
            if ($errors.Count -gt 0) {
                $firstError = $errors[0]
                throw "Failed to parse ${Path}: $firstError"
            }

            return @(
                $ast.ParamBlock.Parameters |
                    ForEach-Object { $_.Name.VariablePath.UserPath } |
                    Select-Object -Unique
            )
        }

        $script:smokeContent = Get-Content -LiteralPath $script:smokeScriptPath -Raw
    }

    # =========================================================================
    # Parameter surface
    # =========================================================================
    Context "Parameter surface" {
        It "declares -SchemaPackageId with default EdFi.DataStandard52.ApiSchema" {
            $params = Get-DeclaredScriptParameters -Path $script:smokeScriptPath
            $params | Should -Contain "SchemaPackageId"
            $script:smokeContent | Should -Match 'SchemaPackageId\s*=\s*"EdFi\.DataStandard52\.ApiSchema"'
        }

        It "declares -SchemaPackageVersion with default 1.0.333" {
            $params = Get-DeclaredScriptParameters -Path $script:smokeScriptPath
            $params | Should -Contain "SchemaPackageVersion"
            $script:smokeContent | Should -Match 'SchemaPackageVersion\s*=\s*"1\.0\.332"'
        }

        It "declares -SchemaPackageFeedUrl pointing at the Ed-Fi Alliance OSS feed" {
            $params = Get-DeclaredScriptParameters -Path $script:smokeScriptPath
            $params | Should -Contain "SchemaPackageFeedUrl"
            $script:smokeContent | Should -Match 'ed-fi-alliance.*EdFi.*nuget/v3/index\.json'
        }

        It "declares -ApiSchemaPath falling back to DMS_SMOKE_API_SCHEMA_PATH" {
            $params = Get-DeclaredScriptParameters -Path $script:smokeScriptPath
            $params | Should -Contain "ApiSchemaPath"
            $script:smokeContent | Should -Match 'ApiSchemaPath\s*=\s*\$env:DMS_SMOKE_API_SCHEMA_PATH'
        }

        It "declares -SkipTeardown" {
            $params = Get-DeclaredScriptParameters -Path $script:smokeScriptPath
            $params | Should -Contain "SkipTeardown"
        }

        It "declares -ResultsPath" {
            $params = Get-DeclaredScriptParameters -Path $script:smokeScriptPath
            $params | Should -Contain "ResultsPath"
        }

        It "declares -EnvironmentFile" {
            $params = Get-DeclaredScriptParameters -Path $script:smokeScriptPath
            $params | Should -Contain "EnvironmentFile"
        }
    }

    # =========================================================================
    # Default mode: package download when ApiSchemaPath is absent
    # =========================================================================
    Context "Default package-download mode" {
        It "contains download-core-apischema-package step name" {
            $script:smokeContent | Should -Match 'download-core-apischema-package'
        }

        It "resolves PackageBaseAddress from the v3 feed index before falling back to flat2 URL" {
            $script:smokeContent | Should -Match 'PackageBaseAddress/3\.0\.0'
            $script:smokeContent | Should -Match '/v3/flat2/'
        }

        It "downloads only when ApiSchemaPath is not supplied" {
            # The download step must be conditional on an empty ApiSchemaPath
            $script:smokeContent | Should -Match 'IsNullOrWhiteSpace.*ApiSchemaPath'
        }

        It "sets EffectiveApiSchemaPath from the download path" {
            $script:smokeContent | Should -Match 'EffectiveApiSchemaPath\s*=\s*\$apiSchemaDir'
        }

        It "sets EffectiveApiSchemaPath from caller-supplied ApiSchemaPath when provided" {
            $script:smokeContent | Should -Match 'EffectiveApiSchemaPath\s*=\s*\$ApiSchemaPath'
        }

        It "asserts ApiSchema.json exists inside the extracted package" {
            $script:smokeContent | Should -Match 'ApiSchema\.json'
        }

        It "reports the count of xsd files found in the package" {
            $script:smokeContent | Should -Match 'xsd.*file'
        }

        It "downloads to a temp directory stored in PackageDownloadTempDir" {
            $script:smokeContent | Should -Match 'PackageDownloadTempDir\s*=\s*\$tempDir'
        }
    }

    # =========================================================================
    # Temp directory cleanup
    # =========================================================================
    Context "Temp directory cleanup" {
        It "removes PackageDownloadTempDir in the finally block regardless of SkipTeardown" {
            # The cleanup must happen in the finally block after SkipTeardown handling
            $finallyIndex = $script:smokeContent.IndexOf('finally {')
            $cleanupIndex = $script:smokeContent.IndexOf('PackageDownloadTempDir', $finallyIndex)
            $cleanupIndex | Should -BeGreaterThan $finallyIndex -Because "temp dir cleanup must be in the finally block"
        }

        It "uses Remove-Item to clean up the temp directory" {
            $script:smokeContent | Should -Match 'Remove-Item.*PackageDownloadTempDir'
        }
    }

    # =========================================================================
    # No longer throws for missing ApiSchemaPath
    # =========================================================================
    Context "ApiSchemaPath guard removed" {
        It "does not throw 'ApiSchemaPath is required' unconditionally" {
            # The old unconditional throw must not appear; the download step replaces it.
            $script:smokeContent | Should -Not -Match 'throw.*ApiSchemaPath is required'
        }

        It "uses EffectiveApiSchemaPath in the prepare-dms-schema step" {
            $script:smokeContent | Should -Match 'EffectiveApiSchemaPath'
        }
    }

    # =========================================================================
    # Log sanitization
    # =========================================================================
    Context "Log sanitization" {
        It "defines Format-LogSafeText in the script body" {
            $script:smokeContent | Should -Match 'function Format-LogSafeText'
        }

        It "uses Format-LogSafeText when logging the environment file path" {
            $script:smokeContent | Should -Match 'Format-LogSafeText.*resolvedEnvFile'
        }

        It "uses Format-LogSafeText when logging the package download URL" {
            $script:smokeContent | Should -Match 'Format-LogSafeText.*downloadUrl'
        }
    }

    # =========================================================================
    # Script parses without errors
    # =========================================================================
    Context "Script parse validity" {
        It "parses without syntax errors" {
            $tokens = $null
            $errors = $null
            [System.Management.Automation.Language.Parser]::ParseFile(
                $script:smokeScriptPath, [ref]$tokens, [ref]$errors) | Out-Null
            $errors | Should -BeNullOrEmpty
        }
    }
}
