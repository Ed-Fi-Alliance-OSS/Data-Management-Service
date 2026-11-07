// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Manifest;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning;

/// <summary>
/// A passing baseline for the validator: the facts a clean PostgreSQL DMS data store yields (one resource
/// project with its companion, the enqueue-owned function, exactly the core DDL's grants, PUBLIC's
/// defaults) and a manifest that describes them. Each vector changes exactly one thing.
/// </summary>
internal static class TargetFactsSample
{
    public const string Admin = "postgres";
    public const string Enqueue = DmsTargetValidator.EnqueueOwnerRoleName;
    public const string EnqueueFunction = "dms.\"TF_Document_EnqueueProjectionInsert\"()";

    public static readonly Guid PackageIdentity = Guid.Parse("6f1c9a52-2d3e-4b7a-9c8d-1e2f3a4b5c6d");

    public static readonly string EffectiveSchemaHash = string.Concat(Enumerable.Repeat("ab", 32));

    public static readonly string ResourceKeySeedHashB64 = Convert.ToBase64String(
        Enumerable.Range(1, 32).Select(value => (byte)value).ToArray()
    );

    public static readonly string[] Projects = ["edfi"];

    public static DmsEffectiveSchemaRead.Found EffectiveSchema() =>
        new("1.0.0", EffectiveSchemaHash, 42, ResourceKeySeedHashB64);

    public static DmsIdentityRead.Found Identity() => new(PackageIdentity);

    public static DmsPostgresqlOwnershipRow Owned(string kind, string owner, string identity) =>
        new(kind, owner, identity);

    public static DmsPostgresqlPrivilegeRow Held(
        string kind,
        string privilege,
        string owner,
        string grantee,
        string identity
    ) => new(kind, privilege, owner, grantee, identity);

    public static DmsTargetFacts PostgresqlFacts() =>
        new()
        {
            EffectiveSchema = EffectiveSchema(),
            EngineMajorVersion = 18,
            CompatibilityLevel = null,
            DocumentJsonColumns = [new DmsDocumentJsonColumn("dms", "DocumentCache", "jsonb")],
            SchemaNames = ["dms", "edfi", "public", "tracked_changes_edfi"],
            InventoryObjects =
            [
                new DmsInventoryObject("dms", "DataStoreIdentity", "table"),
                new DmsInventoryObject("dms", "EffectiveSchema", "table"),
                new DmsInventoryObject("dms", "DocumentCache", "table"),
                new DmsInventoryObject("dms", "DocumentCacheState", "table"),
                new DmsInventoryObject("dms", "DocumentProjectionWork", "table"),
                new DmsInventoryObject("dms", "GetMaxChangeVersion()", "function"),
                new DmsInventoryObject("dms", "TF_Document_EnqueueProjectionInsert()", "function"),
                new DmsInventoryObject("edfi", "School", "table"),
                new DmsInventoryObject("edfi", "ReferenceKind", "enum"),
                new DmsInventoryObject("tracked_changes_edfi", "School", "table"),
            ],
            Principals = [],
            DatabaseLevelObjects = [],
            Security = new DmsSecurityFacts.Postgresql(
                [Owned("function", Enqueue, EnqueueFunction), Owned("schema", "pg_database_owner", "public")],
                [
                    Held("function", "EXECUTE", Enqueue, Enqueue, EnqueueFunction),
                    Held("function", "EXECUTE", Admin, "-", "dms.\"GetMaxChangeVersion\"()"),
                    Held("function", "EXECUTE", Admin, Admin, "dms.\"GetMaxChangeVersion\"()"),
                    Held("relation", "SELECT", Admin, Admin, "dms.\"DataStoreIdentity\""),
                    Held("relation", "SELECT", Admin, Enqueue, "dms.\"DocumentCacheState\""),
                    Held("relation", "SELECT", Admin, Enqueue, "dms.\"DocumentProjectionWork\""),
                    Held("relation", "INSERT", Admin, Enqueue, "dms.\"DocumentProjectionWork\""),
                    Held("relation", "UPDATE", Admin, Enqueue, "dms.\"DocumentProjectionWork\""),
                    Held("schema", "USAGE", Admin, Enqueue, "dms"),
                    Held("schema", "USAGE", "pg_database_owner", "-", "public"),
                    Held("schema", "USAGE", "pg_database_owner", "pg_database_owner", "public"),
                    Held("type", "USAGE", Admin, "-", "edfi.\"ReferenceKind\""),
                    Held("type", "USAGE", Admin, Admin, "edfi.\"ReferenceKind\""),
                ]
            ),
            HasPopulatedDocument = false,
        };

