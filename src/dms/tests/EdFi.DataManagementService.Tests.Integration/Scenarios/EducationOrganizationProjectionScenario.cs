// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EdFi.DataManagementService.Core.EducationOrganizationProjection;
using EdFi.DataManagementService.Core.Handler;
using EdFi.DataManagementService.Core.Response;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Core.Security.Model;
using EdFi.DataManagementService.Tests.Integration.Doubles;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using static EdFi.DataManagementService.Tests.Integration.Scenarios.EducationOrganizationProjectionHost;
using Credentials = EdFi.DataManagementService.Tests.Integration.Doubles.EducationOrganizationProjectionCredentials;
using Problem = EdFi.DataManagementService.Core.Response.EducationOrganizationProjectionProblem;

namespace EdFi.DataManagementService.Tests.Integration.Scenarios;

/// <summary>
/// The education-organization projection endpoint over HTTP, through the production host and the
/// production provider readers against a provisioned database, on each engine. Authentication, the
/// claim-set lookup, client binding and the data-store catalog are the doubles; everything from
/// routing to the database read is real.
/// </summary>
/// <remarks>
/// Hierarchies are created through the resource API with the smoke credential, which does not grant
/// the projection claim; contradictions the API refuses to write are written directly to the
/// database. Each test starts from its own freshly provisioned database.
/// </remarks>
internal static class EducationOrganizationProjectionScenario
{
    private const string JsonMediaType = "application/json";
    private const string ProblemMediaType = "application/problem+json";
    private const string AuthenticationType = "urn:ed-fi:api:security:authentication";
    private const string ForbiddenType = "urn:ed-fi:api:security:authorization:";
    private const string NotFoundType = "urn:ed-fi:api:not-found";
    private const string BadRequestType = "urn:ed-fi:api:bad-request";
    private const string ParameterValidationType = "urn:ed-fi:api:bad-request:parameter-validation-failed";
    private const string ServiceUnavailableType = "urn:ed-fi:api:service-unavailable";
    private const string AuthorizationDeniedType = "urn:ed-fi:api:authorization-denied";
    private const string SecurityConfigurationType = "urn:ed-fi:api:system:configuration:security";
    private const string TooManyRequestsType = "urn:ed-fi:api:too-many-requests";
    private const string UseSnapshotHeader = "Use-Snapshot";

    private const string StateAgency = "edfi.StateEducationAgency";
    private const string ServiceCenter = "edfi.EducationServiceCenter";
    private const string LocalAgency = "edfi.LocalEducationAgency";
    private const string School = "edfi.School";

    /// <summary>A bound on waiting for a database session to reach a state; reaching it ends the wait.</summary>
    private static readonly TimeSpan _sessionStateBound = TimeSpan.FromSeconds(30);

    private static readonly string[] _envelopeMembers =
    [
        "contractVersion",
        "dataStoreId",
        "nextCursor",
        "items",
    ];

    private static readonly string[] _itemMembers =
    [
        "educationOrganizationId",
        "nameOfInstitution",
        "shortNameOfInstitution",
        "discriminator",
        "parentId",
    ];

    /// <summary>
    /// The full hierarchy, read page by page and in one page: ascending numeric order across negative
    /// and beyond-double-precision int64 identifiers, each parent chosen by precedence, the four core
    /// types only, five members per item and no internal discriminator literal anywhere.
    /// </summary>
    public static async Task It_walks_the_hierarchy_in_identifier_order_with_parent_precedence(
        EducationOrganizationProjectionContext context
    )
    {
        ApiIntegrationHarness harness = Prepare(context);
        Seeder seeder = await Seeder.CreateAsync(harness, TenantA, RouteA);
        await seeder.StateAgencyAsync(1, "Projection State; \"Agency\": one", "PSA");
        await seeder.ServiceCenterAsync(10, "Region Ten", stateAgency: 1);
        await seeder.ServiceCenterAsync(11, "Region Eleven", "R11");
        await seeder.LocalAgencyAsync(100, "District 100", "D100", serviceCenter: 10, stateAgency: 1);
        await seeder.LocalAgencyAsync(101, "District 101", parent: 100, serviceCenter: 10, stateAgency: 1);
        await seeder.LocalAgencyAsync(102, "District 102", stateAgency: 1);
        await seeder.LocalAgencyAsync(103, "District 103");
        await seeder.SchoolAsync(long.MinValue, "Minimum School", localAgency: 100);
        await seeder.SchoolAsync(100001, "School 100001", localAgency: 100);
        await seeder.SchoolAsync(101001, "School 101001", "S101001", localAgency: 101);
        await seeder.SchoolAsync(900001, "School Without District");
        await seeder.SchoolAsync(2_147_483_648, "School Past Int32", localAgency: 102);
        await seeder.SchoolAsync(9_007_199_254_740_993, "School Past Double Precision", localAgency: 103);
        await seeder.SchoolAsync(long.MaxValue, "Maximum School", localAgency: 101);
        await seeder.PostSecondaryInstitutionAsync(800001, "Excluded Institution");

        Item[] expected =
        [
            new(long.MinValue, "Minimum School", null, School, 100),
            new(1, "Projection State; \"Agency\": one", "PSA", StateAgency, null),
            new(10, "Region Ten", null, ServiceCenter, 1),
            new(11, "Region Eleven", "R11", ServiceCenter, null),
            new(100, "District 100", "D100", LocalAgency, 10),
            new(101, "District 101", null, LocalAgency, 100),
            new(102, "District 102", null, LocalAgency, 1),
            new(103, "District 103", null, LocalAgency, null),
            new(100001, "School 100001", null, School, 100),
            new(101001, "School 101001", "S101001", School, 101),
            new(900001, "School Without District", null, School, null),
            new(2_147_483_648, "School Past Int32", null, School, 102),
            new(9_007_199_254_740_993, "School Past Double Precision", null, School, 103),
            new(long.MaxValue, "Maximum School", null, School, 101),
        ];

        Walk paged = await WalkAsync(
            harness,
            Credentials.Projection,
            TenantA,
            RouteA,
            PrimaryStoreId,
            limit: 4
        );
        paged.Items.Should().Equal(expected);
        paged.Pages.Select(page => ItemsOf(page).Count).Should().Equal(4, 4, 4, 2);

        Walk whole = await WalkAsync(
            harness,
            Credentials.Projection,
            TenantA,
            RouteA,
            PrimaryStoreId,
            limit: null
        );
        whole.Pages.Should().HaveCount(1);
        whole.Items.Should().Equal(expected);

        // Replaying a cursor while nothing changed returns the same page, cursor included.
        string secondPageCursor = NextCursorOf(paged.Pages[0])!;
        ProjectionResponse first = await GetAsync(
            harness,
            Credentials.Projection,
            ProjectionPath(TenantA, RouteA, Query(PrimaryStoreId, 4, secondPageCursor))
        );
        ProjectionResponse replay = await GetAsync(
            harness,
            Credentials.Projection,
            ProjectionPath(TenantA, RouteA, Query(PrimaryStoreId, 4, secondPageCursor))
        );
        ShouldBeSuccess(first, PrimaryStoreId);
        ShouldBeSuccess(replay, PrimaryStoreId);
        replay.Body.Should().Be(first.Body);
        first.Body.Should().Be(paged.Pages[1].Body);
    }

