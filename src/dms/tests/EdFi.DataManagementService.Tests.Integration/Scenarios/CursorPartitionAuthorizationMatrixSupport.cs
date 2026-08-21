// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Core.External.Security;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Tests.Integration.Fixtures;
using FluentAssertions;
using static EdFi.DataManagementService.Tests.Integration.Scenarios.PeopleRelationshipGetManyScenarioHelpers;

namespace EdFi.DataManagementService.Tests.Integration.Scenarios;

/// <summary>
/// Seeding, principal configuration, and database-side support for the cursor/partition authorization
/// matrix. Kept beside the assertions rather than inside them so the scenario file states what is proven
/// and this file states what it is proven over.
/// </summary>
internal static class CursorPartitionAuthorizationMatrixSupport
{
    // The shared authorization fixture already publishes the principal, the two schools, and the prefix
    // pair its DDL and provider suites are built around; restating them here would let this matrix drift
    // from the fixture it seeds.
    public const long ClaimEducationOrganizationId =
        RelationshipAuthorizationCrudTestSupport.ClaimEducationOrganizationId;

    /// <summary>
    /// A second education organization claim held alongside the first. Both reach the authorized school
    /// through the hierarchy table, so every accessible candidate matches more than one authorization row.
    /// </summary>
    public const long SecondClaimEducationOrganizationId = ClaimEducationOrganizationId + 1;

    public const long AuthorizedSchoolId = RelationshipAuthorizationCrudTestSupport.AuthorizedSchoolId;
    public const long UnauthorizedSchoolId = RelationshipAuthorizationCrudTestSupport.UnauthorizedSchoolId;

    public const string AuthorizedNamespacePrefix =
        RelationshipAuthorizationCrudTestSupport.AuthorizedNamespacePrefix;
    public const string UnauthorizedNamespacePrefix =
        RelationshipAuthorizationCrudTestSupport.UnauthorizedNamespacePrefix;

    /// <summary>
    /// A prefix the caller holds that no seeded value starts with. Distinct from holding no prefixes at
    /// all, which is the no-prefixes-configured 403 preflight terminal rather than an authorized-but-empty
    /// candidate set.
    /// </summary>
    public const string UnmatchedNamespacePrefix = "uri://nothing.example/";

    /// <summary>
    /// Obeys the "{BasisResource}With..." convention the strategy classifier requires, so the basis
    /// resource resolves to the seeded carrier and the check filters its root DocumentId by membership in
    /// the matching auth view.
    /// </summary>
    public const string CustomViewStrategyName =
        RelationshipAuthorizationCrudTestSupport.NamespaceResourceName + "WithCursorPartitionMatrix";

    public const string NamespaceResourcesEndpoint = "/data/authz/authorizationNamespaceResources";
    public const string AcademicSubjectDescriptorsEndpoint = "/data/ed-fi/academicSubjectDescriptors";

    private const string SchoolsEndpoint = "/data/ed-fi/schools";
    private const string EducationOrganizationCategoryDescriptorsEndpoint =
        "/data/ed-fi/educationOrganizationCategoryDescriptors";
    private const string GradeLevelDescriptorsEndpoint = "/data/ed-fi/gradeLevelDescriptors";

    private const string SchoolCategoryDescriptor =
        "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School";
    private const string GradeLevelDescriptor = "uri://ed-fi.org/GradeLevelDescriptor#Tenth grade";

    private const string EdFiProjectName = "Ed-Fi";
    private const string AuthzProjectName = "Authz";
    private const string AcademicSubjectDescriptorResourceName = "AcademicSubjectDescriptor";

    /// <summary>
    /// Documents seeded per row. Every fourth document is the inaccessible control, leaving 21 accessible
    /// candidates: at the lowered maximum page size the mandatory minimum partition size is ten candidate
    /// rows, so a request for three partitions over 21 candidates really produces three.
    /// </summary>
    public const int SeededDocumentCount = 28;

    /// <summary>
    /// The inaccessible controls are spread through the seed rather than clustered at one end. Boundary
    /// rows then fall in different places for the authorized and the unauthorized candidate sets, so
    /// boundaries calculated before authorization cannot coincidentally agree with a correctly authorized
    /// walk.
    /// </summary>
    private static bool IsAccessibleIndex(int index) => index % 4 != 3;

    private static bool IsAccessible(MatrixAccessibility accessibility, int index) =>
        accessibility switch
        {
            MatrixAccessibility.All => true,
            MatrixAccessibility.None => false,
            _ => IsAccessibleIndex(index),
        };