    /// <summary>The manifest describing <paramref name="facts"/>: inventory hash from the facts, every other field from the baseline unless overridden.</summary>
    public static RestoreManifest ManifestFor(
        DmsTargetFacts facts,
        string templateKind = RestoreManifest.MinimalTemplateKind,
        string databaseEngine = RestoreManifest.PostgresqlEngine,
        string[]? projects = null,
        string engineVersion = "18.1",
        int? databaseCompatibilityLevel = null,
        string? documentJsonColumnType = null
    )
    {
        if (
            !DmsTargetValidator.TryBuildInventory(
                facts,
                out CanonicalInventory? inventory,
                out string? defect
            )
        )
        {
            throw new InvalidOperationException(
                $"The sample facts do not form a canonical inventory: {defect}"
            );
        }

        return new RestoreManifest
        {
            Version = RestoreManifest.SupportedVersion,
            PackageId = "EdFi.Api.Minimal.Template.PostgreSql.5.2.0",
            PackageVersion = "1.0.123",
            DatabaseEngine = databaseEngine,
            TemplateKind = templateKind,
            DataStandardVersion = "5.2.0",
            ContentProfile = RestoreManifest.ContentProfileDmsDatastoreOnly,
            Projects = projects ?? Projects,
            ApiSchemaFormatVersion = "1.0.0",
            EffectiveSchemaHash = EffectiveSchemaHash,
            ResourceKeyCount = 42,
            ResourceKeySeedHashB64 = ResourceKeySeedHashB64,
            RelationalMappingVersion = "v3",
            EngineVersion = engineVersion,
            DatabaseCompatibilityLevel = databaseCompatibilityLevel,
            DocumentJsonColumnType =
                documentJsonColumnType ?? RestoreManifest.DocumentJsonColumnTypeFor(databaseEngine),
            Inventory = inventory,
            InventorySha256 = CanonicalInventoryHash.Compute(inventory),
            ArtifactFileName = "EdFi.Api.Minimal.Template.PostgreSql.5.2.0.sql",
            ArtifactSha256 = string.Concat(Enumerable.Repeat("cd", 32)),
        };
    }

    public static DmsTargetFacts WithObjects(this DmsTargetFacts facts, params DmsInventoryObject[] added) =>
        facts with
        {
            InventoryObjects = [.. facts.InventoryObjects, .. added],
        };

    public static DmsTargetFacts WithSchemas(this DmsTargetFacts facts, params string[] added) =>
        facts with
        {
            SchemaNames = [.. facts.SchemaNames, .. added],
        };

    public static DmsTargetFacts WithoutSchema(this DmsTargetFacts facts, string schemaName) =>
        facts with
        {
            SchemaNames = [.. facts.SchemaNames.Where(name => name != schemaName)],
            InventoryObjects = [.. facts.InventoryObjects.Where(entry => entry.SchemaName != schemaName)],
        };

    public static DmsTargetFacts WithPrivileges(
        this DmsTargetFacts facts,
        params DmsPostgresqlPrivilegeRow[] added
    )
    {
        DmsSecurityFacts.Postgresql security = (DmsSecurityFacts.Postgresql)facts.Security;
        return facts with { Security = security with { Privileges = [.. security.Privileges, .. added] } };
    }

    public static DmsTargetFacts WithOwnership(
        this DmsTargetFacts facts,
        params DmsPostgresqlOwnershipRow[] added
    )
    {
        DmsSecurityFacts.Postgresql security = (DmsSecurityFacts.Postgresql)facts.Security;
        return facts with { Security = security with { Ownership = [.. security.Ownership, .. added] } };
    }