    /// <summary>
    /// A committed change between pages refuses the next page as <c>projection-changed</c>, a restarted
    /// read completes on the changed set, and a cursor is honored for its lifetime from the start of
    /// its read and refused after it.
    /// </summary>
    public static async Task It_refuses_a_changed_set_and_honors_the_cursor_lifetime(
        EducationOrganizationProjectionContext context
    )
    {
        ApiIntegrationHarness harness = Prepare(context);
        Seeder seeder = await Seeder.CreateAsync(harness, TenantA, RouteA);
        await seeder.StateAgencyAsync(1, "Projection State");
        await seeder.LocalAgencyAsync(100, "District 100", stateAgency: 1);
        await seeder.SchoolAsync(100001, "School One", localAgency: 100);
        await seeder.SchoolAsync(100002, "School Two", localAgency: 100);
        await seeder.SchoolAsync(100003, "School Three", localAgency: 100);

        ProjectionResponse firstPage = await GetPageAsync(harness, limit: 2, cursor: null);
        ShouldBeSuccess(firstPage, PrimaryStoreId);
        string cursor = NextCursorOf(firstPage)!;
        ProjectionResponse secondPage = await GetPageAsync(harness, limit: 2, cursor);
        ShouldBeSuccess(secondPage, PrimaryStoreId);
        ItemsOf(secondPage).Select(item => item.Id).Should().Equal(100001, 100002);

        await seeder.SchoolAsync(100002, "School Two Renamed", localAgency: 100, update: true);

        ShouldBeProjectionProblem(await GetPageAsync(harness, limit: 2, cursor), Problem.ProjectionChanged);
        ShouldBeProjectionProblem(
            await GetPageAsync(harness, limit: 2, NextCursorOf(secondPage)),
            Problem.ProjectionChanged
        );

        Walk restarted = await WalkAsync(
            harness,
            Credentials.Projection,
            TenantA,
            RouteA,
            PrimaryStoreId,
            limit: 2
        );
        restarted.Items.Single(item => item.Id == 100002).Name.Should().Be("School Two Renamed");

        // The lifetime runs from the start of the read, not from the page that issued the cursor.
        ProjectionResponse walkStart = await GetPageAsync(harness, limit: 2, cursor: null);
        string startCursor = NextCursorOf(walkStart)!;
        context.Clock.Advance(TimeSpan.FromMinutes(59));
        ProjectionResponse withinLifetime = await GetPageAsync(harness, limit: 2, startCursor);
        ShouldBeSuccess(withinLifetime, PrimaryStoreId);
        string laterCursor = NextCursorOf(withinLifetime)!;
        context.Clock.Advance(TimeSpan.FromMinutes(2));
        ShouldBeProjectionProblem(await GetPageAsync(harness, limit: 2, laterCursor), Problem.InvalidCursor);
        ShouldBeProjectionProblem(await GetPageAsync(harness, limit: 2, startCursor), Problem.InvalidCursor);

        static Task<ProjectionResponse> GetPageAsync(
            ApiIntegrationHarness harness,
            int limit,
            string? cursor
        ) =>
            GetAsync(
                harness,
                Credentials.Projection,
                ProjectionPath(TenantA, RouteA, Query(PrimaryStoreId, limit, cursor))
            );
    }

    /// <summary>
    /// Every credential shape against the projection, and the projection credential against the
    /// resource API. Authentication, tenant existence, client binding and the service claim are decided
    /// before any parameter is read.
    /// </summary>
    public static async Task It_answers_the_authorization_matrix(
        EducationOrganizationProjectionContext context
    )
    {
        ApiIntegrationHarness harness = Prepare(context);
        Seeder seeder = await Seeder.CreateAsync(harness, TenantA, RouteA);
        await seeder.StateAgencyAsync(1, "Projection State");

        string valid = ProjectionPath(TenantA, RouteA, Query(PrimaryStoreId));
        string invalidParameters = ProjectionPath(TenantA, RouteA, "dataStoreId=abc&limit=0&cursor=%21%21");

        (
            string Name,
            ProjectionCredential? Credential,
            string? RawAuthorization,
            string Path,
            int Status,
            string Type
        )[] denials =
        [
            ("no Authorization header", null, null, valid, 401, AuthenticationType),
            ("a non-bearer scheme", null, "Basic cHJvamVjdGlvbjpzZWNyZXQ=", valid, 401, AuthenticationType),
            (
                "an unknown token",
                new ProjectionCredential("not-a-token", "nobody", "none"),
                null,
                valid,
                401,
                AuthenticationType
            ),
            (
                "an unknown token with invalid parameters",
                new ProjectionCredential("not-a-token", "nobody", "none"),
                null,
                invalidParameters,
                401,
                AuthenticationType
            ),
            ("the resource credential", Credentials.Resource, null, valid, 403, ForbiddenType),
            (
                "the resource credential with invalid parameters",
                Credentials.Resource,
                null,
                invalidParameters,
                403,
                ForbiddenType
            ),
            ("the identity credential", Credentials.IdentityOnly, null, valid, 403, ForbiddenType),
            (
                "a claim set granting the claim without Read",
                Credentials.ProjectionWithoutRead,
                null,
                valid,
                403,
                ForbiddenType
            ),
            ("a claim set that does not exist", Credentials.UnknownClaimSet, null, valid, 403, ForbiddenType),
            (
                "a Read grant under another strategy",
                Credentials.ProjectionMisconfigured,
                null,
                valid,
                500,
                SecurityConfigurationType
            ),
            (
                "a Read grant under another strategy with invalid parameters",
                Credentials.ProjectionMisconfigured,
                null,
                invalidParameters,
                500,
                SecurityConfigurationType
            ),
            (
                "a client with no application in the tenant",
                Credentials.Unbound,
                null,
                valid,
                401,
                AuthenticationType
            ),
            (
                "a client whose binding is unavailable",
                Credentials.BindingUnavailable,
                null,
                valid,
                503,
                ServiceUnavailableType
            ),
            (
                "a tenant that does not exist",
                Credentials.Projection,
                null,
                ProjectionPath("Tenant_999999", RouteA, Query(PrimaryStoreId)),
                404,
                NotFoundType
            ),
            (
                "a malformed tenant",
                Credentials.Projection,
                null,
                ProjectionPath("bad.tenant", RouteA, Query(PrimaryStoreId)),
                400,
                BadRequestType
            ),
        ];

        foreach (var denial in denials)
        {
            ProjectionResponse response = await GetAsync(
                harness,
                denial.Credential,
                denial.Path,
                denial.RawAuthorization
            );
            ShouldBeProblem(response, denial.Status, denial.Type, denial.Name);
            response.Body.Should().NotContain("Projection State", denial.Name);
        }

        // The projection credential has no data stores and no resource claims, yet reads the projection.
        Walk walk = await WalkAsync(
            harness,
            Credentials.Projection,
            TenantA,
            RouteA,
            PrimaryStoreId,
            limit: null
        );
        walk.Items.Should().Equal(new Item(1, "Projection State", null, StateAgency, null));
        ShouldBeProblem(
            await GetAsync(harness, Credentials.Projection, invalidParameters),
            400,
            ParameterValidationType,
            "the same invalid parameters reach the parse step once the caller is authorized"
        );

        // ...and is denied by the resource API, reading or writing.
        string schools = DataPath(TenantA, RouteA, "schools");
        ProjectionResponse resourceRead = await GetAsync(harness, Credentials.Projection, schools);
        resourceRead.Status.Should().Be(HttpStatusCode.Forbidden, resourceRead.Body);
        TypeOf(resourceRead).Should().Be(AuthorizationDeniedType);
        (HttpStatusCode writeStatus, string writeBody, _) = await SendAsync(
            harness,
            Credentials.Projection,
            HttpMethod.Post,
            schools,
            Seeder.SchoolBody(100001, "Not Written", null, null)
        );
        writeStatus.Should().Be(HttpStatusCode.Forbidden, writeBody);
        ProjectionResponse written = await GetAsync(harness, Credentials.Resource, schools);
        written.Status.Should().Be(HttpStatusCode.OK, written.Body);
        JsonNode.Parse(written.Body)!.AsArray().Should().BeEmpty();

        // The suite's smoke claim set, which the resource credential uses, never grants the claim.
        IList<ClaimSet> smokeClaimSets = await new AllowAllClaimSetProvider(
            harness.Fixture
        ).GetAllClaimSets();
        smokeClaimSets
            .SelectMany(claimSet => claimSet.ResourceClaims)
            .Should()
            .NotContain(claim => claim.Name == Conventions.EducationOrganizationProjectionServiceClaimUri);
    }

