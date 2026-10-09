# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

Set-StrictMode -Version Latest

<#
.SYNOPSIS
    Decision and evidence helpers for the manual restore smoke (Invoke-BootstrapRestoreSmoke.ps1).

.DESCRIPTION
    Holds the smoke logic that can be proven without a live stack: which wrapper, teardown script,
    and compose project a run uses; which wrapper arguments carry -DataStandardVersion; whether
    Docker already holds a stack the smoke did not create; and the provenance record that decides
    whether a run's result is final evidence. Every provenance value is either observed from its
    source (git, MSBuild, docker inspect, the built package) or recorded as null with a reason; a
    missing observation is never reported as verified.
#>

$script:WrapperProfiles = @{
    local     = [pscustomobject]@{
        Wrapper             = "local"
        BootstrapScriptName = "bootstrap-local-dms.ps1"
        TeardownScriptName  = "start-local-dms.ps1"
        ComposeProject      = "dms-local"
    }
    published = [pscustomobject]@{
        Wrapper             = "published"
        BootstrapScriptName = "bootstrap-published-dms.ps1"
        TeardownScriptName  = "start-published-dms.ps1"
        ComposeProject      = "dms-published"
    }
}

# Both projects are inventoried whichever wrapper runs: their compose files share fixed container
# names, and a teardown with -v removes the selected project's named volumes.
$script:GuardedComposeProjects = @("dms-local", "dms-published")

# The engine compose file each start script swaps in ($databaseComposeFile in start-local-dms.ps1
# and start-published-dms.ps1). A volume a confirmed removal leaves behind is identified only
# against the top-level volumes these files declare.
$script:EngineComposeFiles = [ordered]@{
    postgresql = "postgresql.yml"
    mssql      = "mssql.yml"
}

# The smoke's own image builds (Get-RestoreSmokeImageBuildPlan). Repository, source directory, and
# VERSION mirror build-dms.ps1 / build-config.ps1 DockerBuild, whose version parameters default to
# 8.0.0; BootstrapRestoreSmoke.Tests.ps1 pins both. Tags are run-unique, never the shared local/ tags.
$script:ImageBuildVersion = "8.0.0"
$script:ImageRunTagPrefix = "dms-restore-smoke-"
$script:ImageBuildDefinitions = @(
    [pscustomobject]@{ Key = "Dms"; SourceDirectory = "src/dms"; Repository = "local/ed-fi-api" }
    [pscustomobject]@{ Key = "Config"; SourceDirectory = "src/config"; Repository = "local/ed-fi-api-configuration-service" }
)
$script:PublishedDmsRepository = "edfialliance/ed-fi-api"

# The served-data probe (Test-RestoreSmokeApiRead) uses the repository's own helpers: env-utility
# resolves the ports and the bootstrap admin client exactly as the configure phase does, and the
# CMS/DMS calls are the Dms-Management and SmokeTest functions the bootstrap flows use.
Import-Module (Join-Path $PSScriptRoot "../env-utility.psm1")
Import-Module (Join-Path $PSScriptRoot "../../smoke_test/modules/SmokeTest.psm1")
Import-Module (Join-Path $PSScriptRoot "../../Dms-Management.psm1")
# The data store encryption key is read with the Compose-equivalent resolver provision-dms-schema.ps1
# uses for the same key (Get-ComposeResolvedEnvValue): an ambient value wins over the env file, as it
# does for the containers Compose starts.
Import-Module (Join-Path $PSScriptRoot "../database-safety.psm1")
# The SourceIdentity proof uses the restore consumer's own SQL and MSSQL MOVE builders
# (Get-SourceIdentitySelectSql, ConvertFrom-MssqlBackupFileList, New-MssqlRestoreMoveClause).
Import-Module (Join-Path $PSScriptRoot "../../DatabaseTemplates/Template-RestoreCore.psm1")
# The core-only env reads SCHEMA_PACKAGES with the parser prepare-dms-schema.ps1 uses.
Import-Module (Join-Path $PSScriptRoot "../../schema-package-utility.psm1")

# The engine containers the restore consumer and the smoke's SQL use (Get-RestoreDatabaseContainerName).
$script:EngineContainerNames = @{
    postgresql = "dms-postgresql"
    mssql      = "dms-mssql"
}

# Relative to the DMS base URL. Descriptors exist in every seeded template; schools only in Populated.
$script:ApiProbeDescriptorPath = "data/ed-fi/academicSubjectDescriptors?limit=5"
$script:ApiProbeSchoolPath = "data/ed-fi/schools?limit=5"

# The packages a run can build. A fixture names what its package was built from: the template kind
# and the schema selection of the source stack (default = the env file's own SCHEMA_PACKAGES;
# core-only = that set without its extension packages). Two fixtures can share a template kind, so
# packages, captures, and restores are matched by fixture, never by kind alone. A non-default
# selection also names the project schemas its restore must leave (Get-RestoreSmokeSelectionDefect).
$script:PackageFixtures = [ordered]@{
    "default-minimal"   = [pscustomobject]@{ Name = "default-minimal"; TemplateKind = "Minimal"; Selection = "default"; ProjectSchemas = $null }
    "core-only-minimal" = [pscustomobject]@{ Name = "core-only-minimal"; TemplateKind = "Minimal"; Selection = "core-only"; ProjectSchemas = @("edfi") }
    "default-populated" = [pscustomobject]@{ Name = "default-populated"; TemplateKind = "Populated"; Selection = "default"; ProjectSchemas = $null }
}

# The successful restores each leg performs, in order, by the package fixture it restores. Each one
# must leave exactly one complete served-data API record and one restored SourceIdentity record, and a
# restore of a non-default selection one selection proof (Get-RestoreSmokeResultClassification).
# package-directory restores the same package twice, around a -KeepVolumes stop. The negative legs
# refuse a restore by design, so they require neither.
$script:RestoreLegExecutions = [ordered]@{
    "package-directory"   = @("default-minimal", "default-minimal")
    "separate-config"     = @("default-minimal")
    "directory-feed"      = @("default-minimal")
    "extension-selection" = @("core-only-minimal")
    "populated"           = @("default-populated")
}
$script:NegativeLegs = @("tampered-package", "contaminated-package", "running-stack")
# Packages a leg needs beyond the ones it restores: the negative legs refuse the default Minimal
# package, and extension-selection proves the default package is refused under its core-only env.
$script:LegAdditionalFixtures = @{
    "tampered-package"     = @("default-minimal")
    "contaminated-package" = @("default-minimal")
    "running-stack"        = @("default-minimal")
    "extension-selection"  = @("default-minimal")
}
# The wrapper's core-package pattern (Get-WrapperEffectiveSchemaPackage in bootstrap-wrapper.psm1).
$script:CoreSchemaPackagePattern = '^EdFi\.DataStandard\d+\.ApiSchema$'
$script:ApiReadResources = [ordered]@{
    Minimal   = @("academicSubjectDescriptors")
    Populated = @("academicSubjectDescriptors", "schools")
}

function ConvertTo-RestoreSmokeLogSafeText {
    <#
    .SYNOPSIS
    Normalizes an externally sourced value (docker or git output) to one printable line before it
    reaches a log, an exception message, or the results JSON.
    #>
    param(
        [AllowNull()]
        [AllowEmptyString()]
        [string]$Value
    )

    if ($null -eq $Value) {
        return ""
    }
    return ($Value -replace '[\p{Cc}\p{Cf}]', '?').Trim()
}

function Get-RestoreSmokeWrapperProfile {
    <#
    .SYNOPSIS
    The bootstrap wrapper, matching teardown script, and compose project for -Wrapper.
    #>
    param(
        [Parameter(Mandatory)]
        [ValidateSet("local", "published")]
        [string]$Wrapper
    )

    return $script:WrapperProfiles[$Wrapper.ToLowerInvariant()]
}

function Get-RestoreSmokeWrapperArgumentSet {
    <#
    .SYNOPSIS
    Copies a wrapper argument set and applies the Data Standard selection rule.

    .DESCRIPTION
    bootstrap-local-dms.ps1 always composes the .env.bootstrap.<ds> overlay, so the local wrapper
    always receives -DataStandardVersion (the smoke's default is 5.2). bootstrap-published-dms.ps1
    composes the overlay ONLY when -DataStandardVersion is bound, so the published wrapper receives
    it only when the smoke's caller supplied it explicitly - even when the explicit value equals
    the default. The input hashtable is not modified.
    #>
    param(
        [Parameter(Mandatory)]
        [hashtable]$Arguments,

        [Parameter(Mandatory)]
        [ValidateSet("local", "published")]
        [string]$Wrapper,

        [Parameter(Mandatory)]
        [ValidateSet("5.2", "6.1")]
        [string]$DataStandardVersion,

        [Parameter(Mandatory)]
        [bool]$DataStandardVersionSupplied
    )

    if ($Arguments.ContainsKey("DataStandardVersion")) {
        throw "Wrapper arguments must not carry DataStandardVersion directly; it is applied from the smoke's -DataStandardVersion selection."
    }

    $result = @{} + $Arguments
    if ($Wrapper -eq "local" -or $DataStandardVersionSupplied) {
        $result.DataStandardVersion = $DataStandardVersion
    }
    return $result
}

function Assert-RestoreSmokeLegSelection {
    <#
    .SYNOPSIS
    Refuses a leg selection the run cannot perform as specified: extension-selection proves the
    selection a published-image deployment takes from its own env file, so it needs -Wrapper published
    and no explicit -DataStandardVersion (which would make that wrapper compose the Data Standard
    overlay's SCHEMA_PACKAGES over the env's).
    #>
    param(
        [AllowEmptyCollection()]
        [string[]]$Leg = @(),

        [Parameter(Mandatory)]
        [ValidateSet("local", "published")]
        [string]$Wrapper,

        [Parameter(Mandatory)]
        [bool]$DataStandardVersionSupplied
    )

    if ("extension-selection" -notin $Leg) {
        return
    }
    if ($Wrapper -ne "published") {
        throw "The extension-selection leg requires -Wrapper published; it proves the selection a published-image deployment takes from its env file."
    }
    if ($DataStandardVersionSupplied) {
        throw "The extension-selection leg refuses an explicit -DataStandardVersion: the published wrapper would compose the Data Standard overlay's SCHEMA_PACKAGES over the env file's selection."
    }
}

function Get-RestoreSmokePackageFixture {
    <#
    .SYNOPSIS
    One package fixture: its template kind, schema selection, the project schemas a restore of a
    non-default selection must leave, and the work-directory folder its package is built into.
    #>
    param(
        [Parameter(Mandatory)]
        [string]$Name
    )

    if (-not $script:PackageFixtures.Contains($Name)) {
        throw "Unknown package fixture '$Name'. Known fixtures: $($script:PackageFixtures.Keys -join ', ')."
    }
    $fixture = $script:PackageFixtures[$Name]
    $projectSchemas = $null
    if ($null -ne $fixture.ProjectSchemas) {
        $projectSchemas = @($fixture.ProjectSchemas)
    }
    return [pscustomobject]@{
        Name           = $fixture.Name
        TemplateKind   = $fixture.TemplateKind
        Selection      = $fixture.Selection
        ProjectSchemas = $projectSchemas
        DirectoryName  = "package-$($fixture.Name)"
    }
}

function Get-RestoreSmokeLegPackageFixture {
    <#
    .SYNOPSIS
    The package fixtures the selected legs need built, each once, in the fixture table's order: the
    fixtures their successful restores use plus the ones their refusals use.
    #>
    param(
        [AllowEmptyCollection()]
        [string[]]$Leg = @()
    )

    $needed = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    foreach ($legName in @($Leg | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })) {
        if ($script:RestoreLegExecutions.Contains($legName)) {
            foreach ($fixtureName in @($script:RestoreLegExecutions[$legName])) {
                $null = $needed.Add($fixtureName)
            }
        }
        if ($script:LegAdditionalFixtures.ContainsKey($legName)) {
            foreach ($fixtureName in @($script:LegAdditionalFixtures[$legName])) {
                $null = $needed.Add($fixtureName)
            }
        }
    }
    foreach ($fixtureName in $script:PackageFixtures.Keys) {
        if ($needed.Contains($fixtureName)) {
            $fixtureName
        }
    }
}

function Resolve-RestoreSmokeStandardVersion {
    <#
    .SYNOPSIS
    The package-id Data Standard segment: an explicit -StandardVersion, else <DataStandardVersion>.0.
    #>
    param(
        [AllowEmptyString()]
        [string]$StandardVersion,

        [Parameter(Mandatory)]
        [ValidateSet("5.2", "6.1")]
        [string]$DataStandardVersion
    )

    if (-not [string]::IsNullOrWhiteSpace($StandardVersion)) {
        return $StandardVersion
    }
    return "$DataStandardVersion.0"
}

function Get-RestoreSmokeForeignStackInventory {
    <#
    .SYNOPSIS
    Read-only inventory of every container (any state) and volume labelled with a guarded compose
    project. Fails closed: when docker cannot list them, the state is unknown and the smoke refuses.
    #>
    $projects = foreach ($project in $script:GuardedComposeProjects) {
        $filter = "label=com.docker.compose.project=$project"

        $global:LASTEXITCODE = 0
        $containerLines = @(docker ps -a --filter $filter --format '{{.Names}}|{{.State}}|{{.Label "com.docker.compose.service"}}' 2>&1)
        if ($LASTEXITCODE -ne 0) {
            $detail = ConvertTo-RestoreSmokeLogSafeText ($containerLines | Out-String)
            throw "Refusing to run: could not list containers for compose project '$project' (docker exit ${LASTEXITCODE}: $detail). Foreign Docker state is unknown, so nothing was stopped or removed."
        }

        $global:LASTEXITCODE = 0
        $volumeLines = @(docker volume ls --filter $filter --format '{{.Name}}' 2>&1)
        if ($LASTEXITCODE -ne 0) {
            $detail = ConvertTo-RestoreSmokeLogSafeText ($volumeLines | Out-String)
            throw "Refusing to run: could not list volumes for compose project '$project' (docker exit ${LASTEXITCODE}: $detail). Foreign Docker state is unknown, so nothing was stopped or removed."
        }

        $containers = @(
            $containerLines |
                ForEach-Object { [string]$_ } |
                Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
                ForEach-Object {
                    $fields = $_.Split("|")
                    [pscustomobject]@{
                        Name    = ConvertTo-RestoreSmokeLogSafeText $fields[0]
                        State   = if ($fields.Count -gt 1) { ConvertTo-RestoreSmokeLogSafeText $fields[1] } else { "" }
                        Service = if ($fields.Count -gt 2) { ConvertTo-RestoreSmokeLogSafeText $fields[2] } else { "" }
                    }
                }
        )
        $volumes = @(
            $volumeLines |
                ForEach-Object { [string]$_ } |
                Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
                ForEach-Object { ConvertTo-RestoreSmokeLogSafeText $_ }
        )

        [pscustomobject]@{
            Project    = $project
            Containers = $containers
            Volumes    = $volumes
        }
    }

    $projects = @($projects)
    return [pscustomobject]@{
        Projects        = $projects
        HasForeignState = @($projects | Where-Object { $_.Containers.Count -gt 0 -or $_.Volumes.Count -gt 0 }).Count -gt 0
        ForeignProjects = @($projects | Where-Object { $_.Containers.Count -gt 0 -or $_.Volumes.Count -gt 0 } | ForEach-Object { $_.Project })
    }
}

function Format-RestoreSmokeForeignStackInventory {
    <#
    .SYNOPSIS
    One line per project that holds containers or volumes, for refusal messages and logs.
    #>
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Inventory
    )

    $lines = foreach ($project in $Inventory.Projects) {
        if ($project.Containers.Count -eq 0 -and $project.Volumes.Count -eq 0) {
            continue
        }
        $containerText = if ($project.Containers.Count -gt 0) {
            ($project.Containers | ForEach-Object { "$($_.Name) ($($_.State))" }) -join ", "
        }
        else {
            "none"
        }
        $volumeText = if ($project.Volumes.Count -gt 0) { $project.Volumes -join ", " } else { "none" }
        "$($project.Project): containers $containerText; volumes $volumeText"
    }
    return (@($lines) -join "; ")
}

function Assert-RestoreSmokeNoForeignStack {
    <#
    .SYNOPSIS
    Refuses a run when Docker holds containers (running or stopped) or volumes of a guarded compose
    project, unless the caller confirmed their removal.
    #>
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Inventory,

        [switch]$ConfirmForeignStackRemoval
    )

    if (-not $Inventory.HasForeignState -or $ConfirmForeignStackRemoval) {
        return
    }

    throw ("Refusing to run: Docker already holds a stack this smoke did not create - " +
        (Format-RestoreSmokeForeignStackInventory -Inventory $Inventory) +
        ". The smoke tears down with -d -v, which would remove them. Nothing was stopped or removed. " +
        "Run on an isolated Docker host, or pass -ConfirmForeignStackRemoval only with the stack owner's approval.")
}

function Get-RestoreSmokeEngineVolumeDefinition {
    <#
    .SYNOPSIS
    The named volumes each database engine's compose file declares, read from the repository.

    .DESCRIPTION
    Reads the top-level volumes: keys (two-space-indented under a column-0 volumes:) of
    postgresql.yml and mssql.yml. Service-level volume lists are indented and ignored. Fails closed:
    a missing file, a file that declares no volume, or a key both engines declare means leftovers
    cannot be identified, so the caller refuses before removing anything.
    #>
    param(
        [Parameter(Mandatory)]
        [string]$DockerComposeRoot
    )

    $definitions = foreach ($engine in $script:EngineComposeFiles.Keys) {
        $composeFile = $script:EngineComposeFiles[$engine]
        $path = Join-Path $DockerComposeRoot $composeFile
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Cannot identify leftover volumes: the $engine compose file '$composeFile' was not found in '$DockerComposeRoot'."
        }

        $keys = [System.Collections.Generic.List[string]]::new()
        $inVolumes = $false
        foreach ($line in [System.IO.File]::ReadAllLines($path)) {
            if ([string]::IsNullOrWhiteSpace($line) -or $line.TrimStart().StartsWith("#")) {
                continue
            }
            if ($line -match '^\S') {
                $inVolumes = $line -match '^volumes:\s*(#.*)?$'
                continue
            }
            if ($inVolumes -and $line -match '^ {2}([A-Za-z0-9][A-Za-z0-9._-]*):') {
                $keys.Add($matches[1])
            }
        }
        if ($keys.Count -eq 0) {
            throw "Cannot identify leftover volumes: the $engine compose file '$composeFile' declares no top-level volume."
        }

        [pscustomobject]@{
            Engine      = $engine
            ComposeFile = $composeFile
            Volumes     = @($keys)
        }
    }

    $definitions = @($definitions)
    $declaredBy = @{}
    foreach ($definition in $definitions) {
        foreach ($key in $definition.Volumes) {
            if ($declaredBy.ContainsKey($key)) {
                throw "Cannot identify leftover volumes: volume '$key' is declared by both $($declaredBy[$key]) and $($definition.ComposeFile)."
            }
            $declaredBy[$key] = $definition.ComposeFile
        }
    }
    return $definitions
}

function ConvertTo-RestoreSmokeVolumeTimestamp {
    <#
    .SYNOPSIS
    A volume's CreatedAt as invariant round-trip UTC text. ConvertFrom-Json turns Docker's RFC 3339
    value into a DateTime, whose default string form depends on the culture.
    #>
    param(
        [AllowNull()]
        [object]$Value
    )

    if ($Value -is [System.DateTime]) {
        return $Value.ToUniversalTime().ToString("o", [System.Globalization.CultureInfo]::InvariantCulture)
    }
    if ($Value -is [System.DateTimeOffset]) {
        return $Value.UtcDateTime.ToString("o", [System.Globalization.CultureInfo]::InvariantCulture)
    }
    $text = [string]$Value
    if ([string]::IsNullOrWhiteSpace($text)) {
        return $null
    }
    return ConvertTo-RestoreSmokeLogSafeText $text
}

function Read-RestoreSmokeVolumeInspection {
    <#
    .SYNOPSIS
    One read-only docker volume inspect. Returns the parsed object, or a reason it could not be read.
    #>
    param(
        [Parameter(Mandatory)]
        [string]$Name
    )

    $global:LASTEXITCODE = 0
    $output = @(docker volume inspect --format '{{json .}}' $Name 2>&1)
    $exitCode = $LASTEXITCODE
    $text = (@($output | ForEach-Object { [string]$_ }) -join "`n").Trim()
    if ($exitCode -ne 0) {
        return [pscustomobject]@{ Volume = $null; ExitCode = $exitCode; Reason = "inspection failed (docker exit ${exitCode}: $(ConvertTo-RestoreSmokeLogSafeText $text))" }
    }

    $volume = $null
    try {
        $volume = $text | ConvertFrom-Json -AsHashtable -NoEnumerate -ErrorAction Stop
    }
    catch {
        $volume = $null
    }
    if ($volume -isnot [System.Collections.IDictionary]) {
        return [pscustomobject]@{ Volume = $null; ExitCode = $exitCode; Reason = "the inspection output is not one JSON object" }
    }
    if ([string]$volume["Name"] -cne $Name) {
        return [pscustomobject]@{ Volume = $null; ExitCode = $exitCode; Reason = "the inspection returned volume '$(ConvertTo-RestoreSmokeLogSafeText ([string]$volume["Name"]))' instead" }
    }
    return [pscustomobject]@{ Volume = $volume; ExitCode = $exitCode; Reason = $null }
}