    /// <summary>
    /// The schema-name prefix the tracked-change inventory gives every project schema's shadow tables.
    /// </summary>
    private const string TrackedChangeSchemaPrefix = "tracked_changes_";

    /// <summary>
    /// The identity assigned to accessible rows stays below this value and inaccessible rows start at it,
    /// which lets the custom-view row express accessibility as a range predicate over a real resource
    /// column instead of a hard-coded identifier list.
    /// </summary>
    private const int InaccessibleIdentityFloor = 2000;

    private const int AccessibleIdentityFloor = 1000;

    /// <summary>How a seeded document is made inaccessible to the row's principal.</summary>
    public enum MatrixAccessibility
    {
        /// <summary>Every document is accessible; the row's strategy filters nothing.</summary>
        All,

        /// <summary>The inaccessible documents reference an education organization the caller cannot reach.</summary>
        EducationOrganization,

        /// <summary>The inaccessible documents carry a namespace outside the caller's prefixes.</summary>
        Namespace,

        /// <summary>
        /// The inaccessible documents differ only by identity, which is the column the matrix auth view
        /// selects on. Everything else about them is identical to an accessible document, so nothing but
        /// the view can be what excludes them.
        /// </summary>
        Identity,

        /// <summary>
        /// No document is accessible. The row's principal is configured to reach none of the seed, so an
        /// empty result is authorization's doing rather than an empty collection.
        /// </summary>
        None,
    }

    /// <summary>
    /// What a seed produced: the identities the row's principal may read, the identities it may not, and a
    /// filter that selects exactly one accessible document. The expected sets come from the seed rather
    /// than from a query, so nothing in the assertions re-derives accessibility from the endpoint under
    /// test.
    /// </summary>
    public sealed record SeededMatrix(
        IReadOnlyList<string> AccessibleIds,
        IReadOnlyList<string> InaccessibleIds,
        string FilterQuery,
        string FilterMatchedId
    );

    public static IClaimSetProvider CreateRelationshipReadClaimSetProvider(FixtureContext fixture) =>
        CreateClaimSetProvider(
            fixture,
            [
                new RelationshipReadResource(
                    AuthzProjectName,
                    RelationshipAuthorizationCrudTestSupport.NamespaceResourceName
                ),
            ],
            AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly
        );

    public static IClaimSetProvider CreateNamespaceReadClaimSetProvider(FixtureContext fixture) =>
        CreateClaimSetProvider(
            fixture,
            [
                new RelationshipReadResource(
                    AuthzProjectName,
                    RelationshipAuthorizationCrudTestSupport.NamespaceResourceName
                ),
            ],
            AuthorizationStrategyNameConstants.NamespaceBased
        );

    public static IClaimSetProvider CreateCustomViewReadClaimSetProvider(FixtureContext fixture) =>
        CreateClaimSetProvider(
            fixture,
            [
                new RelationshipReadResource(
                    AuthzProjectName,
                    RelationshipAuthorizationCrudTestSupport.NamespaceResourceName
                ),
            ],
            CustomViewStrategyName
        );

    public static IClaimSetProvider CreateDescriptorNamespaceReadClaimSetProvider(FixtureContext fixture) =>
        CreateClaimSetProvider(
            fixture,
            [new RelationshipReadResource(EdFiProjectName, AcademicSubjectDescriptorResourceName)],
            AuthorizationStrategyNameConstants.NamespaceBased
        );

    /// <summary>
    /// Creates the descriptors and both education organizations the regular-resource carriers reference.
    /// The unauthorized school exists as a real, resolvable reference target: an inaccessible document has
    /// to be creatable before its exclusion from a read can mean anything.
    /// </summary>
    public static async Task SeedRegularReferenceDataAsync(ApiIntegrationHarness harness)
    {
        await CreateDescriptorAsync(
            harness,
            EducationOrganizationCategoryDescriptorsEndpoint,
            "uri://ed-fi.org/EducationOrganizationCategoryDescriptor",
            "School"
        );
        await CreateDescriptorAsync(
            harness,
            GradeLevelDescriptorsEndpoint,
            "uri://ed-fi.org/GradeLevelDescriptor",
            "Tenth grade"
        );

        await CreateSchoolAsync(
            harness,
            SchoolsEndpoint,
            AuthorizedSchoolId,
            "Matrix Authorized School",
            SchoolCategoryDescriptor,
            GradeLevelDescriptor
        );
        await CreateSchoolAsync(
            harness,
            SchoolsEndpoint,
            UnauthorizedSchoolId,
            "Matrix Unauthorized School",
            SchoolCategoryDescriptor,
            GradeLevelDescriptor
        );
    }

