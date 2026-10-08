# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

<#
.SYNOPSIS
    Runs Assert-DocumentEmbeds over every committed document that embeds a committed file.

.DESCRIPTION
    The table below is the single statement of which documents are checked and what each one is
    required to embed. Three callers read it and none of them keeps a second copy:

      - the pull request workflow's Verify Document Embeds job, which runs the checks;
      - eng/verification/tests/AssertDocumentEmbeds.Tests.ps1, whose committed-document cases run
        through here rather than restating the required lists;
      - eng/ci/tests/DmsChangeCategories.Tests.ps1, which reads -ListPath and asserts every path
        named here is one the change classifier routes to that job.

    The third is why -ListPath exists. The check is only a guard while something makes it run, and a
    document whose chapter drifted is exactly the pull request that changes no other file. A path
    added to this table and not to the classifier would leave the check gated behind a lane that
    such a pull request never reaches, which is a silent hole rather than a failure.

    This is deliberately not a Pester file. It is the whole check, it needs no module installed and
    no build, and that is what lets it run on a documentation-only pull request without pulling a
    packaging lane along with it. The verifier's own drift-detection cases are Pester and live in
    eng/verification/tests/AssertDocumentEmbeds.Tests.ps1.

.EXAMPLE
    ./Invoke-DocumentEmbedChecks.ps1

.EXAMPLE
    ./Invoke-DocumentEmbedChecks.ps1 -ListPath
#>
[CmdletBinding()]
[OutputType([string])]
param(
    # Emit the repository-relative paths these checks read - each document, and each file a document
    # is required to embed - instead of running them. Region suffixes are stripped, because what a
    # caller needs is the file that changing would change the answer.
    [switch]
    $ListPath,

    # The root the paths below are resolved against.
    [string]
    $RepositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
)

$ErrorActionPreference = "Stop"

$checkedDocument = @(
    [pscustomobject]@{
        Document = "docs/OPERATIONS.md"
        RequiredEmbed = @(
            "eng/docker-compose/plugins-dms.yml",
            "eng/docker-compose/plugins-fetch-dms.yml",
            "eng/docker-compose/plugins-config.yml"
        )
    }
    [pscustomobject]@{
        Document = "src/plugins/EdFi.Api.Plugins/PLUGINS.md"
        RequiredEmbed = @("eng/verification/PluginsConsumer/AcmePlugin.cs#sample")
    }
    [pscustomobject]@{
        # Three regions rather than one, and all three are required. The guide teaches an
        # implementer to write a validator, a plugin that registers it, and the options type both
        # depend on; a guide that kept two of the three would publish a sample that does not
        # compile where it is read.
        Document = "src/dms/core/EdFi.DataManagementService.CustomValidation/CUSTOM-VALIDATION.md"
        RequiredEmbed = @(
            "eng/verification/CustomValidatorPluginConsumer/StudentIdentityOptions.cs#options",
            "eng/verification/CustomValidatorPluginConsumer/StudentIdentityValidator.cs#validator",
            "eng/verification/CustomValidatorPluginConsumer/StudentIdentityPlugin.cs#plugin"
        )
    }
    [pscustomobject]@{
        # Three regions rather than one, and all three are required, for the same reason as the
        # custom-validation guide above: this how-to's worked example is an options type, a
        # validator, and the plugin that registers it, and dropping any one of the three would
        # publish a sample that does not compile where it is read. The reference plugin these
        # regions come from is loaded by this repository's own integration suite over real HTTP, so
        # the sample this page teaches from is one that has been proven rather than one that only
        # looks right.
        Document = "docs/UNIQUEID-VALIDATION.md"
        RequiredEmbed = @(
            "eng/fixtures/plugins/Acme.UniqueIdValidation/UniqueIdValidationOptions.cs#options",
            "eng/fixtures/plugins/Acme.UniqueIdValidation/UniqueIdValidator.cs#validator",
            "eng/fixtures/plugins/Acme.UniqueIdValidation/UniqueIdValidationPlugin.cs#plugin"
        )
    }
    [pscustomobject]@{
        # Three regions, all required, but for a different reason than the two guides above: these
        # are three independent worked examples rather than three parts of one, and each is what an
        # implementer of that vault copies. Requiring all three is what keeps the guide from quietly
        # dropping one while the others still match. The project they come from is compiled against
        # both packed contracts and pinned vendor SDKs by Invoke-SecretsConsumerCheck.ps1.
        Document = "src/config/contracts/EdFi.DmsConfigurationService.Secrets/README.md"
        RequiredEmbed = @(
            "eng/verification/SecretsPluginExamples/KeyVaultConfigurationPlugin.cs#plugin",
            "eng/verification/SecretsPluginExamples/ParameterStoreConfigurationPlugin.cs#plugin",
            "eng/verification/SecretsPluginExamples/ParameterStoreSecretResolver.cs#resolver"
        )
    }
)

if ($ListPath) {
    $paths = [System.Collections.Generic.List[string]]::new()

    foreach ($entry in $checkedDocument) {
        $paths.Add($entry.Document)

        foreach ($required in $entry.RequiredEmbed) {
            $separator = $required.IndexOf('#')
            $paths.Add($(if ($separator -lt 0) { $required } else { $required.Substring(0, $separator) }))
        }
    }

    return ($paths | Sort-Object -Unique)
}

$verifier = Join-Path $PSScriptRoot "Assert-DocumentEmbeds.ps1"

foreach ($entry in $checkedDocument) {
    & $verifier `
        -DocumentPath (Join-Path $RepositoryRoot $entry.Document) `
        -RequiredEmbed $entry.RequiredEmbed `
        -RepositoryRoot $RepositoryRoot
}