function Get-RestoreSmokeLeftoverVolumeClassification {
    <#
    .SYNOPSIS
    Decides, for every volume a confirmed removal left in a guarded compose project, whether the
    run may continue beside it.

    .DESCRIPTION
    Each volume is inspected (read-only) and allowed ONLY when its observed compose labels identify
    it as a volume the OTHER engine's compose file declares: the selected engine's compose files do
    not declare it, so this run's down -v teardowns leave it in place. Every other volume is
    blocked with a reason: an inspection failure or unreadable output, a missing or conflicting
    com.docker.compose.project label, a missing com.docker.compose.volume label, a name that is not
    <project>_<volume label>, the selected engine's own storage, or a volume no engine compose file
    declares. Never throws for a volume; the caller refuses when anything is blocked.
    #>
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Inventory,

        [Parameter(Mandatory)]
        [ValidateSet("postgresql", "mssql")]
        [string]$DatabaseEngine,

        [Parameter(Mandatory)]
        [object[]]$Definition
    )

    $selected = @($Definition | Where-Object { $_.Engine -eq $DatabaseEngine })
    if ($selected.Count -ne 1) {
        throw "No engine volume definition for '$DatabaseEngine'."
    }
    $selected = $selected[0]
    $declaredFiles = @($Definition | ForEach-Object { $_.ComposeFile }) -join ", "

    $records = [System.Collections.Generic.List[object]]::new()
    foreach ($project in @($Inventory.Projects)) {
        foreach ($name in @($project.Volumes)) {
            $record = [ordered]@{
                Project      = $project.Project
                Name         = $name
                ProjectLabel = $null
                VolumeLabel  = $null
                CreatedAt    = $null
                Engine       = $null
                ComposeFile  = $null
                Decision     = "blocked"
                Reason       = $null
            }
            $inspection = Read-RestoreSmokeVolumeInspection -Name $name
            if ($null -eq $inspection.Volume) {
                $record.Reason = $inspection.Reason
                $records.Add([pscustomobject]$record)
                continue
            }

            $labels = $inspection.Volume["Labels"]
            if ($labels -isnot [System.Collections.IDictionary]) {
                $labels = @{}
            }
            $projectLabel = [string]$labels["com.docker.compose.project"]
            $volumeLabel = [string]$labels["com.docker.compose.volume"]
            $record.ProjectLabel = if ([string]::IsNullOrWhiteSpace($projectLabel)) { $null } else { ConvertTo-RestoreSmokeLogSafeText $projectLabel }
            $record.VolumeLabel = if ([string]::IsNullOrWhiteSpace($volumeLabel)) { $null } else { ConvertTo-RestoreSmokeLogSafeText $volumeLabel }
            $record.CreatedAt = ConvertTo-RestoreSmokeVolumeTimestamp $inspection.Volume["CreatedAt"]

            $owner = @($Definition | Where-Object { @($_.Volumes) -ccontains $volumeLabel })
            if ([string]::IsNullOrWhiteSpace($projectLabel)) {
                $record.Reason = "it has no com.docker.compose.project label"
            }
            elseif ($projectLabel -cne $project.Project) {
                $record.Reason = "its com.docker.compose.project label is '$($record.ProjectLabel)', but it was listed under '$($project.Project)'"
            }
            elseif ([string]::IsNullOrWhiteSpace($volumeLabel)) {
                $record.Reason = "it has no com.docker.compose.volume label"
            }
            elseif ($name -cne "$($project.Project)_$volumeLabel") {
                $record.Reason = "its name does not match its compose labels (expected '$($project.Project)_$($record.VolumeLabel)')"
            }
            elseif ($owner.Count -eq 0) {
                $record.Reason = "volume '$($record.VolumeLabel)' is not declared by any engine compose file ($declaredFiles), so it cannot be identified"
            }
            else {
                $record.Engine = $owner[0].Engine
                $record.ComposeFile = $owner[0].ComposeFile
                if ($owner[0].Engine -eq $DatabaseEngine) {
                    $record.Reason = "it is the selected engine's ($DatabaseEngine) storage, declared in $($owner[0].ComposeFile); the run needs fresh storage"
                }
                else {
                    $record.Decision = "allowed"
                    $record.Reason = "declared in $($owner[0].ComposeFile) as $($owner[0].Engine) storage; this run uses $DatabaseEngine, whose $($selected.ComposeFile) does not declare it, so this run's down -v teardowns leave it in place"
                }
            }
            $records.Add([pscustomobject]$record)
        }
    }

    $volumes = @($records)
    return [pscustomobject]@{
        DatabaseEngine = $DatabaseEngine
        Volumes        = $volumes
        Allowed        = @($volumes | Where-Object { $_.Decision -eq "allowed" })
        Blocked        = @($volumes | Where-Object { $_.Decision -ne "allowed" })
    }
}

function Format-RestoreSmokeLeftoverVolumeClassification {
    <#
    .SYNOPSIS
    "<name> (<reason>)" for each volume with the given decision, for refusal messages and logs.
    #>
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Classification,

        [Parameter(Mandatory)]
        [ValidateSet("allowed", "blocked")]
        [string]$Decision
    )

    $volumes = @($Classification.Blocked)
    if ($Decision -eq "allowed") {
        $volumes = @($Classification.Allowed)
    }
    return (($volumes | ForEach-Object { "$($_.Name) ($($_.Reason))" }) -join "; ")
}

function Get-RestoreSmokeAllowedVolumeState {
    <#
    .SYNOPSIS
    Re-inspects each leftover volume the preflight allowed, at the end of the run: it is preserved
    only when it still exists with the CreatedAt the preflight observed.
    #>
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Classification
    )

    $states = foreach ($volume in @($Classification.Allowed)) {
        $state = [ordered]@{
            Name      = $volume.Name
            Present   = $false
            CreatedAt = $null
            Preserved = $false
            Reason    = $null
        }
        $inspection = Read-RestoreSmokeVolumeInspection -Name $volume.Name
        if ($null -eq $inspection.Volume) {
            $state.Reason = $inspection.Reason
        }
        else {
            $state.Present = $true
            $state.CreatedAt = ConvertTo-RestoreSmokeVolumeTimestamp $inspection.Volume["CreatedAt"]
            if ([string]::IsNullOrWhiteSpace($volume.CreatedAt)) {
                $state.Reason = "the preflight observed no CreatedAt, so preservation cannot be shown"
            }
            elseif ($state.CreatedAt -cne $volume.CreatedAt) {
                $state.Reason = "CreatedAt changed from '$($volume.CreatedAt)' to '$($state.CreatedAt)'; the volume was recreated"
            }
            else {
                $state.Preserved = $true
            }
        }
        [pscustomobject]$state
    }
    return @($states)
}

function Get-RestoreSmokeSourceRevision {
    <#
    .SYNOPSIS
    The observed worktree revision and porcelain status. Unobservable values stay null with a reason.
    #>
    param(
        [Parameter(Mandatory)]
        [string]$RepoRoot
    )

    $global:LASTEXITCODE = 0
    $revisionOutput = @(git -C $RepoRoot rev-parse HEAD 2>&1)
    $revisionExit = $LASTEXITCODE

    $global:LASTEXITCODE = 0
    $statusOutput = @(git -C $RepoRoot status --porcelain 2>&1)
    $statusExit = $LASTEXITCODE

    $observed = ($revisionExit -eq 0 -and $statusExit -eq 0)
    # Assigned directly, never through an if expression: an if expression unrolls an empty array
    # to $null and a one-element array to a scalar, and .Count then throws under strict mode.
    $porcelain = @()
    if ($statusExit -eq 0) {
        $porcelain = @($statusOutput | ForEach-Object { ConvertTo-RestoreSmokeLogSafeText ([string]$_) } | Where-Object { $_ -ne "" })
    }

    return [pscustomobject]@{
        CapturedUtc = [System.DateTime]::UtcNow.ToString("o", [System.Globalization.CultureInfo]::InvariantCulture)
        Revision    = if ($revisionExit -eq 0) { ConvertTo-RestoreSmokeLogSafeText ([string]@($revisionOutput)[0]) } else { $null }
        Porcelain   = $porcelain
        Observed    = $observed
        Clean       = ($observed -and $porcelain.Count -eq 0)
        Reason      = if ($observed) { $null } else { "git rev-parse exit $revisionExit, git status exit $statusExit" }
    }
}