    /// <summary>
    /// A store answers only in its own tenant and route context; a cursor binds to the tenant and the
    /// qualifiers it was issued for; a store registered after startup is found by one catalog reload;
    /// and a provisioned store with no education organizations reads as one empty page.
    /// </summary>
    public static async Task It_isolates_tenants_stores_and_route_contexts(
        EducationOrganizationProjectionContext context
    )
    {
        ApiIntegrationHarness harness = Prepare(context);
        Seeder seeder = await Seeder.CreateAsync(harness, TenantA, RouteA);
        await seeder.StateAgencyAsync(1, "Projection State");
        await seeder.ServiceCenterAsync(10, "Region Ten", stateAgency: 1);

        string tenantBDatabase = await context.LeaseDatabaseAsync();
        context.Catalog.Register(
            TenantB,
            Store(TenantBStoreId, tenantBDatabase, RouteB, context.Engine.OwnToken)
        );

        Walk tenantA = await WalkAsync(
            harness,
            Credentials.Projection,
            TenantA,
            RouteA,
            PrimaryStoreId,
            limit: null
        );
        tenantA.Items.Select(item => item.Id).Should().Equal(1, 10);

        // Tenant B's store is not found from tenant A, after exactly one reload of A's catalog.
        int reloads = context.Catalog.ReloadCount(TenantA);
        ProjectionResponse otherTenantsStore = await GetAsync(
            harness,
            Credentials.Projection,
            ProjectionPath(TenantA, RouteA, Query(TenantBStoreId))
        );
        ShouldBeProjectionProblem(otherTenantsStore, Problem.TargetNotFound);
        context.Catalog.ReloadCount(TenantA).Should().Be(reloads + 1);

        ShouldBeProblem(
            await GetAsync(
                harness,
                Credentials.Projection,
                ProjectionPath(TenantB, RouteB, Query(TenantBStoreId))
            ),
            401,
            AuthenticationType,
            "tenant A's projection client has no application in tenant B"
        );

        Walk tenantB = await WalkAsync(
            harness,
            Credentials.ProjectionTenantB,
            TenantB,
            RouteB,
            TenantBStoreId,
            limit: null
        );
        tenantB.Pages.Should().HaveCount(1);
        tenantB.Items.Should().BeEmpty("tenant B's store is provisioned and holds no education organization");
        ShouldBeProjectionProblem(
            await GetAsync(
                harness,
                Credentials.ProjectionTenantB,
                ProjectionPath(TenantB, RouteB, Query(PrimaryStoreId))
            ),
            Problem.TargetNotFound
        );

        // A qualifier mismatch is indistinguishable from an unknown store.
        ProjectionResponse mismatch = await GetAsync(
            harness,
            Credentials.Projection,
            ProjectionPath(TenantA, RouteA with { SchoolYear = "2026" }, Query(PrimaryStoreId))
        );
        ProjectionResponse unknown = await GetAsync(
            harness,
            Credentials.Projection,
            ProjectionPath(TenantA, RouteA, Query(999))
        );
        ShouldBeProjectionProblem(mismatch, Problem.TargetNotFound);
        ShouldBeProjectionProblem(unknown, Problem.TargetNotFound);
        WithoutCorrelation(mismatch).Should().Be(WithoutCorrelation(unknown));

        // A store registered after startup: one reload on the first miss, none afterwards.
        context.Catalog.Register(
            TenantA,
            Store(LateStoreId, context.PrimaryConnectionString, RouteA, context.Engine.OwnToken)
        );
        reloads = context.Catalog.ReloadCount(TenantA);
        Walk late = await WalkAsync(
            harness,
            Credentials.Projection,
            TenantA,
            RouteA,
            LateStoreId,
            limit: null
        );
        late.Items.Should().Equal(tenantA.Items);
        context.Catalog.ReloadCount(TenantA).Should().Be(reloads + 1);
        await WalkAsync(harness, Credentials.Projection, TenantA, RouteA, LateStoreId, limit: null);
        context.Catalog.ReloadCount(TenantA).Should().Be(reloads + 1);

        // A cursor is bound to its tenant and qualifiers: replayed elsewhere it is refused before the
        // store is even looked up.
        string cursor = NextCursorOf(
            await GetAsync(
                harness,
                Credentials.Projection,
                ProjectionPath(TenantA, RouteA, Query(PrimaryStoreId, 1))
            )
        )!;
        ShouldBeProjectionProblem(
            await GetAsync(
                harness,
                Credentials.ProjectionTenantB,
                ProjectionPath(TenantB, RouteB, Query(PrimaryStoreId, 1, cursor))
            ),
            Problem.InvalidCursor
        );
        ShouldBeProjectionProblem(
            await GetAsync(
                harness,
                Credentials.Projection,
                ProjectionPath(TenantA, RouteA with { SchoolYear = "2026" }, Query(PrimaryStoreId, 1, cursor))
            ),
            Problem.InvalidCursor
        );
        ProjectionResponse sameRoute = await GetAsync(
            harness,
            Credentials.Projection,
            ProjectionPath(TenantA, RouteA, Query(PrimaryStoreId, 1, cursor))
        );
        ShouldBeSuccess(sameRoute, PrimaryStoreId);
        ItemsOf(sameRoute).Select(item => item.Id).Should().Equal(10);
    }

    /// <summary>
    /// Every target state the catalog and the database can be in, each with its exact problem: the
    /// provider metadata states, a missing connection string, an absent database, the three fingerprint
    /// verdicts of a store registered after startup, and a catalog that cannot be reloaded.
    /// </summary>
    public static async Task It_answers_each_target_state(EducationOrganizationProjectionContext context)
    {
        ApiIntegrationHarness harness = Prepare(context);
        EducationOrganizationProjectionEngine engine = context.Engine;
        Seeder seeder = await Seeder.CreateAsync(harness, TenantA, RouteA);
        await seeder.StateAgencyAsync(1, "Projection State");

        Walk primary = await WalkAsync(
            harness,
            Credentials.Projection,
            TenantA,
            RouteA,
            PrimaryStoreId,
            limit: null
        );
        primary.Items.Select(item => item.Id).Should().Equal(1);

        // A legacy registration with no provider token is served by this deployment's dialect.
        Walk legacy = await WalkAsync(
            harness,
            Credentials.Projection,
            TenantA,
            RouteA,
            MissingProviderStoreId,
            limit: null
        );
        legacy.Items.Should().Equal(primary.Items);

        ShouldBeProjectionProblem(
            await GetStoreAsync(OtherProviderStoreId),
            Problem.TargetProviderUnsupported
        );
        ShouldBeProjectionProblem(
            await GetStoreAsync(UnknownProviderStoreId),
            Problem.TargetProviderUnsupported
        );
        ShouldBeProjectionProblem(
            await GetStoreAsync(NoConnectionStringStoreId),
            Problem.ServiceConfigurationError
        );
        ShouldBeProjectionProblem(await GetStoreAsync(AbsentDatabaseStoreId), Problem.TargetUnavailable);

        // One database registered after startup under three connection strings, so each fingerprint
        // verdict is read fresh rather than taken from another store's cache entry.
        string damaged = await context.LeaseDatabaseAsync();
        foreach (
            (long id, string applicationName) in new[]
            {
                (MismatchedFingerprintStoreId, "projection-mismatched"),
                (MalformedFingerprintStoreId, "projection-malformed"),
                (NotProvisionedStoreId, "projection-not-provisioned"),
            }
        )
        {
            context.Catalog.Register(
                TenantA,
                Store(id, engine.WithApplicationName(damaged, applicationName), RouteA, engine.OwnToken)
            );
        }

        await engine.ExecuteAsync(damaged, engine.MismatchFingerprintSql);
        context.Logs.Clear();
        ProjectionResponse mismatched = await GetStoreAsync(MismatchedFingerprintStoreId);
        ShouldBeProjectionProblem(mismatched, Problem.TargetSchemaIncompatible);
        ShouldNotDisclose(context, mismatched, EducationOrganizationProjectionEngine.HostileHash);

        await engine.ExecuteAsync(damaged, engine.MalformFingerprintSql);
        ShouldBeProjectionProblem(
            await GetStoreAsync(MalformedFingerprintStoreId),
            Problem.TargetSchemaIncompatible
        );

        await engine.ExecuteAsync(damaged, engine.RemoveFingerprintSql);
        ShouldBeProjectionProblem(await GetStoreAsync(NotProvisionedStoreId), Problem.DatabaseNotProvisioned);

        // A catalog that cannot be reloaded is an outage, never a missing store.
        context.Catalog.FailReloads(OutageTenant);
        int outageReloads = context.Catalog.ReloadCount(OutageTenant);
        context.Logs.Clear();
        ProjectionResponse outage = await GetAsync(
            harness,
            Credentials.Projection,
            ProjectionPath(OutageTenant, RouteA, Query(PrimaryStoreId))
        );
        ShouldBeProblem(outage, 503, ServiceUnavailableType, "the outage tenant's catalog reload fails");
        context.Catalog.ReloadCount(OutageTenant).Should().Be(outageReloads + 1);
        ShouldNotDisclose(context, outage, "cms-outage-hostile-text", "cms-outage-secret");

        Task<ProjectionResponse> GetStoreAsync(long dataStoreId) =>
            GetAsync(harness, Credentials.Projection, ProjectionPath(TenantA, RouteA, Query(dataStoreId)));
    }