    /// <summary>
    /// Seeds the namespace-and-education-organization carrier. Documents are created in index order, so
    /// their generated DocumentId values follow the interleaving above.
    /// </summary>
    public static async Task<SeededMatrix> SeedNamespaceResourcesAsync(
        ApiIntegrationHarness harness,
        MatrixAccessibility accessibility
    )
    {
        await SeedRegularReferenceDataAsync(harness);

        List<string> accessibleIds = [];
        List<string> inaccessibleIds = [];
        string filterQuery = string.Empty;
        string filterMatchedId = string.Empty;

        for (var index = 0; index < SeededDocumentCount; index++)
        {
            bool accessible = IsAccessible(accessibility, index);
            string name = DocumentName(accessible, index);

            var payload = new JsonObject
            {
                ["authorizationNamespaceId"] = Identity(accessible, index),
                ["name"] = name,
                ["namespace"] = DocumentNamespace(accessibility, accessible, index),
                ["schoolReference"] = new JsonObject
                {
                    ["schoolId"] =
                        accessibility == MatrixAccessibility.EducationOrganization && !accessible
                            ? UnauthorizedSchoolId
                            : AuthorizedSchoolId,
                },
                ["classPeriods"] = new JsonArray(),
            };

            string documentId = await CreateDocumentAsync(harness, NamespaceResourcesEndpoint, payload);

            if (accessible)
            {
                accessibleIds.Add(documentId);
                if (filterQuery.Length == 0)
                {
                    filterQuery = $"name={Uri.EscapeDataString(name)}";
                    filterMatchedId = documentId;
                }
            }
            else
            {
                inaccessibleIds.Add(documentId);
            }
        }

        return new SeededMatrix(accessibleIds, inaccessibleIds, filterQuery, filterMatchedId);
    }

    /// <summary>
    /// Seeds the descriptor carrier. This descriptor resource is not referenced by any reference data the
    /// regular carriers need, so the collection holds exactly what this seed created and the accessible
    /// set can be asserted as an exact equality rather than as containment.
    /// </summary>
    public static async Task<SeededMatrix> SeedAcademicSubjectDescriptorsAsync(
        ApiIntegrationHarness harness,
        MatrixAccessibility accessibility
    )
    {
        List<string> accessibleIds = [];
        List<string> inaccessibleIds = [];
        string filterQuery = string.Empty;
        string filterMatchedId = string.Empty;

        for (var index = 0; index < SeededDocumentCount; index++)
        {
            bool accessible = IsAccessible(accessibility, index);
            string codeValue = DocumentName(accessible, index);
            string descriptorNamespace =
                (accessibility == MatrixAccessibility.Namespace && !accessible)
                    ? UnauthorizedNamespacePrefix + "AcademicSubjectDescriptor"
                    : AuthorizedNamespacePrefix + "AcademicSubjectDescriptor";

            var payload = new JsonObject
            {
                ["namespace"] = descriptorNamespace,
                ["codeValue"] = codeValue,
                ["shortDescription"] = codeValue,
            };

            string documentId = await CreateDocumentAsync(
                harness,
                AcademicSubjectDescriptorsEndpoint,
                payload
            );

            if (accessible)
            {
                accessibleIds.Add(documentId);
                if (filterQuery.Length == 0)
                {
                    filterQuery = $"codeValue={Uri.EscapeDataString(codeValue)}";
                    filterMatchedId = documentId;
                }
            }
            else
            {
                inaccessibleIds.Add(documentId);
            }
        }

        return new SeededMatrix(accessibleIds, inaccessibleIds, filterQuery, filterMatchedId);
    }

    /// <summary>
    /// Creates the auth view the custom-view row is configured against. The view is a deployment artifact
    /// DMS never creates, so the row provisions it on the leased database exactly as an administrator
    /// would, and it selects over a real resource column rather than over a fixed identifier list.
    /// </summary>
    public static async Task CreateMatrixCustomViewAsync(ApiIntegrationHarness harness)
    {
        string rootTable = RelationshipAuthorizationCrudTestSupport.NamespaceResourceName;
        string schema = await ResolveTableSchemaAsync(harness, rootTable);
        bool isMssql = IsMssql(harness.DbConnection);

        string sql = isMssql
            ? $"""
                CREATE VIEW [auth].[{EscapeSqlServerIdentifier(CustomViewStrategyName)}] AS
                SELECT [DocumentId]
                FROM [{EscapeSqlServerIdentifier(schema)}].[{EscapeSqlServerIdentifier(rootTable)}]
                WHERE [AuthorizationNamespaceId] < {InaccessibleIdentityFloor};
                """
            : $"""
                CREATE VIEW "auth"."{EscapePostgresqlIdentifier(CustomViewStrategyName)}" AS
                SELECT "DocumentId"
                FROM "{EscapePostgresqlIdentifier(schema)}"."{EscapePostgresqlIdentifier(rootTable)}"
                WHERE "AuthorizationNamespaceId" < {InaccessibleIdentityFloor};
                """;

        await ExecuteNonQueryAsync(harness.DbConnection, sql);
    }

