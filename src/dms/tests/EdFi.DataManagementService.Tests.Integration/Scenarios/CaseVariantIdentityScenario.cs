// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Security;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Tests.Integration.Doubles;
using EdFi.DataManagementService.Tests.Integration.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace EdFi.DataManagementService.Tests.Integration.Scenarios;

/// <summary>
/// Root identity values that differ only in case, through the public HTTP boundary on DS 5.2.
/// </summary>
/// <remarks>
/// <para>
/// On SQL Server identity text columns carry <c>SQL_Latin1_General_CP1_CI_AS</c>, so a POST whose identity
/// differs from a stored one only in case captures the stored document, and the identity-stability guard
/// rebinds the CI-equal values to the stored ones before proposed authorization, guarded no-op detection and
/// DML. The write is then authorized as an Update, is a no-op when nothing else changed, never rewrites the
/// stored casing, never bumps <c>ContentVersion</c> and never records a key change. A casing-only PUT is the
/// same no-op, and a mixed PUT changes only the genuinely different column.
/// </para>
/// <para>
/// PostgreSQL compares identities ordinally, so every case variant is a different identity: a POST creates a
/// second document, a recased reference does not resolve, and a recased PUT is a key change. Each test asserts
/// the engine's own expected verdict, so the pair documents the dialect difference.
/// </para>
/// </remarks>
internal static class CaseVariantIdentityScenario
{
    public const string OwnerTenant = "case-variant-owner-tenant";
    public const string ForeignTenant = "case-variant-foreign-tenant";

    private const short OwnerToken = 42;
    private const short ForeignToken = 7;
    private const int SchoolYear = 2024;
    private const string DataRoot = "/data";

    private const string KeyChangeNotSupportedType =
        "urn:ed-fi:api:bad-request:data-validation-failed:key-change-not-supported";
    private const string OwnershipMismatchType =
        "urn:ed-fi:api:security:authorization:ownership:access-denied:ownership-mismatch";
    private const string DataPolicyEnforcedType = "urn:ed-fi:api:data-policy-enforced";

    // Fixtures/Profiles/AuthoritativeDs52/studenteducationorganizationassociation-hiddenindicator.xml
    private const string HiddenIndicatorWritableContentType =
        "application/vnd.ed-fi.studenteducationorganizationassociation."
        + "studenteducationorganizationassociation-hiddenindicator.writable+json";

    private static readonly string[] _noFurther =
    [
        AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired,
    ];

    /// <summary>
    /// Create and Update on ClassPeriod switch per request; every other resource and action is granted.
    /// </summary>
    public static IClaimSetProvider CreateSwitchableClassPeriodGrants(FixtureContext fixture) =>
        new SwitchablePostActionGrants(
            fixture,
            static resource =>
                string.Equals(resource.ProjectName, "Ed-Fi", StringComparison.OrdinalIgnoreCase)
                && string.Equals(resource.ResourceName, "ClassPeriod", StringComparison.Ordinal)
        );

    /// <summary>
    /// <c>OwnershipBased</c> on every resource and action, so the owner seeds every row it later defends.
    /// </summary>
    public static IClaimSetProvider CreateOwnershipClaimSetProvider(FixtureContext fixture) =>
        new ConfigurableClaimSetProvider(
            fixture,
            static (_, _) => [AuthorizationStrategyNameConstants.OwnershipBased]
        );

    /// <summary>
    /// The owner stamps and holds <see cref="OwnerToken"/>; the foreign client stamps and holds only
    /// <see cref="ForeignToken"/>, so it can create its own rows but never reach the owner's.
    /// </summary>
    public static ApplicationContextResult ResolveOwnershipTenant(string clientId, string? tenant) =>
        tenant switch
        {
            ForeignTenant => OwnershipContext(applicationId: 402, ForeignToken),
            _ => OwnershipContext(applicationId: 401, OwnerToken),
        };

    /// <summary>
    /// SQL Server: a ClassPeriod POST with a recased <c>classPeriodName</c> updates the stored document (200, same
    /// Location) as a guarded no-op: stored casing is served, <c>ContentVersion</c> is unchanged, no key change
    /// is recorded and the BellSchedule referrer's copy keeps the stored casing. PostgreSQL: a second document.
    /// </summary>
    public static async Task It_resolves_a_case_variant_class_period_post_by_the_identity_collation(
        ApiIntegrationHarness harness
    )
    {
        var seed = await SeedClassPeriodAsync(harness, DataRoot);
        string bellScheduleLocation = await SeedBellScheduleAsync(harness, seed);
        long classPeriodDocumentId = await DocumentIdAsync(harness, seed.Location);
        long bellScheduleDocumentId = await DocumentIdAsync(harness, bellScheduleLocation);
        long originalContentVersion = await ContentVersionAsync(harness, classPeriodDocumentId);
        var (_, originalEtag) = await GetAsync(harness, seed.Location);

        using HttpResponseMessage response = await SendAsync(
            harness,
            HttpMethod.Post,
            $"{DataRoot}/ed-fi/classPeriods",
            ClassPeriodBody(seed.ClassPeriodName.ToUpperInvariant(), seed.SchoolId)
        );
        string responseBody = await response.Content.ReadAsStringAsync();

        if (IsPostgresql(harness))
        {
            response.StatusCode.Should().Be(HttpStatusCode.Created, responseBody);
            LocationPath(response).Should().NotBe(seed.Location);
        }
        else
        {
            response.StatusCode.Should().Be(HttpStatusCode.OK, responseBody);
            LocationPath(response).Should().Be(seed.Location);
            await AssertKeyChangeCountAsync(harness, "ClassPeriod", classPeriodDocumentId, 0);
        }

        var (body, etag) = await GetAsync(harness, seed.Location);
        body["classPeriodName"]!.GetValue<string>().Should().Be(seed.ClassPeriodName);
        etag.Should().Be(originalEtag);
        (await ContentVersionAsync(harness, classPeriodDocumentId)).Should().Be(originalContentVersion);
        (
            await ScalarAsync<string>(
                harness,
                $"SELECT {Q(harness, "ClassPeriod_ClassPeriodName")} FROM {Q(harness, "edfi")}.{Q(harness, "BellScheduleClassPeriod")} "
                    + $"WHERE {Q(harness, "BellSchedule_DocumentId")} = @documentId",
                ("documentId", bellScheduleDocumentId)
            )
        )
            .Should()
            .Be(seed.ClassPeriodName);
    }

