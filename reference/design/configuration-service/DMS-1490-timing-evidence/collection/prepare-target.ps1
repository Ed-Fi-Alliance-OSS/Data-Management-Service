# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    Prepares an isolated measurement target from one pinned commit.
.DESCRIPTION
    Extracts the commit with `git archive` into an empty folder outside any worktree, so switching
    targets never touches a working tree's source. With -BuildHostRelease it also builds the
    Configuration Service frontend in Release there, for a lane that runs the service as a host
    process, and fingerprints the production assemblies it produced.

    Writes target.json into the destination: the commit, its tree, the commit id carried by the
    archive, the command used, the time, and the SHA-256 of each fingerprinted assembly. Nothing
    secret is read or written.
.PARAMETER RepositoryRoot
    A clone of the repository that contains the commit.
.PARAMETER Commit
    The full commit id to extract.
.PARAMETER Destination
    An empty or missing folder outside every worktree.
.PARAMETER BuildHostRelease
    Also build src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore in Release.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $RepositoryRoot,
    [Parameter(Mandatory)] [ValidatePattern('^[0-9a-f]{40}$')] [string] $Commit,
    [Parameter(Mandatory)] [string] $Destination,
    [switch] $BuildHostRelease
)

$ErrorActionPreference = 'Stop'

$fingerprintedAssemblies = @(
    'EdFi.DmsConfigurationService.Frontend.AspNetCore.dll',
    'EdFi.DmsConfigurationService.Backend.dll',
    'EdFi.DmsConfigurationService.Backend.OpenIddict.dll',
    'EdFi.DmsConfigurationService.Backend.Postgresql.dll',
    'EdFi.DmsConfigurationService.Backend.Mssql.dll'
)

if ((Test-Path -LiteralPath $Destination) -and (Get-ChildItem -LiteralPath $Destination -Force | Select-Object -First 1)) {
    throw "Destination '$Destination' is not empty; prepare each target into a new folder."
}
New-Item -ItemType Directory -Force -Path $Destination | Out-Null

$tree = (git -C $RepositoryRoot rev-parse "$Commit^{tree}").Trim()
if ($LASTEXITCODE -ne 0) { throw "Commit $Commit is not in $RepositoryRoot." }

$archive = Join-Path ([System.IO.Path]::GetTempPath()) ("target-" + [guid]::NewGuid().ToString('N') + '.tar')
try {
    git -C $RepositoryRoot archive --format=tar --output=$archive $Commit
    if ($LASTEXITCODE -ne 0) { throw "git archive failed for $Commit." }
    # git get-tar-commit-id reads the archive from standard input, which cmd redirects.
    $archivedCommit = (& cmd /c "git get-tar-commit-id < `"$archive`"").Trim()
    tar -x -f $archive -C $Destination
    if ($LASTEXITCODE -ne 0) { throw "Extracting the archive of $Commit failed." }
}
finally {
    Remove-Item -LiteralPath $archive -ErrorAction SilentlyContinue
}
if ($archivedCommit -ne $Commit) { throw "The archive carries commit '$archivedCommit', not $Commit." }

$target = [ordered]@{
    schema           = 'timing-target/1'
    commit           = $Commit
    tree             = $tree
    archived_commit  = $archivedCommit
    command          = "git -C <repository> archive --format=tar $Commit | tar -x -C <destination>"
    prepared_utc     = (Get-Date).ToUniversalTime().ToString('o')
    host_release     = $null
}

if ($BuildHostRelease) {
    $project = Join-Path $Destination 'src\config\frontend\EdFi.DmsConfigurationService.Frontend.AspNetCore\EdFi.DmsConfigurationService.Frontend.AspNetCore.csproj'
    dotnet build $project -c Release --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "The Release build of $Commit failed." }
    $bin = Join-Path (Split-Path -Parent $project) 'bin\Release\net10.0'
    $fingerprints = [ordered]@{}
    foreach ($assembly in $fingerprintedAssemblies) {
        $path = Join-Path $bin $assembly
        $fingerprints[$assembly] = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    $target.host_release = [ordered]@{
        project      = 'src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore'
        output       = 'bin/Release/net10.0'
        fingerprints = $fingerprints
    }
}

$target | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $Destination 'target.json') -Encoding utf8NoBOM
Get-Content -LiteralPath (Join-Path $Destination 'target.json')
