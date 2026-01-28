// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.External;

/// <summary>
/// Supported SQL dialects for relational model derivation and emission.
/// </summary>
public enum SqlDialect
{
    /// <summary>
    /// PostgreSQL.
    /// </summary>
    Pgsql,

    /// <summary>
    /// SQL Server.
    /// </summary>
    Mssql,
}

/// <summary>
/// Describes a schema component participating in the effective schema set.
/// </summary>
/// <param name="ProjectEndpointName">The stable API endpoint name (e.g., <c>ed-fi</c>).</param>
/// <param name="ProjectName">The logical project name (e.g., <c>Ed-Fi</c>).</param>
/// <param name="ProjectVersion">The project version label.</param>
/// <param name="IsExtensionProject">Whether the project is an extension.</param>
public sealed record SchemaComponentInfo(
    string ProjectEndpointName,
    string ProjectName,
    string ProjectVersion,
    bool IsExtensionProject
);

/// <summary>
/// Represents a resource key entry in the effective schema set.
/// </summary>
/// <param name="ResourceKeyId">The smallint resource key identifier.</param>
/// <param name="Resource">The qualified resource identifier.</param>
/// <param name="ResourceVersion">The resource version label.</param>
/// <param name="IsAbstractResource">Whether the resource is abstract.</param>
public sealed record ResourceKeyEntry(
    short ResourceKeyId,
    QualifiedResourceName Resource,
    string ResourceVersion,
    bool IsAbstractResource
);

/// <summary>
/// Summarizes the effective schema set and deterministic resource key seed.
/// </summary>
/// <param name="ApiSchemaFormatVersion">The ApiSchema.json format version.</param>
/// <param name="RelationalMappingVersion">The relational mapping version.</param>
/// <param name="EffectiveSchemaHash">The effective schema hash (lowercase hex).</param>
/// <param name="ResourceKeyCount">The number of resource keys in the effective schema.</param>
/// <param name="ResourceKeySeedHash">The SHA-256 hash of the resource key seed list.</param>
/// <param name="SchemaComponentsInEndpointOrder">Schema components ordered by endpoint name.</param>
/// <param name="ResourceKeysInIdOrder">Resource keys ordered by identifier.</param>
public sealed record EffectiveSchemaInfo(
    string ApiSchemaFormatVersion,
    string RelationalMappingVersion,
    string EffectiveSchemaHash,
    int ResourceKeyCount,
    byte[] ResourceKeySeedHash,
    IReadOnlyList<SchemaComponentInfo> SchemaComponentsInEndpointOrder,
    IReadOnlyList<ResourceKeyEntry> ResourceKeysInIdOrder
);
