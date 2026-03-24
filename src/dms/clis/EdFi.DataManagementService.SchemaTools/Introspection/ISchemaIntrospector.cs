// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.SchemaTools.Introspection;

/// <summary>
/// Introspects a provisioned database and returns its structural schema as a manifest.
/// Mirrors the <see cref="Provisioning.IDatabaseProvisioner"/> pattern.
/// </summary>
public interface ISchemaIntrospector
{
    /// <summary>
    /// Connects to the database, reads catalog metadata for the specified schemas,
    /// and returns a deterministic manifest of all structural objects.
    /// </summary>
    /// <param name="connectionString">Connection string to the provisioned database.</param>
    /// <param name="schemaAllowlist">
    /// Database schemas to include (e.g., "dms", "edfi", "auth"). Only objects in these
    /// schemas are returned. System schemas are excluded by omission.
    /// </param>
    ProvisionedSchemaManifest Introspect(
        string connectionString,
        IReadOnlyList<string> schemaAllowlist
    );
}
