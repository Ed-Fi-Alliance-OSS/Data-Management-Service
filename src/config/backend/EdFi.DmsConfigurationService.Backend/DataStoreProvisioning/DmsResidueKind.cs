// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;

/// <summary>The kinds of resource bounded cleanup can leave behind.</summary>
public enum DmsResidueKind
{
    OperationDirectory,

    /// <summary>The temporary <c>PGPASSFILE</c>; logged at Critical because it holds a credential.</summary>
    CredentialFile,

    ExtractedArtifact,

    PackageCopy,

    StagedBackup,

    ProbeFile,

    StagingDatabase,

    ChildProcess,

    /// <summary>A validated database renamed into the target name whose marker was never assigned.</summary>
    UnmarkedTarget,

    /// <summary>The target was fenced (connections disallowed / single user) and could not be unfenced.</summary>
    FencedTarget,
}