    /// <summary>
    /// Read failures the handler maps once the schema step has passed on a cached verdict: a host that
    /// cannot be resolved fails in the read's own acquisition (503), and a dropped column fails its
    /// execution (409). Neither the host, the column nor any provider text is disclosed.
    /// </summary>
    public static async Task It_maps_read_failures_behind_a_cached_fingerprint_verdict(
        EducationOrganizationProjectionContext context
    )
    {
        ApiIntegrationHarness harness = Prepare(context);
        EducationOrganizationProjectionEngine engine = context.Engine;
        Seeder seeder = await Seeder.CreateAsync(harness, TenantA, RouteA);
        await seeder.StateAgencyAsync(1, "Projection State");
        await WalkAsync(harness, Credentials.Projection, TenantA, RouteA, PrimaryStoreId, limit: null);

        string host = Regex
            .Match(context.UnresolvableConnectionString, @"projection-unresolvable-[0-9a-f]{32}\.invalid")
            .Value;
        host.Should().NotBeEmpty();

        context.Logs.Clear();
        ProjectionResponse unresolvable = await GetStoreAsync(UnresolvableHostStoreId);
        ShouldBeProjectionProblem(unresolvable, Problem.TargetUnavailable);
        int verdictReads = context.Fingerprints.ReadsOf(context.UnresolvableConnectionString);
        verdictReads.Should().BeGreaterThan(0, "the store's verdict was read from the provisioned database");
        AcquireFailures(context).Should().Be(1, "the read failed acquiring its own connection");

        ProjectionResponse again = await GetStoreAsync(UnresolvableHostStoreId);
        ShouldBeProjectionProblem(again, Problem.TargetUnavailable);
        context
            .Fingerprints.ReadsOf(context.UnresolvableConnectionString)
            .Should()
            .Be(verdictReads, "the verdict is cached, so only the read itself touched the host");
        AcquireFailures(context).Should().Be(2);
        ShouldNotDisclose(context, again, host);

        await engine.ExecuteAsync(context.PrimaryConnectionString, engine.DropSchoolShortNameSql);
        context.Logs.Clear();
        ProjectionResponse dropped = await GetStoreAsync(PrimaryStoreId);
        ShouldBeProjectionProblem(dropped, Problem.TargetSchemaIncompatible);
        context
            .Logs.Events.Should()
            .Contain(
                logEvent =>
                    logEvent.Properties.GetValueOrDefault("ProviderCode") == engine.DroppedColumnProviderCode,
                "the execution failure is classified by the provider's code for the missing column"
            );
        ShouldNotDisclose(context, dropped, "ShortNameOfInstitution");

        Task<ProjectionResponse> GetStoreAsync(long dataStoreId) =>
            GetAsync(harness, Credentials.Projection, ProjectionPath(TenantA, RouteA, Query(dataStoreId)));

        // The handler's record of each request the read answered as unavailable, by stage.
        static int AcquireFailures(EducationOrganizationProjectionContext context) =>
            context.Logs.Events.Count(logEvent =>
                logEvent.Category == typeof(EducationOrganizationProjectionHandler).FullName
                && logEvent.Properties.GetValueOrDefault("Stage") == "Acquire"
            );
    }

    /// <summary>
    /// A mapping set that cannot be produced answers the fixed <c>projection-unsupported</c> body; the
    /// message and diagnostics it carries reach neither the response nor a log.
    /// </summary>
    public static async Task It_answers_an_unavailable_mapping_without_its_diagnostics(
        EducationOrganizationProjectionContext context
    )
    {
        ApiIntegrationHarness harness = Prepare(context);
        Seeder seeder = await Seeder.CreateAsync(harness, TenantA, RouteA);
        await seeder.StateAgencyAsync(1, "Projection State");
        await WalkAsync(harness, Credentials.Projection, TenantA, RouteA, PrimaryStoreId, limit: null);

        context.Mapping.Arm();
        try
        {
            context.Logs.Clear();
            ProjectionResponse unavailable = await GetAsync(
                harness,
                Credentials.Projection,
                ProjectionPath(TenantA, RouteA, Query(PrimaryStoreId))
            );
            ShouldBeProjectionProblem(unavailable, Problem.ProjectionUnsupported);
            ShouldNotDisclose(
                context,
                unavailable,
                "mapping-hostile-message",
                "mapping-secret",
                "mapping-hostile-diagnostic",
                "diag-secret"
            );
        }
        finally
        {
            context.Mapping.Disarm();
        }

        await WalkAsync(harness, Credentials.Projection, TenantA, RouteA, PrimaryStoreId, limit: null);
    }

    /// <summary>
    /// Duplicates and contradictions refuse the whole set wherever they sit, including outside the
    /// requested page; and a set that changed into an invalid one is refused as invalid, because
    /// validation runs before the digest is compared.
    /// </summary>
    public static async Task It_fails_closed_on_contradictory_relationships(
        EducationOrganizationProjectionContext context
    )
    {
        ApiIntegrationHarness harness = Prepare(context);
        Seeder seeder = await Seeder.CreateAsync(harness, TenantA, RouteA);
        await seeder.StateAgencyAsync(1, "Projection State");
        await seeder.LocalAgencyAsync(100, "District 100", stateAgency: 1);
        await seeder.LocalAgencyAsync(101, "District 101", parent: 100, stateAgency: 1);
        await seeder.LocalAgencyAsync(102, "District 102", stateAgency: 1);
        await seeder.SchoolAsync(100001, "School 100001", localAgency: 100);

        ProjectionResponse firstPage = await GetPageAsync(cursor: null);
        ShouldBeSuccess(firstPage, PrimaryStoreId);
        ItemsOf(firstPage).Select(item => item.Id).Should().Equal(1);
        string cursor = NextCursorOf(firstPage)!;

        // A two-agency cycle, far from the one-item first page.
        await seeder.LocalAgencyAsync(100, "District 100", parent: 101, stateAgency: 1, update: true);
        ShouldBeProjectionProblem(await GetPageAsync(cursor), Problem.ProjectionDataInvalid);
        ShouldBeProjectionProblem(await GetPageAsync(cursor: null), Problem.ProjectionDataInvalid);

        await seeder.LocalAgencyAsync(100, "District 100", stateAgency: 1, update: true);
        (await WalkAsync(harness, Credentials.Projection, TenantA, RouteA, PrimaryStoreId, limit: 1))
            .Items.Should()
            .HaveCount(5);

        // An agency that is its own parent.
        await seeder.LocalAgencyAsync(102, "District 102", parent: 102, stateAgency: 1, update: true);
        ShouldBeProjectionProblem(await GetPageAsync(cursor: null), Problem.ProjectionDataInvalid);

        await seeder.LocalAgencyAsync(102, "District 102", stateAgency: 1, update: true);
        (await WalkAsync(harness, Credentials.Projection, TenantA, RouteA, PrimaryStoreId, limit: 1))
            .Items.Should()
            .HaveCount(5);

        Task<ProjectionResponse> GetPageAsync(string? cursor) =>
            GetAsync(
                harness,
                Credentials.Projection,
                ProjectionPath(TenantA, RouteA, Query(PrimaryStoreId, 1, cursor))
            );
    }