function Get-RestoreSmokeFileSha256 {
    <#
    .SYNOPSIS
    Lower-case hex SHA-256 of a file, the form the attestation payload records.
    #>
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Invoke-RestoreSmokeSchemaToolBuild {
    <#
    .SYNOPSIS
    Builds api-schema-tools from the worktree in the configuration the bootstrap resolver prefers
    (Debug), then proves the resolver returns exactly the built executable.

    .DESCRIPTION
    The restore consumer resolves the tool by directory order and ignores DMS_SCHEMA_TOOL_PATH, so
    setting the variable alone would not make the run use the built tool. The record is Verified only
    when the build exited 0 and both the default resolution and the -RequestedPath resolution return
    the built path. -Resolver is invoked as `& $Resolver <requestedPath>` ("" for the default lookup).
    #>
    param(
        [Parameter(Mandatory)]
        [string]$ProjectPath,

        [Parameter(Mandatory)]
        [scriptblock]$Resolver
    )

    $record = [ordered]@{
        BuildCommand          = "dotnet build `"$ProjectPath`" -c Debug"
        BuildExitCode         = $null
        ExecutablePath        = $null
        Sha256AtBuild         = $null
        Sha256AtEnd           = $null
        InformationalVersion  = $null
        ResolvedDefaultPath   = $null
        ResolvedRequestedPath = $null
        Verified              = $false
        Reason                = $null
    }

    $global:LASTEXITCODE = 0
    dotnet build $ProjectPath -c Debug | Out-Host
    $record.BuildExitCode = $LASTEXITCODE
    if ($record.BuildExitCode -ne 0) {
        $record.Reason = "dotnet build exited $($record.BuildExitCode)"
        return [pscustomobject]$record
    }

    $global:LASTEXITCODE = 0
    $targetDirectoryOutput = @(dotnet msbuild $ProjectPath -getProperty:TargetDir -p:Configuration=Debug 2>&1)
    if ($LASTEXITCODE -ne 0 -or $targetDirectoryOutput.Count -eq 0) {
        $record.Reason = "could not read the Debug TargetDir from MSBuild (exit $LASTEXITCODE)"
        return [pscustomobject]$record
    }
    $targetDirectory = ([string]@($targetDirectoryOutput | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) })[-1]).Trim()
    $executableName = if ($IsWindows) { "api-schema-tools.exe" } else { "api-schema-tools" }
    $executablePath = [System.IO.Path]::GetFullPath((Join-Path $targetDirectory $executableName))
    if (-not (Test-Path -LiteralPath $executablePath -PathType Leaf)) {
        $record.Reason = "the build reported success but '$executablePath' does not exist"
        return [pscustomobject]$record
    }

    $record.ExecutablePath = $executablePath
    $record.Sha256AtBuild = Get-RestoreSmokeFileSha256 -Path $executablePath
    $assemblyPath = Join-Path $targetDirectory "api-schema-tools.dll"
    if (Test-Path -LiteralPath $assemblyPath -PathType Leaf) {
        $record.InformationalVersion = (Get-Item -LiteralPath $assemblyPath).VersionInfo.ProductVersion
    }

    $record.ResolvedDefaultPath = [string](& $Resolver "")
    $record.ResolvedRequestedPath = [string](& $Resolver $executablePath)
    if ($record.ResolvedDefaultPath -cne $executablePath) {
        $record.Reason = "the bootstrap resolver selects '$($record.ResolvedDefaultPath)', not the in-run build '$executablePath'"
        return [pscustomobject]$record
    }
    if ($record.ResolvedRequestedPath -cne $executablePath) {
        $record.Reason = "the requested-path resolution returned '$($record.ResolvedRequestedPath)', not '$executablePath'"
        return [pscustomobject]$record
    }
    if ([string]::IsNullOrWhiteSpace($record.InformationalVersion)) {
        $record.Reason = "the built api-schema-tools.dll has no informational version"
        return [pscustomobject]$record
    }

    $record.Verified = $true
    return [pscustomobject]$record
}

function Get-RestoreSmokeImageBuildPlan {
    <#
    .SYNOPSIS
    The smoke's own image builds: one docker buildx build per image, with the Dockerfile, context,
    parentdir build context, and VERSION build argument that build-dms.ps1 / build-config.ps1
    DockerBuild use, plus --load, a fresh --iidfile in the work directory, and run-unique tags.

    .DESCRIPTION
    The smoke does not call the build scripts: their fixed local/ tags are reusable, so an image
    found under them proves nothing about this run's build, and build-dms.ps1 DockerBuild neither
    loads the result nor propagates a docker failure. BootstrapRestoreSmoke.Tests.ps1 pins these
    arguments against both scripts' DockerBuild so they cannot drift apart silently.
    #>
    param(
        [Parameter(Mandatory)]
        [string]$RepoRoot,

        [Parameter(Mandatory)]
        [string]$WorkDirectory,

        [Parameter(Mandatory)]
        [ValidatePattern('^[0-9a-f]{12}$')]
        [string]$RunId,

        [Parameter(Mandatory)]
        [ValidateSet("local", "published")]
        [string]$Wrapper
    )

    $runTag = "$script:ImageRunTagPrefix$RunId"
    $imageDirectory = Join-Path $WorkDirectory "images"
    $plan = foreach ($definition in $script:ImageBuildDefinitions) {
        $tags = @("$($definition.Repository):$runTag")
        if ($definition.Key -eq "Dms" -and $Wrapper -eq "published") {
            # published-dms.yml pins the edfialliance/ed-fi-api repository and takes only the tag.
            $tags += "$($script:PublishedDmsRepository):$runTag"
        }
        $iidFile = Join-Path $imageDirectory "$($definition.Key.ToLowerInvariant()).iid"
        $arguments = @("buildx", "build", "--load", "--iidfile", $iidFile)
        foreach ($tag in $tags) {
            $arguments += @("-t", $tag)
        }
        $arguments += @("-f", "Dockerfile", ".", "--build-context", "parentdir=../", "--build-arg", "VERSION=$script:ImageBuildVersion")

        [pscustomobject]@{
            Key              = $definition.Key
            SourceDirectory  = $definition.SourceDirectory
            WorkingDirectory = [System.IO.Path]::GetFullPath((Join-Path $RepoRoot $definition.SourceDirectory))
            Tags             = $tags
            IidFile          = $iidFile
            Arguments        = $arguments
        }
    }
    return @($plan)
}

function New-RestoreSmokeImageLedger {
    <#
    .SYNOPSIS
    The provenance record for the in-run image builds. It exists before the first build so that tag
    ownership and every build record survive a later build's failure or exception, for cleanup and
    for the results JSON.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Creates an in-memory record only.')]
    param()

    return [ordered]@{
        Version   = $script:ImageBuildVersion
        OwnedTags = [System.Collections.Generic.List[string]]::new()
        Dms       = $null
        Config    = $null
        Cleanup   = [System.Collections.Generic.List[object]]::new()
    }
}

function Get-RestoreSmokeImageTagState {
    <#
    .SYNOPSIS
    Whether an image reference resolves: Present (with its image ID), Absent, or Unknown.

    .DESCRIPTION
    A failed docker image inspect is Absent only when every line of its output is a "No such image"
    message for exactly this reference. Any other failure (daemon unreachable, permission, transport)
    is Unknown, and callers must not treat it as absence.
    #>
    param(
        [Parameter(Mandatory)]
        [string]$Reference
    )

    $global:LASTEXITCODE = 0
    $output = @(docker image inspect $Reference --format '{{.Id}}' 2>&1 | ForEach-Object { [string]$_ } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    $exitCode = $LASTEXITCODE
    if ($exitCode -eq 0) {
        if ($output.Count -eq 1 -and $output[0].Trim() -cmatch '^sha256:[0-9a-f]{64}$') {
            return [pscustomobject]@{ State = "Present"; ImageId = $output[0].Trim(); Detail = $null }
        }
        return [pscustomobject]@{ State = "Unknown"; ImageId = $null; Detail = "docker image inspect $Reference returned an unexpected ID: $(ConvertTo-RestoreSmokeLogSafeText ($output -join ' '))" }
    }

    $absentPattern = '^(?:Error response from daemon: |Error: )?No such image: (?:docker\.io/)?' + [regex]::Escape($Reference) + '$'
    if ($output.Count -gt 0 -and @($output | Where-Object { $_.Trim() -notmatch $absentPattern }).Count -eq 0) {
        return [pscustomobject]@{ State = "Absent"; ImageId = $null; Detail = $null }
    }
    return [pscustomobject]@{ State = "Unknown"; ImageId = $null; Detail = "docker image inspect $Reference exited ${exitCode}: $(ConvertTo-RestoreSmokeLogSafeText ($output -join ' '))" }
}

function Register-RestoreSmokeImageTagOwnership {
    <#
    .SYNOPSIS
    Claims every planned tag for this run, all together, only after proving each one absent and
    each iidfile path unused. A present tag or an inspection failure claims nothing.
    #>
    param(
        [Parameter(Mandatory)]
        [object[]]$Plan,

        [Parameter(Mandatory)]
        [System.Collections.IDictionary]$Ledger
    )

    foreach ($entry in $Plan) {
        if (Test-Path -LiteralPath $entry.IidFile) {
            throw "The iidfile path '$($entry.IidFile)' already exists, so it cannot identify this run's build; no image tag was claimed."
        }
        foreach ($tag in $entry.Tags) {
            $tagState = Get-RestoreSmokeImageTagState -Reference $tag
            if ($tagState.State -eq "Present") {
                throw "The run tag '$tag' already exists ($($tagState.ImageId)), so this run does not own it; no image tag was claimed."
            }
            if ($tagState.State -ne "Absent") {
                throw "Cannot establish that the run tag '$tag' is absent ($($tagState.Detail)); no image tag was claimed."
            }
        }
    }

    foreach ($entry in $Plan) {
        New-Item -ItemType Directory -Path (Split-Path -Parent $entry.IidFile) -Force | Out-Null
        foreach ($tag in $entry.Tags) {
            $Ledger.OwnedTags.Add($tag)
        }
    }
}

function Invoke-RestoreSmokeImageBuild {
    <#
    .SYNOPSIS
    Runs each planned image build in order and records it in the ledger. An image is verified only
    when docker exited 0, the build wrote an image ID to its fresh iidfile, and every run tag
    resolves to that ID. Throws at the first build that is not verified.

    .DESCRIPTION
    Each record is stored in the ledger before docker runs, so a failure or exception in a later
    build leaves the earlier records (and the claimed tags) in place. No comparison is made with any
    image that existed before the run: a cached build may legitimately reproduce an existing ID, or
    produce a new one.
    #>
    param(
        [Parameter(Mandatory)]
        [object[]]$Plan,

        [Parameter(Mandatory)]
        [System.Collections.IDictionary]$Ledger
    )

    foreach ($entry in $Plan) {
        foreach ($tag in $entry.Tags) {
            if (-not $Ledger.OwnedTags.Contains($tag)) {
                throw "The run tag '$tag' was not claimed for this run; refusing to build under it."
            }
        }

        $record = [ordered]@{
            Command         = @("docker") + $entry.Arguments
            SourceDirectory = $entry.SourceDirectory
            Tags            = $entry.Tags
            ExitCode        = $null
            IidFileContent  = $null
            ImageId         = $null
            TagImageIds     = [ordered]@{}
            Verified        = $false
            Reason          = "the build did not complete"
        }
        $Ledger[$entry.Key] = $record

        if (Test-Path -LiteralPath $entry.IidFile) {
            $record.Reason = "the iidfile already existed before the build"
            throw "Image provenance could not be established for $($entry.Key): $($record.Reason)"
        }

        Push-Location $entry.WorkingDirectory
        try {
            $buildArguments = [string[]]$entry.Arguments
            $global:LASTEXITCODE = 0
            docker @buildArguments | Out-Host
            $record.ExitCode = $LASTEXITCODE
        }
        finally {
            Pop-Location
        }

        if ($record.ExitCode -ne 0) {
            $record.Reason = "docker buildx build ($($entry.Key)) exited $($record.ExitCode)"
        }
        elseif (-not (Test-Path -LiteralPath $entry.IidFile -PathType Leaf)) {
            $record.Reason = "the build produced no image ID (no iidfile)"
        }
        else {
            $record.IidFileContent = ConvertTo-RestoreSmokeLogSafeText (Get-Content -LiteralPath $entry.IidFile -Raw)
            if ($record.IidFileContent -cnotmatch '^sha256:[0-9a-f]{64}$') {
                $record.Reason = "the build produced no image ID (iidfile content '$($record.IidFileContent)')"
            }
            else {
                $mismatch = $null
                foreach ($tag in $entry.Tags) {
                    $tagState = Get-RestoreSmokeImageTagState -Reference $tag
                    $record.TagImageIds[$tag] = $tagState.ImageId
                    if ($null -ne $mismatch) {
                        continue
                    }
                    if ($tagState.State -eq "Absent") {
                        $mismatch = "the run tag '$tag' is missing after the build"
                    }
                    elseif ($tagState.State -ne "Present") {
                        $mismatch = "the run tag '$tag' could not be inspected after the build ($($tagState.Detail))"
                    }
                    elseif ($tagState.ImageId -cne $record.IidFileContent) {
                        $mismatch = "the run tag '$tag' resolves to $($tagState.ImageId), but the build reported $($record.IidFileContent)"
                    }
                }
                if ($null -eq $mismatch) {
                    $record.ImageId = $record.IidFileContent
                    $record.Verified = $true
                    $record.Reason = $null
                }
                else {
                    $record.Reason = $mismatch
                }
            }
        }

        if (-not $record.Verified) {
            throw "Image provenance could not be established for $($entry.Key): $($record.Reason)"
        }
    }
}

function Remove-RestoreSmokeOwnedImageTag {
    <#
    .SYNOPSIS
    Removes the tags this run claimed, and nothing else: no -f, no removal by image ID, no prune.
    Docker itself keeps an image a container still uses. Each outcome is recorded in the ledger;
    a failure warns and never changes the run's result.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Internal smoke cleanup; the smoke does not expose -WhatIf.')]
    param(
        [Parameter(Mandatory)]
        [System.Collections.IDictionary]$Ledger,

        [switch]$Keep
    )

    foreach ($tag in @($Ledger.OwnedTags)) {
        if ($Keep) {
            $Ledger.Cleanup.Add([pscustomobject]@{ Tag = $tag; Action = "kept"; Detail = "-SkipTeardown" })
            continue
        }

        $tagState = Get-RestoreSmokeImageTagState -Reference $tag
        if ($tagState.State -eq "Absent") {
            $Ledger.Cleanup.Add([pscustomobject]@{ Tag = $tag; Action = "absent"; Detail = $null })
            continue
        }
        if ($tagState.State -ne "Present") {
            Write-Warning "Could not inspect the run tag '$tag' for cleanup: $($tagState.Detail)"
            $Ledger.Cleanup.Add([pscustomobject]@{ Tag = $tag; Action = "failed"; Detail = $tagState.Detail })
            continue
        }

        $global:LASTEXITCODE = 0
        $output = @(docker image rm $tag 2>&1 | ForEach-Object { [string]$_ })
        if ($LASTEXITCODE -eq 0) {
            $Ledger.Cleanup.Add([pscustomobject]@{ Tag = $tag; Action = "removed"; Detail = $null })
        }
        else {
            $detail = "docker image rm exited ${LASTEXITCODE}: $(ConvertTo-RestoreSmokeLogSafeText ($output -join ' '))"
            Write-Warning "Could not remove the run tag '$tag': $detail"
            $Ledger.Cleanup.Add([pscustomobject]@{ Tag = $tag; Action = "failed"; Detail = $detail })
        }
    }
}

function Write-RestoreSmokeImageEnvironmentFile {
    <#
    .SYNOPSIS
    Writes the smoke-owned env file: the base env with the image keys replaced so the selected
    wrapper's compose files resolve to the in-run built images.
    #>
    param(
        [Parameter(Mandatory)]
        [string]$BaseEnvironmentFile,

        [Parameter(Mandatory)]
        [string]$TargetPath,

        [Parameter(Mandatory)]
        [ValidateSet("local", "published")]
        [string]$Wrapper,

        [Parameter(Mandatory)]
        [string]$ConfigImage,

        [string]$DmsImage,

        [string]$PublishedDmsTag
    )

    $imageKeys = if ($Wrapper -eq "local") {
        if ([string]::IsNullOrWhiteSpace($DmsImage)) {
            throw "The local wrapper needs -DmsImage."
        }
        [ordered]@{ DMS_DOCKER_IMAGE = $DmsImage; DMS_CONFIG_DOCKER_IMAGE = $ConfigImage }
    }
    else {
        if ([string]::IsNullOrWhiteSpace($PublishedDmsTag)) {
            throw "The published wrapper needs -PublishedDmsTag."
        }
        [ordered]@{ DMS_IMAGE_TAG = $PublishedDmsTag; DMS_CONFIG_DOCKER_IMAGE = $ConfigImage }
    }

    $keyPattern = '^\s*(?:' + (($imageKeys.Keys | ForEach-Object { [regex]::Escape($_) }) -join '|') + ')\s*='
    $lines = @(Get-Content -LiteralPath $BaseEnvironmentFile | Where-Object { $_ -notmatch $keyPattern })
    $lines += @($imageKeys.Keys | ForEach-Object { "$_=$($imageKeys[$_])" })
    Set-Content -LiteralPath $TargetPath -Value $lines -Encoding utf8
    return [pscustomobject]$imageKeys
}

function Write-RestoreSmokeCoreOnlyEnvironmentFile {
    <#
    .SYNOPSIS
    Writes the core-only env: the base env (the smoke's image env, so the run tags stay) with
    SCHEMA_PACKAGES reduced to its one core package. Every other line is kept as it is.

    .DESCRIPTION
    SCHEMA_PACKAGES is parsed with schema-package-utility.psm1, the parser prepare-dms-schema.ps1
    uses. Refuses, writing nothing, unless the base env declares SCHEMA_PACKAGES exactly once as a
    quoted JSON array with exactly one core package (EdFi.DataStandard<NN>.ApiSchema, the wrapper's
    rule), every entry has a name and a version, and at least one other package is listed (without
    one, a core-only selection would equal the default one and the leg would prove nothing). Returns
    the selected and removed "<name>@<version>" identities, the form schema.selectedPackages records.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Writes only the smoke-owned env file in its private work directory.')]
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', 'matchInfo', Justification = 'MatchEvaluator delegate parameter: the replacement line is returned literally.')]
    param(
        [Parameter(Mandatory)]
        [string]$BaseEnvironmentFile,

        [Parameter(Mandatory)]
        [string]$TargetPath
    )

    $content = Get-Content -LiteralPath $BaseEnvironmentFile -Raw
    $declarations = [regex]::Matches($content, '(?m)^[ \t]*(?:export[ \t]+)?SCHEMA_PACKAGES[ \t]*=')
    if ($declarations.Count -ne 1) {
        throw "Cannot derive a core-only env: '$BaseEnvironmentFile' declares SCHEMA_PACKAGES $($declarations.Count) times; expected exactly once."
    }
    $blockPattern = "(?ms)^[ \t]*SCHEMA_PACKAGES='\[.*?\]'"
    if (-not [regex]::IsMatch($content, $blockPattern)) {
        throw "Cannot derive a core-only env: SCHEMA_PACKAGES in '$BaseEnvironmentFile' is not a quoted JSON array."
    }

    $packages = @(Get-SchemaPackagesFromEnvironmentFile -EnvironmentFilePath $BaseEnvironmentFile)
    foreach ($package in $packages) {
        # Read null-safely: under strict mode a missing property would throw before this refusal.
        if ([string]::IsNullOrWhiteSpace([string](Get-RestoreSmokeEvidenceValue $package "name")) -or
            [string]::IsNullOrWhiteSpace([string](Get-RestoreSmokeEvidenceValue $package "version"))) {
            throw "Cannot derive a core-only env: SCHEMA_PACKAGES in '$BaseEnvironmentFile' has an entry without both name and version."
        }
    }
    $core = @($packages | Where-Object { [string]$_.name -match $script:CoreSchemaPackagePattern })
    if ($core.Count -ne 1) {
        throw "Cannot derive a core-only env: SCHEMA_PACKAGES in '$BaseEnvironmentFile' lists $($core.Count) core packages (EdFi.DataStandard<NN>.ApiSchema); expected exactly one."
    }
    $removed = @($packages | Where-Object { [string]$_.name -notmatch $script:CoreSchemaPackagePattern })
    if ($removed.Count -eq 0) {
        throw "Cannot derive a core-only env: SCHEMA_PACKAGES in '$BaseEnvironmentFile' lists only the core package, so a core-only selection would equal the default one."
    }

    $coreJson = ConvertTo-Json -InputObject @($core[0]) -Compress -Depth 5
    if ($coreJson.Contains("'")) {
        throw "Cannot derive a core-only env: the core SCHEMA_PACKAGES entry contains a single quote, which the quoted env value cannot carry."
    }
    $newLine = "SCHEMA_PACKAGES='$coreJson'"
    $rewritten = [regex]::Replace($content, $blockPattern, { param($matchInfo) $newLine })
    $utf8NoBom = [System.Text.UTF8Encoding]::new($false)
    [System.IO.File]::WriteAllText($TargetPath, $rewritten, $utf8NoBom)

    return [pscustomobject]@{
        Selection        = "core-only"
        FileName         = [System.IO.Path]::GetFileName($TargetPath)
        SelectedPackages = @($core | ForEach-Object { "$($_.name)@$($_.version)" })
        RemovedPackages  = @($removed | ForEach-Object { "$($_.name)@$($_.version)" })
    }
}

function Get-RestoreSmokeStackObservation {
    <#
    .SYNOPSIS
    What the started stack actually runs: for the dms, config, and db services of the project, the
    container name, the image reference it was created from, and its image ID. The db service adds
    its image's repository digests, or the reason none were observed (a locally built or untagged
    image has none; the image ID alone does not identify the published engine image).
    #>
    param(
        [Parameter(Mandatory)]
        [string]$ComposeProject,

        [Parameter(Mandatory)]
        [string]$Label
    )

    $global:LASTEXITCODE = 0
    $containerLines = @(docker ps --filter "label=com.docker.compose.project=$ComposeProject" --format '{{.ID}}|{{.Names}}|{{.Label "com.docker.compose.service"}}' 2>&1)
    if ($LASTEXITCODE -ne 0) {
        return [pscustomobject]@{ Label = $Label; Services = [ordered]@{}; Reason = "docker ps failed (exit $LASTEXITCODE)" }
    }

    $services = [ordered]@{}
    foreach ($line in @($containerLines | ForEach-Object { [string]$_ } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })) {
        $fields = $line.Split("|")
        if ($fields.Count -lt 3 -or $fields[2] -notin @("dms", "config", "db")) {
            continue
        }

        $global:LASTEXITCODE = 0
        $inspect = @(docker inspect $fields[0] --format '{{.Image}}|{{.Config.Image}}' 2>&1)
        if ($LASTEXITCODE -ne 0 -or $inspect.Count -eq 0) {
            continue
        }
        $imageFields = ([string]$inspect[0]).Split("|")
        $observation = [ordered]@{
            Container  = ConvertTo-RestoreSmokeLogSafeText $fields[1]
            ImageId    = ConvertTo-RestoreSmokeLogSafeText $imageFields[0]
            ImageRef   = if ($imageFields.Count -gt 1) { ConvertTo-RestoreSmokeLogSafeText $imageFields[1] } else { $null }
            RepoDigests = @()
            RepoDigestsReason = $null
        }
        if ($fields[2] -eq "db") {
            $global:LASTEXITCODE = 0
            $digests = @(docker image inspect $observation.ImageId --format '{{join .RepoDigests ","}}' 2>&1)
            if ($LASTEXITCODE -ne 0) {
                $observation.RepoDigestsReason = "docker image inspect $($observation.ImageId) exited ${LASTEXITCODE}: $(ConvertTo-RestoreSmokeLogSafeText (($digests | ForEach-Object { [string]$_ }) -join ' '))"
            }
            else {
                $observation.RepoDigests = @(([string]@($digests)[0]).Split(",") | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object { ConvertTo-RestoreSmokeLogSafeText $_ })
                if ($observation.RepoDigests.Count -eq 0) {
                    $observation.RepoDigestsReason = "the image has no repository digest (locally built or untagged)"
                }
            }
        }
        $services[$fields[2]] = [pscustomobject]$observation
    }

    return [pscustomobject]@{ Label = $Label; Services = $services; Reason = $null }
}

function Get-RestoreSmokeWorkspaceManifestFile {
    <#
    .SYNOPSIS
    One read of the active workspace's bootstrap-manifest.json: whether it is present, its bytes,
    their SHA-256, and its last-write time as round-trip UTC text, or the reason it could not be
    read. Never throws. The bytes stay inside the module; callers record the rest.
    #>
    param(
        [Parameter(Mandatory)]
        [string]$BootstrapRoot
    )

    $record = [ordered]@{ Present = $false; Sha256 = $null; LastWriteTimeUtc = $null; Bytes = $null; Reason = $null }
    $manifestPath = Join-Path $BootstrapRoot "bootstrap-manifest.json"
    try {
        if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
            $record.Reason = "the active workspace has no bootstrap-manifest.json"
            return [pscustomobject]$record
        }
        $lastWrite = [System.IO.File]::GetLastWriteTimeUtc($manifestPath)
        $bytes = [System.IO.File]::ReadAllBytes($manifestPath)
        $record.Present = $true
        $record.Bytes = $bytes
        $record.Sha256 = [System.Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
        $record.LastWriteTimeUtc = $lastWrite.ToString("o", [System.Globalization.CultureInfo]::InvariantCulture)
    }
    catch {
        $record.Present = $false
        $record.Bytes = $null
        $record.Sha256 = $null
        $record.Reason = "bootstrap-manifest.json could not be read: $(ConvertTo-RestoreSmokeLogSafeText $_.Exception.Message)"
    }
    return [pscustomobject]$record
}

function Read-RestoreSmokeStagedSelection {
    <#
    .SYNOPSIS
    The package selection the active workspace actually staged: bootstrap-manifest.json's
    schema.selectedPackages ("<packageId>@<version>", which prepare-dms-schema.ps1 records for the
    packages it staged from the effective SCHEMA_PACKAGES, after any wrapper Data Standard overlay),
    with the manifest's SHA-256 and last-write time taken from the same read. This is staged
    evidence, never the raw SCHEMA_PACKAGES input. Never throws: anything unreadable is a Reason.
    #>
    param(
        [Parameter(Mandatory)]
        [string]$BootstrapRoot
    )

    $record = [ordered]@{
        Source                   = "bootstrap-manifest.json schema.selectedPackages"
        ManifestSha256           = $null
        ManifestLastWriteTimeUtc = $null
        StagedPackages           = $null
        Reason                   = $null
    }
    $file = Get-RestoreSmokeWorkspaceManifestFile -BootstrapRoot $BootstrapRoot
    if ($null -ne $file.Reason) {
        $record.Reason = $file.Reason
        return [pscustomobject]$record
    }
    $record.ManifestSha256 = $file.Sha256
    $record.ManifestLastWriteTimeUtc = $file.LastWriteTimeUtc

    try {
        $text = [System.Text.Encoding]::UTF8.GetString($file.Bytes).TrimStart([char]0xFEFF)
        $manifest = $text | ConvertFrom-Json -AsHashtable -ErrorAction Stop
    }
    catch {
        $record.Reason = "bootstrap-manifest.json is not valid JSON: $(ConvertTo-RestoreSmokeLogSafeText $_.Exception.Message)"
        return [pscustomobject]$record
    }
    if ($manifest -isnot [System.Collections.IDictionary] -or -not $manifest.Contains("schema") -or $manifest["schema"] -isnot [System.Collections.IDictionary]) {
        $record.Reason = "bootstrap-manifest.json has no schema section"
        return [pscustomobject]$record
    }
    $schema = $manifest["schema"]
    if (-not $schema.Contains("selectedPackages") -or $null -eq $schema["selectedPackages"]) {
        $record.Reason = "bootstrap-manifest.json records no schema.selectedPackages"
        return [pscustomobject]$record
    }
    if ($schema["selectedPackages"] -isnot [System.Collections.IList]) {
        $record.Reason = "schema.selectedPackages in bootstrap-manifest.json is not an array"
        return [pscustomobject]$record
    }
    $packages = [System.Collections.Generic.List[string]]::new()
    foreach ($item in $schema["selectedPackages"]) {
        $packages.Add((ConvertTo-RestoreSmokeLogSafeText ([string]$item)))
    }
    $record.StagedPackages = $packages.ToArray()
    return [pscustomobject]$record
}

function New-RestoreSmokeStackStart {
    <#
    .SYNOPSIS
    The record of one wrapper run that may start a stack, taken just before the wrapper runs, so
    each stack observation can be bound to the start that produced it: run-wide id
    "stack-start#<Sequence>", step label, whether it restores, the schema selection whose env it
    passed, the active workspace manifest before it (presence, SHA-256, last-write time), and its
    start time, taken after that capture.

    .DESCRIPTION
    RequestedPackages is the env file's raw SCHEMA_PACKAGES input as "<name>@<version>", before any
    wrapper Data Standard overlay. It is recorded for comparison only and never classifies a
    stack; a parse failure is RequestedReason, not an exception.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Creates an in-memory record only.')]
    param(
        [Parameter(Mandatory)]
        [int]$Sequence,

        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]$Label,

        [Parameter(Mandatory)]
        [string]$Selection,

        [Parameter(Mandatory)]
        [string]$EnvironmentFile,

        [Parameter(Mandatory)]
        [string]$BootstrapRoot,

        [switch]$Restore
    )

    $requested = $null
    $requestedReason = $null
    try {
        $entries = @(Get-SchemaPackagesFromEnvironmentFile -EnvironmentFilePath $EnvironmentFile)
        $identities = [System.Collections.Generic.List[string]]::new()
        foreach ($entry in $entries) {
            $identities.Add((ConvertTo-RestoreSmokeLogSafeText "$(Get-RestoreSmokeEvidenceValue $entry 'name')@$(Get-RestoreSmokeEvidenceValue $entry 'version')"))
        }
        $requested = $identities.ToArray()
    }
    catch {
        $requestedReason = "SCHEMA_PACKAGES in the env file could not be parsed: $(ConvertTo-RestoreSmokeLogSafeText $_.Exception.Message)"
    }

    $before = Get-RestoreSmokeWorkspaceManifestFile -BootstrapRoot $BootstrapRoot
    $startedUtc = [System.DateTime]::UtcNow.ToString("o", [System.Globalization.CultureInfo]::InvariantCulture)
    return [pscustomobject]@{
        StackStart        = "stack-start#$Sequence"
        Label             = ConvertTo-RestoreSmokeLogSafeText $Label
        Restore           = [bool]$Restore
        Selection         = ConvertTo-RestoreSmokeLogSafeText $Selection
        EnvironmentFile   = ConvertTo-RestoreSmokeLogSafeText ([System.IO.Path]::GetFileName($EnvironmentFile))
        RequestedPackages = $requested
        RequestedReason   = $requestedReason
        WorkspaceBefore   = [pscustomobject]@{ Present = $before.Present; Sha256 = $before.Sha256; LastWriteTimeUtc = $before.LastWriteTimeUtc; Reason = $before.Reason }
        StartedUtc        = $startedUtc
    }
}

function Get-RestoreSmokePackageProvenance {
    <#
    .SYNOPSIS
    The built template package as observed on disk: file SHA-256, the attestation payload's package
    id, version, recorded SHA-256 and producer, the signing key id, and the in-package restore
    manifest's projects and effective schema hash. Verified only when every value was read and the
    payload SHA equals the file SHA. The record names the package fixture it was built for.
    #>
    param(
        [Parameter(Mandatory)]
        [string]$PackageDirectory,

        [Parameter(Mandatory)]
        [string]$RestoreManifestFileName,

        [Parameter(Mandatory)]
        [string]$PackageFixture
    )

    $fixture = Get-RestoreSmokePackageFixture -Name $PackageFixture
    $record = [ordered]@{
        PackageFixture          = $fixture.Name
        TemplateKind            = $fixture.TemplateKind
        PackageFile             = $null
        Sha256                  = $null
        PackageId               = $null
        PackageVersion          = $null
        AttestationPackageSha256 = $null
        AttestationProducer     = $null
        AttestationKeyId        = $null
        RestoreManifestProjects = $null
        RestoreManifestEffectiveSchemaHash = $null
        Verified                = $false
        Reason                  = $null
    }

    $packages = @(Get-ChildItem -LiteralPath $PackageDirectory -Filter "*.nupkg" -File | Where-Object { $_.Name -notlike "*.Attestation.*" })
    if ($packages.Count -ne 1) {
        $record.Reason = "expected one template .nupkg, found $($packages.Count)"
        return [pscustomobject]$record
    }
    $record.PackageFile = $packages[0].Name
    $record.Sha256 = Get-RestoreSmokeFileSha256 -Path $packages[0].FullName

    $attestationPath = "$($packages[0].FullName).attestation.json"
    if (-not (Test-Path -LiteralPath $attestationPath -PathType Leaf)) {
        $record.Reason = "attestation document '$([System.IO.Path]::GetFileName($attestationPath))' not found"
        return [pscustomobject]$record
    }
    $attestation = Get-Content -LiteralPath $attestationPath -Raw | ConvertFrom-Json
    $payload = [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String([string]$attestation.payloadB64)) | ConvertFrom-Json
    $record.PackageId = [string]$payload.packageId
    $record.PackageVersion = [string]$payload.packageVersion
    $record.AttestationPackageSha256 = ([string]$payload.packageSha256).ToLowerInvariant()
    $record.AttestationProducer = [string]$payload.producer
    $record.AttestationKeyId = [string]$attestation.signature.keyId

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($packages[0].FullName)
    try {
        $manifestEntry = @($archive.Entries | Where-Object { $_.Name -eq $RestoreManifestFileName })
        if ($manifestEntry.Count -ne 1) {
            $record.Reason = "expected one '$RestoreManifestFileName' in the package, found $($manifestEntry.Count)"
            return [pscustomobject]$record
        }
        $reader = [System.IO.StreamReader]::new($manifestEntry[0].Open())
        try {
            $manifest = $reader.ReadToEnd() | ConvertFrom-Json
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $archive.Dispose()
    }
    $record.RestoreManifestProjects = @($manifest.projects | ForEach-Object { [string]$_ })
    $hashProperty = $manifest.PSObject.Properties["effectiveSchemaHash"]
    if ($null -ne $hashProperty) {
        $record.RestoreManifestEffectiveSchemaHash = [string]$hashProperty.Value
    }

    if ($record.AttestationPackageSha256 -cne $record.Sha256) {
        $record.Reason = "the attestation payload records packageSha256 $($record.AttestationPackageSha256), but the file hashes to $($record.Sha256)"
        return [pscustomobject]$record
    }
    if ([string]::IsNullOrWhiteSpace($record.AttestationProducer) -or [string]::IsNullOrWhiteSpace($record.AttestationKeyId)) {
        $record.Reason = "the attestation lacks a producer or key id"
        return [pscustomobject]$record
    }

    $record.Verified = $true
    return [pscustomobject]$record
}

function ConvertTo-RestoreSmokeRedactedText {
    <#
    .SYNOPSIS
    Log-safe text in which every supplied secret (a client secret or a bearer token) is replaced by
    ***. Used for messages built from a helper's exception, whose text the probe does not control.
    #>
    param(
        [AllowNull()]
        [AllowEmptyString()]
        [string]$Value,

        [AllowNull()]
        [AllowEmptyCollection()]
        [string[]]$Secret = @()
    )

    $text = [string]$Value
    foreach ($item in @($Secret | Where-Object { -not [string]::IsNullOrEmpty($_) })) {
        $text = $text.Replace($item, "***")
    }
    return (ConvertTo-RestoreSmokeLogSafeText $text)
}

function Resolve-RestoreSmokeApiEndpoint {
    <#
    .SYNOPSIS
    The CMS and DMS base URLs and the bootstrap admin client of the stack an env file starts, read
    with the helpers the configure phase uses (Resolve-CmsBaseUrl, Resolve-DockerLocalDmsBaseUrl,
    Resolve-BootstrapAdminClient). The engine, Data Standard, and image overlays the wrappers compose
    set none of these keys. A multi-tenant stack is refused: its data stores are tenant-scoped and
    its DMS routes carry a tenant segment, which the probe does not model.

    It also returns the stack's DMS_CONFIG_DATABASE_ENCRYPTION_KEY (DataStoreEncryptionKey): CMS
    returns every data store connection string as Base64 AES cipher text under that key, so data
    store selection needs it. A stack without it is refused. The value is never logged or recorded.
    #>
    param(
        [Parameter(Mandatory)]
        [string]$EnvironmentFile
    )

    $values = ReadValuesFromEnvFile -EnvironmentFile $EnvironmentFile
    $multiTenancy = [string](Get-EnvValue -EnvValues $values -Name "DMS_CONFIG_MULTI_TENANCY" -DefaultValue "false")
    if ($multiTenancy.Trim().Trim('"').Trim("'") -ine "false") {
        throw "The served-data probe supports only a single-tenant stack, but DMS_CONFIG_MULTI_TENANCY is '$(ConvertTo-RestoreSmokeLogSafeText $multiTenancy)' in '$EnvironmentFile'."
    }

    $encryptionKey = [string](Get-ComposeResolvedEnvValue -EnvironmentValues $values -Name "DMS_CONFIG_DATABASE_ENCRYPTION_KEY")
    if ([string]::IsNullOrWhiteSpace($encryptionKey)) {
        throw "The served-data probe needs DMS_CONFIG_DATABASE_ENCRYPTION_KEY, the key CMS encrypts data store connection strings with, but it is not set for the stack started from '$EnvironmentFile'."
    }

    return [pscustomobject]@{
        CmsUrl                 = ([string](Resolve-CmsBaseUrl -EnvValues $values)).TrimEnd("/")
        DmsUrl                 = ([string](Resolve-DockerLocalDmsBaseUrl -EnvValues $values)).TrimEnd("/")
        AdminClient            = Resolve-BootstrapAdminClient -EnvValues $values
        DataStoreEncryptionKey = $encryptionKey
    }
}

function New-RestoreSmokeApiSession {
    <#
    .SYNOPSIS
    The served-data probe's per-stack credential cache. The first Test-RestoreSmokeApiRead against a
    started stack fills it (one smoke application, one DMS token); every later probe request against
    that stack reuses the token. Reset-RestoreSmokeApiSession empties it whenever the stack is torn
    down or started again. The token is held only here and is never logged or recorded.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Creates an in-memory record only.')]
    param()

    return [pscustomobject]@{
        StackGeneration   = 0
        Token             = $null
        DataStore         = $null
        TokenRequestCount = 0
    }
}

function Reset-RestoreSmokeApiSession {
    <#
    .SYNOPSIS
    Forgets the cached token and data-store selection, so the next probe obtains new ones. The smoke
    calls it before every teardown and every wrapper run, because each recreates the stack.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Clears an in-memory record only.')]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Session
    )

    $Session.Token = $null
    $Session.DataStore = $null
    $Session.StackGeneration++
}

function Get-RestoreSmokeDataStoreAesKey {
    <#
    .SYNOPSIS
    The AES key CMS derives from DMS_CONFIG_DATABASE_ENCRYPTION_KEY, exactly as
    ConnectionStringEncryptionService does: the key string padded with '0' to 32 characters, cut to
    its first 32, then UTF-8 encoded. Throws for a blank key, or for one whose first 32 characters
    do not encode to 32 bytes (CMS cannot use such a key either). Messages never carry the key.
    #>
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]$EncryptionKey
    )

    if ([string]::IsNullOrWhiteSpace($EncryptionKey)) {
        throw "Data store selection needs DMS_CONFIG_DATABASE_ENCRYPTION_KEY, the key CMS encrypts data store connection strings with, but it is not set."
    }
    $keyBytes = [System.Text.Encoding]::UTF8.GetBytes($EncryptionKey.PadRight(32, [char]'0').Substring(0, 32))
    if ($keyBytes.Length -ne 32) {
        throw "DMS_CONFIG_DATABASE_ENCRYPTION_KEY does not derive a 32-byte AES key: its first 32 characters encode to $($keyBytes.Length) UTF-8 bytes."
    }
    return , $keyBytes
}

function ConvertFrom-RestoreSmokeDataStoreCipherText {
    <#
    .SYNOPSIS
    Decrypts one connection string as a CMS data store response carries it: Base64 of a 16-byte IV
    followed by AES-CBC/PKCS7 cipher text (ConnectionStringEncryptionService.Decrypt). Returns
    PlainText, or a Reason when the value is not Base64, is too short or not whole AES blocks, does not
    decrypt under the key, or does not decrypt to UTF-8 text. Plaintext input is rejected, never parsed
    as a fallback. A Reason never carries the key, the value, its plaintext, or exception text.
    #>
    param(
        [AllowNull()]
        [AllowEmptyString()]
        [string]$CipherText,

        [Parameter(Mandatory)]
        [byte[]]$Key
    )

    $result = [pscustomobject]@{ PlainText = $null; Reason = $null }
    $payload = $null
    try {
        $payload = [System.Convert]::FromBase64String([string]$CipherText)
    }
    catch {
        $result.Reason = "its connection string is not CMS Base64 cipher text"
        return $result
    }
    if ($payload.Length -lt 32 -or ($payload.Length % 16) -ne 0) {
        $result.Reason = "its connection string cipher text is truncated or malformed ($($payload.Length) bytes; expected a 16-byte IV and whole 16-byte AES blocks)"
        return $result
    }

    $iv = [byte[]]::new(16)
    [System.Array]::Copy($payload, 0, $iv, 0, 16)
    $plainTextBytes = $null
    $aes = [System.Security.Cryptography.Aes]::Create()
    try {
        $aes.Mode = [System.Security.Cryptography.CipherMode]::CBC
        $aes.Padding = [System.Security.Cryptography.PaddingMode]::PKCS7
        $aes.Key = $Key
        $aes.IV = $iv
        $decryptor = $aes.CreateDecryptor()
        try {
            $plainTextBytes = $decryptor.TransformFinalBlock($payload, 16, $payload.Length - 16)
        }
        finally {
            $decryptor.Dispose()
        }
    }
    catch {
        $result.Reason = "its connection string could not be decrypted with DMS_CONFIG_DATABASE_ENCRYPTION_KEY"
        return $result
    }
    finally {
        $aes.Dispose()
    }

    try {
        # Strict decoding: bytes that are not UTF-8 mean the key was wrong even though the padding
        # happened to verify.
        $result.PlainText = [System.Text.UTF8Encoding]::new($false, $true).GetString($plainTextBytes)
    }
    catch {
        $result.Reason = "its connection string did not decrypt to UTF-8 text with DMS_CONFIG_DATABASE_ENCRYPTION_KEY"
    }
    return $result
}

function Select-RestoreSmokeDataStore {
    <#
    .SYNOPSIS
    The one route-unqualified CMS data store whose stored connection string has the selected engine's
    form and names the restored target database. No match, or more than one, throws, listing every
    data store by id and name with the reason it was or was not selected. Connection strings carry
    credentials, so only the parsed database name is ever reported.

    CMS returns each connection string as Base64 AES cipher text, so each one is decrypted with
    -EncryptionKey (the stack's DMS_CONFIG_DATABASE_ENCRYPTION_KEY) before it is parsed. A value
    that does not decrypt is not selected, with a reason that names no key or value. A blank key throws
    before any data store is examined.
    #>
    param(
        [AllowNull()]
        [AllowEmptyCollection()]
        [object[]]$DataStores,

        [Parameter(Mandatory)]
        [string]$TargetDatabaseName,

        [Parameter(Mandatory)]
        [ValidateSet("postgresql", "mssql")]
        [string]$DatabaseEngine,

        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]$EncryptionKey
    )

    $keyBytes = Get-RestoreSmokeDataStoreAesKey -EncryptionKey $EncryptionKey

    # The keys configure-local-data-store.ps1 writes: host/database for PostgreSQL, Server/Database
    # for SQL Server (Data Source / Initial Catalog are the SqlClient synonyms).
    $engineKeys = @("Host")
    $databaseKeys = @("Database")
    if ($DatabaseEngine -eq "mssql") {
        $engineKeys = @("Server", "Data Source")
        $databaseKeys = @("Database", "Initial Catalog")
    }

    $records = [System.Collections.Generic.List[object]]::new()
    foreach ($dataStore in @($DataStores | Where-Object { $null -ne $_ })) {
        $record = [ordered]@{ Id = $null; Name = $null; DatabaseName = $null; Selected = $false; Reason = $null }
        $records.Add($record)

        $record.Name = ConvertTo-RestoreSmokeLogSafeText ([string](Get-RestoreSmokeEvidenceValue $dataStore "name"))
        $parsedId = [long]0
        if (-not [long]::TryParse([string](Get-RestoreSmokeEvidenceValue $dataStore "id"), [System.Globalization.NumberStyles]::None, [System.Globalization.CultureInfo]::InvariantCulture, [ref]$parsedId) -or $parsedId -lt 1) {
            $record.Reason = "it has no positive integer id"
            continue
        }
        $record.Id = $parsedId

        $contextList = Get-RestoreSmokeEvidenceValue $dataStore "dataStoreContexts"
        if ($contextList -is [string]) {
            $record.Reason = "its route contexts are not a list"
            continue
        }
        $contexts = @()
        if ($null -ne $contextList) {
            $contexts = @(@($contextList) | Where-Object { $null -ne $_ })
        }
        if ($contexts.Count -gt 0) {
            $pairs = @($contexts | ForEach-Object {
                    "$(ConvertTo-RestoreSmokeLogSafeText ([string](Get-RestoreSmokeEvidenceValue $_ 'contextKey')))=$(ConvertTo-RestoreSmokeLogSafeText ([string](Get-RestoreSmokeEvidenceValue $_ 'contextValue')))"
                })
            $record.Reason = "it is route-qualified ($($pairs -join ', '))"
            continue
        }

        $cipherText = [string](Get-RestoreSmokeEvidenceValue $dataStore "connectionString")
        if ([string]::IsNullOrWhiteSpace($cipherText)) {
            $record.Reason = "CMS returned no connection string for it"
            continue
        }
        $decrypted = ConvertFrom-RestoreSmokeDataStoreCipherText -CipherText $cipherText -Key $keyBytes
        if ($null -ne $decrypted.Reason) {
            $record.Reason = $decrypted.Reason
            continue
        }
        $connectionString = $decrypted.PlainText
        $builder = [System.Data.Common.DbConnectionStringBuilder]::new()
        try {
            # The setter, not property syntax: PowerShell adapts the builder as a dictionary, so
            # "$builder.ConnectionString = ..." would only add a key named ConnectionString.
            $builder.set_ConnectionString($connectionString)
        }
        catch {
            $record.Reason = "its connection string could not be parsed"
            continue
        }
        if (@($engineKeys | Where-Object { $builder.ContainsKey($_) }).Count -eq 0) {
            $record.Reason = "its connection string is not the $DatabaseEngine form (no $($engineKeys -join ' or ') key)"
            continue
        }
        $databaseNames = @($databaseKeys | Where-Object { $builder.ContainsKey($_) } | ForEach-Object { [string]$builder[$_] })
        if ($databaseNames.Count -ne 1) {
            $record.Reason = "its connection string names $($databaseNames.Count) databases"
            continue
        }
        $record.DatabaseName = ConvertTo-RestoreSmokeLogSafeText $databaseNames[0]
        if ($databaseNames[0] -cne $TargetDatabaseName) {
            $record.Reason = "it targets database '$($record.DatabaseName)', not '$TargetDatabaseName'"
            continue
        }
        $record.Selected = $true
        $record.Reason = "route-unqualified $DatabaseEngine data store for '$TargetDatabaseName'"
    }

    $candidates = @($records | ForEach-Object { [pscustomobject]$_ })
    $selected = @($candidates | Where-Object { $_.Selected })
    $listing = ($candidates | ForEach-Object { "id=$($_.Id) name='$($_.Name)': $($_.Reason)" }) -join "; "
    if ($selected.Count -eq 0) {
        $detail = ""
        if ($candidates.Count -gt 0) {
            $detail = ": $listing"
        }
        throw "No route-unqualified data store targets the restored database '$TargetDatabaseName' ($($candidates.Count) listed$detail)."
    }
    if ($selected.Count -gt 1) {
        throw "$($selected.Count) route-unqualified data stores target the restored database '$TargetDatabaseName' (ids $(($selected | ForEach-Object { $_.Id }) -join ', ')); the probe needs exactly one. Listed: $listing."
    }

    return [pscustomobject]@{
        DataStoreId = $selected[0].Id
        Name        = $selected[0].Name
        Candidates  = $candidates
    }
}

function Get-RestoreSmokeJsonArrayLength {
    <#
    .SYNOPSIS
    Classifies a response body with System.Text.Json rather than ConvertFrom-Json, whose collection
    conversion hides the shape: PowerShell unrolls an empty array to nothing and a one-element array
    to its element, which then looks like an object. Returns the length only for a JSON array whose
    elements are all objects; anything else returns the reason it is not one.
    #>
    param(
        [AllowNull()]
        [AllowEmptyString()]
        [string]$Content
    )

    if ([string]::IsNullOrWhiteSpace($Content)) {
        return [pscustomobject]@{ IsArray = $false; Length = $null; Reason = "the body is empty" }
    }
    try {
        $document = [System.Text.Json.JsonDocument]::Parse($Content)
    }
    catch {
        return [pscustomobject]@{ IsArray = $false; Length = $null; Reason = "the body is not a single JSON value" }
    }
    try {
        $root = $document.RootElement
        if ($root.ValueKind -ne [System.Text.Json.JsonValueKind]::Array) {
            return [pscustomobject]@{ IsArray = $false; Length = $null; Reason = "the body's JSON root is $($root.ValueKind.ToString().ToLowerInvariant()), not an array" }
        }
        $index = 0
        foreach ($element in $root.EnumerateArray()) {
            if ($element.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) {
                return [pscustomobject]@{ IsArray = $false; Length = $null; Reason = "array element $index is $($element.ValueKind.ToString().ToLowerInvariant()), not a resource object" }
            }
            $index++
        }
        return [pscustomobject]@{ IsArray = $true; Length = $root.GetArrayLength(); Reason = $null }
    }
    finally {
        $document.Dispose()
    }
}

function Invoke-RestoreSmokeApiGet {
    <#
    .SYNOPSIS
    One authenticated GET, returning the status code and the raw body whatever the status.
    #>
    param(
        [Parameter(Mandatory)]
        [string]$Uri,

        [Parameter(Mandatory)]
        [string]$Token
    )

    $response = Invoke-WebRequest -Uri $Uri -Method Get -Headers @{ Authorization = "Bearer $Token" } -SkipHttpErrorCheck -TimeoutSec 30 -ErrorAction Stop
    $content = $response.Content
    if ($content -is [byte[]]) {
        $content = [System.Text.Encoding]::UTF8.GetString($content)
    }
    return [pscustomobject]@{ StatusCode = [int]$response.StatusCode; Content = [string]$content }
}

function Test-RestoreSmokeApiRead {
    <#
    .SYNOPSIS
    The authenticated served-data probe. Reads the restored data through the DMS API and throws
    unless every read returns HTTP 200 with a JSON array of resource objects. Returns the evidence
    record; no token or secret appears in it or in any message.

    .DESCRIPTION
    academicSubjectDescriptors must be a non-empty array. -SchemaOnly (the source was not seeded)
    accepts an empty array and reports the read as schema-only. -RequirePopulatedData also requires
    a non-empty schools array.

    Credentials and token budget: the first call against a stack lists the CMS data stores with the
    bootstrap admin client, selects the one route-unqualified data store for the restored target,
    creates the smoke application bound to it (Get-SmokeTestCredential), waits until CMS accepts the
    new client (Wait-CmsClientAvailable; OpenIddict surfaces new applications asynchronously), and
    calls Get-DmsToken once. Every later read against the same stack reuses that token, until
    Reset-RestoreSmokeApiSession. The CMS per-client token limit keeps its default.
    #>
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Session,

        [Parameter(Mandatory)]
        [pscustomobject]$Endpoint,

        [Parameter(Mandatory)]
        [string]$TargetDatabaseName,

        [Parameter(Mandatory)]
        [ValidateSet("postgresql", "mssql")]
        [string]$DatabaseEngine,

        [switch]$RequirePopulatedData,

        [switch]$SchemaOnly
    )

    if ($RequirePopulatedData -and $SchemaOnly) {
        throw "A Populated served-data read needs a seeded source; -SchemaOnly cannot be combined with -RequirePopulatedData."
    }

    $adminSecret = [string]$Endpoint.AdminClient.ClientSecret
    $encryptionKey = [string](Get-RestoreSmokeEvidenceValue $Endpoint "DataStoreEncryptionKey")
    $tokenReused = $null -ne $Session.Token
    if (-not $tokenReused) {
        $cmsToken = $null
        try {
            $cmsToken = Get-CmsToken -CmsUrl $Endpoint.CmsUrl -ClientId ([string]$Endpoint.AdminClient.ClientId) -ClientSecret $adminSecret
            $dataStores = @(Get-DataStore -CmsUrl $Endpoint.CmsUrl -AccessToken $cmsToken)
        }
        catch {
            throw "The served-data probe could not list the CMS data stores: $(ConvertTo-RestoreSmokeRedactedText $_.Exception.Message -Secret @($adminSecret, $cmsToken, $encryptionKey))"
        }
        $selection = Select-RestoreSmokeDataStore -DataStores $dataStores -TargetDatabaseName $TargetDatabaseName -DatabaseEngine $DatabaseEngine -EncryptionKey $encryptionKey

        $applicationSecret = $null
        $token = $null
        try {
            $credential = Get-SmokeTestCredential -ConfigServiceUrl $Endpoint.CmsUrl -DataStoreIds @($selection.DataStoreId)
            $applicationSecret = [string]$credential.Secret
            Wait-CmsClientAvailable -CmsUrl $Endpoint.CmsUrl -ClientId ([string]$credential.Key) -ClientSecret $applicationSecret
            $Session.TokenRequestCount++
            $token = Get-DmsToken -DmsUrl $Endpoint.DmsUrl -Key ([string]$credential.Key) -Secret $applicationSecret
        }
        catch {
            throw "The served-data probe could not obtain a DMS token for data store $($selection.DataStoreId): $(ConvertTo-RestoreSmokeRedactedText $_.Exception.Message -Secret @($adminSecret, $cmsToken, $applicationSecret, $token, $encryptionKey))"
        }
        if ([string]::IsNullOrWhiteSpace([string]$token)) {
            throw "The served-data probe received no DMS token for data store $($selection.DataStoreId)."
        }
        $Session.Token = [string]$token
        $Session.DataStore = $selection
    }

    $resources = [System.Collections.Generic.List[object]]::new()
    $resources.Add([pscustomobject]@{ Name = "academicSubjectDescriptors"; Path = $script:ApiProbeDescriptorPath; AllowEmpty = [bool]$SchemaOnly })
    if ($RequirePopulatedData) {
        $resources.Add([pscustomobject]@{ Name = "schools"; Path = $script:ApiProbeSchoolPath; AllowEmpty = $false })
    }

    $reads = [System.Collections.Generic.List[object]]::new()
    foreach ($resource in $resources) {
        $uri = "$($Endpoint.DmsUrl.TrimEnd('/'))/$($resource.Path)"
        try {
            $response = Invoke-RestoreSmokeApiGet -Uri $uri -Token $Session.Token
        }
        catch {
            throw "GET $uri failed: $(ConvertTo-RestoreSmokeRedactedText $_.Exception.Message -Secret @($Session.Token))"
        }
        if ($response.StatusCode -ne 200) {
            $body = ConvertTo-RestoreSmokeRedactedText $response.Content -Secret @($Session.Token)
            if ($body.Length -gt 200) {
                $body = $body.Substring(0, 200) + "..."
            }
            throw "GET $uri returned HTTP $($response.StatusCode); the served-data probe requires 200. Body: $body"
        }
        $shape = Get-RestoreSmokeJsonArrayLength -Content $response.Content
        if (-not $shape.IsArray) {
            throw "GET $uri returned HTTP 200, but $($shape.Reason); the served-data probe requires a JSON array of $($resource.Name)."
        }
        if ($shape.Length -lt 1 -and -not $resource.AllowEmpty) {
            throw "GET $uri returned an empty array; the restored $($resource.Name) were not served."
        }
        $reads.Add([pscustomobject]@{ Resource = $resource.Name; Uri = $uri; StatusCode = $response.StatusCode; Count = $shape.Length })
    }

    $mode = "seeded"
    if ($RequirePopulatedData) {
        $mode = "populated"
    }
    elseif ($SchemaOnly) {
        $mode = "schema-only"
    }
    return [pscustomobject]@{
        Mode                = $mode
        CmsUrl              = $Endpoint.CmsUrl
        DmsUrl              = $Endpoint.DmsUrl
        DataStoreId         = $Session.DataStore.DataStoreId
        DataStoreName       = $Session.DataStore.Name
        DataStoreCandidates = $Session.DataStore.Candidates
        StackGeneration     = $Session.StackGeneration
        TokenReused         = $tokenReused
        TokenRequestCount   = $Session.TokenRequestCount
        Reads               = @($reads)
    }
}

function Get-RestoreSmokeRestoreExecutionId {
    <#
    .SYNOPSIS
    The id of one successful restore a leg performs ("<leg>#<ordinal>"), which the smoke attaches to
    that restore's served-data API record. Throws for a leg or ordinal with no defined restore, so a
    probe can never be recorded under an id the classifier does not require.
    #>
    param(
        [Parameter(Mandatory)]
        [string]$Leg,

        [int]$Ordinal = 1
    )

    if (-not $script:RestoreLegExecutions.Contains($Leg)) {
        throw "Leg '$Leg' performs no successful restore, so it has no restore execution id."
    }
    $fixtures = @($script:RestoreLegExecutions[$Leg])
    if ($Ordinal -lt 1 -or $Ordinal -gt $fixtures.Count) {
        throw "Leg '$Leg' performs $($fixtures.Count) successful restore(s); restore $Ordinal is not defined."
    }
    return "$Leg#$Ordinal"
}

function Get-RestoreSmokeRestoreExecutionFixture {
    <#
    .SYNOPSIS
    The package fixture a restore execution ("<leg>#<ordinal>", Get-RestoreSmokeRestoreExecutionId)
    restores, from the same execution map the classifier derives its requirements from. Throws for
    an id that names no defined restore.
    #>
    param(
        [Parameter(Mandatory)]
        [string]$RestoreExecution
    )

    if ($RestoreExecution -notmatch '^(?<leg>[a-z-]+)#(?<ordinal>[1-9]\d*)$') {
        throw "'$RestoreExecution' is not a restore execution id ('<leg>#<ordinal>')."
    }
    $leg = $Matches["leg"]
    $ordinal = [int]$Matches["ordinal"]
    $null = Get-RestoreSmokeRestoreExecutionId -Leg $leg -Ordinal $ordinal
    return Get-RestoreSmokePackageFixture -Name @($script:RestoreLegExecutions[$leg])[$ordinal - 1]
}

function Get-RestoreSmokeRequiredApiRead {
    <#
    .SYNOPSIS
    The successful restores a run's selected legs perform, each requiring its own served-data API
    read and restored SourceIdentity (and, for a non-default selection, a selection proof): the
    package fixture it restores and that fixture's template kind, which decides which resources
    must be served. Negative legs require none; a leg that is neither is returned as unknown, so
    the classifier reports it instead of ignoring it.
    #>
    param(
        [AllowNull()]
        [AllowEmptyCollection()]
        [object[]]$Legs
    )

    $required = [System.Collections.Generic.List[object]]::new()
    $unknown = [System.Collections.Generic.List[string]]::new()
    foreach ($leg in @($Legs | Where-Object { $null -ne $_ } | ForEach-Object { [string]$_ } | Select-Object -Unique)) {
        if ($script:RestoreLegExecutions.Contains($leg)) {
            $fixtures = @($script:RestoreLegExecutions[$leg])
            for ($index = 0; $index -lt $fixtures.Count; $index++) {
                $fixture = Get-RestoreSmokePackageFixture -Name $fixtures[$index]
                $required.Add([pscustomobject]@{ RestoreExecution = "$leg#$($index + 1)"; Leg = $leg; PackageFixture = $fixture.Name; TemplateKind = $fixture.TemplateKind })
            }
        }
        elseif ($leg -notin $script:NegativeLegs) {
            $unknown.Add($leg)
        }
    }
    return [pscustomobject]@{ Required = @($required); UnknownLegs = @($unknown) }
}

function Get-RestoreSmokeApiReadDefect {
    <#
    .SYNOPSIS
    Why one served-data API record does not prove its restore served the template's data: the mode
    must match the template kind (a schema-only read never does), the record must name a data store,
    and each required resource must have exactly one read with HTTP 200 and at least one row.
    Returns no reason for a complete record. Only ids, resources, statuses, and counts are reported.
    #>
    param(
        [AllowNull()]
        [object]$Record,

        [Parameter(Mandatory)]
        [ValidateSet("Minimal", "Populated")]
        [string]$TemplateKind
    )

    $defects = [System.Collections.Generic.List[string]]::new()
    $mode = ConvertTo-RestoreSmokeLogSafeText ([string](Get-RestoreSmokeEvidenceValue $Record "Mode"))
    if ($mode -ceq "schema-only") {
        $defects.Add("the API read was schema-only (unseeded source), which cannot prove seeded served data")
        return @($defects)
    }
    $expectedMode = "seeded"
    if ($TemplateKind -eq "Populated") {
        $expectedMode = "populated"
    }
    if ($mode -cne $expectedMode) {
        $defects.Add("the API read mode is '$mode', expected '$expectedMode' for a $TemplateKind restore")
    }

    $dataStoreId = [long]0
    if (-not [long]::TryParse([string](Get-RestoreSmokeEvidenceValue $Record "DataStoreId"), [System.Globalization.NumberStyles]::None, [System.Globalization.CultureInfo]::InvariantCulture, [ref]$dataStoreId) -or $dataStoreId -lt 1) {
        $defects.Add("the API read names no data store")
    }

    $readList = Get-RestoreSmokeEvidenceValue $Record "Reads"
    $reads = @()
    if ($null -ne $readList) {
        $reads = @(@($readList) | Where-Object { $null -ne $_ })
    }
    foreach ($resource in @($script:ApiReadResources[$TemplateKind])) {
        $matching = @($reads | Where-Object { [string](Get-RestoreSmokeEvidenceValue $_ "Resource") -ceq $resource })
        if ($matching.Count -eq 0) {
            $defects.Add("no $resource read was recorded")
            continue
        }
        if ($matching.Count -gt 1) {
            $defects.Add("$($matching.Count) $resource reads were recorded; expected exactly one")
            continue
        }
        $status = [string](Get-RestoreSmokeEvidenceValue $matching[0] "StatusCode")
        if ($status -cne "200") {
            $defects.Add("the $resource read returned HTTP $(ConvertTo-RestoreSmokeLogSafeText $status), not 200")
        }
        $countText = [string](Get-RestoreSmokeEvidenceValue $matching[0] "Count")
        $count = [long]0
        if (-not [long]::TryParse($countText, [System.Globalization.NumberStyles]::None, [System.Globalization.CultureInfo]::InvariantCulture, [ref]$count)) {
            $defects.Add("the $resource read recorded no row count")
        }
        elseif ($count -lt 1) {
            $defects.Add("the $resource read returned no rows")
        }
    }
    return @($defects)
}

function Get-RestoreSmokeSourceIdentityValue {
    <#
    .SYNOPSIS
    Judges the rows a dms.DataStoreIdentity.SourceIdentity SELECT returned: exactly one non-blank
    row holding a UUID in its canonical 36-character form that is not the zero UUID. Returns the
    value in lower case, or null with the defect. The classifier calls it on the recorded rows, so a
    stored Identity field is never trusted on its own.
    #>
    param(
        [AllowNull()]
        [AllowEmptyCollection()]
        [object[]]$Row
    )

    $rows = @(@($Row) | Where-Object { $null -ne $_ } | ForEach-Object { ([string]$_).Trim() } | Where-Object { $_ -ne "" })
    if ($rows.Count -ne 1) {
        return [pscustomobject]@{ Identity = $null; Defect = "has $($rows.Count) rows; expected exactly one" }
    }
    $parsed = [System.Guid]::Empty
    if (-not [System.Guid]::TryParseExact($rows[0], "D", [ref]$parsed)) {
        return [pscustomobject]@{ Identity = $null; Defect = "is not a UUID ('$(ConvertTo-RestoreSmokeLogSafeText $rows[0])')" }
    }
    if ($parsed -eq [System.Guid]::Empty) {
        return [pscustomobject]@{ Identity = $null; Defect = "is the zero UUID" }
    }
    return [pscustomobject]@{ Identity = $parsed.ToString("D"); Defect = $null }
}

function Invoke-RestoreSmokeDockerCommand {
    <#
    .SYNOPSIS
    Runs docker with an argument list and returns its exit code and output lines. A nonzero exit or
    a docker that cannot be invoked is returned, never thrown, so the caller records it.
    #>
    param(
        [Parameter(Mandatory)]
        [string[]]$ArgumentList
    )

    $global:LASTEXITCODE = 0
    try {
        $output = @(docker @ArgumentList 2>&1 | ForEach-Object { [string]$_ })
        $exitCode = $LASTEXITCODE
    }
    catch {
        return [pscustomobject]@{ ExitCode = -1; Output = @("docker could not be invoked: $($_.Exception.Message)") }
    }
    return [pscustomobject]@{ ExitCode = $exitCode; Output = $output }
}

function Get-RestoreSmokeMssqlPassword {
    # The sa password the restore consumer and the smoke's own SQL use.
    return ($env:MSSQL_SA_PASSWORD ?? "abcdefgh1!")
}

function Get-RestoreSmokeDockerFailureText {
    <#
    .SYNOPSIS
    The last lines of a failed docker command's output as one log-safe line, with the sa password
    redacted. A replay's output can run to thousands of lines; the error is at the end.
    #>
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Result
    )

    $tail = @($Result.Output | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Last 5)
    return ConvertTo-RestoreSmokeRedactedText -Value ($tail -join " ") -Secret @(Get-RestoreSmokeMssqlPassword)
}

function Invoke-RestoreSmokeEngineQuery {
    <#
    .SYNOPSIS
    One query against a database in the engine container, through the transports the restore
    consumer uses (psql -tA; sqlcmd -h -1 -W). Returns the exit code, the non-blank output rows
    (trimmed, log-safe), and on failure the redacted error text. Never throws for a failed query.
    #>
    param(
        [Parameter(Mandatory)]
        [ValidateSet("postgresql", "mssql")]
        [string]$DatabaseEngine,

        [Parameter(Mandatory)]
        [string]$DatabaseName,

        [Parameter(Mandatory)]
        [string]$Query,

        [string]$ContainerName
    )

    if ([string]::IsNullOrWhiteSpace($ContainerName)) {
        $ContainerName = $script:EngineContainerNames[$DatabaseEngine]
    }
    if ($DatabaseEngine -eq "mssql") {
        $arguments = @("exec", "-e", "SQLCMDPASSWORD=$(Get-RestoreSmokeMssqlPassword)", $ContainerName, "/opt/mssql-tools18/bin/sqlcmd", "-S", "localhost", "-U", "sa", "-d", $DatabaseName, "-C", "-b", "-h", "-1", "-W", "-Q", $Query)
    }
    else {
        $arguments = @("exec", $ContainerName, "psql", "-U", "postgres", "-d", $DatabaseName, "-tA", "-c", $Query)
    }
    $result = Invoke-RestoreSmokeDockerCommand -ArgumentList $arguments
    $rows = @()
    $errorText = $null
    if ($result.ExitCode -eq 0) {
        $rows = @($result.Output | ForEach-Object { ConvertTo-RestoreSmokeLogSafeText $_ } | Where-Object { $_ -ne "" })
    }
    else {
        $errorText = Get-RestoreSmokeDockerFailureText -Result $result
    }
    return [pscustomobject]@{ ExitCode = $result.ExitCode; Rows = $rows; Error = $errorText }
}

function Get-RestoreSmokeSourceIdentityRead {
    <#
    .SYNOPSIS
    Reads dms.DataStoreIdentity.SourceIdentity from one database with the restore consumer's own
    SELECT (Get-SourceIdentitySelectSql) and judges the rows with Get-RestoreSmokeSourceIdentityValue.
    Read-only: it never issues the reseed. The record keeps the rows, so the classifier can judge them
    again; Reason is null only for exactly one valid, nonzero UUID.
    #>
    param(
        [Parameter(Mandatory)]
        [ValidateSet("postgresql", "mssql")]
        [string]$DatabaseEngine,

        [Parameter(Mandatory)]
        [string]$DatabaseName,

        [string]$ContainerName
    )

    $result = Invoke-RestoreSmokeEngineQuery -DatabaseEngine $DatabaseEngine -DatabaseName $DatabaseName -ContainerName $ContainerName -Query (Get-SourceIdentitySelectSql -DatabaseEngine $DatabaseEngine)
    $record = [ordered]@{
        DatabaseName = $DatabaseName
        CapturedUtc  = [System.DateTime]::UtcNow.ToString("o", [System.Globalization.CultureInfo]::InvariantCulture)
        ExitCode     = $result.ExitCode
        Rows         = @($result.Rows)
        Identity     = $null
        Reason       = $null
    }
    if ($result.ExitCode -ne 0) {
        $record.Reason = "the SourceIdentity query against '$DatabaseName' exited $($result.ExitCode): $($result.Error)"
        return [pscustomobject]$record
    }
    $value = Get-RestoreSmokeSourceIdentityValue -Row $record.Rows
    $record.Identity = $value.Identity
    if ($null -ne $value.Defect) {
        $record.Reason = "dms.DataStoreIdentity.SourceIdentity in '$DatabaseName' $($value.Defect)"
    }
    return [pscustomobject]$record
}

function Invoke-RestoreSmokeIdentityBoundPackageBuild {
    <#
    .SYNOPSIS
    Builds one template package with its source database's SourceIdentity read immediately before
    the build and again after it, and binds both reads to the built .nupkg's file name and SHA-256
    (Get-RestoreSmokePackageProvenance hashes the exact file).

    .DESCRIPTION
    The binding is added to -BindingList before anything runs, and the package record to
    -PackageList as soon as it exists, so a failure keeps the evidence gathered so far. Throws, with
    the binding's Reason set, when the source identity is not exactly one valid, nonzero UUID (then
    nothing is built), when the package provenance cannot be established, or when the identity read
    after the build is invalid or differs from the one before it. Bound is true only when none of
    those happened. Each package fixture gets its own binding, so packages never overwrite each other.
    #>
    param(
        [Parameter(Mandatory)]
        [string]$PackageFixture,

        [Parameter(Mandatory)]
        [ValidateSet("postgresql", "mssql")]
        [string]$DatabaseEngine,

        [Parameter(Mandatory)]
        [string]$SourceDatabaseName,

        [Parameter(Mandatory)]
        [string]$PackageDirectory,

        [Parameter(Mandatory)]
        [string]$RestoreManifestFileName,

        [Parameter(Mandatory)]
        [scriptblock]$BuildPackage,

        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [System.Collections.Generic.List[object]]$BindingList,

        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [System.Collections.Generic.List[object]]$PackageList
    )

    $fixture = Get-RestoreSmokePackageFixture -Name $PackageFixture
    $binding = [pscustomobject]@{
        PackageFixture = $fixture.Name
        TemplateKind   = $fixture.TemplateKind
        SourceDatabase = $SourceDatabaseName
        BeforeBackup   = $null
        AfterBackup    = $null
        PackageFile    = $null
        PackageSha256  = $null
        Bound          = $false
        Reason         = "the capture did not complete"
    }
    $BindingList.Add($binding)

    $binding.BeforeBackup = Get-RestoreSmokeSourceIdentityRead -DatabaseEngine $DatabaseEngine -DatabaseName $SourceDatabaseName
    if ($null -ne $binding.BeforeBackup.Reason) {
        $binding.Reason = "before the backup, $($binding.BeforeBackup.Reason)"
        throw "Cannot bind the $($fixture.Name) package to its source's SourceIdentity: $($binding.Reason). Nothing was built."
    }

    # The build's own output (for example `dotnet pack` stdout) goes to the host, so it stays in
    # the log but never becomes part of this function's result.
    & $BuildPackage | Out-Host

    $package = Get-RestoreSmokePackageProvenance -PackageDirectory $PackageDirectory -RestoreManifestFileName $RestoreManifestFileName -PackageFixture $fixture.Name
    $PackageList.Add($package)
    $binding.PackageFile = $package.PackageFile
    $binding.PackageSha256 = $package.Sha256
    $binding.AfterBackup = Get-RestoreSmokeSourceIdentityRead -DatabaseEngine $DatabaseEngine -DatabaseName $SourceDatabaseName

    if (-not $package.Verified) {
        $binding.Reason = "the package provenance could not be established: $($package.Reason)"
        throw "Package provenance could not be established: $($package.Reason)"
    }
    if ($null -ne $binding.AfterBackup.Reason) {
        $binding.Reason = "after the backup, $($binding.AfterBackup.Reason)"
        throw "Cannot bind the $($fixture.Name) package to its source's SourceIdentity: $($binding.Reason)."
    }
    if ($binding.AfterBackup.Identity -cne $binding.BeforeBackup.Identity) {
        $binding.Reason = "the source's SourceIdentity changed across the backup (before $($binding.BeforeBackup.Identity), after $($binding.AfterBackup.Identity))"
        throw "Cannot bind the $($fixture.Name) package to its source's SourceIdentity: $($binding.Reason)."
    }

    $binding.Bound = $true
    $binding.Reason = $null
    return [pscustomobject]@{ Binding = $binding; Package = $package }
}

function Invoke-RestoreSmokePackageInspection {
    <#
    .SYNOPSIS
    The package's own SourceIdentity, read independently of the restore: the artifact inside the
    exact .nupkg is replayed (PostgreSQL) or restored (SQL Server) into a run-owned scratch database
    restore_smoke_inspect_<12 hex> in the running engine container, and dms.DataStoreIdentity is
    selected from it.

    .DESCRIPTION
    The inspection never reseeds and never reads the identity from the artifact's text. It never
    throws: Reason names what failed, and Cleanup records what was removed. Only what this inspection
    created is removed: the database only after it was proven absent before creation (DatabaseOwned),
    the in-container copy only after a copy was attempted, and the local extraction directory.
    Cleanup.Complete is true only when each of those is shown gone. No PostgreSQL global role is
    created: the inspection runs after a successful restore on the same server, which already
    ensured the role the dump references, and it would not be removed again.
    #>
    param(
        [Parameter(Mandatory)]
        [string]$PackagePath,

        [Parameter(Mandatory)]
        [ValidateSet("Minimal", "Populated")]
        [string]$TemplateKind,

        [Parameter(Mandatory)]
        [ValidateSet("postgresql", "mssql")]
        [string]$DatabaseEngine,

        [Parameter(Mandatory)]
        [string]$WorkDirectory,

        [Parameter(Mandatory)]
        [string]$RestoreManifestFileName,

        [string]$ContainerName
    )

    if ([string]::IsNullOrWhiteSpace($ContainerName)) {
        $ContainerName = $script:EngineContainerNames[$DatabaseEngine]
    }
    $suffix = [System.Guid]::NewGuid().ToString("N").Substring(0, 12)
    $databaseName = "restore_smoke_inspect_$suffix"
    $adminDatabase = if ($DatabaseEngine -eq "mssql") { "master" } else { "postgres" }
    $presenceQuery = if ($DatabaseEngine -eq "mssql") {
        "SET NOCOUNT ON; SELECT CASE WHEN DB_ID(N'$databaseName') IS NULL THEN 0 ELSE 1 END;"
    }
    else {
        "SELECT COUNT(*) FROM pg_database WHERE datname = '$databaseName';"
    }
    $queryArguments = @{ DatabaseEngine = $DatabaseEngine; ContainerName = $ContainerName }

    $cleanup = [pscustomobject]@{
        DatabaseOwned         = $false
        DatabaseDropped       = $null
        DatabaseAbsent        = $null
        ContainerFile         = $null
        ContainerFileRemoved  = $null
        LocalDirectoryRemoved = $null
        Complete              = $false
        Reasons               = [System.Collections.Generic.List[string]]::new()
    }
    $record = [pscustomobject]@{
        TemplateKind       = $TemplateKind
        DatabaseEngine     = $DatabaseEngine
        PackageFile        = [System.IO.Path]::GetFileName($PackagePath)
        PackageSha256      = $null
        ArtifactFileName   = $null
        ArtifactSha256     = $null
        InspectionDatabase = $databaseName
        Rows               = @()
        Identity           = $null
        Reason             = $null
        Cleanup            = $cleanup
    }
    $localDirectory = Join-Path $WorkDirectory "inspect-$suffix"

    try {
        $record.PackageSha256 = Get-RestoreSmokeFileSha256 -Path $PackagePath
        New-Item -ItemType Directory -Path $localDirectory -Force | Out-Null

        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $archive = [System.IO.Compression.ZipFile]::OpenRead($PackagePath)
        try {
            $manifestEntries = @($archive.Entries | Where-Object { $_.Name -eq $RestoreManifestFileName })
            if ($manifestEntries.Count -ne 1) {
                throw "expected one '$RestoreManifestFileName' in the package, found $($manifestEntries.Count)"
            }
            $reader = [System.IO.StreamReader]::new($manifestEntries[0].Open())
            try {
                $manifest = $reader.ReadToEnd() | ConvertFrom-Json
            }
            finally {
                $reader.Dispose()
            }
            $record.ArtifactFileName = ConvertTo-RestoreSmokeLogSafeText ([string]$manifest.artifactFileName)
            if ([string]::IsNullOrWhiteSpace($record.ArtifactFileName) -or $record.ArtifactFileName -ne [System.IO.Path]::GetFileName($record.ArtifactFileName)) {
                throw "the restore manifest names no usable artifactFileName"
            }
            $artifactEntries = @($archive.Entries | Where-Object { $_.Name -eq $record.ArtifactFileName })
            if ($artifactEntries.Count -ne 1) {
                throw "expected one '$($record.ArtifactFileName)' in the package, found $($artifactEntries.Count)"
            }
            $localArtifact = Join-Path $localDirectory $record.ArtifactFileName
            [System.IO.Compression.ZipFileExtensions]::ExtractToFile($artifactEntries[0], $localArtifact)
        }
        finally {
            $archive.Dispose()
        }

        $record.ArtifactSha256 = Get-RestoreSmokeFileSha256 -Path $localArtifact
        $manifestArtifactSha256 = ([string]$manifest.artifactSha256).ToLowerInvariant()
        if ($record.ArtifactSha256 -cne $manifestArtifactSha256) {
            throw "the artifact hashes to $($record.ArtifactSha256), but the restore manifest records $(ConvertTo-RestoreSmokeLogSafeText $manifestArtifactSha256)"
        }

        # Ownership: the generated name must be absent before this inspection creates it.
        $presence = Invoke-RestoreSmokeEngineQuery @queryArguments -DatabaseName $adminDatabase -Query $presenceQuery
        if ($presence.ExitCode -ne 0) {
            throw "could not establish that '$databaseName' is absent (exit $($presence.ExitCode): $($presence.Error))"
        }
        if ((@($presence.Rows) -join "|") -cne "0") {
            throw "'$databaseName' already exists; it is not this inspection's and is left in place"
        }
        $cleanup.DatabaseOwned = $true

        if ($DatabaseEngine -eq "postgresql") {
            $created = Invoke-RestoreSmokeEngineQuery @queryArguments -DatabaseName $adminDatabase -Query "CREATE DATABASE `"$databaseName`";"
            if ($created.ExitCode -ne 0) {
                throw "CREATE DATABASE $databaseName exited $($created.ExitCode): $($created.Error)"
            }
            $cleanup.ContainerFile = "/tmp/restore-smoke-inspect-$suffix.sql"
            $copied = Invoke-RestoreSmokeDockerCommand -ArgumentList @("cp", $localArtifact, "$($ContainerName):$($cleanup.ContainerFile)")
            if ($copied.ExitCode -ne 0) {
                throw "docker cp of the artifact exited $($copied.ExitCode): $(Get-RestoreSmokeDockerFailureText -Result $copied)"
            }
            $replayed = Invoke-RestoreSmokeDockerCommand -ArgumentList @("exec", $ContainerName, "psql", "-U", "postgres", "-d", $databaseName, "-v", "ON_ERROR_STOP=1", "-f", $cleanup.ContainerFile)
            if ($replayed.ExitCode -ne 0) {
                throw "the replay into $databaseName exited $($replayed.ExitCode): $(Get-RestoreSmokeDockerFailureText -Result $replayed)"
            }
        }
        else {
            $cleanup.ContainerFile = "/var/opt/mssql/data/restore-smoke-inspect-$suffix.bak"
            $copied = Invoke-RestoreSmokeDockerCommand -ArgumentList @("cp", $localArtifact, "$($ContainerName):$($cleanup.ContainerFile)")
            if ($copied.ExitCode -ne 0) {
                throw "docker cp of the artifact exited $($copied.ExitCode): $(Get-RestoreSmokeDockerFailureText -Result $copied)"
            }
            $fileList = Invoke-RestoreSmokeDockerCommand -ArgumentList @("exec", "-e", "SQLCMDPASSWORD=$(Get-RestoreSmokeMssqlPassword)", $ContainerName, "/opt/mssql-tools18/bin/sqlcmd", "-S", "localhost", "-U", "sa", "-d", "master", "-C", "-b", "-h", "-1", "-W", "-s", "|", "-Q", "SET NOCOUNT ON; RESTORE FILELISTONLY FROM DISK = N'$($cleanup.ContainerFile)';")
            if ($fileList.ExitCode -ne 0) {
                throw "RESTORE FILELISTONLY exited $($fileList.ExitCode): $(Get-RestoreSmokeDockerFailureText -Result $fileList)"
            }
            $backupFiles = ConvertFrom-MssqlBackupFileList -FileListOutput ([string[]]$fileList.Output) -BackupFileName $record.ArtifactFileName
            $moveClauses = New-MssqlRestoreMoveClause -DatabaseName $databaseName -DataLogicalNames $backupFiles.DataLogicalNames -LogLogicalNames $backupFiles.LogLogicalNames -BackupFileName $record.ArtifactFileName
            # No REPLACE: the restore must create the database, never overwrite one.
            $restored = Invoke-RestoreSmokeDockerCommand -ArgumentList @("exec", "-e", "SQLCMDPASSWORD=$(Get-RestoreSmokeMssqlPassword)", $ContainerName, "/opt/mssql-tools18/bin/sqlcmd", "-S", "localhost", "-U", "sa", "-d", "master", "-C", "-b", "-Q", "RESTORE DATABASE [$databaseName] FROM DISK = N'$($cleanup.ContainerFile)' WITH $($moveClauses -join ', ');")
            if ($restored.ExitCode -ne 0) {
                throw "RESTORE DATABASE $databaseName exited $($restored.ExitCode): $(Get-RestoreSmokeDockerFailureText -Result $restored)"
            }
        }

        $read = Get-RestoreSmokeSourceIdentityRead -DatabaseEngine $DatabaseEngine -DatabaseName $databaseName -ContainerName $ContainerName
        $record.Rows = @($read.Rows)
        $record.Identity = $read.Identity
        if ($read.ExitCode -ne 0) {
            $record.Reason = $read.Reason
        }
        elseif ($null -ne $read.Reason) {
            $record.Reason = "the package's $($read.Reason)"
        }
    }
    catch {
        $record.Reason = ConvertTo-RestoreSmokeRedactedText -Value $_.Exception.Message -Secret @(Get-RestoreSmokeMssqlPassword)
    }
    finally {
        if ($cleanup.DatabaseOwned) {
            # Assigned directly, never through an if expression: an if expression unrolls arrays.
            $dropQueries = @(
                "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '$databaseName' AND pid <> pg_backend_pid();",
                "DROP DATABASE IF EXISTS `"$databaseName`";"
            )
            if ($DatabaseEngine -eq "mssql") {
                $dropQueries = @("IF DB_ID(N'$databaseName') IS NOT NULL BEGIN IF DATABASEPROPERTYEX(N'$databaseName', 'Status') = N'ONLINE' ALTER DATABASE [$databaseName] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$databaseName]; END")
            }
            $cleanup.DatabaseDropped = $true
            foreach ($dropQuery in $dropQueries) {
                $dropped = Invoke-RestoreSmokeEngineQuery @queryArguments -DatabaseName $adminDatabase -Query $dropQuery
                if ($dropped.ExitCode -ne 0) {
                    $cleanup.DatabaseDropped = $false
                    $cleanup.Reasons.Add("dropping $databaseName exited $($dropped.ExitCode): $($dropped.Error)")
                    break
                }
            }
            $absent = Invoke-RestoreSmokeEngineQuery @queryArguments -DatabaseName $adminDatabase -Query $presenceQuery
            $cleanup.DatabaseAbsent = ($absent.ExitCode -eq 0 -and (@($absent.Rows) -join "|") -ceq "0")
            if (-not $cleanup.DatabaseAbsent) {
                $cleanup.Reasons.Add("$databaseName was not shown absent after the drop")
            }
        }
        if ($null -ne $cleanup.ContainerFile) {
            $removed = Invoke-RestoreSmokeDockerCommand -ArgumentList @("exec", $ContainerName, "rm", "-f", $cleanup.ContainerFile)
            $cleanup.ContainerFileRemoved = ($removed.ExitCode -eq 0)
            if (-not $cleanup.ContainerFileRemoved) {
                $cleanup.Reasons.Add("removing $($cleanup.ContainerFile) from $ContainerName exited $($removed.ExitCode): $(Get-RestoreSmokeDockerFailureText -Result $removed)")
            }
        }
        if (Test-Path -LiteralPath $localDirectory) {
            Remove-Item -LiteralPath $localDirectory -Recurse -Force -ErrorAction SilentlyContinue
        }
        $cleanup.LocalDirectoryRemoved = -not (Test-Path -LiteralPath $localDirectory)
        if (-not $cleanup.LocalDirectoryRemoved) {
            $cleanup.Reasons.Add("the local extraction directory could not be removed")
        }
        $cleanup.Complete = ($cleanup.Reasons.Count -eq 0)
    }

    return $record
}

function Get-RestoreSmokeRestoredIdentityDefect {
    <#
    .SYNOPSIS
    Why a restored target's SourceIdentity rows do not prove a new identity: they must hold exactly
    one valid, nonzero UUID that differs from the package's inspected identity and from the identity
    of every earlier restore. Returns no defect for a proven identity. A blank -PackageIdentity skips
    only that comparison; the caller reports why the package identity is unknown.
    #>
    param(
        [AllowNull()]
        [AllowEmptyCollection()]
        [object[]]$Row,

        [AllowNull()]
        [AllowEmptyString()]
        [string]$PackageIdentity,

        # Earlier restores' records ({ RestoreExecution, Rows }), in restore order.
        [AllowNull()]
        [AllowEmptyCollection()]
        [object[]]$EarlierRestore = @()
    )

    $value = Get-RestoreSmokeSourceIdentityValue -Row $Row
    if ($null -ne $value.Defect) {
        return @("the restored SourceIdentity $($value.Defect)")
    }
    $defects = [System.Collections.Generic.List[string]]::new()
    if (-not [string]::IsNullOrWhiteSpace($PackageIdentity) -and $value.Identity -eq $PackageIdentity.Trim()) {
        $defects.Add("the restored SourceIdentity $($value.Identity) equals the package's inspected SourceIdentity")
    }
    foreach ($earlier in @($EarlierRestore | Where-Object { $null -ne $_ })) {
        $earlierRows = Get-RestoreSmokeEvidenceList $earlier "Rows"
        $earlierValue = Get-RestoreSmokeSourceIdentityValue -Row $earlierRows
        if ($null -ne $earlierValue.Identity -and $earlierValue.Identity -eq $value.Identity) {
            $defects.Add("the restored SourceIdentity $($value.Identity) equals restore $(ConvertTo-RestoreSmokeLogSafeText ([string](Get-RestoreSmokeEvidenceValue $earlier 'RestoreExecution')))'s")
        }
    }
    return @($defects)
}

function ConvertTo-RestoreSmokeProjectSchemaName {
    <#
    .SYNOPSIS
    A project endpoint name as the resource schema the database uses: lowercase, hyphens removed
    ('ed-fi' -> 'edfi'), the rule the restore cross-check applies (ConvertTo-RestoreProjectSchemaName).
    #>
    param(
        [AllowEmptyString()]
        [string]$ProjectEndpointName
    )

    return $ProjectEndpointName.ToLowerInvariant().Replace("-", "")
}

function Format-RestoreSmokeNameSet {
    # A set of names for a reason: log-safe, ordinal-sorted, bracketed.
    param(
        [AllowNull()]
        [AllowEmptyCollection()]
        [object[]]$Name
    )

    $names = [System.Collections.Generic.List[string]]::new()
    foreach ($item in @($Name | Where-Object { $null -ne $_ })) {
        $names.Add((ConvertTo-RestoreSmokeLogSafeText ([string]$item)))
    }
    $names.Sort([System.StringComparer]::Ordinal)
    return "[" + ($names -join ", ") + "]"
}

function Test-RestoreSmokeNameSetEqual {
    # True when both sets hold the same distinct names and neither repeats one.
    param(
        [AllowNull()]
        [AllowEmptyCollection()]
        [object[]]$Actual,

        [AllowNull()]
        [AllowEmptyCollection()]
        [object[]]$Expected,

        [switch]$IgnoreCase
    )

    $comparer = if ($IgnoreCase) { [System.StringComparer]::OrdinalIgnoreCase } else { [System.StringComparer]::Ordinal }
    $actualNames = @($Actual | Where-Object { $null -ne $_ } | ForEach-Object { [string]$_ })
    $expectedNames = @($Expected | Where-Object { $null -ne $_ } | ForEach-Object { [string]$_ })
    $actualSet = [System.Collections.Generic.HashSet[string]]::new([string[]]$actualNames, $comparer)
    $expectedSet = [System.Collections.Generic.HashSet[string]]::new([string[]]$expectedNames, $comparer)
    if ($actualSet.Count -ne $actualNames.Count -or $expectedSet.Count -ne $expectedNames.Count) {
        return $false
    }
    return $actualSet.SetEquals($expectedSet)
}

function Read-RestoreSmokeWorkspaceSelection {
    <#
    .SYNOPSIS
    The schema selection the active bootstrap workspace records: bootstrap-manifest.json's
    schema.selectedPackages (as prepare-dms-schema.ps1 staged them from the effective
    SCHEMA_PACKAGES), schema.selectedExtensions, schema.effectiveSchemaHash, and the core project's
    endpoint from the staged ApiSchema manifest, with the resulting project schema names. Never
    throws: anything unreadable is a Reason. The ApiSchema manifest path must stay inside the
    workspace (relative, no empty, '.' or '..' segment).
    #>
    param(
        [Parameter(Mandatory)]
        [string]$BootstrapRoot
    )

    $record = [ordered]@{
        Manifest                = "bootstrap-manifest.json"
        SelectedPackages        = $null
        SelectedExtensions      = $null
        CoreProjectEndpointName = $null
        ProjectSchemas          = $null
        EffectiveSchemaHash     = $null
        Reason                  = $null
    }
    try {
        $manifestPath = Join-Path $BootstrapRoot "bootstrap-manifest.json"
        if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
            $record.Reason = "the active workspace has no bootstrap-manifest.json"
            return [pscustomobject]$record
        }
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -AsHashtable
        if ($manifest -isnot [System.Collections.IDictionary] -or -not $manifest.Contains("schema") -or $manifest["schema"] -isnot [System.Collections.IDictionary]) {
            $record.Reason = "bootstrap-manifest.json has no schema section"
            return [pscustomobject]$record
        }
        $schema = $manifest["schema"]

        if ($schema.Contains("selectedPackages") -and $schema["selectedPackages"] -is [System.Collections.IList]) {
            $record.SelectedPackages = @($schema["selectedPackages"] | ForEach-Object { ConvertTo-RestoreSmokeLogSafeText ([string]$_) })
        }
        $extensions = @()
        if ($schema.Contains("selectedExtensions") -and $null -ne $schema["selectedExtensions"]) {
            if ($schema["selectedExtensions"] -isnot [System.Collections.IList]) {
                $record.Reason = "schema.selectedExtensions is not an array"
                return [pscustomobject]$record
            }
            $extensions = @($schema["selectedExtensions"] | ForEach-Object { ConvertTo-RestoreSmokeLogSafeText ([string]$_) })
        }
        $record.SelectedExtensions = $extensions
        if ($schema.Contains("effectiveSchemaHash")) {
            $record.EffectiveSchemaHash = ConvertTo-RestoreSmokeLogSafeText ([string]$schema["effectiveSchemaHash"])
        }

        $relativePath = if ($schema.Contains("apiSchemaManifestPath")) { [string]$schema["apiSchemaManifestPath"] } else { "" }
        $segments = @($relativePath -split '[\\/]')
        if ([string]::IsNullOrWhiteSpace($relativePath) -or [System.IO.Path]::IsPathRooted($relativePath) -or
            @($segments | Where-Object { $_ -eq "" -or $_ -eq "." -or $_ -eq ".." }).Count -gt 0) {
            $record.Reason = "schema.apiSchemaManifestPath '$(ConvertTo-RestoreSmokeLogSafeText $relativePath)' is not a path inside the workspace"
            return [pscustomobject]$record
        }
        $apiSchemaManifestPath = Join-Path $BootstrapRoot ($segments -join [System.IO.Path]::DirectorySeparatorChar)
        if (-not (Test-Path -LiteralPath $apiSchemaManifestPath -PathType Leaf)) {
            $record.Reason = "the staged ApiSchema manifest '$(ConvertTo-RestoreSmokeLogSafeText $relativePath)' is missing"
            return [pscustomobject]$record
        }
        $apiSchemaManifest = Get-Content -LiteralPath $apiSchemaManifestPath -Raw | ConvertFrom-Json -AsHashtable
        if ($apiSchemaManifest -isnot [System.Collections.IDictionary] -or -not $apiSchemaManifest.Contains("projects") -or $apiSchemaManifest["projects"] -isnot [System.Collections.IList]) {
            $record.Reason = "the staged ApiSchema manifest declares no projects"
            return [pscustomobject]$record
        }
        $coreProjects = @($apiSchemaManifest["projects"] | Where-Object { $_ -is [System.Collections.IDictionary] -and -not [bool]$_["isExtensionProject"] })
        if ($coreProjects.Count -ne 1) {
            $record.Reason = "the staged ApiSchema manifest declares $($coreProjects.Count) core projects; expected exactly one"
            return [pscustomobject]$record
        }
        $record.CoreProjectEndpointName = ConvertTo-RestoreSmokeLogSafeText ([string]$coreProjects[0]["projectEndpointName"])
        $projectSchemas = [System.Collections.Generic.List[string]]::new()
        $projectSchemas.Add((ConvertTo-RestoreSmokeProjectSchemaName -ProjectEndpointName $record.CoreProjectEndpointName))
        foreach ($extension in $extensions) {
            $projectSchemas.Add((ConvertTo-RestoreSmokeProjectSchemaName -ProjectEndpointName $extension))
        }
        $record.ProjectSchemas = $projectSchemas.ToArray()
    }
    catch {
        $record.Reason = "the active workspace could not be read: $(ConvertTo-RestoreSmokeLogSafeText $_.Exception.Message)"
    }
    return [pscustomobject]$record
}

function Get-RestoreSmokeCatalogSelection {
    <#
    .SYNOPSIS
    The schema selection the restored target's catalog holds, read with the restore consumer's own
    queries: the schemas its DMS-only gate enumerates (Get-InventorySchemaQuerySql
    InventoryEnumeration), partitioned by Get-TemplateProjectSchemaPartition into project schemas and
    tracked_changes companions, and the dms.EffectiveSchema singleton's hash
    (Get-EffectiveSchemaRowQuerySql + ConvertFrom-EffectiveSchemaRow). Never throws: a failed query or
    an unreadable row is a Reason.
    #>
    param(
        [Parameter(Mandatory)]
        [ValidateSet("postgresql", "mssql")]
        [string]$DatabaseEngine,

        [Parameter(Mandatory)]
        [string]$DatabaseName
    )

    $record = [ordered]@{
        DatabaseName           = $DatabaseName
        SchemaNames            = $null
        ProjectSchemas         = $null
        TrackedChangesProjects = $null
        EffectiveSchemaHash    = $null
        Reason                 = $null
    }

    $schemaQuery = Invoke-RestoreSmokeEngineQuery -DatabaseEngine $DatabaseEngine -DatabaseName $DatabaseName -Query (Get-InventorySchemaQuerySql -DatabaseEngine $DatabaseEngine -Purpose InventoryEnumeration)
    if ($schemaQuery.ExitCode -ne 0) {
        $record.Reason = "the catalog schema query against '$DatabaseName' exited $($schemaQuery.ExitCode): $($schemaQuery.Error)"
        return [pscustomobject]$record
    }
    $record.SchemaNames = @($schemaQuery.Rows)
    $partition = Get-TemplateProjectSchemaPartition -DatabaseEngine $DatabaseEngine -SchemaName ([string[]]@($schemaQuery.Rows))
    $record.ProjectSchemas = @($partition.ProjectSchemaNames)
    $record.TrackedChangesProjects = @($partition.TrackedChangesProjectNames)

    $effectiveSchemaQuery = Invoke-RestoreSmokeEngineQuery -DatabaseEngine $DatabaseEngine -DatabaseName $DatabaseName -Query (Get-EffectiveSchemaRowQuerySql -DatabaseEngine $DatabaseEngine)
    if ($effectiveSchemaQuery.ExitCode -ne 0) {
        $record.Reason = "the dms.EffectiveSchema query against '$DatabaseName' exited $($effectiveSchemaQuery.ExitCode): $($effectiveSchemaQuery.Error)"
        return [pscustomobject]$record
    }
    try {
        $record.EffectiveSchemaHash = (ConvertFrom-EffectiveSchemaRow -Row ([string[]]@($effectiveSchemaQuery.Rows))).EffectiveSchemaHash
    }
    catch {
        $record.Reason = "the dms.EffectiveSchema row of '$DatabaseName' could not be read: $(ConvertTo-RestoreSmokeLogSafeText $_.Exception.Message)"
    }
    return [pscustomobject]$record
}

function Get-RestoreSmokeRefusalState {
    <#
    .SYNOPSIS
    The state a refused restore must leave as it found it: the containers (any state) and volumes
    Docker holds for the compose project, whether the active .bootstrap workspace exists, and the
    restore candidate directories (candidate-*) under the restore workspace root. Never throws: a
    failed listing is a Reason, and the Docker lists are then unknown (null).
    #>
    param(
        [Parameter(Mandatory)]
        [string]$ComposeProject,

        [Parameter(Mandatory)]
        [string]$BootstrapRoot,

        [Parameter(Mandatory)]
        [string]$RestoreWorkspaceRoot
    )

    $filter = "label=com.docker.compose.project=$ComposeProject"
    $candidates = @()
    if (Test-Path -LiteralPath $RestoreWorkspaceRoot -PathType Container) {
        $candidates = @(Get-ChildItem -LiteralPath $RestoreWorkspaceRoot -Directory -Filter "candidate-*" | ForEach-Object { $_.Name })
    }
    $record = [ordered]@{
        ComposeProject       = $ComposeProject
        Containers           = $null
        Volumes              = $null
        WorkspacePresent     = (Test-Path -LiteralPath $BootstrapRoot)
        CandidateDirectories = $candidates
        Reason               = $null
    }
    $containers = Invoke-RestoreSmokeDockerCommand -ArgumentList @("ps", "-a", "--filter", $filter, "--format", "{{.Names}}")
    if ($containers.ExitCode -ne 0) {
        $record.Reason = "docker ps exited $($containers.ExitCode): $(Get-RestoreSmokeDockerFailureText -Result $containers)"
        return [pscustomobject]$record
    }
    $volumes = Invoke-RestoreSmokeDockerCommand -ArgumentList @("volume", "ls", "--filter", $filter, "--format", "{{.Name}}")
    if ($volumes.ExitCode -ne 0) {
        $record.Reason = "docker volume ls exited $($volumes.ExitCode): $(Get-RestoreSmokeDockerFailureText -Result $volumes)"
        return [pscustomobject]$record
    }
    $record.Containers = @($containers.Output | ForEach-Object { ConvertTo-RestoreSmokeLogSafeText $_ } | Where-Object { $_ -ne "" })
    $record.Volumes = @($volumes.Output | ForEach-Object { ConvertTo-RestoreSmokeLogSafeText $_ } | Where-Object { $_ -ne "" })
    return [pscustomobject]$record
}

function Get-RestoreSmokeSelectionDefect {
    <#
    .SYNOPSIS
    Why a restore of a non-default selection does not prove that selection took effect: the active
    workspace's staged packages must equal the selection env's packages, and the workspace's projects,
    the restored package's restore-manifest projects, and the target catalog's project schemas must
    each equal -ExpectedProject exactly, with no tracked_changes companion of another project; the
    workspace, restore-manifest, and catalog effective schema hashes must be 64 lowercase hex and
    equal. Each disagreement is its own defect; a complete proof returns none. Values are read from
    the recorded evidence (-Proof's Workspace and Catalog, -Package's restore manifest), so the
    classifier can re-judge a results record.
    #>
    param(
        [AllowNull()]
        [object]$Proof,

        # The package record (Get-RestoreSmokePackageProvenance) of the restored package.
        [AllowNull()]
        [object]$Package,

        [AllowNull()]
        [AllowEmptyCollection()]
        [string[]]$ExpectedProject,

        # The selection env's "<name>@<version>" identities (Write-RestoreSmokeCoreOnlyEnvironmentFile).
        [AllowNull()]
        [AllowEmptyCollection()]
        [string[]]$ExpectedPackage
    )

    $defects = [System.Collections.Generic.List[string]]::new()
    $expectedProjects = @($ExpectedProject | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    $expectedPackages = @($ExpectedPackage | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($expectedProjects.Count -eq 0) {
        $defects.Add("no expected project schemas are defined for the selection")
    }
    if ($expectedPackages.Count -eq 0) {
        $defects.Add("the selection env records no selected packages")
    }
    $expectedProjectText = Format-RestoreSmokeNameSet $expectedProjects
    $hashes = [ordered]@{}

    $workspace = Get-RestoreSmokeEvidenceValue $Proof "Workspace"
    $workspaceReason = [string](Get-RestoreSmokeEvidenceValue $workspace "Reason")
    if ($null -eq $workspace) {
        $defects.Add("the active workspace's selection was not recorded")
    }
    elseif (-not [string]::IsNullOrWhiteSpace($workspaceReason)) {
        $defects.Add("the active workspace could not be read: $(ConvertTo-RestoreSmokeLogSafeText $workspaceReason)")
    }
    else {
        $stagedValue = Get-RestoreSmokeEvidenceValue $workspace "SelectedPackages"
        if ($null -eq $stagedValue) {
            $defects.Add("the workspace manifest records no schema.selectedPackages")
        }
        else {
            $staged = Get-RestoreSmokeEvidenceList $workspace "SelectedPackages"
            if ($expectedPackages.Count -gt 0 -and -not (Test-RestoreSmokeNameSetEqual -Actual $staged -Expected $expectedPackages -IgnoreCase)) {
                $defects.Add("the workspace staged packages $(Format-RestoreSmokeNameSet $staged), but the selection env selects $(Format-RestoreSmokeNameSet $expectedPackages)")
            }
        }
        $workspaceProjects = Get-RestoreSmokeEvidenceList $workspace "ProjectSchemas"
        if ($expectedProjects.Count -gt 0 -and -not (Test-RestoreSmokeNameSetEqual -Actual $workspaceProjects -Expected $expectedProjects)) {
            $defects.Add("the workspace selects projects $(Format-RestoreSmokeNameSet $workspaceProjects), expected $expectedProjectText")
        }
        $hashes["workspace"] = [string](Get-RestoreSmokeEvidenceValue $workspace "EffectiveSchemaHash")
    }

    if ($null -eq $Package) {
        $defects.Add("no package record exists for the restored package, so its restore manifest is unknown")
    }
    else {
        $manifestProjects = Get-RestoreSmokeEvidenceList $Package "RestoreManifestProjects"
        if ($expectedProjects.Count -gt 0 -and -not (Test-RestoreSmokeNameSetEqual -Actual $manifestProjects -Expected $expectedProjects)) {
            $defects.Add("the restore manifest declares projects $(Format-RestoreSmokeNameSet $manifestProjects), expected $expectedProjectText")
        }
        $hashes["restore manifest"] = [string](Get-RestoreSmokeEvidenceValue $Package "RestoreManifestEffectiveSchemaHash")
    }

    $catalog = Get-RestoreSmokeEvidenceValue $Proof "Catalog"
    $catalogReason = [string](Get-RestoreSmokeEvidenceValue $catalog "Reason")
    if ($null -eq $catalog) {
        $defects.Add("the target catalog's selection was not recorded")
    }
    elseif (-not [string]::IsNullOrWhiteSpace($catalogReason)) {
        $defects.Add("the target catalog could not be read: $(ConvertTo-RestoreSmokeLogSafeText $catalogReason)")
    }
    else {
        $catalogProjects = Get-RestoreSmokeEvidenceList $catalog "ProjectSchemas"
        if ($expectedProjects.Count -gt 0 -and -not (Test-RestoreSmokeNameSetEqual -Actual $catalogProjects -Expected $expectedProjects)) {
            $defects.Add("the target catalog holds project schemas $(Format-RestoreSmokeNameSet $catalogProjects), expected $expectedProjectText")
        }
        $trackedChanges = Get-RestoreSmokeEvidenceList $catalog "TrackedChangesProjects"
        $foreignTracked = @($trackedChanges | Where-Object { [string]$_ -cnotin $expectedProjects })
        if ($foreignTracked.Count -gt 0) {
            $defects.Add("the target catalog holds tracked_changes companions of projects outside the selection: $(Format-RestoreSmokeNameSet $foreignTracked)")
        }
        $hashes["target catalog"] = [string](Get-RestoreSmokeEvidenceValue $catalog "EffectiveSchemaHash")
    }

    $validHashes = $true
    foreach ($source in $hashes.Keys) {
        if ($hashes[$source] -cnotmatch '^[0-9a-f]{64}$') {
            $defects.Add("the $source effective schema hash '$(ConvertTo-RestoreSmokeLogSafeText $hashes[$source])' is not 64 lowercase hex")
            $validHashes = $false
        }
    }
    if ($validHashes -and @($hashes.Values | Select-Object -Unique).Count -gt 1) {
        $defects.Add("the effective schema hashes disagree: " + (($hashes.Keys | ForEach-Object { "$_ $($hashes[$_])" }) -join ", "))
    }
    return @($defects)
}

function Get-RestoreSmokeSelectionRefusalExpectedMessage {
    <#
    .SYNOPSIS
    The sentence the package-to-candidate cross-check (Assert-RestoreManifestMatchesCandidate) throws
    when a package built for one selection meets a candidate staged for another: it compares the
    effective schema hash before the project set, so differing selections are refused on the hash.
    #>
    param(
        [Parameter(Mandatory)]
        [string]$PackageEffectiveSchemaHash,

        [Parameter(Mandatory)]
        [string]$CandidateEffectiveSchemaHash
    )

    return "Effective schema hash mismatch: the restore manifest declares '$PackageEffectiveSchemaHash' but the candidate workspace staged '$CandidateEffectiveSchemaHash'."
}

function Get-RestoreSmokeSelectionRefusalDefect {
    <#
    .SYNOPSIS
    Why a refusal record does not prove that the default package is refused under the core-only env
    during the restore preflight, before any container starts: the attempt must have restored the
    default package and been refused with the cross-check's effective-schema-hash sentence naming
    the default package's hash and the core-only candidate's (the core-only package's) hash, which
    must be valid and differ; the project held no container before or after, its volumes did not
    change, no .bootstrap workspace existed before or after, and no candidate workspace was left
    behind. A complete record returns no defect.
    #>
    param(
        [AllowNull()]
        [object]$Refusal,

        [AllowNull()]
        [object]$DefaultPackage,

        [AllowNull()]
        [object]$SelectedPackage
    )

    $defects = [System.Collections.Generic.List[string]]::new()
    if ($null -eq $Refusal) {
        return @("no refusal was recorded")
    }
    if ($null -eq $DefaultPackage -or $null -eq $SelectedPackage) {
        $defects.Add("the default and the core-only package records are both needed to judge the refusal")
        return @($defects)
    }

    $defaultHash = [string](Get-RestoreSmokeEvidenceValue $DefaultPackage "RestoreManifestEffectiveSchemaHash")
    $selectedHash = [string](Get-RestoreSmokeEvidenceValue $SelectedPackage "RestoreManifestEffectiveSchemaHash")
    $expectedMessage = $null
    if ($defaultHash -cnotmatch '^[0-9a-f]{64}$' -or $selectedHash -cnotmatch '^[0-9a-f]{64}$') {
        $defects.Add("the default and core-only packages' effective schema hashes are not both 64 lowercase hex")
    }
    elseif ($defaultHash -ceq $selectedHash) {
        $defects.Add("the default and core-only packages declare the same effective schema hash, so the refusal cannot show the selections differ")
    }
    else {
        $expectedMessage = Get-RestoreSmokeSelectionRefusalExpectedMessage -PackageEffectiveSchemaHash $defaultHash -CandidateEffectiveSchemaHash $selectedHash
    }

    $refusedSha = [string](Get-RestoreSmokeEvidenceValue $Refusal "PackageSha256")
    if ($refusedSha -cne [string](Get-RestoreSmokeEvidenceValue $DefaultPackage "Sha256")) {
        $defects.Add("the attempted package '$(ConvertTo-RestoreSmokeLogSafeText $refusedSha)' is not the default package")
    }
    $message = [string](Get-RestoreSmokeEvidenceValue $Refusal "Message")
    if ((Get-RestoreSmokeEvidenceValue $Refusal "Refused") -ne $true) {
        $defects.Add("the default package was not refused under the core-only env")
    }
    elseif ($null -ne $expectedMessage -and -not $message.Contains($expectedMessage, [System.StringComparison]::Ordinal)) {
        $defects.Add("the refusal was not the package-to-candidate cross-check's effective schema hash mismatch: $(ConvertTo-RestoreSmokeLogSafeText $message)")
    }

    $states = [ordered]@{}
    foreach ($point in @("Before", "After")) {
        $state = Get-RestoreSmokeEvidenceValue $Refusal $point
        $stateReason = [string](Get-RestoreSmokeEvidenceValue $state "Reason")
        if ($null -eq $state) {
            $defects.Add("the project state $($point.ToLowerInvariant()) the attempt was not recorded")
            continue
        }
        if (-not [string]::IsNullOrWhiteSpace($stateReason)) {
            $defects.Add("the project state $($point.ToLowerInvariant()) the attempt could not be observed: $(ConvertTo-RestoreSmokeLogSafeText $stateReason)")
            continue
        }
        $states[$point] = $state
        $containers = Get-RestoreSmokeEvidenceList $state "Containers"
        if ($containers.Count -gt 0) {
            $defects.Add("containers existed $($point.ToLowerInvariant()) the attempt: $(Format-RestoreSmokeNameSet $containers)")
        }
        if ((Get-RestoreSmokeEvidenceValue $state "WorkspacePresent") -ne $false) {
            $defects.Add("an active .bootstrap workspace existed $($point.ToLowerInvariant()) the attempt (or was not checked)")
        }
    }
    if ($states.Count -eq 2) {
        $volumesBefore = Get-RestoreSmokeEvidenceList $states["Before"] "Volumes"
        $volumesAfter = Get-RestoreSmokeEvidenceList $states["After"] "Volumes"
        if (-not (Test-RestoreSmokeNameSetEqual -Actual $volumesAfter -Expected $volumesBefore)) {
            $defects.Add("the project's volumes changed across the attempt: before $(Format-RestoreSmokeNameSet $volumesBefore), after $(Format-RestoreSmokeNameSet $volumesAfter)")
        }
        $candidatesBefore = Get-RestoreSmokeEvidenceList $states["Before"] "CandidateDirectories"
        $candidatesAfter = Get-RestoreSmokeEvidenceList $states["After"] "CandidateDirectories"
        $leftCandidates = @($candidatesAfter | Where-Object { [string]$_ -cnotin @($candidatesBefore | ForEach-Object { [string]$_ }) })
        if ($leftCandidates.Count -gt 0) {
            $defects.Add("the attempt left candidate workspaces behind: $(Format-RestoreSmokeNameSet $leftCandidates)")
        }
    }
    return @($defects)
}

function Get-RestoreSmokeSourceIdentityReason {
    <#
    .SYNOPSIS
    The SourceIdentity part of the classification: every reason the run's identity evidence does not
    prove that each successful restore received a new identity, different from its package's.

    .DESCRIPTION
    Per built package: exactly one pre-backup capture bound to its SHA-256, of the same package
    fixture, whose before- and after-backup reads are the same valid UUID. A capture bound to a SHA no
    built package has is reported. Per package a required restore used: exactly one independent
    inspection of that SHA, without a failure, with a valid identity equal to the bound capture.
    Every inspection must have cleaned up completely and must be of a built package. Per required
    restore execution: exactly one restored-identity record, whose package SHA is a built package of
    the fixture the leg restores, holding a valid identity that differs from the package identity and
    from every earlier required restore's. Untagged records and records of restores no selected leg
    performs are reported. Identities are re-derived from the recorded rows.
    #>
    param(
        [Parameter(Mandatory)]
        [System.Collections.IDictionary]$Provenance,

        [AllowNull()]
        [AllowEmptyCollection()]
        [object[]]$Package,

        # The required restores (Get-RestoreSmokeRequiredApiRead), or $null when the legs are unknown.
        [AllowNull()]
        [object[]]$RequiredRestore
    )

    $reasons = [System.Collections.Generic.List[string]]::new()
    $evidence = @{}
    foreach ($name in @("SourceIdentityBindings", "PackageInspections", "RestoredIdentities")) {
        $evidence[$name] = Get-RestoreSmokeEvidenceList $Provenance $name
    }
    $packages = @(@($Package) | Where-Object { $null -ne $_ -and -not [string]::IsNullOrWhiteSpace([string](Get-RestoreSmokeEvidenceValue $_ "Sha256")) })
    $packageShas = @($packages | ForEach-Object { [string](Get-RestoreSmokeEvidenceValue $_ "Sha256") })

    # Pre-backup captures, per built package.
    $capturedIdentity = @{}
    foreach ($builtPackage in $packages) {
        $fixtureName = ConvertTo-RestoreSmokeLogSafeText ([string](Get-RestoreSmokeEvidenceValue $builtPackage "PackageFixture"))
        $sha = [string](Get-RestoreSmokeEvidenceValue $builtPackage "Sha256")
        $label = "$fixtureName package $sha"
        $bindings = @($evidence.SourceIdentityBindings | Where-Object { [string](Get-RestoreSmokeEvidenceValue $_ "PackageSha256") -ceq $sha })
        if ($bindings.Count -eq 0) {
            $reasons.Add("${label}: no pre-backup SourceIdentity capture is bound to it")
            continue
        }
        if ($bindings.Count -gt 1) {
            $reasons.Add("${label}: $($bindings.Count) pre-backup SourceIdentity captures are bound to it; expected exactly one")
            continue
        }
        $bindingFixture = [string](Get-RestoreSmokeEvidenceValue $bindings[0] "PackageFixture")
        if ($bindingFixture -cne $fixtureName) {
            $reasons.Add("${label}: the bound capture is for a $(ConvertTo-RestoreSmokeLogSafeText $bindingFixture) package")
            continue
        }
        $beforeRows = Get-RestoreSmokeEvidenceList (Get-RestoreSmokeEvidenceValue $bindings[0] "BeforeBackup") "Rows"
        $before = Get-RestoreSmokeSourceIdentityValue -Row $beforeRows
        if ($null -ne $before.Defect) {
            $reasons.Add("${label}: the pre-backup SourceIdentity $($before.Defect)")
            continue
        }
        $afterRows = Get-RestoreSmokeEvidenceList (Get-RestoreSmokeEvidenceValue $bindings[0] "AfterBackup") "Rows"
        $after = Get-RestoreSmokeSourceIdentityValue -Row $afterRows
        if ($null -ne $after.Defect) {
            $reasons.Add("${label}: the SourceIdentity read after the backup $($after.Defect)")
            continue
        }
        if ($after.Identity -cne $before.Identity) {
            $reasons.Add("${label}: the source's SourceIdentity changed across the backup (before $($before.Identity), after $($after.Identity))")
            continue
        }
        $capturedIdentity[$sha] = $before.Identity
    }
    foreach ($binding in $evidence.SourceIdentityBindings) {
        $sha = ConvertTo-RestoreSmokeLogSafeText ([string](Get-RestoreSmokeEvidenceValue $binding "PackageSha256"))
        $bindingFixture = ConvertTo-RestoreSmokeLogSafeText ([string](Get-RestoreSmokeEvidenceValue $binding "PackageFixture"))
        if ([string]::IsNullOrWhiteSpace($sha)) {
            $reasons.Add("a pre-backup SourceIdentity capture ($bindingFixture) is bound to no package: $(ConvertTo-RestoreSmokeLogSafeText ([string](Get-RestoreSmokeEvidenceValue $binding 'Reason')))")
        }
        elseif ($sha -cnotin $packageShas) {
            $reasons.Add("a pre-backup SourceIdentity capture ($bindingFixture) is bound to package SHA-256 $sha, which no package built in this run has")
        }
    }

    # Independent inspections: each must have cleaned up and must be of a built package.
    foreach ($inspection in $evidence.PackageInspections) {
        $database = ConvertTo-RestoreSmokeLogSafeText ([string](Get-RestoreSmokeEvidenceValue $inspection "InspectionDatabase"))
        $sha = ConvertTo-RestoreSmokeLogSafeText ([string](Get-RestoreSmokeEvidenceValue $inspection "PackageSha256"))
        if ($sha -cnotin $packageShas) {
            $reasons.Add("package inspection ${database}: it inspected package SHA-256 '$sha', which no package built in this run has")
        }
        $cleanup = Get-RestoreSmokeEvidenceValue $inspection "Cleanup"
        if ((Get-RestoreSmokeEvidenceValue $cleanup "Complete") -ne $true) {
            $cleanupReasons = Get-RestoreSmokeEvidenceList $cleanup "Reasons"
            $cleanupReasons = @($cleanupReasons | ForEach-Object { ConvertTo-RestoreSmokeLogSafeText ([string]$_) })
            $detail = if ($cleanupReasons.Count -gt 0) { ": $($cleanupReasons -join '; ')" } else { "" }
            $reasons.Add("package inspection ${database}: its cleanup was not shown complete$detail")
        }
    }

    if ($null -eq $RequiredRestore) {
        return @($reasons)
    }

    $requiredIds = @($RequiredRestore | ForEach-Object { $_.RestoreExecution })
    $usedShas = [System.Collections.Generic.List[string]]::new()
    foreach ($restore in $RequiredRestore) {
        $records = @($evidence.RestoredIdentities | Where-Object { [string](Get-RestoreSmokeEvidenceValue $_ "RestoreExecution") -ceq $restore.RestoreExecution })
        if ($records.Count -eq 1) {
            $sha = [string](Get-RestoreSmokeEvidenceValue $records[0] "PackageSha256")
            if ($sha -cin $packageShas -and $sha -cnotin $usedShas) {
                $usedShas.Add($sha)
            }
        }
    }

    # The package identity, per package a required restore used.
    $packageIdentity = @{}
    foreach ($sha in $usedShas) {
        $builtPackage = @($packages | Where-Object { [string](Get-RestoreSmokeEvidenceValue $_ "Sha256") -ceq $sha })[0]
        $label = "$(ConvertTo-RestoreSmokeLogSafeText ([string](Get-RestoreSmokeEvidenceValue $builtPackage 'PackageFixture'))) package $sha"
        $inspections = @($evidence.PackageInspections | Where-Object { [string](Get-RestoreSmokeEvidenceValue $_ "PackageSha256") -ceq $sha })
        if ($inspections.Count -eq 0) {
            $reasons.Add("${label}: it was not independently inspected")
            continue
        }
        if ($inspections.Count -gt 1) {
            $reasons.Add("${label}: it was inspected $($inspections.Count) times; expected exactly once")
            continue
        }
        $inspectionReason = [string](Get-RestoreSmokeEvidenceValue $inspections[0] "Reason")
        if (-not [string]::IsNullOrWhiteSpace($inspectionReason)) {
            $reasons.Add("${label}: the independent inspection failed: $(ConvertTo-RestoreSmokeLogSafeText $inspectionReason)")
            continue
        }
        $inspectedRows = Get-RestoreSmokeEvidenceList $inspections[0] "Rows"
        $inspected = Get-RestoreSmokeSourceIdentityValue -Row $inspectedRows
        if ($null -ne $inspected.Defect) {
            $reasons.Add("${label}: the inspected package SourceIdentity $($inspected.Defect)")
            continue
        }
        if ($capturedIdentity.ContainsKey($sha) -and $capturedIdentity[$sha] -cne $inspected.Identity) {
            $reasons.Add("${label}: the inspected package SourceIdentity $($inspected.Identity) differs from the pre-backup capture $($capturedIdentity[$sha]) bound to its SHA-256")
            continue
        }
        $packageIdentity[$sha] = $inspected.Identity
    }

    # The restored identity, per required restore.
    $earlier = [System.Collections.Generic.List[object]]::new()
    foreach ($restore in $RequiredRestore) {
        $id = $restore.RestoreExecution
        $records = @($evidence.RestoredIdentities | Where-Object { [string](Get-RestoreSmokeEvidenceValue $_ "RestoreExecution") -ceq $id })
        if ($records.Count -eq 0) {
            $reasons.Add("restore ${id}: no restored SourceIdentity was recorded")
            continue
        }
        if ($records.Count -gt 1) {
            $reasons.Add("restore ${id}: $($records.Count) restored SourceIdentity records were recorded; expected exactly one")
            continue
        }
        $record = $records[0]
        $sha = [string](Get-RestoreSmokeEvidenceValue $record "PackageSha256")
        $restoredPackage = @($packages | Where-Object { [string](Get-RestoreSmokeEvidenceValue $_ "Sha256") -ceq $sha })
        $comparedIdentity = $null
        if ([string]::IsNullOrWhiteSpace($sha) -or $restoredPackage.Count -eq 0) {
            $reasons.Add("restore ${id}: its package SHA-256 '$(ConvertTo-RestoreSmokeLogSafeText $sha)' matches no package built in this run")
        }
        else {
            $restoredFixture = [string](Get-RestoreSmokeEvidenceValue $restoredPackage[0] "PackageFixture")
            if ($restoredFixture -cne $restore.PackageFixture) {
                $reasons.Add("restore ${id}: it restored the $(ConvertTo-RestoreSmokeLogSafeText $restoredFixture) package; the leg restores $($restore.PackageFixture)")
            }
            if ($packageIdentity.ContainsKey($sha)) {
                $comparedIdentity = $packageIdentity[$sha]
            }
        }
        $restoredRows = Get-RestoreSmokeEvidenceList $record "Rows"
        foreach ($defect in @(Get-RestoreSmokeRestoredIdentityDefect -Row $restoredRows -PackageIdentity $comparedIdentity -EarlierRestore $earlier.ToArray())) {
            $reasons.Add("restore ${id}: $defect")
        }
        $earlier.Add($record)
    }
    foreach ($record in $evidence.RestoredIdentities) {
        $executionId = ConvertTo-RestoreSmokeLogSafeText ([string](Get-RestoreSmokeEvidenceValue $record "RestoreExecution"))
        if ([string]::IsNullOrWhiteSpace($executionId)) {
            $reasons.Add("a restored SourceIdentity record names no restore execution")
        }
        elseif ($executionId -cnotin $requiredIds) {
            $reasons.Add("a restored SourceIdentity was recorded for restore $executionId, which no selected leg performs")
        }
    }
    return @($reasons)
}

function Get-RestoreSmokeSelectionReason {
    <#
    .SYNOPSIS
    The schema-selection part of the classification. Every required restore of a non-default
    fixture needs exactly one selection proof of its own, re-judged here by
    Get-RestoreSmokeSelectionDefect against the fixture's expected projects, the one recorded env of
    that selection (which must have removed at least one package), and the package record of the
    proof's SHA-256 (a built package of the restore's fixture). With extension-selection selected,
    exactly one refusal record is required and re-judged by Get-RestoreSmokeSelectionRefusalDefect
    against the default-minimal and core-only-minimal package records. Untagged proofs and proofs or
    refusals nothing requires are reported.
    #>
    param(
        [Parameter(Mandatory)]
        [System.Collections.IDictionary]$Provenance,

        [AllowNull()]
        [AllowEmptyCollection()]
        [object[]]$Package,

        # The required restores (Get-RestoreSmokeRequiredApiRead), or $null when the legs are unknown.
        [AllowNull()]
        [object[]]$RequiredRestore,

        [AllowNull()]
        [AllowEmptyCollection()]
        [object[]]$Leg
    )

    $reasons = [System.Collections.Generic.List[string]]::new()
    if ($null -eq $RequiredRestore) {
        return @($reasons)
    }
    $packages = @(@($Package) | Where-Object { $null -ne $_ })
    $proofs = Get-RestoreSmokeEvidenceList $Provenance "SelectionProofs"
    $refusals = Get-RestoreSmokeEvidenceList $Provenance "SelectionRefusals"
    $environments = Get-RestoreSmokeEvidenceList $Provenance "SelectionEnvironments"

    $requiredSelections = @($RequiredRestore | Where-Object { (Get-RestoreSmokePackageFixture -Name $_.PackageFixture).Selection -cne "default" })
    foreach ($restore in $requiredSelections) {
        $id = $restore.RestoreExecution
        $fixture = Get-RestoreSmokePackageFixture -Name $restore.PackageFixture
        $records = @($proofs | Where-Object { [string](Get-RestoreSmokeEvidenceValue $_ "RestoreExecution") -ceq $id })
        if ($records.Count -eq 0) {
            $reasons.Add("restore ${id}: no selection proof was recorded")
            continue
        }
        if ($records.Count -gt 1) {
            $reasons.Add("restore ${id}: $($records.Count) selection proofs were recorded; expected exactly one")
            continue
        }

        $expectedPackages = @()
        $selectionEnvironments = @($environments | Where-Object { [string](Get-RestoreSmokeEvidenceValue $_ "Selection") -ceq $fixture.Selection })
        if ($selectionEnvironments.Count -ne 1) {
            $reasons.Add("restore ${id}: expected one recorded $($fixture.Selection) env, found $($selectionEnvironments.Count)")
        }
        else {
            $expectedPackages = Get-RestoreSmokeEvidenceList $selectionEnvironments[0] "SelectedPackages"
            $removedPackages = Get-RestoreSmokeEvidenceList $selectionEnvironments[0] "RemovedPackages"
            if ($removedPackages.Count -eq 0) {
                $reasons.Add("restore ${id}: the $($fixture.Selection) env removed no package, so it does not differ from the default selection")
            }
        }

        $sha = [string](Get-RestoreSmokeEvidenceValue $records[0] "PackageSha256")
        $restoredPackage = @($packages | Where-Object { [string](Get-RestoreSmokeEvidenceValue $_ "Sha256") -ceq $sha -and [string](Get-RestoreSmokeEvidenceValue $_ "PackageFixture") -ceq $fixture.Name })
        $packageRecord = $null
        if ([string]::IsNullOrWhiteSpace($sha) -or $restoredPackage.Count -eq 0) {
            $reasons.Add("restore ${id}: the selection proof's package SHA-256 '$(ConvertTo-RestoreSmokeLogSafeText $sha)' is not a $($fixture.Name) package built in this run")
        }
        else {
            $packageRecord = $restoredPackage[0]
        }
        foreach ($defect in @(Get-RestoreSmokeSelectionDefect -Proof $records[0] -Package $packageRecord -ExpectedProject ([string[]]@($fixture.ProjectSchemas)) -ExpectedPackage ([string[]]@($expectedPackages)))) {
            $reasons.Add("restore ${id}: $defect")
        }
    }
    $requiredIds = @($requiredSelections | ForEach-Object { $_.RestoreExecution })
    foreach ($proof in $proofs) {
        $executionId = ConvertTo-RestoreSmokeLogSafeText ([string](Get-RestoreSmokeEvidenceValue $proof "RestoreExecution"))
        if ([string]::IsNullOrWhiteSpace($executionId)) {
            $reasons.Add("a selection proof names no restore execution")
        }
        elseif ($executionId -cnotin $requiredIds) {
            $reasons.Add("a selection proof was recorded for restore $executionId, which no selected leg requires")
        }
    }

    $refusalRequired = "extension-selection" -cin @($Leg | ForEach-Object { [string]$_ })
    if ($refusalRequired) {
        $records = @($refusals | Where-Object { [string](Get-RestoreSmokeEvidenceValue $_ "Leg") -ceq "extension-selection" })
        if ($records.Count -eq 0) {
            $reasons.Add("leg extension-selection: no refusal of the default package under the core-only env was recorded")
        }
        elseif ($records.Count -gt 1) {
            $reasons.Add("leg extension-selection: $($records.Count) refusals were recorded; expected exactly one")
        }
        else {
            $defaultPackages = @($packages | Where-Object { [string](Get-RestoreSmokeEvidenceValue $_ "PackageFixture") -ceq "default-minimal" })
            $coreOnlyPackages = @($packages | Where-Object { [string](Get-RestoreSmokeEvidenceValue $_ "PackageFixture") -ceq "core-only-minimal" })
            $defaultPackage = $null
            if ($defaultPackages.Count -eq 1) {
                $defaultPackage = $defaultPackages[0]
            }
            $coreOnlyPackage = $null
            if ($coreOnlyPackages.Count -eq 1) {
                $coreOnlyPackage = $coreOnlyPackages[0]
            }
            foreach ($defect in @(Get-RestoreSmokeSelectionRefusalDefect -Refusal $records[0] -DefaultPackage $defaultPackage -SelectedPackage $coreOnlyPackage)) {
                $reasons.Add("leg extension-selection: $defect")
            }
        }
    }
    foreach ($refusal in $refusals) {
        $refusalLeg = ConvertTo-RestoreSmokeLogSafeText ([string](Get-RestoreSmokeEvidenceValue $refusal "Leg"))
        if (-not $refusalRequired -or $refusalLeg -cne "extension-selection") {
            $reasons.Add("a selection refusal was recorded for leg '$refusalLeg', which no selected leg requires")
        }
    }
    return @($reasons)
}

function Get-RestoreSmokeEvidenceValue {
    <#
    .SYNOPSIS
    Reads one field of an evidence record (a dictionary or an object) and returns null when the
    record or the field is missing, so an incomplete run is classified rather than thrown on.
    Collections are returned as one object (not enumerated); callers normalize them with @().
    #>
    param(
        [AllowNull()]
        [object]$Source,

        [Parameter(Mandatory)]
        [string]$Name
    )

    if ($null -eq $Source) {
        return $null
    }
    if ($Source -is [System.Collections.IDictionary]) {
        if ($Source.Contains($Name)) {
            return , $Source[$Name]
        }
        return $null
    }
    $property = $Source.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return $null
    }
    return , $property.Value
}

function Get-RestoreSmokeEvidenceList {
    <#
    .SYNOPSIS
    One evidence field as a flat array of its non-null items: @() when the record or field is
    missing, and a single value as a one-item array. Assign the result to a variable before use;
    wrapping the call in @() would nest the array.
    #>
    param(
        [AllowNull()]
        [object]$Source,

        [Parameter(Mandatory)]
        [string]$Name
    )

    $value = Get-RestoreSmokeEvidenceValue $Source $Name
    if ($null -eq $value) {
        return , @()
    }
    return , @(@($value) | Where-Object { $null -ne $_ })
}

function ConvertTo-RestoreSmokeUtcInstant {
    # A recorded time (round-trip text, or a DateTime/DateTimeOffset after a JSON round trip) as a
    # UTC DateTime, or null when it is missing or unparseable.
    param(
        [AllowNull()]
        [object]$Value
    )

    if ($Value -is [System.DateTime]) {
        return $Value.ToUniversalTime()
    }
    if ($Value -is [System.DateTimeOffset]) {
        return $Value.UtcDateTime
    }
    $parsed = [System.DateTimeOffset]::MinValue
    if (-not [string]::IsNullOrWhiteSpace([string]$Value) -and
        [System.DateTimeOffset]::TryParse([string]$Value, [System.Globalization.CultureInfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::AssumeUniversal, [ref]$parsed)) {
        return $parsed.UtcDateTime
    }
    return $null
}

function Get-RestoreSmokeStagedSelectionReason {
    <#
    .SYNOPSIS
    Why one stack observation's staged-selection evidence is not final evidence; empty when it is.

    .DESCRIPTION
    Binding: the observation names exactly one recorded stack start, of its own step. Evidence:
    the staged selection was read, lists at least one "<name>@<version>" identity, repeats none,
    and has exactly one core package. Freshness: the workspace manifest was written at or after
    the stack start began, or - for a restore only - it is the manifest present before the start
    (same SHA-256 and write time), which the restore commit keeps only when the candidate that
    start staged and cross-checked is byte-identical. Agreement, for a non-default selection only:
    the staged identities equal the one recorded env of that selection. Default-selection stacks
    are not compared with any requested set (the local wrapper's Data Standard overlay
    legitimately stages packages other than the base env's).
    #>
    param(
        [AllowNull()]
        [object]$Observation,

        [Parameter(Mandatory)]
        [string]$Label,

        [AllowNull()]
        [AllowEmptyCollection()]
        [object[]]$StackStart,

        [AllowNull()]
        [AllowEmptyCollection()]
        [object[]]$SelectionEnvironment
    )

    $reasons = [System.Collections.Generic.List[string]]::new()
    $start = $null
    $startId = [string](Get-RestoreSmokeEvidenceValue $Observation "StackStart")
    $safeStartId = ConvertTo-RestoreSmokeLogSafeText $startId
    if ([string]::IsNullOrWhiteSpace($startId)) {
        $reasons.Add("${Label}: the observation names no stack start, so its staged selection is not bound to the stack it observed")
    }
    else {
        $matching = @($StackStart | Where-Object { $null -ne $_ -and [string](Get-RestoreSmokeEvidenceValue $_ "StackStart") -ceq $startId })
        if ($matching.Count -ne 1) {
            $reasons.Add("${Label}: $($matching.Count) recorded stack starts are named '$safeStartId'; expected exactly one")
        }
        else {
            $start = $matching[0]
            $startLabel = [string](Get-RestoreSmokeEvidenceValue $start "Label")
            if ($startLabel -cne [string](Get-RestoreSmokeEvidenceValue $Observation "Label")) {
                $reasons.Add("${Label}: stack start '$safeStartId' belongs to step '$(ConvertTo-RestoreSmokeLogSafeText $startLabel)'")
            }
        }
    }

    $staged = Get-RestoreSmokeEvidenceValue $Observation "StagedSelection"
    if ($null -eq $staged) {
        $reasons.Add("${Label}: the staged schema selection was not observed")
        return @($reasons)
    }
    $stagedReason = [string](Get-RestoreSmokeEvidenceValue $staged "Reason")
    if (-not [string]::IsNullOrWhiteSpace($stagedReason)) {
        $reasons.Add("${Label}: the staged schema selection was not observed: $(ConvertTo-RestoreSmokeLogSafeText $stagedReason)")
        return @($reasons)
    }

    $identities = Get-RestoreSmokeEvidenceList $staged "StagedPackages"
    if ($identities.Count -eq 0) {
        $reasons.Add("${Label}: the workspace manifest records no staged package")
    }
    else {
        foreach ($identity in $identities) {
            if ([string]$identity -cnotmatch '^[^@\s]+@[^@\s]+$') {
                $reasons.Add("${Label}: staged package '$(ConvertTo-RestoreSmokeLogSafeText ([string]$identity))' is not a <name>@<version> identity")
            }
        }
        $distinct = [System.Collections.Generic.HashSet[string]]::new([string[]]@($identities | ForEach-Object { [string]$_ }), [System.StringComparer]::OrdinalIgnoreCase)
        if ($distinct.Count -ne $identities.Count) {
            $reasons.Add("${Label}: the staged packages repeat an identity")
        }
        $cores = @($identities | Where-Object { ([string]$_).Split("@")[0] -match $script:CoreSchemaPackagePattern })
        if ($cores.Count -ne 1) {
            $reasons.Add("${Label}: the staged packages list $($cores.Count) core packages (EdFi.DataStandard<NN>.ApiSchema); expected exactly one")
        }
    }

    if ($null -eq $start) {
        return @($reasons)
    }

    $before = Get-RestoreSmokeEvidenceValue $start "WorkspaceBefore"
    $manifestSha = [string](Get-RestoreSmokeEvidenceValue $staged "ManifestSha256")
    $writtenAt = ConvertTo-RestoreSmokeUtcInstant (Get-RestoreSmokeEvidenceValue $staged "ManifestLastWriteTimeUtc")
    $beforeWrittenAt = ConvertTo-RestoreSmokeUtcInstant (Get-RestoreSmokeEvidenceValue $before "LastWriteTimeUtc")
    $unchanged = (Get-RestoreSmokeEvidenceValue $before "Present") -eq $true -and
        -not [string]::IsNullOrWhiteSpace($manifestSha) -and
        [string](Get-RestoreSmokeEvidenceValue $before "Sha256") -ceq $manifestSha -and
        $null -ne $writtenAt -and $writtenAt -eq $beforeWrittenAt
    if ($unchanged) {
        if ((Get-RestoreSmokeEvidenceValue $start "Restore") -ne $true) {
            $reasons.Add("${Label}: the workspace manifest is the one present before stack start '$safeStartId', which is not a restore, so the stack staged no selection of its own")
        }
    }
    else {
        $startedAt = ConvertTo-RestoreSmokeUtcInstant (Get-RestoreSmokeEvidenceValue $start "StartedUtc")
        if ($null -eq $writtenAt -or $null -eq $startedAt) {
            $reasons.Add("${Label}: the workspace manifest's write time or the start time of stack start '$safeStartId' is not recorded")
        }
        elseif ($writtenAt -lt $startedAt) {
            $invariant = [System.Globalization.CultureInfo]::InvariantCulture
            $reasons.Add("${Label}: the workspace manifest was written at $($writtenAt.ToString('o', $invariant)), before stack start '$safeStartId' began at $($startedAt.ToString('o', $invariant)), so its selection is not this stack's")
        }
    }

    $selection = [string](Get-RestoreSmokeEvidenceValue $start "Selection")
    if ([string]::IsNullOrWhiteSpace($selection)) {
        $reasons.Add("${Label}: stack start '$safeStartId' records no schema selection")
    }
    elseif ($selection -cne "default") {
        $safeSelection = ConvertTo-RestoreSmokeLogSafeText $selection
        $environments = @($SelectionEnvironment | Where-Object { $null -ne $_ -and [string](Get-RestoreSmokeEvidenceValue $_ "Selection") -ceq $selection })
        if ($environments.Count -ne 1) {
            $reasons.Add("${Label}: stack start '$safeStartId' used the '$safeSelection' selection, which has $($environments.Count) recorded envs; expected exactly one")
        }
        elseif ($identities.Count -gt 0) {
            $expected = Get-RestoreSmokeEvidenceList $environments[0] "SelectedPackages"
            if (-not (Test-RestoreSmokeNameSetEqual -Actual $identities -Expected $expected -IgnoreCase)) {
                $reasons.Add("${Label}: the workspace staged $(Format-RestoreSmokeNameSet $identities), but stack start '$safeStartId' used the $safeSelection selection $(Format-RestoreSmokeNameSet $expected)")
            }
        }
    }
    return @($reasons)
}

function Get-RestoreSmokeResultClassification {
    <#
    .SYNOPSIS
    Decides whether a run's evidence is final. Every unmet condition is a reason; any reason makes
    the result non-final. A missing observation is never treated as verified, and missing or
    malformed evidence produces a reason instead of an exception.

    .DESCRIPTION
    Final requires, besides the build, package, and SourceIdentity evidence: observed, non-empty,
    equal start and end revisions with a clean tree at both points; and for EVERY stack observation
    the dms/config containers running the verified in-run image IDs, a db container whose image has
    an observed repository digest (an image ID alone is not a digest), and staged-selection evidence
    bound to its own stack start (Get-RestoreSmokeStagedSelectionReason); no stack start may be
    observed twice. Every leftover volume the preflight allowed must be shown
    preserved at run end. Every successful restore the selected legs perform must have exactly one
    served-data API record of its own, complete for its template kind (schema-only reads never are).
    Every built package needs its pre-backup SourceIdentity capture bound to its SHA-256, and every
    such restore needs a restored identity of its own that differs from its independently inspected
    package's identity and from every other restore's (Get-RestoreSmokeSourceIdentityReason). Each
    package names a known fixture of its template kind, one package per fixture. A restore of a
    non-default selection needs its selection proof, and extension-selection its refusal of the
    default package (Get-RestoreSmokeSelectionReason).
    #>
    param(
        [Parameter(Mandatory)]
        [System.Collections.IDictionary]$Provenance
    )

    $reasons = [System.Collections.Generic.List[string]]::new()

    if (Get-RestoreSmokeEvidenceValue $Provenance "ExploratoryPackage") {
        $reasons.Add("-ExploratoryPackage marks this run exploratory")
    }

    $revisions = [ordered]@{}
    foreach ($point in @("SourceAtStart", "SourceAtEnd")) {
        $source = Get-RestoreSmokeEvidenceValue $Provenance $point
        if ($null -eq $source) {
            $reasons.Add("$point not captured")
            continue
        }
        if ((Get-RestoreSmokeEvidenceValue $source "Observed") -ne $true) {
            $reasons.Add("$point not observed: $(Get-RestoreSmokeEvidenceValue $source 'Reason')")
            continue
        }
        $revision = [string](Get-RestoreSmokeEvidenceValue $source "Revision")
        if ([string]::IsNullOrWhiteSpace($revision)) {
            $reasons.Add("$point revision is empty")
        }
        else {
            $revisions[$point] = $revision
        }
        $porcelain = Get-RestoreSmokeEvidenceValue $source "Porcelain"
        # Assigned directly, never through an if expression: an if expression unrolls arrays.
        $porcelainEntries = @()
        if ($null -ne $porcelain) {
            $porcelainEntries = @(@($porcelain) | Where-Object { $null -ne $_ })
        }
        $porcelainCount = $porcelainEntries.Count
        if ((Get-RestoreSmokeEvidenceValue $source "Clean") -ne $true -or $porcelainCount -gt 0) {
            $reasons.Add("$point worktree is dirty ($porcelainCount porcelain entries)")
        }
    }
    if ($revisions.Count -eq 2 -and $revisions["SourceAtStart"] -cne $revisions["SourceAtEnd"]) {
        $reasons.Add("the revision changed during the run (start $($revisions['SourceAtStart']), end $($revisions['SourceAtEnd']))")
    }

    $schemaTools = Get-RestoreSmokeEvidenceValue $Provenance "SchemaTools"
    if ($null -eq $schemaTools) {
        $reasons.Add("SchemaTools was not built in this run")
    }
    elseif ((Get-RestoreSmokeEvidenceValue $schemaTools "Verified") -ne $true) {
        $reasons.Add("SchemaTools not verified: $(Get-RestoreSmokeEvidenceValue $schemaTools 'Reason')")
    }
    elseif ([string]::IsNullOrWhiteSpace([string](Get-RestoreSmokeEvidenceValue $schemaTools "Sha256AtBuild")) -or
        (Get-RestoreSmokeEvidenceValue $schemaTools "Sha256AtEnd") -cne (Get-RestoreSmokeEvidenceValue $schemaTools "Sha256AtBuild")) {
        $reasons.Add("the SchemaTools executable changed during the run (or was not re-hashed at the end)")
    }

    $images = Get-RestoreSmokeEvidenceValue $Provenance "Images"
    $verifiedImageIds = @{}
    if ($null -eq $images) {
        $reasons.Add("images were not built in this run")
    }
    else {
        foreach ($key in @("Dms", "Config")) {
            $built = Get-RestoreSmokeEvidenceValue $images $key
            $builtId = [string](Get-RestoreSmokeEvidenceValue $built "ImageId")
            if ($null -eq $built -or (Get-RestoreSmokeEvidenceValue $built "Verified") -ne $true -or [string]::IsNullOrWhiteSpace($builtId)) {
                $reason = if ($null -ne $built) { Get-RestoreSmokeEvidenceValue $built "Reason" } else { "missing" }
                $reasons.Add("$key image not verified as built in this run: $reason")
            }
            else {
                $verifiedImageIds[$key] = $builtId
            }
        }
    }

    $observationList = Get-RestoreSmokeEvidenceValue $Provenance "StackObservations"
    $observations = @()
    if ($null -ne $observationList) {
        $observations = @($observationList)
    }
    if ($observations.Count -eq 0) {
        $reasons.Add("no started stack was observed, so the images actually used are unknown")
    }
    $stackStarts = Get-RestoreSmokeEvidenceList $Provenance "StackStarts"
    $selectionEnvironmentRecords = Get-RestoreSmokeEvidenceList $Provenance "SelectionEnvironments"
    $observationsPerStart = [System.Collections.Generic.Dictionary[string, int]]::new([System.StringComparer]::Ordinal)
    for ($index = 0; $index -lt $observations.Count; $index++) {
        $observation = $observations[$index]
        $label = [string](Get-RestoreSmokeEvidenceValue $observation "Label")
        if ([string]::IsNullOrWhiteSpace($label)) {
            $label = "stack observation $($index + 1)"
        }
        if ($null -eq $observation) {
            $reasons.Add("${label}: the observation record is missing")
            continue
        }

        $observationReason = [string](Get-RestoreSmokeEvidenceValue $observation "Reason")
        if (-not [string]::IsNullOrWhiteSpace($observationReason)) {
            $reasons.Add("${label}: the stack could not be observed: $observationReason")
        }

        $services = Get-RestoreSmokeEvidenceValue $observation "Services"
        if ($null -ne $images) {
            foreach ($pair in @(@{ Service = "dms"; Key = "Dms" }, @{ Service = "config"; Key = "Config" })) {
                $observed = Get-RestoreSmokeEvidenceValue $services $pair.Service
                $observedId = [string](Get-RestoreSmokeEvidenceValue $observed "ImageId")
                $builtId = if ($verifiedImageIds.ContainsKey($pair.Key)) { $verifiedImageIds[$pair.Key] } else { "none verified" }
                if ($null -eq $observed) {
                    $reasons.Add("${label}: no $($pair.Service) container observed")
                }
                elseif (-not $verifiedImageIds.ContainsKey($pair.Key) -or $observedId -cne $builtId) {
                    $reasons.Add("${label}: $($pair.Service) runs image $observedId, not the in-run build ($builtId)")
                }
            }
        }

        $database = Get-RestoreSmokeEvidenceValue $services "db"
        if ($null -eq $database) {
            $reasons.Add("${label}: no db container observed")
        }
        else {
            $digestList = Get-RestoreSmokeEvidenceValue $database "RepoDigests"
            $digests = @()
            if ($null -ne $digestList) {
                $digests = @(@($digestList) | Where-Object { [string]$_ -cmatch '^[^@\s]+@sha256:[0-9a-f]{64}$' })
            }
            if ($digests.Count -eq 0) {
                $digestReason = [string](Get-RestoreSmokeEvidenceValue $database "RepoDigestsReason")
                $suffix = if ([string]::IsNullOrWhiteSpace($digestReason)) { "" } else { ": $digestReason" }
                $reasons.Add("${label}: the db image $(Get-RestoreSmokeEvidenceValue $database 'ImageId') has no observed repository digest$suffix")
            }
        }

        foreach ($selectionReason in (Get-RestoreSmokeStagedSelectionReason -Observation $observation -Label $label -StackStart $stackStarts -SelectionEnvironment $selectionEnvironmentRecords)) {
            $reasons.Add($selectionReason)
        }
        $startId = [string](Get-RestoreSmokeEvidenceValue $observation "StackStart")
        if (-not [string]::IsNullOrWhiteSpace($startId)) {
            $count = 0
            $null = $observationsPerStart.TryGetValue($startId, [ref]$count)
            $observationsPerStart[$startId] = $count + 1
        }
    }
    $observedTwice = [System.Collections.Generic.List[string]]::new()
    foreach ($pair in $observationsPerStart.GetEnumerator()) {
        if ($pair.Value -gt 1) {
            $observedTwice.Add($pair.Key)
        }
    }
    $observedTwice.Sort([System.StringComparer]::Ordinal)
    foreach ($startId in $observedTwice) {
        $reasons.Add("stack start '$(ConvertTo-RestoreSmokeLogSafeText $startId)' is named by $($observationsPerStart[$startId]) observations; each started stack is observed once")
    }

    $packageList = Get-RestoreSmokeEvidenceValue $Provenance "Packages"
    $packages = @()
    if ($null -ne $packageList) {
        $packages = @($packageList)
    }
    if ($packages.Count -eq 0) {
        $reasons.Add("no package was built in this run")
    }
    $packagesPerFixture = @{}
    foreach ($package in $packages) {
        $fixtureName = ConvertTo-RestoreSmokeLogSafeText ([string](Get-RestoreSmokeEvidenceValue $package "PackageFixture"))
        $packageKind = ConvertTo-RestoreSmokeLogSafeText ([string](Get-RestoreSmokeEvidenceValue $package "TemplateKind"))
        if ((Get-RestoreSmokeEvidenceValue $package "Verified") -ne $true) {
            $reasons.Add("package ($fixtureName) not verified: $(Get-RestoreSmokeEvidenceValue $package 'Reason')")
        }
        if (-not $script:PackageFixtures.Contains($fixtureName)) {
            $reasons.Add("package $(ConvertTo-RestoreSmokeLogSafeText ([string](Get-RestoreSmokeEvidenceValue $package 'PackageFile'))): '$fixtureName' is not a known package fixture")
            continue
        }
        if ($packageKind -cne $script:PackageFixtures[$fixtureName].TemplateKind) {
            $reasons.Add("package ($fixtureName): it is recorded as a $packageKind package, but the fixture is $($script:PackageFixtures[$fixtureName].TemplateKind)")
        }
        $packagesPerFixture[$fixtureName] = 1 + [int]$packagesPerFixture[$fixtureName]
    }
    foreach ($fixtureName in @($packagesPerFixture.Keys | Sort-Object)) {
        if ($packagesPerFixture[$fixtureName] -gt 1) {
            $reasons.Add("$($packagesPerFixture[$fixtureName]) packages were recorded for fixture $fixtureName; expected one")
        }
    }

    # A leftover volume the preflight allowed must still be there, unchanged, when the run ends.
    $foreignStack = Get-RestoreSmokeEvidenceValue $Provenance "ForeignStack"
    $allowedList = Get-RestoreSmokeEvidenceValue (Get-RestoreSmokeEvidenceValue $foreignStack "LeftoverVolumes") "Allowed"
    $allowedVolumes = @()
    if ($null -ne $allowedList) {
        $allowedVolumes = @($allowedList | Where-Object { $null -ne $_ })
    }
    $stateList = Get-RestoreSmokeEvidenceValue $foreignStack "AllowedVolumesAtEnd"
    $endStates = @()
    if ($null -ne $stateList) {
        $endStates = @($stateList | Where-Object { $null -ne $_ })
    }
    foreach ($volume in $allowedVolumes) {
        $volumeName = [string](Get-RestoreSmokeEvidenceValue $volume "Name")
        $endState = @($endStates | Where-Object { [string](Get-RestoreSmokeEvidenceValue $_ "Name") -ceq $volumeName })
        if ($endState.Count -eq 0) {
            $reasons.Add("leftover volume '$volumeName' was allowed by the preflight, but no run-end check was recorded")
        }
        elseif ((Get-RestoreSmokeEvidenceValue $endState[0] "Preserved") -ne $true) {
            $reasons.Add("leftover volume '$volumeName' was allowed by the preflight, but was not shown preserved at run end: $(Get-RestoreSmokeEvidenceValue $endState[0] 'Reason')")
        }
    }

    # Served data: every successful restore the selected legs perform needs exactly one complete API
    # record of its own; a record anywhere else in the run does not stand in for it.
    $legList = Get-RestoreSmokeEvidenceValue $Provenance "Legs"
    $apiReadList = Get-RestoreSmokeEvidenceValue $Provenance "ApiReads"
    $apiReads = @()
    if ($null -ne $apiReadList) {
        $apiReads = @(@($apiReadList) | Where-Object { $null -ne $_ })
    }
    $requiredRestores = $null
    if ($null -eq $legList) {
        $reasons.Add("the selected legs were not recorded, so the required served-data API reads and restored SourceIdentity records are unknown")
    }
    else {
        $requirement = Get-RestoreSmokeRequiredApiRead -Legs @($legList)
        $requiredRestores = @($requirement.Required)
        foreach ($leg in $requirement.UnknownLegs) {
            $reasons.Add("leg '$(ConvertTo-RestoreSmokeLogSafeText $leg)' has no defined served-data requirement")
        }
        $requiredIds = @($requirement.Required | ForEach-Object { $_.RestoreExecution })
        foreach ($restore in $requirement.Required) {
            $records = @($apiReads | Where-Object { [string](Get-RestoreSmokeEvidenceValue $_ "RestoreExecution") -ceq $restore.RestoreExecution })
            if ($records.Count -eq 0) {
                $reasons.Add("restore $($restore.RestoreExecution): no served-data API read was recorded")
                continue
            }
            if ($records.Count -gt 1) {
                $reasons.Add("restore $($restore.RestoreExecution): $($records.Count) served-data API reads were recorded; expected exactly one")
                continue
            }
            foreach ($defect in @(Get-RestoreSmokeApiReadDefect -Record $records[0] -TemplateKind $restore.TemplateKind)) {
                $reasons.Add("restore $($restore.RestoreExecution): $defect")
            }
        }
        foreach ($record in $apiReads) {
            $executionId = ConvertTo-RestoreSmokeLogSafeText ([string](Get-RestoreSmokeEvidenceValue $record "RestoreExecution"))
            if ([string]::IsNullOrWhiteSpace($executionId)) {
                $reasons.Add("a served-data API read names no restore execution")
            }
            elseif ($executionId -cnotin $requiredIds) {
                $reasons.Add("a served-data API read was recorded for restore $executionId, which no selected leg performs")
            }
        }
    }

    # SourceIdentity: each package bound to its pre-backup capture and independently inspected, and
    # every successful restore holding a new identity of its own.
    foreach ($identityReason in @(Get-RestoreSmokeSourceIdentityReason -Provenance $Provenance -Package $packages -RequiredRestore $requiredRestores)) {
        $reasons.Add($identityReason)
    }

    # Schema selection: each non-default selection proven to have taken effect, and the default
    # package refused under the core-only env before any container started.
    $selectedLegs = @()
    if ($null -ne $legList) {
        $selectedLegs = @($legList)
    }
    foreach ($selectionReason in @(Get-RestoreSmokeSelectionReason -Provenance $Provenance -Package $packages -RequiredRestore $requiredRestores -Leg $selectedLegs)) {
        $reasons.Add($selectionReason)
    }

    return [pscustomobject]@{
        Final   = ($reasons.Count -eq 0)
        Reasons = @($reasons)
    }
}

Export-ModuleMember -Function `
    ConvertTo-RestoreSmokeLogSafeText, `
    Get-RestoreSmokeWrapperProfile, `
    Get-RestoreSmokeWrapperArgumentSet, `
    Assert-RestoreSmokeLegSelection, `
    Get-RestoreSmokePackageFixture, `
    Get-RestoreSmokeLegPackageFixture, `
    Resolve-RestoreSmokeStandardVersion, `
    Get-RestoreSmokeForeignStackInventory, `
    Format-RestoreSmokeForeignStackInventory, `
    Assert-RestoreSmokeNoForeignStack, `
    Get-RestoreSmokeEngineVolumeDefinition, `
    Get-RestoreSmokeLeftoverVolumeClassification, `
    Format-RestoreSmokeLeftoverVolumeClassification, `
    Get-RestoreSmokeAllowedVolumeState, `
    Get-RestoreSmokeSourceRevision, `
    Get-RestoreSmokeFileSha256, `
    Invoke-RestoreSmokeSchemaToolBuild, `
    Get-RestoreSmokeImageBuildPlan, `
    New-RestoreSmokeImageLedger, `
    Get-RestoreSmokeImageTagState, `
    Register-RestoreSmokeImageTagOwnership, `
    Invoke-RestoreSmokeImageBuild, `
    Remove-RestoreSmokeOwnedImageTag, `
    Write-RestoreSmokeImageEnvironmentFile, `
    Write-RestoreSmokeCoreOnlyEnvironmentFile, `
    Get-RestoreSmokeStackObservation, `
    Read-RestoreSmokeStagedSelection, `
    New-RestoreSmokeStackStart, `
    Get-RestoreSmokeStagedSelectionReason, `
    Get-RestoreSmokePackageProvenance, `
    Resolve-RestoreSmokeApiEndpoint, `
    New-RestoreSmokeApiSession, `
    Reset-RestoreSmokeApiSession, `
    Get-RestoreSmokeDataStoreAesKey, `
    ConvertFrom-RestoreSmokeDataStoreCipherText, `
    Select-RestoreSmokeDataStore, `
    Get-RestoreSmokeJsonArrayLength, `
    Test-RestoreSmokeApiRead, `
    Get-RestoreSmokeRestoreExecutionId, `
    Get-RestoreSmokeRestoreExecutionFixture, `
    Get-RestoreSmokeRequiredApiRead, `
    Get-RestoreSmokeSourceIdentityValue, `
    Get-RestoreSmokeSourceIdentityRead, `
    Invoke-RestoreSmokeIdentityBoundPackageBuild, `
    Invoke-RestoreSmokePackageInspection, `
    Get-RestoreSmokeRestoredIdentityDefect, `
    Read-RestoreSmokeWorkspaceSelection, `
    Get-RestoreSmokeCatalogSelection, `
    Get-RestoreSmokeRefusalState, `
    Get-RestoreSmokeSelectionDefect, `
    Get-RestoreSmokeSelectionRefusalExpectedMessage, `
    Get-RestoreSmokeSelectionRefusalDefect, `
    Get-RestoreSmokeResultClassification