    /// <summary>The fixed principal ids of every SQL Server database.</summary>
    public const int MssqlPublic = 0;

    public const int MssqlDbo = 1;
    public const int MssqlGuest = 2;
    public const int MssqlDbOwner = 16384;

    /// <summary>A permission row; the names only describe it and never take part in judgment.</summary>
    public static DmsMssqlPermissionRow Permission(
        int securableClass,
        string permissionName,
        string stateDesc,
        int granteeId,
        int majorId,
        int minorId = 0
    ) => new(securableClass, permissionName, stateDesc, granteeId, majorId, minorId, "grantee", "securable");

    public static DmsMssqlRoleMemberRow Member(int roleId, int memberId) =>
        new(roleId, memberId, "role", "member");

    /// <summary>
    /// The facts a clean SQL Server DMS data store yields: <c>dbo</c> always enumerated, one resource project with
    /// its companion, the <c>nvarchar(max)</c> baseline, a compatibility level, and exactly the built-in security
    /// state of a new database (the three database-scope defaults, public SELECT on system objects, dbo in
    /// db_owner).
    /// </summary>
    public static DmsTargetFacts MssqlFacts() =>
        new()
        {
            EffectiveSchema = EffectiveSchema(),
            EngineMajorVersion = 17,
            CompatibilityLevel = 170,
            DocumentJsonColumns = [new DmsDocumentJsonColumn("dms", "DocumentCache", "nvarchar(max)")],
            SchemaNames = ["dbo", "dms", "edfi", "tracked_changes_edfi"],
            InventoryObjects =
            [
                new DmsInventoryObject("dms", "DataStoreIdentity", "table"),
                new DmsInventoryObject("dms", "EffectiveSchema", "table"),
                new DmsInventoryObject("dms", "DocumentCache", "table"),
                new DmsInventoryObject("dms", "GetMaxChangeVersion", "function"),
                new DmsInventoryObject("edfi", "School", "table"),
                new DmsInventoryObject("tracked_changes_edfi", "School", "table"),
            ],
            Principals = [],
            DatabaseLevelObjects = [],
            Security = new DmsSecurityFacts.Mssql(
                [
                    Permission(0, "CONNECT", "GRANT", MssqlDbo, 0),
                    Permission(0, "VIEW ANY COLUMN ENCRYPTION KEY DEFINITION", "GRANT", MssqlPublic, 0),
                    Permission(0, "VIEW ANY COLUMN MASTER KEY DEFINITION", "GRANT", MssqlPublic, 0),
                    Permission(1, "SELECT", "GRANT", MssqlPublic, -385),
                    Permission(1, "SELECT", "GRANT", MssqlPublic, -1073624922),
                ],
                [Member(MssqlDbOwner, MssqlDbo)]
            ),
            HasPopulatedDocument = false,
        };

    /// <summary>The SQL Server manifest describing <paramref name="facts"/>.</summary>
    public static RestoreManifest MssqlManifestFor(DmsTargetFacts facts) =>
        ManifestFor(
            facts,
            databaseEngine: RestoreManifest.MssqlEngine,
            engineVersion: "17.0.1000.7",
            databaseCompatibilityLevel: 170
        );

    public static DmsTargetFacts WithPermissions(
        this DmsTargetFacts facts,
        params DmsMssqlPermissionRow[] added
    )
    {
        DmsSecurityFacts.Mssql security = (DmsSecurityFacts.Mssql)facts.Security;
        return facts with { Security = security with { Permissions = [.. security.Permissions, .. added] } };
    }

    public static DmsTargetFacts WithRoleMembers(
        this DmsTargetFacts facts,
        params DmsMssqlRoleMemberRow[] added
    )
    {
        DmsSecurityFacts.Mssql security = (DmsSecurityFacts.Mssql)facts.Security;
        return facts with { Security = security with { RoleMembers = [.. security.RoleMembers, .. added] } };
    }
}