    /// <summary>
    /// SQL Server can store a lone surrogate, which no client can decode: the set is refused as invalid
    /// data, never answered with a replacement character or an unexpected error.
    /// </summary>
    public static async Task It_refuses_a_name_that_is_not_well_formed_utf16(
        EducationOrganizationProjectionContext context
    )
    {
        ApiIntegrationHarness harness = Prepare(context);
        EducationOrganizationProjectionEngine engine = context.Engine;
        Seeder seeder = await Seeder.CreateAsync(harness, TenantA, RouteA);
        await seeder.StateAgencyAsync(1, "Projection State");
        await seeder.SchoolAsync(100001, "School 100001");

        await engine.ExecuteAsync(
            context.PrimaryConnectionString,
            $"UPDATE {engine.Table("edfi", "School")} SET {engine.Q("NameOfInstitution")} = N'Lone ' + NCHAR(55296);"
        );

        ProjectionResponse refused = await GetAsync(
            harness,
            Credentials.Projection,
            ProjectionPath(TenantA, RouteA, Query(PrimaryStoreId, 1))
        );
        ShouldBeProjectionProblem(refused, Problem.ProjectionDataInvalid);
        refused.Body.Should().NotContain("�");
    }

    /// <summary>
    /// The parameter, cursor and contract-version refusals, each with its exact type and errors.
    /// </summary>
    public static async Task It_refuses_invalid_parameters_cursors_and_versions(
        EducationOrganizationProjectionContext context
    )
    {
        ApiIntegrationHarness harness = Prepare(context);
        Seeder seeder = await Seeder.CreateAsync(harness, TenantA, RouteA);
        await seeder.StateAgencyAsync(1, "Projection State");
        await seeder.ServiceCenterAsync(10, "Region Ten", stateAgency: 1);

        const string DataStoreIdError =
            "DataStoreId must be set to a numeric value between 1 and 2147483647.";
        const string LimitError = "Limit must be omitted or set to a numeric value between 1 and 2000.";

        (string Query, string[] Errors)[] invalid =
        [
            ("", [DataStoreIdError]),
            ("dataStoreId=0", [DataStoreIdError]),
            ("dataStoreId=-1", [DataStoreIdError]),
            ("dataStoreId=abc", [DataStoreIdError]),
            ("dataStoreId=2147483648", [DataStoreIdError]),
            ("dataStoreId=1&limit=0", [LimitError]),
            ("dataStoreId=1&limit=2001", [LimitError]),
            ("dataStoreId=abc&limit=abc", [DataStoreIdError, LimitError]),
            ("dataStoreId=1&DataStoreId=1", ["DataStoreId must not be supplied more than once."]),
            ("dataStoreId=1&limit=1&LIMIT=1", ["Limit must not be supplied more than once."]),
            ("dataStoreId=1&cursor=a&Cursor=b", ["Cursor must not be supplied more than once."]),
            (
                "dataStoreId=1&contractVersion=educationOrganizationProjection.v1&ContractVersion=educationOrganizationProjection.v1",
                ["ContractVersion must not be supplied more than once."]
            ),
        ];

        foreach ((string query, string[] errors) in invalid)
        {
            ProjectionResponse response = await GetAsync(
                harness,
                Credentials.Projection,
                ProjectionPath(TenantA, RouteA, query)
            );
            ShouldBeProblem(response, 400, ParameterValidationType, query);
            response.Json["errors"]!
                .AsArray()
                .Select(error => error!.GetValue<string>())
                .Should()
                .Equal(errors, query);
        }

        string cursor = NextCursorOf(
            await GetAsync(
                harness,
                Credentials.Projection,
                ProjectionPath(TenantA, RouteA, Query(PrimaryStoreId, 1))
            )
        )!;
        foreach (
            string refused in new[]
            {
                Query(PrimaryStoreId, 1, "not-a-cursor"),
                Query(PrimaryStoreId, 1, cursor + "="),
                Query(MissingProviderStoreId, 1, cursor),
            }
        )
        {
            ShouldBeProjectionProblem(
                await GetAsync(harness, Credentials.Projection, ProjectionPath(TenantA, RouteA, refused)),
                Problem.InvalidCursor
            );
        }

        ShouldBeProjectionProblem(
            await GetAsync(
                harness,
                Credentials.Projection,
                ProjectionPath(
                    TenantA,
                    RouteA,
                    $"{Query(PrimaryStoreId)}&contractVersion=educationOrganizationProjection.v2"
                )
            ),
            Problem.UnsupportedContractVersion
        );

        ProjectionResponse explicitVersion = await GetAsync(
            harness,
            Credentials.Projection,
            ProjectionPath(
                TenantA,
                RouteA,
                $"{Query(PrimaryStoreId)}&contractVersion={ProjectionContractVersions.V1}"
            )
        );
        ShouldBeSuccess(explicitVersion, PrimaryStoreId);
        ItemsOf(explicitVersion).Select(item => item.Id).Should().Equal(1, 10);
    }

    /// <summary>
    /// A client that disconnects while the read waits on a lock ends the read: the database session
    /// stops waiting, its transaction is gone, and the store serves the next request.
    /// </summary>
    public static async Task It_abandons_the_read_when_the_client_disconnects(
        EducationOrganizationProjectionContext context
    )
    {
        ApiIntegrationHarness harness = Prepare(context);
        Seeder seeder = await Seeder.CreateAsync(harness, TenantA, RouteA);
        await seeder.StateAgencyAsync(1, "Projection State");
        await WalkAsync(harness, Credentials.Projection, TenantA, RouteA, PrimaryStoreId, limit: null);

        await using (
            IExclusiveSchoolLock schoolLock = await context.Engine.LockSchoolsAsync(
                context.PrimaryConnectionString
            )
        )
        {
            using CancellationTokenSource disconnect = new();
            Task<ProjectionResponse> request = GetAsync(
                harness,
                Credentials.Projection,
                ProjectionPath(TenantA, RouteA, Query(PrimaryStoreId)),
                cancellationToken: disconnect.Token
            );

            await WaitUntilAsync(
                async () => await schoolLock.BlockedReadsAsync() == 1 || request.IsCompleted,
                "the projection read to wait on the lock"
            );
            request
                .IsCompleted.Should()
                .BeFalse("the read must still be waiting when the client disconnects");

            await disconnect.CancelAsync();
            Func<Task> awaitRequest = () => request;
            await awaitRequest.Should().ThrowAsync<OperationCanceledException>();

            await WaitUntilAsync(
                async () => await schoolLock.BlockedReadsAsync() == 0,
                "the abandoned read to stop waiting while the lock is still held"
            );
            await WaitUntilAsync(
                async () => await schoolLock.OpenTransactionsAsync() == 0,
                "the abandoned read's transaction to end"
            );

            // Ended by the disconnect, not by waiting out its lock timeout: a timed-out read is
            // recorded with the stage it failed in. (A client call against the in-process server
            // returns only once the server side finishes, so its completion alone proves nothing.)
            context
                .Logs.Events.Where(logEvent =>
                    logEvent.Category.StartsWith("EdFi.", StringComparison.Ordinal)
                )
                .Should()
                .NotContain(
                    logEvent => logEvent.Properties.ContainsKey("Stage"),
                    "the read must end because the client disconnected, not by timing out"
                );
        }

        Walk after = await WalkAsync(
            harness,
            Credentials.Projection,
            TenantA,
            RouteA,
            PrimaryStoreId,
            limit: null
        );
        after.Items.Select(item => item.Id).Should().Equal(1);
    }

    /// <summary>
    /// A client over the request rate limit is answered by the host's rate limiter before the
    /// endpoint runs: the inherited 429 <c>too-many-requests</c> problem, still marked
    /// <c>no-store</c>.
    /// </summary>
    public static async Task It_answers_the_rate_limit_with_the_inherited_problem(
        EducationOrganizationProjectionContext context,
        int permits
    )
    {
        ApiIntegrationHarness harness = Prepare(context);
        string path = ProjectionPath(TenantA, RouteA, Query(PrimaryStoreId));

        for (int request = 0; request < permits; request++)
        {
            ShouldBeSuccess(await GetAsync(harness, Credentials.Projection, path), PrimaryStoreId);
        }

        ShouldBeProblem(
            await GetAsync(harness, Credentials.Projection, path),
            429,
            TooManyRequestsType,
            "the request after the last permit"
        );
    }