    /// <summary>
    /// SQL Server: a Section POST whose <c>courseOfferingReference.sessionName</c> is recased resolves the
    /// reference and captures the stored Section; the propagated reference copy is rebound to the stored casing,
    /// so the POST is a guarded no-op (200, same Location, no key change, unchanged <c>ContentVersion</c>).
    /// PostgreSQL: the recased reference does not resolve (409).
    /// </summary>
    public static async Task It_resolves_a_case_variant_section_reference_post_by_the_identity_collation(
        ApiIntegrationHarness harness
    )
    {
        var seed = await SeedSectionAsync(harness);
        long sectionDocumentId = await DocumentIdAsync(harness, seed.SectionLocation);
        long originalContentVersion = await ContentVersionAsync(harness, sectionDocumentId);
        var (_, originalEtag) = await GetAsync(harness, seed.SectionLocation);

        using HttpResponseMessage response = await SendAsync(
            harness,
            HttpMethod.Post,
            $"{DataRoot}/ed-fi/sections",
            SectionBody(seed, seed.SectionIdentifier, seed.SessionName.ToUpperInvariant())
        );
        string responseBody = await response.Content.ReadAsStringAsync();

        if (IsPostgresql(harness))
        {
            response.StatusCode.Should().Be(HttpStatusCode.Conflict, responseBody);
        }
        else
        {
            response.StatusCode.Should().Be(HttpStatusCode.OK, responseBody);
            LocationPath(response).Should().Be(seed.SectionLocation);
            await AssertKeyChangeCountAsync(harness, "Section", sectionDocumentId, 0);
        }

        var (body, etag) = await GetAsync(harness, seed.SectionLocation);
        body["courseOfferingReference"]!["sessionName"]!.GetValue<string>().Should().Be(seed.SessionName);
        etag.Should().Be(originalEtag);
        (await ContentVersionAsync(harness, sectionDocumentId)).Should().Be(originalContentVersion);
    }

    /// <summary>
    /// The comparer residue (Resolved Decision R2). SQL Server: the collation equates <c>Strasse</c> and
    /// <c>Straße</c>, so the capture finds the stored Program, but <c>OrdinalIgnoreCase</c> calls the pair a
    /// genuine change and Program identities are immutable, so the POST fails closed with 400 and the stored
    /// record is unchanged. PostgreSQL: a second document.
    /// </summary>
    public static async Task It_fails_closed_on_a_post_matched_only_by_the_collation(
        ApiIntegrationHarness harness
    )
    {
        var seed = await SeedProgramAsync(harness, "Strasse Program");
        long programDocumentId = await DocumentIdAsync(harness, seed.Location);
        long originalContentVersion = await ContentVersionAsync(harness, programDocumentId);

        using HttpResponseMessage response = await SendAsync(
            harness,
            HttpMethod.Post,
            $"{DataRoot}/ed-fi/programs",
            ProgramBody(seed, "Straße Program")
        );
        string responseBody = await response.Content.ReadAsStringAsync();

        if (IsPostgresql(harness))
        {
            response.StatusCode.Should().Be(HttpStatusCode.Created, responseBody);
            LocationPath(response).Should().NotBe(seed.Location);
        }
        else
        {
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, responseBody);
            JsonNode.Parse(responseBody)!["type"]!.GetValue<string>().Should().Be(KeyChangeNotSupportedType);
        }

