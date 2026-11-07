// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics.CodeAnalysis;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Manifest;

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;

/// <summary>
/// The provider-neutral validation set of 6.9c over engine-supplied facts: (1) the effective-schema row
/// against the manifest, (2) the identity row by the stage's rule, (3) the engine facts and the physical
/// baseline, (4) the canonical inventory hash recomputed from the catalog, (5) the <c>DmsDatastoreOnly</c>
/// content profile (F-P9, <c>Assert-DmsOnlyInventory</c>), (6) the resource-schema partition against the
/// manifest's projects and the deployment's expectations, (7) the engine's security rules (F-P9b,
/// <c>Assert-PostgresqlArtifactOwnership</c> and <c>Assert-PostgresqlArtifactPrivilege</c>, or
/// <c>Assert-MssqlArtifactSecurityState</c>), and (8) the Populated predicate. Checks run in that order and
/// the first failure is the verdict, named for
/// <c>Diagnostic.Operation</c>; messages carry no value from a row. Nothing here reaches a server.
/// </summary>
public static class DmsTargetValidator
{
    public const string EffectiveSchemaCheck = "EffectiveSchema";
    public const string IdentityCheck = "Identity";
    public const string EngineMajorCheck = "EngineMajor";
    public const string CompatibilityLevelCheck = "CompatibilityLevel";
    public const string DocumentJsonColumnTypeCheck = "DocumentJsonColumnType";
    public const string InventoryHashCheck = "InventoryHash";
    public const string ContentProfileCheck = "ContentProfile";
    public const string ProjectSetCheck = "ProjectSet";
    public const string OwnershipCheck = "Ownership";
    public const string PrivilegesCheck = "Privileges";
    public const string SecurityStateCheck = "SecurityState";
    public const string PopulatedPredicateCheck = "PopulatedPredicate";

    /// <summary>The locked-down global role the authoritative DDL makes own the SECURITY DEFINER enqueue functions (D-16).</summary>
    public const string EnqueueOwnerRoleName = "edfi_dms_enqueue_owner";

    private const string DocumentCacheTableName = "DocumentCache";
    private const string DmsSchemaName = "dms";
    private const string AuthSchemaName = "auth";
    private const string CoreProjectSchemaName = "edfi";
    private const string ConfigurationServiceSchemaName = "dmscs";
    private const string TrackedChangesPrefix = "tracked_changes_";
    private const string IdentityStateObjectPrefix = "OpenIddict";
    private const string PublicGrantee = "-";

    /// <summary>The fixed principal ids every SQL Server database has: <c>public</c>, <c>dbo</c>, and the <c>db_owner</c> role.</summary>
    private const int MssqlPublicId = 0;

    private const int MssqlDboId = 1;
    private const int MssqlDbOwnerId = 16384;

    /// <summary>The <c>sys.database_permissions.class</c> values the built-in defaults use: the database and an object.</summary>
    private const int MssqlDatabaseClass = 0;

    private const int MssqlObjectClass = 1;

    /// <summary>The built-in database-scope grants of a new SQL Server database, as <c>permission_name|grantee_id</c>.</summary>
    private static readonly HashSet<string> _mssqlDatabaseDefaults = new(StringComparer.Ordinal)
    {
        $"CONNECT|{MssqlDboId}",
        $"VIEW ANY COLUMN ENCRYPTION KEY DEFINITION|{MssqlPublicId}",
        $"VIEW ANY COLUMN MASTER KEY DEFINITION|{MssqlPublicId}",
    };

    /// <summary>Exactly the grants the core DDL emitter makes to the enqueue owner, as <c>kind|identity|privilege</c> with identities quoted as the privilege query quotes them.</summary>
    private static readonly HashSet<string> _enqueueOwnerGrants = new(StringComparer.Ordinal)
    {
        "schema|dms|USAGE",
        "relation|dms.\"DocumentProjectionWork\"|SELECT",
        "relation|dms.\"DocumentProjectionWork\"|INSERT",
        "relation|dms.\"DocumentProjectionWork\"|UPDATE",
        "relation|dms.\"DocumentCacheState\"|SELECT",
    };