    /// <summary>A read that cannot take its locks in time is a transient outage of the target.</summary>
    public static async Task It_answers_a_lock_timeout_as_target_unavailable(
        EducationOrganizationProjectionContext context
    )
    {
        ApiIntegrationHarness harness = Prepare(context);
        Seeder seeder = await Seeder.CreateAsync(harness, TenantA, RouteA);
        await seeder.StateAgencyAsync(1, "Projection State");
        await WalkAsync(harness, Credentials.Projection, TenantA, RouteA, PrimaryStoreId, limit: null);

        await using (await context.Engine.LockSchoolsAsync(context.PrimaryConnectionString))
        {
            context.Logs.Clear();
            ProjectionResponse timedOut = await GetAsync(
                harness,
                Credentials.Projection,
                ProjectionPath(TenantA, RouteA, Query(PrimaryStoreId))
            );
            ShouldBeProjectionProblem(timedOut, Problem.TargetUnavailable);
            context
                .Logs.Events.Should()
                .Contain(logEvent => logEvent.Properties.GetValueOrDefault("Stage") == "Execute");
        }

        await WalkAsync(harness, Credentials.Projection, TenantA, RouteA, PrimaryStoreId, limit: null);
    }

    /// <summary>
    /// A set at the configured row cap is served; one more row refuses the whole set as too large.
    /// </summary>
    public static async Task It_refuses_a_set_larger_than_the_row_cap(
        EducationOrganizationProjectionContext context,
        int maxProjectionRows
    )
    {
        ApiIntegrationHarness harness = Prepare(context);
        Seeder seeder = await Seeder.CreateAsync(harness, TenantA, RouteA);
        await seeder.StateAgencyAsync(1, "Projection State");
        await seeder.SchoolsAsync(
            Enumerable.Range(1, maxProjectionRows - 1).Select(index => 100_000L + index)
        );

        Walk atCap = await WalkAsync(
            harness,
            Credentials.Projection,
            TenantA,
            RouteA,
            PrimaryStoreId,
            limit: null
        );
        atCap.Items.Should().HaveCount(maxProjectionRows);

        await seeder.SchoolAsync(100_000L + maxProjectionRows, "One Too Many");
        ShouldBeProjectionProblem(
            await GetAsync(
                harness,
                Credentials.Projection,
                ProjectionPath(TenantA, RouteA, Query(PrimaryStoreId))
            ),
            Problem.ProjectionTooLarge
        );
    }

    /// <summary>
    /// The projection reads the store's primary even when the store publishes a snapshot and a read
    /// replica and the request asks for the snapshot: both derivatives are unreachable, and the
    /// fingerprint is never read from either.
    /// </summary>
    public static async Task It_reads_the_primary_when_the_store_publishes_derivatives(
        EducationOrganizationProjectionContext context,
        string snapshotConnectionString,
        string replicaConnectionString
    )
    {
        ApiIntegrationHarness harness = Prepare(context);
        Seeder seeder = await Seeder.CreateAsync(harness, TenantA, RouteA);
        await seeder.StateAgencyAsync(1, "Projection State");
        await seeder.LocalAgencyAsync(100, "District 100", stateAgency: 1);

        await context.Reachability.MakeUnreachableAsync(snapshotConnectionString);
        await context.Reachability.MakeUnreachableAsync(replicaConnectionString);
        try
        {
            context.Fingerprints.ResetReads();
            Walk walk = await WalkAsync(
                harness,
                Credentials.Projection,
                TenantA,
                RouteA,
                PrimaryStoreId,
                limit: 1,
                new Dictionary<string, string> { [UseSnapshotHeader] = "true" }
            );
            walk.Items.Select(item => item.Id).Should().Equal(1, 100);
            context.Fingerprints.ReadConnectionStrings.Should().NotContain(snapshotConnectionString);
            context.Fingerprints.ReadConnectionStrings.Should().NotContain(replicaConnectionString);
        }
        finally
        {
            await context.Reachability.MakeReachableAsync(snapshotConnectionString);
            await context.Reachability.MakeReachableAsync(replicaConnectionString);
        }
    }

    /// <summary>
    /// Items whose identifiers and parents are 20-character negative int64 values, the longest
    /// discriminator and names of the engine's longest serialized form, served through the frontend
    /// serializer: each item within the 2,048-byte allowance, the envelope within 512 bytes, the page
    /// within its bound, and each name within 6 bytes per UTF-16 unit and decoded unchanged.
    /// </summary>
    public static async Task It_bounds_the_response_body(
        EducationOrganizationProjectionContext context,
        string fixtureName,
        string name
    )
    {
        ApiIntegrationHarness harness = Prepare(context);
        EducationOrganizationProjectionEngine engine = context.Engine;
        Seeder seeder = await Seeder.CreateAsync(harness, TenantA, RouteA);
        const long StateAgencyId = long.MinValue + 3;
        const long ServiceCenterId = long.MinValue + 2;
        const long LocalAgencyId = long.MinValue + 1;
        await seeder.StateAgencyAsync(StateAgencyId, "Bound State");
        await seeder.ServiceCenterAsync(ServiceCenterId, "Bound Region", stateAgency: StateAgencyId);
        await seeder.LocalAgencyAsync(LocalAgencyId, "Bound District", serviceCenter: ServiceCenterId);
        await seeder.SchoolAsync(long.MinValue, "Bound School", localAgency: LocalAgencyId);

        foreach (
            string table in new[]
            {
                "StateEducationAgency",
                "EducationServiceCenter",
                "LocalEducationAgency",
                "School",
            }
        )
        {
            await engine.ExecuteAsync(
                context.PrimaryConnectionString,
                $"UPDATE {engine.Table("edfi", table)} SET {engine.Q("NameOfInstitution")} = @name, {engine.Q("ShortNameOfInstitution")} = @name;",
                ("name", name)
            );
        }

        const int Limit = 3;
        ProjectionResponse page = await GetAsync(
            harness,
            Credentials.Projection,
            ProjectionPath(TenantA, RouteA, Query(PrimaryStoreId, Limit))
        );
        ShouldBeSuccess(page, PrimaryStoreId);
        NextCursorOf(page).Should().NotBeNull();

        byte[] body = Encoding.UTF8.GetBytes(page.Body);
        BodyMeasure measure = Measure(body);
        measure.Items.Should().HaveCount(Limit);
        int envelope = body.Length - measure.Items.Sum(item => item.Bytes) - (measure.Items.Count - 1);

        await TestContext.Out.WriteLineAsync(
            $"MEASURE projection body engine={engine.Name} fixture={fixtureName} items={string.Join(",", measure.Items.Select(item => item.Bytes))} "
                + $"envelope={envelope} page={body.Length} name_bytes={string.Join(",", measure.Names.Select(served => served.Bytes).Distinct())}"
        );

        measure.Items.Should().OnlyContain(item => item.Bytes <= 2048);
        envelope.Should().BeLessThanOrEqualTo(512);
        body.Length.Should().BeLessThanOrEqualTo(Limit * 2048 + 512);
        measure.Names.Should().HaveCount(Limit * 2);
        foreach ((string decoded, int bytes) in measure.Names)
        {
            decoded.Should().Be(name);
            bytes.Should().BeLessThanOrEqualTo(6 * name.Length + 2);
        }

        ItemsOf(page)
            .Select(item => (item.Id, item.ParentId))
            .Should()
            .Equal(
                (long.MinValue, LocalAgencyId),
                (LocalAgencyId, ServiceCenterId),
                (ServiceCenterId, StateAgencyId)
            );
    }

    private static ApiIntegrationHarness Prepare(EducationOrganizationProjectionContext context)
    {
        // Every request names its own credential; a missing header must stay missing.
        context.Harness.HttpClient.DefaultRequestHeaders.Authorization = null;
        context.Logs.Clear();
        return context.Harness;
    }