    /// <summary>
    /// The database identities behind a set of public identifiers, read straight from the document table.
    /// This is an identity lookup rather than an authorization decision, which is what makes it usable as
    /// the yardstick for the partition starting identifiers.
    /// </summary>
    public static async Task<IReadOnlyList<long>> ReadDocumentIdsAsync(
        ApiIntegrationHarness harness,
        IEnumerable<string> documentUuids
    )
    {
        string sql = IsMssql(harness.DbConnection)
            ? """
                SELECT [DocumentId] FROM [dms].[Document] WHERE [DocumentUuid] = @documentUuid;
                """
            : """
                SELECT "DocumentId" FROM "dms"."Document" WHERE "DocumentUuid" = @documentUuid;
                """;

        List<long> documentIds = [];

        foreach (string documentUuid in documentUuids)
        {
            documentIds.Add(
                await ReadInt64Async(harness.DbConnection, sql, ("@documentUuid", Guid.Parse(documentUuid)))
            );
        }

        return documentIds;
    }

    private static async Task<string> ResolveTableSchemaAsync(ApiIntegrationHarness harness, string tableName)
    {
        string sql = IsMssql(harness.DbConnection)
            ? """
                SELECT s.[name]
                FROM [sys].[tables] t
                INNER JOIN [sys].[schemas] s ON s.[schema_id] = t.[schema_id]
                WHERE t.[name] = @tableName;
                """
            : """
                SELECT "table_schema"
                FROM "information_schema"."tables"
                WHERE "table_name" = @tableName;
                """;

        await using DbCommand command = harness.DbConnection.CreateCommand();
        command.CommandText = sql;
        AddParameters(command, ("@tableName", tableName));

        List<string> schemas = [];
        await using (DbDataReader reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                string schema = reader.GetString(0);

                // Every project schema has a tracked-change twin holding a same-named table of Old/New
                // column pairs. It is not the resource root the auth view must select from, and it is named
                // by the fixed convention the model builder applies to the project schema.
                if (!schema.StartsWith(TrackedChangeSchemaPrefix, StringComparison.Ordinal))
                {
                    schemas.Add(schema);
                }
            }
        }

        // Resolved rather than assumed: a schema-naming change then fails here, where the message names the
        // table, instead of silently creating an auth view over nothing.
        schemas.Should().ContainSingle($"exactly one schema must own the '{tableName}' root table");

        return schemas[0];
    }

    private static async Task<string> CreateDocumentAsync(
        ApiIntegrationHarness harness,
        string endpoint,
        JsonObject payload
    )
    {
        using HttpResponseMessage response = await PostJsonAsync(harness, endpoint, payload);
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Created, $"POST {endpoint} body: {body}");
        response.Headers.Location.Should().NotBeNull($"POST {endpoint} must return a Location header");

        Uri location = response.Headers.Location!;
        string locationPath = location.IsAbsoluteUri ? location.AbsolutePath : location.OriginalString;

        return locationPath[(locationPath.LastIndexOf('/') + 1)..];
    }

    private static int Identity(bool accessible, int index) =>
        (accessible ? AccessibleIdentityFloor : InaccessibleIdentityFloor) + index;

    private static string DocumentName(bool accessible, int index) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"matrix-{(accessible ? "accessible" : "denied")}-{index:D2}"
        );

    private static string DocumentNamespace(MatrixAccessibility accessibility, bool accessible, int index) =>
        (accessibility == MatrixAccessibility.Namespace && !accessible)
            ? $"{UnauthorizedNamespacePrefix}matrix/{index}"
            : $"{AuthorizedNamespacePrefix}matrix/{index}";

    private static string EscapeSqlServerIdentifier(string identifierPart) =>
        identifierPart.Replace("]", "]]", StringComparison.Ordinal);

    private static string EscapePostgresqlIdentifier(string identifierPart) =>
        identifierPart.Replace("\"", "\"\"", StringComparison.Ordinal);
}