    /// <summary>
    /// Judges <paramref name="facts"/> and <paramref name="identity"/> against <paramref name="manifest"/>
    /// (already proven equal to the pin) and the deployment's <paramref name="expectedProjects"/>
    /// (<c>Compatibility:Projects</c>), under the stage's <paramref name="rule"/>.
    /// </summary>
    public static DmsTargetValidation Validate(
        DmsTargetFacts facts,
        DmsIdentityRead identity,
        RestoreManifest manifest,
        IReadOnlyList<string> expectedProjects,
        DmsTargetValidationRule rule
    )
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(expectedProjects);
        ArgumentNullException.ThrowIfNull(rule);

        if (!EffectiveSchemaMatches(facts.EffectiveSchema, manifest))
        {
            return Invalid(DmsProvisioningFailureCode.TargetIncompatible, EffectiveSchemaCheck);
        }

        if (IdentityVerdict(identity, rule) is { } identityFailure)
        {
            return identityFailure;
        }

        if (rule is DmsTargetValidationRule.AfterRename)
        {
            // The rename changes no content; only items 1 and 2 are re-checked before the marker.
            return new DmsTargetValidation.Valid();
        }

        return EngineVerdict(facts, manifest)
            ?? InventoryVerdict(facts, manifest, expectedProjects)
            ?? SecurityVerdict(facts.Security)
            ?? PopulatedVerdict(facts, manifest)
            ?? new DmsTargetValidation.Valid();
    }

    /// <summary>
    /// The canonical inventory of the catalog <paramref name="facts"/> describe: every enumerated schema with
    /// its objects, plus the principals (F-P8, <c>ConvertFrom-InventoryQueryRow</c>). False, with a value-free
    /// <paramref name="defect"/>, when a schema repeats, an object names a schema the enumeration did not
    /// report, or the canonical rules refuse an entry.
    /// </summary>
    public static bool TryBuildInventory(
        DmsTargetFacts facts,
        [NotNullWhen(true)] out CanonicalInventory? inventory,
        [NotNullWhen(false)] out string? defect
    )
    {
        ArgumentNullException.ThrowIfNull(facts);
        inventory = null;
        defect = null;

        Dictionary<string, List<CanonicalInventory.Entry>> objectsBySchema = new(StringComparer.Ordinal);
        for (int index = 0; index < facts.SchemaNames.Count; index++)
        {
            if (!objectsBySchema.TryAdd(facts.SchemaNames[index], []))
            {
                defect = $"schema entry {index + 1} repeats an earlier schema name.";
                return false;
            }
        }

        for (int index = 0; index < facts.InventoryObjects.Count; index++)
        {
            DmsInventoryObject entry = facts.InventoryObjects[index];
            if (!objectsBySchema.TryGetValue(entry.SchemaName, out List<CanonicalInventory.Entry>? objects))
            {
                defect = $"object entry {index + 1} names a schema the schema enumeration did not report.";
                return false;
            }

            objects.Add(new CanonicalInventory.Entry(entry.Name, entry.Type));
        }

        IEnumerable<CanonicalInventory.Schema> schemas = facts.SchemaNames.Select(
            name => new CanonicalInventory.Schema(name, objectsBySchema[name])
        );
        return CanonicalInventory.TryCreate(schemas, facts.Principals, out inventory, out defect);
    }

    private static bool EffectiveSchemaMatches(DmsEffectiveSchemaRead read, RestoreManifest manifest) =>
        read is DmsEffectiveSchemaRead.Found row
        && Same(row.ApiSchemaFormatVersion, manifest.ApiSchemaFormatVersion)
        && Same(row.EffectiveSchemaHash, manifest.EffectiveSchemaHash)
        && row.ResourceKeyCount == manifest.ResourceKeyCount
        && Same(row.ResourceKeySeedHashB64, manifest.ResourceKeySeedHashB64);

    private static DmsTargetValidation? IdentityVerdict(
        DmsIdentityRead identity,
        DmsTargetValidationRule rule
    )
    {
        Guid? found = identity is DmsIdentityRead.Found row ? row.SourceIdentity : null;
        return rule switch
        {
            DmsTargetValidationRule.Staging staging => found is null
            || found == staging.ExpectedSourceIdentity
                ? Invalid(DmsProvisioningFailureCode.PackageIdentityInvalid, IdentityCheck)
                : null,
            DmsTargetValidationRule.Reconcile reconcile => found switch
            {
                null => Invalid(DmsProvisioningFailureCode.TargetUnverifiable, IdentityCheck),
                _ when found != reconcile.ExpectedSourceIdentity => Invalid(
                    DmsProvisioningFailureCode.TargetUnowned,
                    IdentityCheck
                ),
                _ => null,
            },
            DmsTargetValidationRule.AfterRename afterRename => found is null
            || found != afterRename.PackageSourceIdentity
                ? Invalid(DmsProvisioningFailureCode.InstanceChanged, IdentityCheck)
                : null,
            _ => throw new InvalidOperationException("Unknown target validation rule."),
        };
    }

    /// <summary>Item 3: live major at least the manifest's, the SQL Server compatibility level, and the <c>DocumentJson</c> physical baseline (F-P10).</summary>
    private static DmsTargetValidation? EngineVerdict(DmsTargetFacts facts, RestoreManifest manifest)
    {
        if (
            !TryParseLeadingMajor(manifest.EngineVersion, out int manifestMajor)
            || facts.EngineMajorVersion < manifestMajor
        )
        {
            return Invalid(DmsProvisioningFailureCode.TargetIncompatible, EngineMajorCheck);
        }

        if (
            manifest.DatabaseCompatibilityLevel is { } manifestLevel
            && facts.CompatibilityLevel != manifestLevel
        )
        {
            return Invalid(DmsProvisioningFailureCode.TargetIncompatible, CompatibilityLevelCheck);
        }

        return DocumentJsonColumnTypeMatches(facts.DocumentJsonColumns, manifest)
            ? null
            : Invalid(DmsProvisioningFailureCode.TargetIncompatible, DocumentJsonColumnTypeCheck);
    }

    /// <summary>
    /// The delivered rule (<c>ConvertFrom-DocumentJsonColumnTypeRow</c>): at least one carrier, <c>DocumentCache</c>
    /// among them, every carrier the same type, and that type equal to both the manifest and the engine baseline.
    /// </summary>
    private static bool DocumentJsonColumnTypeMatches(
        IReadOnlyList<DmsDocumentJsonColumn> columns,
        RestoreManifest manifest
    )
    {
        if (columns.Count == 0)
        {
            return false;
        }

        if (
            !columns.Any(column =>
                Same(column.SchemaName, DmsSchemaName) && Same(column.TableName, DocumentCacheTableName)
            )
        )
        {
            return false;
        }

        HashSet<string> types = new(
            columns.Select(column => column.ColumnType.Trim().ToLowerInvariant()),
            StringComparer.Ordinal
        );
        if (types.Count != 1)
        {
            return false;
        }

        string type = types.Single();
        return Same(type, manifest.DocumentJsonColumnType)
            && Same(type, RestoreManifest.DocumentJsonColumnTypeFor(manifest.DatabaseEngine));
    }

    /// <summary>Items 4 to 6 over one canonical inventory built from the catalog.</summary>
    private static DmsTargetValidation? InventoryVerdict(
        DmsTargetFacts facts,
        RestoreManifest manifest,
        IReadOnlyList<string> expectedProjects
    )
    {
        if (
            !TryBuildInventory(facts, out CanonicalInventory? inventory, out _)
            || !Same(CanonicalInventoryHash.Compute(inventory), manifest.InventorySha256)
        )
        {
            return Invalid(DmsProvisioningFailureCode.TargetIncompatible, InventoryHashCheck);
        }

        string alwaysPresentSchemaName = Same(manifest.DatabaseEngine, RestoreManifest.MssqlEngine)
            ? "dbo"
            : "public";
        SchemaPartition partition = SchemaPartition.Of(
            inventory.Schemas.Select(schema => schema.SchemaName),
            alwaysPresentSchemaName
        );
        if (!IsDmsOnly(inventory, partition, facts.DatabaseLevelObjects))
        {
            return Invalid(DmsProvisioningFailureCode.ContentProfileViolation, ContentProfileCheck);
        }

        HashSet<string> projects = new(partition.ProjectSchemaNames, StringComparer.Ordinal);
        HashSet<string> projectsIgnoringCase = new(
            partition.ProjectSchemaNames,
            StringComparer.OrdinalIgnoreCase
        );
        return projects.SetEquals(manifest.Projects) && projectsIgnoringCase.SetEquals(expectedProjects)
            ? null
            : Invalid(DmsProvisioningFailureCode.TargetIncompatible, ProjectSetCheck);
    }

    /// <summary>
    /// The <c>DmsDatastoreOnly</c> gate (F-P9, <c>Assert-DmsOnlyInventory</c>), in the delivered order: the
    /// <c>dms</c> schema present and non-empty; no <c>dmscs</c>; no <c>OpenIddict*</c> object; the always-present
    /// schema empty; every resource schema paired with its <c>tracked_changes_</c> companion and vice versa;
    /// <c>edfi</c> present when any resource schema is; no principals; no database-level objects.
    /// </summary>
    private static bool IsDmsOnly(
        CanonicalInventory inventory,
        SchemaPartition partition,
        IReadOnlyList<DmsDatabaseLevelObject> databaseLevelObjects
    ) =>
        partition.HasDms
        && inventory.Schemas.All(schema => IsCleanSchema(schema, partition))
        && IsPaired(partition)
        && inventory.Principals.Count == 0
        && databaseLevelObjects.Count == 0;

    private static bool IsCleanSchema(CanonicalInventory.Schema schema, SchemaPartition partition)
    {
        if (Same(schema.SchemaName, DmsSchemaName) && schema.Objects.Count == 0)
        {
            return false;
        }

        if (
            string.Equals(
                schema.SchemaName,
                ConfigurationServiceSchemaName,
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            return false;
        }

        // Judged for every schema, dms included: identity state inside the DMS schema is contamination too.
        bool carriesIdentityState = schema.Objects.Any(entry =>
            entry.Name.StartsWith(IdentityStateObjectPrefix, StringComparison.OrdinalIgnoreCase)
        );
        bool alwaysPresentHoldsObjects =
            partition.AlwaysPresent.Contains(schema.SchemaName, StringComparer.Ordinal)
            && schema.Objects.Count > 0;
        return !carriesIdentityState && !alwaysPresentHoldsObjects;
    }

    /// <summary>Every resource schema has its companion and vice versa, and <c>edfi</c> is present when any resource schema is; <c>dmscs</c> is judged once, above.</summary>
    private static bool IsPaired(SchemaPartition partition)
    {
        string[] resourceSchemas =
        [
            .. partition.ResourceSchemas.Where(name =>
                !string.Equals(name, ConfigurationServiceSchemaName, StringComparison.OrdinalIgnoreCase)
            ),
        ];
        bool everyResourceHasCompanion = Array.TrueForAll(
            resourceSchemas,
            resource => partition.TrackedChangesProjects.Contains(resource, StringComparer.Ordinal)
        );
        bool everyCompanionHasResource = partition.TrackedChangesProjects.All(project =>
            partition.ResourceSchemas.Contains(project, StringComparer.Ordinal)
        );
        bool coreIsPresent =
            resourceSchemas.Length == 0
            || partition.ResourceSchemas.Contains(CoreProjectSchemaName, StringComparer.Ordinal);
        return everyResourceHasCompanion && everyCompanionHasResource && coreIsPresent;
    }

    /// <summary>Item 7 by engine (F-P9b).</summary>
    private static DmsTargetValidation? SecurityVerdict(DmsSecurityFacts security) =>
        security switch
        {
            DmsSecurityFacts.Postgresql postgresql => PostgresqlSecurityVerdict(postgresql),
            DmsSecurityFacts.Mssql mssql => MssqlSecurityVerdict(mssql),
            _ => throw new InvalidOperationException("Unknown security facts."),
        };

    /// <summary>Ownership is judged before privileges (D-15), so an owner change is reported as ownership even though it also moves privileges.</summary>
    private static DmsTargetValidation? PostgresqlSecurityVerdict(DmsSecurityFacts.Postgresql security)
    {
        if (!security.Ownership.All(IsAllowedOwnership))
        {
            return Invalid(DmsProvisioningFailureCode.SecurityStateViolation, OwnershipCheck);
        }

        return security.Privileges.All(IsAllowedPrivilege)
            ? null
            : Invalid(DmsProvisioningFailureCode.SecurityStateViolation, PrivilegesCheck);
    }

    /// <summary>
    /// <c>Assert-PostgresqlArtifactOwnership</c>: the rows are the objects the administrator does not own; only
    /// functions owned by the enqueue owner and the <c>public</c> schema owned by <c>pg_database_owner</c> are allowed.
    /// </summary>
    private static bool IsAllowedOwnership(DmsPostgresqlOwnershipRow row) =>
        (Same(row.Kind, "function") && Same(row.Owner, EnqueueOwnerRoleName))
        || (Same(row.Kind, "schema") && Same(row.Identity, "public") && Same(row.Owner, "pg_database_owner"));

    /// <summary>
    /// <c>Assert-PostgresqlArtifactPrivilege</c>: an object's owner may hold anything; PUBLIC may hold only its
    /// built-in defaults (EXECUTE on functions not owned by the enqueue owner, USAGE on types, USAGE on the
    /// <c>public</c> schema); the enqueue owner may hold exactly the core DDL's grants; nobody else may hold anything.
    /// </summary>
    private static bool IsAllowedPrivilege(DmsPostgresqlPrivilegeRow row)
    {
        if (Same(row.Grantee, row.Owner))
        {
            return true;
        }

        if (Same(row.Grantee, PublicGrantee))
        {
            return row.Kind switch
            {
                "function" => Same(row.Privilege, "EXECUTE") && !Same(row.Owner, EnqueueOwnerRoleName),
                "type" => Same(row.Privilege, "USAGE"),
                "schema" => Same(row.Identity, "public") && Same(row.Privilege, "USAGE"),
                _ => false,
            };
        }

        return Same(row.Grantee, EnqueueOwnerRoleName)
            && _enqueueOwnerGrants.Contains($"{row.Kind}|{row.Identity}|{row.Privilege}");
    }

    /// <summary>
    /// <c>Assert-MssqlArtifactSecurityState</c>: the generated DDL emits no GRANT, DENY, user, role, or
    /// membership, so only the built-in defaults of a new database are allowed, judged by principal id,
    /// securable class and scope, and grant state rather than by name.
    /// </summary>
    private static DmsTargetValidation? MssqlSecurityVerdict(DmsSecurityFacts.Mssql security) =>
        security.RoleMembers.All(IsAllowedRoleMember) && security.Permissions.All(IsAllowedPermission)
            ? null
            : Invalid(DmsProvisioningFailureCode.SecurityStateViolation, SecurityStateCheck);

    /// <summary><c>dbo</c> as a member of <c>db_owner</c> is the only membership a new database has.</summary>
    private static bool IsAllowedRoleMember(DmsMssqlRoleMemberRow row) =>
        row.RoleId == MssqlDbOwnerId && row.MemberId == MssqlDboId;

    /// <summary>
    /// A plain <c>GRANT</c> without a column scope that is either one of the database-scope defaults, or
    /// <c>SELECT</c> to <c>public</c> on a system object (a negative object id, which no user object has).
    /// </summary>
    private static bool IsAllowedPermission(DmsMssqlPermissionRow row)
    {
        if (!Same(row.StateDesc, "GRANT") || row.MinorId != 0)
        {
            return false;
        }

        return row.Class switch
        {
            MssqlDatabaseClass => row.MajorId == 0
                && _mssqlDatabaseDefaults.Contains($"{row.PermissionName}|{row.GranteeId}"),
            MssqlObjectClass => row.MajorId < 0
                && row.GranteeId == MssqlPublicId
                && Same(row.PermissionName, "SELECT"),
            _ => false,
        };
    }

    /// <summary>Item 8: a Populated template must carry at least one non-descriptor, non-school-year document.</summary>
    private static DmsTargetValidation? PopulatedVerdict(DmsTargetFacts facts, RestoreManifest manifest) =>
        Same(manifest.TemplateKind, RestoreManifest.PopulatedTemplateKind)
        && facts.HasPopulatedDocument != true
            ? Invalid(DmsProvisioningFailureCode.TargetIncompatible, PopulatedPredicateCheck)
            : null;

    /// <summary>The leading integer of an engine version string (<c>18.1</c>, <c>16.8 (Debian …)</c>); false when there is none.</summary>
    private static bool TryParseLeadingMajor(string engineVersion, out int major)
    {
        ReadOnlySpan<char> text = engineVersion.AsSpan().TrimStart();
        int length = 0;
        while (length < text.Length && char.IsAsciiDigit(text[length]))
        {
            length++;
        }

        return int.TryParse(text[..length], out major);
    }

    private static bool Same(string? left, string? right) =>
        string.Equals(left, right, StringComparison.Ordinal);

    private static DmsTargetValidation.Invalid Invalid(DmsProvisioningFailureCode code, string check) =>
        new(code, check);

    /// <summary>The DMS-owned role a schema name plays (<c>Get-TemplateProjectSchemaPartition</c>).</summary>
    private enum SchemaRole
    {
        Dms,
        Auth,
        AlwaysPresent,
        TrackedChanges,
        Resource,
    }

    /// <summary>
    /// <c>Get-TemplateProjectSchemaPartition</c>: the DMS-owned roles of the enumerated schema names. A name is
    /// a <c>tracked_changes_</c> companion only with the full prefix, so lookalikes partition as resource
    /// schemas and fail the pairing gate. Project order is <c>edfi</c> first, then ordinal.
    /// </summary>
    internal sealed record SchemaPartition(
        bool HasDms,
        bool HasAuth,
        IReadOnlyList<string> AlwaysPresent,
        IReadOnlyList<string> TrackedChangesProjects,
        IReadOnlyList<string> ResourceSchemas,
        IReadOnlyList<string> ProjectSchemaNames
    )
    {
        public static SchemaPartition Of(IEnumerable<string> schemaNames, string alwaysPresentSchemaName)
        {
            ILookup<SchemaRole, string> byRole = schemaNames
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToLookup(name => RoleOf(name, alwaysPresentSchemaName));
            List<string> trackedChangesProjects =
            [
                .. byRole[SchemaRole.TrackedChanges].Select(name => name[TrackedChangesPrefix.Length..]),
            ];
            List<string> resourceSchemas = [.. byRole[SchemaRole.Resource]];
            trackedChangesProjects.Sort(StringComparer.Ordinal);
            resourceSchemas.Sort(StringComparer.Ordinal);

            return new SchemaPartition(
                byRole[SchemaRole.Dms].Any(),
                byRole[SchemaRole.Auth].Any(),
                [.. byRole[SchemaRole.AlwaysPresent]],
                trackedChangesProjects,
                resourceSchemas,
                ProjectOrder(resourceSchemas)
            );
        }

        private static SchemaRole RoleOf(string name, string alwaysPresentSchemaName)
        {
            if (Same(name, DmsSchemaName))
            {
                return SchemaRole.Dms;
            }

            if (Same(name, AuthSchemaName))
            {
                return SchemaRole.Auth;
            }

            if (string.Equals(name, alwaysPresentSchemaName, StringComparison.OrdinalIgnoreCase))
            {
                return SchemaRole.AlwaysPresent;
            }

            return
                name.StartsWith(TrackedChangesPrefix, StringComparison.Ordinal)
                && name.Length > TrackedChangesPrefix.Length
                ? SchemaRole.TrackedChanges
                : SchemaRole.Resource;
        }

        /// <summary>Core first, then the remaining resource schemas in ordinal order.</summary>
        private static List<string> ProjectOrder(List<string> resourceSchemas)
        {
            List<string> projects = [];
            if (resourceSchemas.Contains(CoreProjectSchemaName, StringComparer.Ordinal))
            {
                projects.Add(CoreProjectSchemaName);
            }

            projects.AddRange(resourceSchemas.Where(name => !Same(name, CoreProjectSchemaName)));
            return projects;
        }
    }
}