    private static string ProjectionPath(string tenant, ProjectionRoute route, string query) =>
        $"/{tenant}/{route.DistrictId}/{route.SchoolYear}/management/education-organizations?{query}";

    private static string DataPath(string tenant, ProjectionRoute route, string resource) =>
        $"/{tenant}/{route.DistrictId}/{route.SchoolYear}/data/ed-fi/{resource}";

    private static string Query(long dataStoreId, int? limit = null, string? cursor = null) =>
        $"dataStoreId={dataStoreId}"
        + (limit is null ? "" : $"&limit={limit}")
        + (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}");

    private sealed record ProjectionResponse(
        HttpStatusCode Status,
        string? ContentType,
        string? CacheControl,
        string Body
    )
    {
        public JsonNode Json => JsonNode.Parse(Body)!;
    }

    private sealed record Item(long Id, string Name, string? ShortName, string Discriminator, long? ParentId);

    private sealed record Walk(IReadOnlyList<ProjectionResponse> Pages, IReadOnlyList<Item> Items);

    private static async Task<ProjectionResponse> GetAsync(
        ApiIntegrationHarness harness,
        ProjectionCredential? credential,
        string path,
        string? rawAuthorization = null,
        IReadOnlyDictionary<string, string>? headers = null,
        CancellationToken cancellationToken = default
    )
    {
        using HttpRequestMessage request = new(HttpMethod.Get, path);
        if (rawAuthorization is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", rawAuthorization);
        }
        else if (credential is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.Token);
        }

        foreach ((string name, string value) in headers ?? new Dictionary<string, string>())
        {
            request.Headers.Add(name, value);
        }