        (await GetAsync(harness, seed.Location)).Body["programName"]!
            .GetValue<string>()
            .Should()
            .Be("Strasse Program");
        (await ContentVersionAsync(harness, programDocumentId)).Should().Be(originalContentVersion);
    }

    /// <summary>
    /// SQL Server: a casing-only PUT of an immutable identity (Program) is a guarded no-op (204, stored casing
    /// served, unchanged <c>ContentVersion</c>). PostgreSQL: an identity change, refused with 400.
    /// </summary>
    public static async Task It_treats_a_casing_only_put_of_an_immutable_identity_by_the_identity_collation(
        ApiIntegrationHarness harness
    )
    {
        var seed = await SeedProgramAsync(harness, "Robotics Program");
        long programDocumentId = await DocumentIdAsync(harness, seed.Location);
        long originalContentVersion = await ContentVersionAsync(harness, programDocumentId);

        JsonObject putBody = ProgramBody(seed, "ROBOTICS PROGRAM");
        putBody["id"] = IdOf(seed.Location);
        using HttpResponseMessage response = await SendAsync(harness, HttpMethod.Put, seed.Location, putBody);
        string responseBody = await response.Content.ReadAsStringAsync();

        if (IsPostgresql(harness))
        {
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, responseBody);
            JsonNode.Parse(responseBody)!["type"]!.GetValue<string>().Should().Be(KeyChangeNotSupportedType);
        }
        else
        {
            response.StatusCode.Should().Be(HttpStatusCode.NoContent, responseBody);
        }

        (await GetAsync(harness, seed.Location)).Body["programName"]!
            .GetValue<string>()
            .Should()
            .Be("Robotics Program");
        (await ContentVersionAsync(harness, programDocumentId)).Should().Be(originalContentVersion);
    }

    /// <summary>
    /// SQL Server: a casing-only PUT of an updatable identity (Section's <c>sectionIdentifier</c>) is a guarded
    /// no-op: no key change is recorded and the StudentSectionAssociation referrer's copy keeps the stored casing.
    /// PostgreSQL: a key change that cascades the new casing to the referrer.
    /// </summary>
    public static async Task It_treats_a_casing_only_section_put_by_the_identity_collation(
        ApiIntegrationHarness harness
    )
    {
        var seed = await SeedSectionAsync(harness);
        string associationLocation = await SeedStudentSectionAssociationAsync(harness, seed);
        long sectionDocumentId = await DocumentIdAsync(harness, seed.SectionLocation);
        long associationDocumentId = await DocumentIdAsync(harness, associationLocation);
        long originalContentVersion = await ContentVersionAsync(harness, sectionDocumentId);
        string recasedSectionIdentifier = seed.SectionIdentifier.ToUpperInvariant();

        JsonObject putBody = SectionBody(seed, recasedSectionIdentifier, seed.SessionName);
        putBody["id"] = IdOf(seed.SectionLocation);
        using HttpResponseMessage response = await SendAsync(
            harness,
            HttpMethod.Put,
            seed.SectionLocation,
            putBody
        );
        response.StatusCode.Should().Be(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync());

        string expectedSectionIdentifier = IsPostgresql(harness)
            ? recasedSectionIdentifier
            : seed.SectionIdentifier;
        (await GetAsync(harness, seed.SectionLocation)).Body["sectionIdentifier"]!
            .GetValue<string>()
            .Should()
            .Be(expectedSectionIdentifier);
        (await AssociationSectionCopyAsync(harness, associationDocumentId))
            .SectionIdentifier.Should()
            .Be(expectedSectionIdentifier);

        if (IsPostgresql(harness))
        {
            await AssertKeyChangeCountAsync(harness, "Section", sectionDocumentId, 1);
        }
        else
        {
            await AssertKeyChangeCountAsync(harness, "Section", sectionDocumentId, 0);
            (await ContentVersionAsync(harness, sectionDocumentId)).Should().Be(originalContentVersion);
        }
    }

    /// <summary>
    /// A PUT that recases <c>sectionIdentifier</c> and moves the Section to another Session. SQL Server: one key
    /// change in which only the session name changes; the StudentSectionAssociation referrer receives the new
    /// session name and keeps the stored section identifier casing. PostgreSQL: both columns change and cascade.
    /// </summary>
    public static async Task It_cascades_only_the_genuinely_changed_column_of_a_mixed_put(
        ApiIntegrationHarness harness
    )
    {
        var seed = await SeedSectionAsync(harness);
        string secondSessionName = $"Spring {seed.Suffix}";
        await SeedSessionAndCourseOfferingAsync(harness, seed, secondSessionName);
        string associationLocation = await SeedStudentSectionAssociationAsync(harness, seed);
        long sectionDocumentId = await DocumentIdAsync(harness, seed.SectionLocation);
        long associationDocumentId = await DocumentIdAsync(harness, associationLocation);
        string recasedSectionIdentifier = seed.SectionIdentifier.ToUpperInvariant();

        JsonObject putBody = SectionBody(seed, recasedSectionIdentifier, secondSessionName);
        putBody["id"] = IdOf(seed.SectionLocation);
        using HttpResponseMessage response = await SendAsync(
            harness,
            HttpMethod.Put,
            seed.SectionLocation,
            putBody
        );
        response.StatusCode.Should().Be(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync());

        string expectedSectionIdentifier = IsPostgresql(harness)
            ? recasedSectionIdentifier
            : seed.SectionIdentifier;
        var (sectionBody, _) = await GetAsync(harness, seed.SectionLocation);
        sectionBody["sectionIdentifier"]!.GetValue<string>().Should().Be(expectedSectionIdentifier);
        sectionBody["courseOfferingReference"]!["sessionName"]!
            .GetValue<string>()
            .Should()
            .Be(secondSessionName);

        var associationCopy = await AssociationSectionCopyAsync(harness, associationDocumentId);
        associationCopy.SectionIdentifier.Should().Be(expectedSectionIdentifier);
        associationCopy.SessionName.Should().Be(secondSessionName);

        await AssertKeyChangeCountAsync(harness, "Section", sectionDocumentId, 1);
        (
            await ScalarAsync<string>(
                harness,
                $"SELECT {Q(harness, "NewSectionIdentifier")} FROM {Q(harness, "tracked_changes_edfi")}.{Q(harness, "Section")} "
                    + $"WHERE {Q(harness, "DocumentId")} = @documentId",
                ("documentId", sectionDocumentId)
            )
        )
            .Should()
            .Be(expectedSectionIdentifier);
    }

    /// <summary>
    /// A ClassPeriod PUT that recases <c>classPeriodName</c> and moves the class period to another School. Its
    /// BellSchedule referrer's foreign key cascades on both engines, so the cascade is observable on SQL Server
    /// too. SQL Server: one key change in which only the school changes; the referrer receives the new school
    /// and keeps the stored class period name casing. PostgreSQL: both columns change and cascade.
    /// </summary>
    public static async Task It_cascades_only_the_genuinely_changed_column_of_a_mixed_class_period_put(
        ApiIntegrationHarness harness
    )
    {
        var seed = await SeedClassPeriodAsync(harness, DataRoot);
        var (_, _, secondSchoolId) = await SeedSchoolAsync(harness, DataRoot);
        string bellScheduleLocation = await SeedBellScheduleAsync(harness, seed);
        long classPeriodDocumentId = await DocumentIdAsync(harness, seed.Location);
        long bellScheduleDocumentId = await DocumentIdAsync(harness, bellScheduleLocation);
        string recasedClassPeriodName = seed.ClassPeriodName.ToUpperInvariant();

        JsonObject putBody = ClassPeriodBody(recasedClassPeriodName, secondSchoolId);
        putBody["id"] = IdOf(seed.Location);
        using HttpResponseMessage response = await SendAsync(harness, HttpMethod.Put, seed.Location, putBody);
        response.StatusCode.Should().Be(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync());

        string expectedClassPeriodName = IsPostgresql(harness)
            ? recasedClassPeriodName
            : seed.ClassPeriodName;
        var (classPeriodBody, _) = await GetAsync(harness, seed.Location);
        classPeriodBody["classPeriodName"]!.GetValue<string>().Should().Be(expectedClassPeriodName);
        classPeriodBody["schoolReference"]!["schoolId"]!.GetValue<long>().Should().Be(secondSchoolId);

        string referrerFilter =
            $"FROM {Q(harness, "edfi")}.{Q(harness, "BellScheduleClassPeriod")} "
            + $"WHERE {Q(harness, "BellSchedule_DocumentId")} = @documentId";
        (
            await ScalarAsync<string>(
                harness,
                $"SELECT {Q(harness, "ClassPeriod_ClassPeriodName")} {referrerFilter}",
                ("documentId", bellScheduleDocumentId)
            )
        )
            .Should()
            .Be(expectedClassPeriodName);
        (
            await ScalarAsync<long>(
                harness,
                $"SELECT {Q(harness, "ClassPeriod_SchoolId")} {referrerFilter}",
                ("documentId", bellScheduleDocumentId)
            )
        )
            .Should()
            .Be(secondSchoolId);

        await AssertKeyChangeCountAsync(harness, "ClassPeriod", classPeriodDocumentId, 1);
        string keyChangeFilter =
            $"FROM {Q(harness, "tracked_changes_edfi")}.{Q(harness, "ClassPeriod")} "
            + $"WHERE {Q(harness, "DocumentId")} = @documentId";
        (
            await ScalarAsync<string>(
                harness,
                $"SELECT {Q(harness, "NewClassPeriodName")} {keyChangeFilter}",
                ("documentId", classPeriodDocumentId)
            )
        )
            .Should()
            .Be(expectedClassPeriodName);
        (
            await ScalarAsync<long>(
                harness,
                $"SELECT {Q(harness, "NewSchool_SchoolId")} {keyChangeFilter}",
                ("documentId", classPeriodDocumentId)
            )
        )
            .Should()
            .Be(secondSchoolId);
    }

    /// <summary>
    /// A profiled PUT that recases a <c>studentIndicators</c> item's <c>indicatorName</c> through a profile that
    /// hides the item's required <c>indicator</c>. Core compares items by exact text, so it treats the recased
    /// item as new and not creatable. SQL Server: the backend matches it to the stored row under the identity
    /// collation, so the PUT succeeds, the row keeps its <c>CollectionItemId</c>, and the stored casing and hidden
    /// <c>indicator</c> are preserved. PostgreSQL: the item is new, so the profile's creatability rule rejects it.
    /// A recased item that also adds a new <c>periods</c> child is rejected on both engines. That is a known
    /// limitation on SQL Server: Core's creatability treats the recased parent as new and not creatable, and the
    /// backend receives only that combined decision. The same new child under the stored casing is accepted.
    /// </summary>
    public static async Task It_merges_a_recased_profiled_item_whose_profile_hides_a_required_member(
        ApiIntegrationHarness harness
    )
    {
        var (suffix, _, schoolId) = await SeedSchoolAsync(harness, DataRoot);
        string studentUniqueId = $"stu-{suffix}";
        await PostExpectingCreatedAsync(
            harness,
            $"{DataRoot}/ed-fi/students",
            new JsonObject
            {
                ["studentUniqueId"] = studentUniqueId,
                ["firstName"] = "Case",
                ["lastSurname"] = "Variant",
                ["birthDate"] = "2012-01-01",
            }
        );
        const string storedIndicatorName = "Reading Level";
        const string hiddenIndicator = "Proficient";
        JsonObject AssociationBody(string indicatorName, string indicator)
        {
            var item = new JsonObject
            {
                ["indicatorName"] = indicatorName,
                ["indicator"] = indicator,
                ["periods"] = new JsonArray(new JsonObject { ["beginDate"] = "2024-08-20" }),
            };

            return new JsonObject
            {
                ["studentReference"] = new JsonObject { ["studentUniqueId"] = studentUniqueId },
                ["educationOrganizationReference"] = new JsonObject
                {
                    ["educationOrganizationId"] = schoolId,
                },
                ["studentIndicators"] = new JsonArray(item),
            };
        }

        string location = await PostExpectingCreatedAsync(
            harness,
            $"{DataRoot}/ed-fi/studentEducationOrganizationAssociations",
            AssociationBody(storedIndicatorName, hiddenIndicator)
        );
        long documentId = await DocumentIdAsync(harness, location);
        string indicatorRowFilter =
            $"FROM {Q(harness, "edfi")}.{Q(harness, "StudentEducationOrganizationAssociationStudentIndicator")} "
            + $"WHERE {Q(harness, "StudentEducationOrganizationAssociation_DocumentId")} = @documentId";
        long originalCollectionItemId = await ScalarAsync<long>(
            harness,
            $"SELECT {Q(harness, "CollectionItemId")} {indicatorRowFilter}",
            ("documentId", documentId)
        );

        // The profile strips the hidden indicator from the request, so its value must not reach storage.
        JsonObject putBody = AssociationBody(storedIndicatorName.ToUpperInvariant(), "Advanced");
        putBody["id"] = IdOf(location);
        using HttpResponseMessage response = await SendAsync(
            harness,
            HttpMethod.Put,
            location,
            putBody,
            HiddenIndicatorWritableContentType
        );
        string responseBody = await response.Content.ReadAsStringAsync();

        if (IsPostgresql(harness))
        {
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, responseBody);
            JsonNode.Parse(responseBody)!["type"]!.GetValue<string>().Should().Be(DataPolicyEnforcedType);
        }
        else
        {
            response.StatusCode.Should().Be(HttpStatusCode.NoContent, responseBody);
        }

        await AssertStoredIndicatorAsync(expectedPeriodCount: 1);

        // Core gates a new child on its parent by exact text, so a new period under the recased parent is
        // rejected on both engines.
        using (
            HttpResponseMessage recasedWithNewChild = await SendAsync(
                harness,
                HttpMethod.Put,
                location,
                PutBodyWithNewPeriod(storedIndicatorName.ToUpperInvariant()),
                HiddenIndicatorWritableContentType
            )
        )
        {
            string recasedWithNewChildBody = await recasedWithNewChild.Content.ReadAsStringAsync();
            recasedWithNewChild.StatusCode.Should().Be(HttpStatusCode.BadRequest, recasedWithNewChildBody);
            JsonNode.Parse(recasedWithNewChildBody)!["type"]!
                .GetValue<string>()
                .Should()
                .Be(DataPolicyEnforcedType);
        }

        await AssertStoredIndicatorAsync(expectedPeriodCount: 1);

        // Control: under the stored casing the same new period is creatable, so the rejection above comes
        // from the recase, not from the profile.
        using (
            HttpResponseMessage storedWithNewChild = await SendAsync(
                harness,
                HttpMethod.Put,
                location,
                PutBodyWithNewPeriod(storedIndicatorName),
                HiddenIndicatorWritableContentType
            )
        )
        {
            storedWithNewChild
                .StatusCode.Should()
                .Be(HttpStatusCode.NoContent, await storedWithNewChild.Content.ReadAsStringAsync());
        }

        await AssertStoredIndicatorAsync(expectedPeriodCount: 2);

        JsonObject PutBodyWithNewPeriod(string indicatorName)
        {
            JsonObject newPeriodBody = AssociationBody(indicatorName, "Advanced");
            newPeriodBody["id"] = IdOf(location);
            newPeriodBody["studentIndicators"]![0]!["periods"]!
                .AsArray()
                .Add(new JsonObject { ["beginDate"] = "2025-01-15" });
            return newPeriodBody;
        }

        async Task AssertStoredIndicatorAsync(int expectedPeriodCount)
        {
            var (body, _) = await GetAsync(harness, location);
            JsonNode storedItem = body["studentIndicators"]!.AsArray().Should().ContainSingle().Subject!;
            storedItem["indicatorName"]!.GetValue<string>().Should().Be(storedIndicatorName);
            storedItem["indicator"]!.GetValue<string>().Should().Be(hiddenIndicator);
            storedItem["periods"]!.AsArray().Should().HaveCount(expectedPeriodCount);
            (
                await ScalarAsync<long>(
                    harness,
                    $"SELECT {Q(harness, "CollectionItemId")} {indicatorRowFilter}",
                    ("documentId", documentId)
                )
            )
                .Should()
                .Be(originalCollectionItemId);
        }
    }

    /// <summary>
    /// A client granted Create but not Update on ClassPeriod. SQL Server: the recased POST targets the stored
    /// document, so it is authorized as an Update and refused with 403 naming Update; body and ETag are
    /// unchanged. PostgreSQL: a create, so it succeeds with 201.
    /// </summary>
    public static async Task It_authorizes_a_case_variant_post_of_a_create_only_client_by_the_identity_collation(
        ApiIntegrationHarness harness
    )
    {
        Grants(harness).Set(create: _noFurther, update: _noFurther);
        var seed = await SeedClassPeriodAsync(harness, DataRoot);
        var (originalBody, originalEtag) = await GetAsync(harness, seed.Location);

        Grants(harness).Set(create: _noFurther, update: null);
        using HttpResponseMessage response = await SendAsync(
            harness,
            HttpMethod.Post,
            $"{DataRoot}/ed-fi/classPeriods",
            ClassPeriodBody(seed.ClassPeriodName.ToUpperInvariant(), seed.SchoolId)
        );
        string responseBody = await response.Content.ReadAsStringAsync();

        if (IsPostgresql(harness))
        {
            response.StatusCode.Should().Be(HttpStatusCode.Created, responseBody);
        }
        else
        {
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden, responseBody);
            JsonNode.Parse(responseBody)!["errors"]!
                .AsArray()
                .Select(static error => error!.GetValue<string>())
                .Should()
                .Equal(
                    $"The API client's assigned claim set (currently '{ExternalDoublesConstants.SmokeClaimSetName}') must grant permission of the 'Update' action on one of the following resource claims: ClassPeriod"
                );
        }

        var (body, etag) = await GetAsync(harness, seed.Location);
        body.ToJsonString().Should().Be(originalBody.ToJsonString());
        etag.Should().Be(originalEtag);
    }

    /// <summary>
    /// A client granted Update but not Create on ClassPeriod. SQL Server: the recased POST is authorized as an
    /// Update and succeeds with 200 against the stored document. PostgreSQL: a create, refused with 403 naming
    /// Create.
    /// </summary>
    public static async Task It_authorizes_a_case_variant_post_of_an_update_only_client_by_the_identity_collation(
        ApiIntegrationHarness harness
    )
    {
        Grants(harness).Set(create: _noFurther, update: _noFurther);
        var seed = await SeedClassPeriodAsync(harness, DataRoot);

        Grants(harness).Set(create: null, update: _noFurther);
        using HttpResponseMessage response = await SendAsync(
            harness,
            HttpMethod.Post,
            $"{DataRoot}/ed-fi/classPeriods",
            ClassPeriodBody(seed.ClassPeriodName.ToUpperInvariant(), seed.SchoolId)
        );
        string responseBody = await response.Content.ReadAsStringAsync();

        if (IsPostgresql(harness))
        {
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden, responseBody);
            JsonNode.Parse(responseBody)!["errors"]!
                .AsArray()
                .Select(static error => error!.GetValue<string>())
                .Should()
                .Equal(
                    $"The API client's assigned claim set (currently '{ExternalDoublesConstants.SmokeClaimSetName}') must grant permission of the 'Create' action on one of the following resource claims: ClassPeriod"
                );
        }
        else
        {
            response.StatusCode.Should().Be(HttpStatusCode.OK, responseBody);
            LocationPath(response).Should().Be(seed.Location);
        }

        (await GetAsync(harness, seed.Location)).Body["classPeriodName"]!
            .GetValue<string>()
            .Should()
            .Be(seed.ClassPeriodName);
    }

    /// <summary>
    /// A foreign client that cannot reach the owner's row. SQL Server: the recased POST targets the stored row,
    /// so ownership is evaluated against it and the POST is refused with the ownership mismatch. PostgreSQL: a
    /// create the foreign client owns, 201.
    /// </summary>
    public static async Task It_evaluates_ownership_of_a_case_variant_post_against_the_stored_row(
        ApiIntegrationHarness harness
    )
    {
        var seed = await SeedClassPeriodAsync(harness, $"/{OwnerTenant}{DataRoot}");
        var (originalBody, originalEtag) = await GetAsync(harness, seed.Location);

        using HttpResponseMessage response = await SendAsync(
            harness,
            HttpMethod.Post,
            $"/{ForeignTenant}{DataRoot}/ed-fi/classPeriods",
            ClassPeriodBody(seed.ClassPeriodName.ToUpperInvariant(), seed.SchoolId)
        );
        string responseBody = await response.Content.ReadAsStringAsync();

        if (IsPostgresql(harness))
        {
            response.StatusCode.Should().Be(HttpStatusCode.Created, responseBody);
        }
        else
        {
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden, responseBody);
            JsonNode.Parse(responseBody)!["type"]!.GetValue<string>().Should().Be(OwnershipMismatchType);
        }

        var (body, etag) = await GetAsync(harness, seed.Location);
        body.ToJsonString().Should().Be(originalBody.ToJsonString());
        etag.Should().Be(originalEtag);
    }

    private sealed record ClassPeriodSeed(
        string Suffix,
        long SchoolId,
        string ClassPeriodName,
        string Location
    );

    private sealed record ProgramSeed(long SchoolId, string ProgramTypeDescriptor, string Location);

    private sealed record SectionSeed(
        string Suffix,
        string NamespaceUri,
        long SchoolId,
        string LocalCourseCode,
        string CourseCode,
        string SessionName,
        string SectionIdentifier,
        string SectionLocation
    );

    private static async Task<(string Suffix, string NamespaceUri, long SchoolId)> SeedSchoolAsync(
        ApiIntegrationHarness harness,
        string root
    )
    {
        // The schoolId derives from the suffix's own hex value, which keeps it int32-safe and unique per test.
        string suffix = Guid.NewGuid().ToString("N")[..8];
        long schoolId = 1_000_000_000L + (Convert.ToInt64(suffix, 16) % 1_000_000_000L);
        string namespaceUri = $"uri://ed-fi.org/CaseVariant/{suffix}";

        await SeedDescriptorAsync(
            harness,
            root,
            "educationOrganizationCategoryDescriptors",
            namespaceUri,
            "EducationOrganizationCategoryDescriptor",
            "School"
        );
        await SeedDescriptorAsync(
            harness,
            root,
            "gradeLevelDescriptors",
            namespaceUri,
            "GradeLevelDescriptor",
            "Tenth grade"
        );
        await PostExpectingCreatedAsync(
            harness,
            $"{root}/ed-fi/schools",
            new JsonObject
            {
                ["schoolId"] = schoolId,
                ["nameOfInstitution"] = $"Case Variant School {suffix}",
                ["educationOrganizationCategories"] = new JsonArray(
                    new JsonObject
                    {
                        ["educationOrganizationCategoryDescriptor"] =
                            $"{namespaceUri}/EducationOrganizationCategoryDescriptor#School",
                    }
                ),
                ["gradeLevels"] = new JsonArray(
                    new JsonObject
                    {
                        ["gradeLevelDescriptor"] = $"{namespaceUri}/GradeLevelDescriptor#Tenth grade",
                    }
                ),
            }
        );

        return (suffix, namespaceUri, schoolId);
    }

    private static async Task<ClassPeriodSeed> SeedClassPeriodAsync(
        ApiIntegrationHarness harness,
        string root
    )
    {
        var (suffix, _, schoolId) = await SeedSchoolAsync(harness, root);
        string classPeriodName = $"First Period {suffix}";
        string location = await PostExpectingCreatedAsync(
            harness,
            $"{root}/ed-fi/classPeriods",
            ClassPeriodBody(classPeriodName, schoolId)
        );

        return new ClassPeriodSeed(suffix, schoolId, classPeriodName, location);
    }

    private static Task<string> SeedBellScheduleAsync(ApiIntegrationHarness harness, ClassPeriodSeed seed) =>
        PostExpectingCreatedAsync(
            harness,
            $"{DataRoot}/ed-fi/bellSchedules",
            new JsonObject
            {
                ["bellScheduleName"] = $"Bell {seed.Suffix}",
                ["schoolReference"] = new JsonObject { ["schoolId"] = seed.SchoolId },
                ["classPeriods"] = new JsonArray(
                    new JsonObject
                    {
                        ["classPeriodReference"] = new JsonObject
                        {
                            ["classPeriodName"] = seed.ClassPeriodName,
                            ["schoolId"] = seed.SchoolId,
                        },
                    }
                ),
            }
        );

    private static async Task<ProgramSeed> SeedProgramAsync(ApiIntegrationHarness harness, string programName)
    {
        var (_, namespaceUri, schoolId) = await SeedSchoolAsync(harness, DataRoot);
        await SeedDescriptorAsync(
            harness,
            DataRoot,
            "programTypeDescriptors",
            namespaceUri,
            "ProgramTypeDescriptor",
            "Career and Technical Education"
        );
        var seed = new ProgramSeed(
            schoolId,
            $"{namespaceUri}/ProgramTypeDescriptor#Career and Technical Education",
            Location: string.Empty
        );
        string location = await PostExpectingCreatedAsync(
            harness,
            $"{DataRoot}/ed-fi/programs",
            ProgramBody(seed, programName)
        );

        return seed with
        {
            Location = location,
        };
    }

    private static async Task<SectionSeed> SeedSectionAsync(ApiIntegrationHarness harness)
    {
        var (suffix, namespaceUri, schoolId) = await SeedSchoolAsync(harness, DataRoot);
        await SeedDescriptorAsync(
            harness,
            DataRoot,
            "termDescriptors",
            namespaceUri,
            "TermDescriptor",
            "Fall Semester"
        );
        await SeedDescriptorAsync(
            harness,
            DataRoot,
            "courseIdentificationSystemDescriptors",
            namespaceUri,
            "CourseIdentificationSystemDescriptor",
            "LEA course code"
        );

        // The school year is a shared global identity, so a repeat POST upserts instead of creating.
        using (
            HttpResponseMessage schoolYear = await SendAsync(
                harness,
                HttpMethod.Post,
                $"{DataRoot}/ed-fi/schoolYearTypes",
                new JsonObject
                {
                    ["schoolYear"] = SchoolYear,
                    ["currentSchoolYear"] = false,
                    ["schoolYearDescription"] = $"{SchoolYear}",
                }
            )
        )
        {
            schoolYear
                .StatusCode.Should()
                .BeOneOf(
                    [HttpStatusCode.Created, HttpStatusCode.OK],
                    await schoolYear.Content.ReadAsStringAsync()
                );
        }

        string courseCode = $"ALG-1-{suffix}";
        await PostExpectingCreatedAsync(
            harness,
            $"{DataRoot}/ed-fi/courses",
            new JsonObject
            {
                ["courseCode"] = courseCode,
                ["courseTitle"] = $"Algebra I {suffix}",
                ["numberOfParts"] = 1,
                ["educationOrganizationReference"] = new JsonObject
                {
                    ["educationOrganizationId"] = schoolId,
                },
                ["identificationCodes"] = new JsonArray(
                    new JsonObject
                    {
                        ["identificationCode"] = courseCode,
                        ["courseIdentificationSystemDescriptor"] =
                            $"{namespaceUri}/CourseIdentificationSystemDescriptor#LEA course code",
                    }
                ),
            }
        );

        var seed = new SectionSeed(
            suffix,
            namespaceUri,
            schoolId,
            LocalCourseCode: $"LCC-{suffix}",
            courseCode,
            SessionName: $"Fall {suffix}",
            SectionIdentifier: $"Sec-{suffix}",
            SectionLocation: string.Empty
        );
        await SeedSessionAndCourseOfferingAsync(harness, seed, seed.SessionName);
        string sectionLocation = await PostExpectingCreatedAsync(
            harness,
            $"{DataRoot}/ed-fi/sections",
            SectionBody(seed, seed.SectionIdentifier, seed.SessionName)
        );

        return seed with
        {
            SectionLocation = sectionLocation,
        };
    }

    private static async Task SeedSessionAndCourseOfferingAsync(
        ApiIntegrationHarness harness,
        SectionSeed seed,
        string sessionName
    )
    {
        await PostExpectingCreatedAsync(
            harness,
            $"{DataRoot}/ed-fi/sessions",
            new JsonObject
            {
                ["sessionName"] = sessionName,
                ["schoolReference"] = new JsonObject { ["schoolId"] = seed.SchoolId },
                ["schoolYearTypeReference"] = new JsonObject { ["schoolYear"] = SchoolYear },
                ["beginDate"] = "2024-08-01",
                ["endDate"] = "2024-12-20",
                ["termDescriptor"] = $"{seed.NamespaceUri}/TermDescriptor#Fall Semester",
                ["totalInstructionalDays"] = 90,
            }
        );
        await PostExpectingCreatedAsync(
            harness,
            $"{DataRoot}/ed-fi/courseOfferings",
            new JsonObject
            {
                ["localCourseCode"] = seed.LocalCourseCode,
                ["schoolReference"] = new JsonObject { ["schoolId"] = seed.SchoolId },
                ["sessionReference"] = new JsonObject
                {
                    ["schoolId"] = seed.SchoolId,
                    ["schoolYear"] = SchoolYear,
                    ["sessionName"] = sessionName,
                },
                ["courseReference"] = new JsonObject
                {
                    ["courseCode"] = seed.CourseCode,
                    ["educationOrganizationId"] = seed.SchoolId,
                },
            }
        );
    }

    private static async Task<string> SeedStudentSectionAssociationAsync(
        ApiIntegrationHarness harness,
        SectionSeed seed
    )
    {
        string studentUniqueId = $"stu-{seed.Suffix}";
        await PostExpectingCreatedAsync(
            harness,
            $"{DataRoot}/ed-fi/students",
            new JsonObject
            {
                ["studentUniqueId"] = studentUniqueId,
                ["firstName"] = "Case",
                ["lastSurname"] = "Variant",
                ["birthDate"] = "2012-01-01",
            }
        );

        return await PostExpectingCreatedAsync(
            harness,
            $"{DataRoot}/ed-fi/studentSectionAssociations",
            new JsonObject
            {
                ["beginDate"] = "2024-08-01",
                ["studentReference"] = new JsonObject { ["studentUniqueId"] = studentUniqueId },
                ["sectionReference"] = new JsonObject
                {
                    ["localCourseCode"] = seed.LocalCourseCode,
                    ["schoolId"] = seed.SchoolId,
                    ["schoolYear"] = SchoolYear,
                    ["sectionIdentifier"] = seed.SectionIdentifier,
                    ["sessionName"] = seed.SessionName,
                },
            }
        );
    }

    private static Task SeedDescriptorAsync(
        ApiIntegrationHarness harness,
        string root,
        string endpoint,
        string namespaceUri,
        string descriptorName,
        string codeValue
    ) =>
        PostExpectingCreatedAsync(
            harness,
            $"{root}/ed-fi/{endpoint}",
            new JsonObject
            {
                ["namespace"] = $"{namespaceUri}/{descriptorName}",
                ["codeValue"] = codeValue,
                ["shortDescription"] = codeValue,
            }
        );

    private static JsonObject ClassPeriodBody(string classPeriodName, long schoolId) =>
        new()
        {
            ["classPeriodName"] = classPeriodName,
            ["schoolReference"] = new JsonObject { ["schoolId"] = schoolId },
        };

    private static JsonObject ProgramBody(ProgramSeed seed, string programName) =>
        new()
        {
            ["programName"] = programName,
            ["programTypeDescriptor"] = seed.ProgramTypeDescriptor,
            ["educationOrganizationReference"] = new JsonObject
            {
                ["educationOrganizationId"] = seed.SchoolId,
            },
        };

    private static JsonObject SectionBody(SectionSeed seed, string sectionIdentifier, string sessionName) =>
        new()
        {
            ["sectionIdentifier"] = sectionIdentifier,
            ["courseOfferingReference"] = new JsonObject
            {
                ["localCourseCode"] = seed.LocalCourseCode,
                ["schoolId"] = seed.SchoolId,
                ["schoolYear"] = SchoolYear,
                ["sessionName"] = sessionName,
            },
        };

    private static async Task<string> PostExpectingCreatedAsync(
        ApiIntegrationHarness harness,
        string endpoint,
        JsonObject payload
    )
    {
        using HttpResponseMessage response = await SendAsync(harness, HttpMethod.Post, endpoint, payload);
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Created, $"POST {endpoint} body: {body}");

        return LocationPath(response);
    }

    private static async Task<HttpResponseMessage> SendAsync(
        ApiIntegrationHarness harness,
        HttpMethod method,
        string endpoint,
        JsonObject body,
        string contentType = "application/json"
    )
    {
        using var request = new HttpRequestMessage(method, endpoint)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, contentType),
        };

        return await harness.HttpClient.SendAsync(request);
    }

    private static async Task<(JsonObject Body, string Etag)> GetAsync(
        ApiIntegrationHarness harness,
        string location
    )
    {
        using HttpResponseMessage response = await harness.HttpClient.GetAsync(location);
        string body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        response.TryReadRawEtag(out string etag).Should().BeTrue();

        return (JsonNode.Parse(body)!.AsObject(), etag);
    }

    private static string LocationPath(HttpResponseMessage response) =>
        response.Headers.Location!.IsAbsoluteUri
            ? response.Headers.Location.AbsolutePath
            : response.Headers.Location.OriginalString;

    private static string IdOf(string location) =>
        location.Split('/', StringSplitOptions.RemoveEmptyEntries)[^1];

    private static SwitchablePostActionGrants Grants(ApiIntegrationHarness harness) =>
        (SwitchablePostActionGrants)harness.Services.GetRequiredService<IClaimSetProvider>();

    private static ApplicationContextResult OwnershipContext(long applicationId, short ownershipToken) =>
        new ApplicationContextResult.Success(
            new ApplicationContext(
                Id: applicationId,
                ApplicationId: applicationId,
                ClientId: ExternalDoublesConstants.SmokeClientId,
                ClientUuid: ExternalDoublesConstants.StableClientUuid,
                DataStoreIds: [ExternalDoublesConstants.StableDataStoreId],
                CreatorOwnershipTokenId: ownershipToken,
                OwnershipTokenIds: [ownershipToken]
            )
        );

    private static bool IsPostgresql(ApiIntegrationHarness harness) =>
        harness.DbConnection is NpgsqlConnection;

    private static string Q(ApiIntegrationHarness harness, string identifier) =>
        IsPostgresql(harness) ? $"\"{identifier}\"" : $"[{identifier}]";

    private static Task<long> DocumentIdAsync(ApiIntegrationHarness harness, string location) =>
        ScalarAsync<long>(
            harness,
            $"SELECT {Q(harness, "DocumentId")} FROM {Q(harness, "dms")}.{Q(harness, "Document")} "
                + $"WHERE {Q(harness, "DocumentUuid")} = @documentUuid",
            ("documentUuid", Guid.Parse(IdOf(location)))
        );

    private static Task<long> ContentVersionAsync(ApiIntegrationHarness harness, long documentId) =>
        ScalarAsync<long>(
            harness,
            $"SELECT {Q(harness, "ContentVersion")} FROM {Q(harness, "dms")}.{Q(harness, "Document")} "
                + $"WHERE {Q(harness, "DocumentId")} = @documentId",
            ("documentId", documentId)
        );

    private static async Task AssertKeyChangeCountAsync(
        ApiIntegrationHarness harness,
        string resourceTable,
        long documentId,
        long expectedCount
    ) =>
        (
            await ScalarAsync<long>(
                harness,
                $"SELECT COUNT(*) FROM {Q(harness, "tracked_changes_edfi")}.{Q(harness, resourceTable)} "
                    + $"WHERE {Q(harness, "DocumentId")} = @documentId",
                ("documentId", documentId)
            )
        )
            .Should()
            .Be(expectedCount, $"the {resourceTable} key-change rows recorded for document {documentId}");

    private static async Task<(string SectionIdentifier, string SessionName)> AssociationSectionCopyAsync(
        ApiIntegrationHarness harness,
        long associationDocumentId
    )
    {
        await using DbCommand command = harness.DbConnection.CreateCommand();
        command.CommandText =
            $"SELECT {Q(harness, "Section_SectionIdentifier")}, {Q(harness, "Section_SessionName")} "
            + $"FROM {Q(harness, "edfi")}.{Q(harness, "StudentSectionAssociation")} "
            + $"WHERE {Q(harness, "DocumentId")} = @documentId";
        AddParameter(command, "documentId", associationDocumentId);

        await using DbDataReader reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();

        return (reader.GetString(0), reader.GetString(1));
    }

    private static async Task<T> ScalarAsync<T>(
        ApiIntegrationHarness harness,
        string sql,
        params (string Name, object Value)[] parameters
    )
    {
        await using DbCommand command = harness.DbConnection.CreateCommand();
        command.CommandText = sql;

        foreach (var (name, value) in parameters)
        {
            AddParameter(command, name, value);
        }

        object? result = await command.ExecuteScalarAsync();

        return (T)Convert.ChangeType(result!, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
