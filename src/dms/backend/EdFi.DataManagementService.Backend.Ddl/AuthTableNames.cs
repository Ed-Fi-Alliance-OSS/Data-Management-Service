// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Ddl;

/// <summary>
/// Shared table and column name constants for the <c>auth.*</c> schema,
/// used by <see cref="AuthDdlEmitter"/>.
/// </summary>
internal static class AuthTableNames
{
    public static readonly DbSchemaName AuthSchema = new("auth");

    public static readonly DbTableName EdOrgIdToEdOrgId = new(
        AuthSchema,
        "EducationOrganizationIdToEducationOrganizationId"
    );

    public static readonly DbColumnName SourceEdOrgId = new("SourceEducationOrganizationId");
    public static readonly DbColumnName TargetEdOrgId = new("TargetEducationOrganizationId");
}