        using HttpResponseMessage response = await harness.HttpClient.SendAsync(request, cancellationToken);
        return new ProjectionResponse(
            response.StatusCode,
            response.Content.Headers.ContentType?.MediaType,
            response.Headers.CacheControl?.ToString(),
            await response.Content.ReadAsStringAsync(cancellationToken)
        );
    }

    private static async Task<(HttpStatusCode Status, string Body, string? Location)> SendAsync(
        ApiIntegrationHarness harness,
        ProjectionCredential credential,
        HttpMethod method,
        string path,
        JsonObject payload
    )
    {
        using HttpRequestMessage request = new(method, path)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, JsonMediaType),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.Token);
        using HttpResponseMessage response = await harness.HttpClient.SendAsync(request);
        string? location = response.Headers.Location switch
        {
            null => null,
            { IsAbsoluteUri: true } absolute => absolute.AbsolutePath,
            Uri relative => relative.OriginalString,
        };
        return (response.StatusCode, await response.Content.ReadAsStringAsync(), location);
    }

    private static async Task<Walk> WalkAsync(
        ApiIntegrationHarness harness,
        ProjectionCredential credential,
        string tenant,
        ProjectionRoute route,
        long dataStoreId,
        int? limit,
        IReadOnlyDictionary<string, string>? headers = null
    )
    {
        List<ProjectionResponse> pages = [];
        List<Item> items = [];
        string? cursor = null;

        do
        {
            ProjectionResponse page = await GetAsync(
                harness,
                credential,
                ProjectionPath(tenant, route, Query(dataStoreId, limit, cursor)),
                headers: headers
            );
            ShouldBeSuccess(page, dataStoreId);
            pages.Add(page);

            IReadOnlyList<Item> pageItems = ItemsOf(page);
            cursor = NextCursorOf(page);
            if (cursor is not null && limit is { } pageSize)
            {
                pageItems.Should().HaveCount(pageSize, "a page with a next cursor is full");
            }

            if (pages.Count > 1)
            {
                pageItems.Should().NotBeEmpty("a continuation page is never empty");
            }

            items.AddRange(pageItems);
            pages.Should().HaveCountLessThan(100, "the walk must end");
        } while (cursor is not null);

        items.Select(item => item.Id).Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
        return new Walk(pages, items);
    }

    private static IReadOnlyList<Item> ItemsOf(ProjectionResponse page) =>
        [
            .. page.Json["items"]!
                .AsArray()
                .Select(node =>
                {
                    JsonObject item = node!.AsObject();
                    item.Select(member => member.Key).Should().BeEquivalentTo(_itemMembers);
                    return new Item(
                        item["educationOrganizationId"]!.GetValue<long>(),
                        item["nameOfInstitution"]!.GetValue<string>(),
                        item["shortNameOfInstitution"]?.GetValue<string>(),
                        item["discriminator"]!.GetValue<string>(),
                        item["parentId"]?.GetValue<long>()
                    );
                }),
        ];

    private static string? NextCursorOf(ProjectionResponse page) =>
        page.Json["nextCursor"]?.GetValue<string>();

    private static string? TypeOf(ProjectionResponse response) =>
        JsonNode.Parse(response.Body)?["type"]?.GetValue<string>();

    private static void ShouldBeSuccess(ProjectionResponse response, long dataStoreId)
    {
        response.Status.Should().Be(HttpStatusCode.OK, response.Body);
        response.ContentType.Should().Be(JsonMediaType);
        response.CacheControl.Should().Be("no-store");
        response.Body.Should().NotContain("Ed-Fi:");

        JsonObject envelope = response.Json.AsObject();
        envelope.Select(member => member.Key).Should().BeEquivalentTo(_envelopeMembers);
        envelope["contractVersion"]!.GetValue<string>().Should().Be(ProjectionContractVersions.V1);
        envelope["dataStoreId"]!.GetValue<long>().Should().Be(dataStoreId);
    }

    private static void ShouldBeProblem(ProjectionResponse response, int status, string type, string because)
    {
        ((int)response.Status).Should().Be(status, $"{because}: {response.Body}");
        response.ContentType.Should().Be(ProblemMediaType, because);
        response.CacheControl.Should().Be("no-store", because);
        JsonNode problem = response.Json;
        problem["type"]!.GetValue<string>().Should().Be(type, because);
        problem["status"]!.GetValue<int>().Should().Be(status, because);
    }

    private static void ShouldBeProjectionProblem(ProjectionResponse response, Problem expected)
    {
        ShouldBeProblem(response, expected.Status, expected.Type, expected.Type);
        JsonNode problem = response.Json;
        problem["title"]!.GetValue<string>().Should().Be(expected.Title);
        problem["detail"]!.GetValue<string>().Should().Be(expected.Detail);
        problem["errors"]!.AsArray().Should().BeEmpty();
    }

    private static string WithoutCorrelation(ProjectionResponse response)
    {
        JsonObject problem = response.Json.AsObject();
        problem.Remove("correlationId");
        return problem.ToJsonString();
    }

    /// <summary>
    /// Neither the response nor any log the deployment would ship carries the value: everything the
    /// DMS categories wrote at any level, and everything at <c>Information</c> and above.
    /// </summary>
    private static void ShouldNotDisclose(
        EducationOrganizationProjectionContext context,
        ProjectionResponse response,
        params string[] values
    )
    {
        string[] logged =
        [
            .. context
                .Logs.Events.Where(logEvent =>
                    logEvent.Category.StartsWith("EdFi.", StringComparison.Ordinal)
                    || logEvent.Level >= LogLevel.Information
                )
                .Select(logEvent => logEvent.Text),
        ];
        logged.Should().NotBeEmpty("the failing request was logged");

        foreach (string value in values)
        {
            response.Body.Should().NotContain(value);
            logged.Should().NotContain(text => text.Contains(value, StringComparison.Ordinal), value);
        }
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string what)
    {
        using CancellationTokenSource bound = new(_sessionStateBound);
        while (!await condition())
        {
            if (bound.IsCancellationRequested)
            {
                Assert.Fail($"Timed out after {_sessionStateBound.TotalSeconds:F0} s waiting for {what}.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), CancellationToken.None);
        }
    }

    private sealed record BodyMeasure(
        IReadOnlyList<(int Start, int Bytes)> Items,
        IReadOnlyList<(string Decoded, int Bytes)> Names
    );

    /// <summary>
    /// The byte span of each item as served, and of each name token including its quotes, read from the
    /// raw body so the measurement is the frontend serializer's output, not a re-serialization.
    /// </summary>
    private static BodyMeasure Measure(byte[] body)
    {
        List<(int Start, int Bytes)> items = [];
        List<(string Decoded, int Bytes)> names = [];
        Utf8JsonReader reader = new(body);
        int itemStart = -1;
        bool inItems = false;

        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.PropertyName when reader.CurrentDepth == 1:
                    inItems = reader.ValueTextEquals("items");
                    break;
                case JsonTokenType.StartObject when inItems && reader.CurrentDepth == 2:
                    itemStart = (int)reader.TokenStartIndex;
                    break;
                case JsonTokenType.EndObject when inItems && reader.CurrentDepth == 2:
                    items.Add((itemStart, (int)reader.BytesConsumed - itemStart));
                    break;
                case JsonTokenType.PropertyName
                    when inItems
                        && reader.CurrentDepth == 3
                        && (
                            reader.ValueTextEquals("nameOfInstitution")
                            || reader.ValueTextEquals("shortNameOfInstitution")
                        ):
                    reader.Read();
                    names.Add((reader.GetString()!, reader.ValueSpan.Length + 2));
                    break;
            }
        }

        return new BodyMeasure(items, names);
    }

    /// <summary>
    /// Creates education organizations through the resource API with the smoke credential, which
    /// has every fixture resource and no service claim.
    /// </summary>
    private sealed class Seeder
    {
        private const string Namespace = "uri://ed-fi.org";
        private const int Workers = 8;

        private readonly ApiIntegrationHarness _harness;
        private readonly string _tenant;
        private readonly ProjectionRoute _route;
        private readonly Dictionary<long, string> _locations = [];

        private Seeder(ApiIntegrationHarness harness, string tenant, ProjectionRoute route)
        {
            _harness = harness;
            _tenant = tenant;
            _route = route;
        }

        public static async Task<Seeder> CreateAsync(
            ApiIntegrationHarness harness,
            string tenant,
            ProjectionRoute route
        )
        {
            Seeder seeder = new(harness, tenant, route);
            foreach (
                (string resource, string name, string code) in new[]
                {
                    (
                        "educationOrganizationCategoryDescriptors",
                        "EducationOrganizationCategoryDescriptor",
                        "Other"
                    ),
                    ("gradeLevelDescriptors", "GradeLevelDescriptor", "Tenth grade"),
                    (
                        "localEducationAgencyCategoryDescriptors",
                        "LocalEducationAgencyCategoryDescriptor",
                        "Independent"
                    ),
                }
            )
            {
                await seeder.PostAsync(
                    resource,
                    id: null,
                    new JsonObject
                    {
                        ["namespace"] = $"{Namespace}/{name}",
                        ["codeValue"] = code,
                        ["shortDescription"] = code,
                    }
                );
            }

            return seeder;
        }

        public Task StateAgencyAsync(long id, string name, string? shortName = null) =>
            PostAsync(
                "stateEducationAgencies",
                id,
                Named(
                    new JsonObject { ["stateEducationAgencyId"] = id, ["categories"] = Categories() },
                    name,
                    shortName
                )
            );

        public Task ServiceCenterAsync(
            long id,
            string name,
            string? shortName = null,
            long? stateAgency = null
        )
        {
            JsonObject body = Named(
                new JsonObject { ["educationServiceCenterId"] = id, ["categories"] = Categories() },
                name,
                shortName
            );
            Reference(body, "stateEducationAgencyReference", "stateEducationAgencyId", stateAgency);
            return PostAsync("educationServiceCenters", id, body);
        }

        public Task LocalAgencyAsync(
            long id,
            string name,
            string? shortName = null,
            long? parent = null,
            long? serviceCenter = null,
            long? stateAgency = null,
            bool update = false
        )
        {
            JsonObject body = Named(
                new JsonObject
                {
                    ["localEducationAgencyId"] = id,
                    ["localEducationAgencyCategoryDescriptor"] =
                        $"{Namespace}/LocalEducationAgencyCategoryDescriptor#Independent",
                    ["categories"] = Categories(),
                },
                name,
                shortName
            );
            Reference(body, "parentLocalEducationAgencyReference", "localEducationAgencyId", parent);
            Reference(body, "educationServiceCenterReference", "educationServiceCenterId", serviceCenter);
            Reference(body, "stateEducationAgencyReference", "stateEducationAgencyId", stateAgency);
            return update ? PutAsync(id, body) : PostAsync("localEducationAgencies", id, body);
        }

        public Task SchoolAsync(
            long id,
            string name,
            string? shortName = null,
            long? localAgency = null,
            bool update = false
        )
        {
            JsonObject body = SchoolBody(id, name, shortName, localAgency);
            return update ? PutAsync(id, body) : PostAsync("schools", id, body);
        }

        /// <summary>Creates many schools without a district, concurrently.</summary>
        public Task SchoolsAsync(IEnumerable<long> ids)
        {
            long[] pending = [.. ids];
            int next = -1;
            return Task.WhenAll(
                Enumerable
                    .Range(0, Workers)
                    .Select(_ =>
                        Task.Run(async () =>
                        {
                            for (
                                int index = Interlocked.Increment(ref next);
                                index < pending.Length;
                                index = Interlocked.Increment(ref next)
                            )
                            {
                                long id = pending[index];
                                await PostAsync(
                                    "schools",
                                    id: null,
                                    SchoolBody(id, $"School {id}", null, null)
                                );
                            }
                        })
                    )
            );
        }

        public Task PostSecondaryInstitutionAsync(long id, string name) =>
            PostAsync(
                "postSecondaryInstitutions",
                id,
                Named(
                    new JsonObject { ["postSecondaryInstitutionId"] = id, ["categories"] = Categories() },
                    name,
                    null
                )
            );

        public static JsonObject SchoolBody(long id, string name, string? shortName, long? localAgency)
        {
            JsonObject body = Named(
                new JsonObject
                {
                    ["schoolId"] = id,
                    ["educationOrganizationCategories"] = Categories(),
                    ["gradeLevels"] = new JsonArray(
                        new JsonObject
                        {
                            ["gradeLevelDescriptor"] = $"{Namespace}/GradeLevelDescriptor#Tenth grade",
                        }
                    ),
                },
                name,
                shortName
            );
            Reference(body, "localEducationAgencyReference", "localEducationAgencyId", localAgency);
            return body;
        }

        private static JsonObject Named(JsonObject body, string name, string? shortName)
        {
            body["nameOfInstitution"] = name;
            if (shortName is not null)
            {
                body["shortNameOfInstitution"] = shortName;
            }

            return body;
        }

        private static void Reference(JsonObject body, string member, string key, long? id)
        {
            if (id is { } value)
            {
                body[member] = new JsonObject { [key] = value };
            }
        }

        private static JsonArray Categories() =>
            new(
                new JsonObject
                {
                    ["educationOrganizationCategoryDescriptor"] =
                        $"{Namespace}/EducationOrganizationCategoryDescriptor#Other",
                }
            );

        private async Task PostAsync(string resource, long? id, JsonObject body)
        {
            (HttpStatusCode status, string responseBody, string? location) = await SendAsync(
                _harness,
                Credentials.Resource,
                HttpMethod.Post,
                DataPath(_tenant, _route, resource),
                body
            );
            status.Should().Be(HttpStatusCode.Created, $"seeding {resource} {id} returned {responseBody}");
            if (id is { } key)
            {
                lock (_locations)
                {
                    _locations[key] =
                        location
                        ?? throw new InvalidOperationException($"Seeding {resource} returned no Location.");
                }
            }
        }

        private async Task PutAsync(long id, JsonObject body)
        {
            string location = _locations[id];
            body["id"] = location[(location.LastIndexOf('/') + 1)..];
            (HttpStatusCode status, string responseBody, _) = await SendAsync(
                _harness,
                Credentials.Resource,
                HttpMethod.Put,
                location,
                body
            );
            status.Should().Be(HttpStatusCode.NoContent, $"updating {id} returned {responseBody}");
        }
    }
}
