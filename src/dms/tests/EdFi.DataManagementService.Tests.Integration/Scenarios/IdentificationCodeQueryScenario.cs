// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.Extraction;
using EdFi.DataManagementService.Core.Middleware;
using EdFi.DataManagementService.Tests.Integration.Doubles;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.DependencyInjection;
using static EdFi.DataManagementService.Tests.Integration.Scenarios.CursorContractSupport;
using static EdFi.DataManagementService.Tests.Integration.Scenarios.IgnoredParameterWarningAssertions;

namespace EdFi.DataManagementService.Tests.Integration.Scenarios;

/// <summary>
/// GET-many and partitions filters over a resource's identification-code collection, driven through the
/// public HTTP surface of the Data Standard 5.2 host.
/// </summary>
/// <remarks>
/// <para>
/// Every filter value is chosen so that ignoring the filter, or matching it against the wrong element,
/// returns a different set than the one asserted. The seeded schools are:
/// </para>
/// <list type="bullet">
///   <item><description>Alpha: A-1/SEA, SHARED/NCES, SHARED/LEA</description></item>
///   <item><description>Beta: SHARED/SEA</description></item>
///   <item><description>Gamma: no identification codes</description></item>
/// </list>
/// <para>
/// SHARED with SEA therefore matches only Beta, because Alpha's SHARED code and SEA system sit on
/// different elements, and SHARED alone matches two of Alpha's elements but returns Alpha once. The
/// system descriptor identifies an element, so a school cannot carry one system twice.
/// </para>
/// </remarks>
internal static class IdentificationCodeQueryScenario
{
    internal const string ProfileName = "School-WithoutIdentificationCodes";

    private const string ProfileReadableContentType =
        "application/vnd.ed-fi.school.school-withoutidentificationcodes.readable+json";

    private const string StandardJsonContentType = "application/json";
    private const string DescriptorNamespace = "uri://ed-fi.org";
    private const string ResourcesSpecification = "/metadata/specifications/resources-spec.json";
    private const string ProfileResourcesSpecification =
        $"/metadata/specifications/profiles/{ProfileName}/resources-spec.json";

    private const string SchoolsEndpoint = "/data/ed-fi/schools";
    private const string StaffsEndpoint = "/data/ed-fi/staffs";
    private const string StudentsEndpoint = "/data/ed-fi/students";
    private const string AssociationsEndpoint = "/data/ed-fi/studentEducationOrganizationAssociations";
    private const string CourseTranscriptsEndpoint = "/data/ed-fi/courseTranscripts";

    private const string EdOrgSystemType = "EducationOrganizationIdentificationSystemDescriptor";
    private const string StaffSystemType = "StaffIdentificationSystemDescriptor";
    private const string StudentSystemType = "StudentIdentificationSystemDescriptor";
    private const string CourseSystemType = "CourseIdentificationSystemDescriptor";

    private const string EdOrgSystemParameter = "educationOrganizationIdentificationSystemDescriptor";
    private const string EdOrgSystemPath = $"$.identificationCodes[*].{EdOrgSystemParameter}";

    private const long Alpha = 255901001;
    private const long Beta = 255901002;
    private const long Gamma = 255901003;
    private const long Delta = 255901004;
    private const int SchoolYear = 2024;

    private const string RootCatalogUrl = "http://root.example.org/catalog";
    private const string CollectionCatalogUrl = "http://collection.example.org/catalog";

    /// <summary>
    /// The collection names each resource publishes and accepts, for the served-metadata assertions.
    /// </summary>
    private static readonly (string Path, string Endpoint, string[] Names)[] _publishedParameters =
    [
        ("/ed-fi/schools", SchoolsEndpoint, [EdOrgSystemParameter, "identificationCode"]),
        (
            "/ed-fi/staffs",
            StaffsEndpoint,
            [
                "assigningOrganizationIdentificationCode",
                "identificationCode",
                "staffIdentificationSystemDescriptor",
            ]
        ),
        (
            "/ed-fi/studentEducationOrganizationAssociations",
            AssociationsEndpoint,
            [
                "assigningOrganizationIdentificationCode",
                "identificationCode",
                "studentIdentificationSystemDescriptor",
            ]
        ),
        (
            "/ed-fi/courseTranscripts",
            CourseTranscriptsEndpoint,
            ["courseIdentificationSystemDescriptor", "identificationCode"]
        ),
    ];

    private static string Uri(string descriptorType, string codeValue) =>
        $"{DescriptorNamespace}/{descriptorType}#{codeValue}";

    private static string Escape(string value) => System.Uri.EscapeDataString(value);

    private static string SchoolKey(JsonNode item) => item["schoolId"]!.GetValue<long>().ToString();

    private static string StaffKey(JsonNode item) => item["staffUniqueId"]!.GetValue<string>();

    private static string StudentKey(JsonNode item) =>
        item["studentReference"]!["studentUniqueId"]!.GetValue<string>();

    private static string TranscriptKey(JsonNode item) =>
        item["studentAcademicRecordReference"]!["studentUniqueId"]!.GetValue<string>();

    // ---------------------------------------------------------------------------------------------
    // Seeding
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Seeds every resource the scenario filters.
    /// </summary>
    public static async Task SeedAsync(ApiIntegrationHarness harness)
    {
        await SeedSchoolsAsync(harness);
        await SeedStaffsAsync(harness);
        await SeedStudentAssociationsAsync(harness);
        await SeedCourseTranscriptsAsync(harness);
    }

    /// <summary>
    /// Seeds the three schools and the descriptors they reference.
    /// </summary>
    public static async Task SeedSchoolsAsync(ApiIntegrationHarness harness)
    {
        await SeedDescriptorAsync(harness, "EducationOrganizationCategoryDescriptor", "School");
        await SeedDescriptorAsync(harness, "GradeLevelDescriptor", "Tenth grade");
        await SeedDescriptorAsync(harness, EdOrgSystemType, "SEA");
        await SeedDescriptorAsync(harness, EdOrgSystemType, "NCES");
        await SeedDescriptorAsync(harness, EdOrgSystemType, "LEA");

        await PostSchoolAsync(harness, Alpha, ("A-1", "SEA"), ("SHARED", "NCES"), ("SHARED", "LEA"));
        await PostSchoolAsync(harness, Beta, ("SHARED", "SEA"));
        await PostSchoolAsync(harness, Gamma);
    }

    private static async Task SeedStaffsAsync(ApiIntegrationHarness harness)
    {
        await SeedDescriptorAsync(harness, StaffSystemType, "State");
        await SeedDescriptorAsync(harness, StaffSystemType, "District");

        await PostAsync(
            harness,
            StaffsEndpoint,
            new JsonObject
            {
                ["staffUniqueId"] = "STAFF-1",
                ["firstName"] = "First",
                ["lastSurname"] = "Staff",
                ["identificationCodes"] = new JsonArray(
                    StaffCode("ST-1", "State", "ORG-A"),
                    StaffCode("ST-2", "District", "ORG-B")
                ),
            }
        );
        await PostAsync(
            harness,
            StaffsEndpoint,
            new JsonObject
            {
                ["staffUniqueId"] = "STAFF-2",
                ["firstName"] = "Second",
                ["lastSurname"] = "Staff",
                ["identificationCodes"] = new JsonArray(StaffCode("ST-3", "State", "ORG-B")),
            }
        );

        static JsonObject StaffCode(string code, string system, string organization) =>
            new()
            {
                ["identificationCode"] = code,
                ["staffIdentificationSystemDescriptor"] = Uri(StaffSystemType, system),
                ["assigningOrganizationIdentificationCode"] = organization,
            };
    }

    private static async Task SeedStudentAssociationsAsync(ApiIntegrationHarness harness)
    {
        await SeedDescriptorAsync(harness, StudentSystemType, "State");
        await SeedDescriptorAsync(harness, StudentSystemType, "District");
        await PostStudentsAsync(harness);

        foreach (
            (string student, string code, string system) in new[]
            {
                ("STU-1", "SC-1", "State"),
                ("STU-2", "SC-2", "District"),
            }
        )
        {
            await PostAsync(
                harness,
                AssociationsEndpoint,
                new JsonObject
                {
                    ["educationOrganizationReference"] = new JsonObject
                    {
                        ["educationOrganizationId"] = Alpha,
                    },
                    ["studentReference"] = new JsonObject { ["studentUniqueId"] = student },
                    ["studentIdentificationCodes"] = new JsonArray(
                        new JsonObject
                        {
                            ["identificationCode"] = code,
                            ["studentIdentificationSystemDescriptor"] = Uri(StudentSystemType, system),
                            ["assigningOrganizationIdentificationCode"] = "ORG-A",
                        }
                    ),
                }
            );
        }
    }

    /// <summary>
    /// Two transcripts whose root and collection values for the two colliding names differ: STU-1 has
    /// the collection values on its alternative code and different root values, STU-2 has the
    /// collection values at the root and no alternative codes.
    /// </summary>
    private static async Task SeedCourseTranscriptsAsync(ApiIntegrationHarness harness)
    {
        await SeedDescriptorAsync(harness, CourseSystemType, "LEA course code");
        await SeedDescriptorAsync(harness, CourseSystemType, "State course code");
        await SeedDescriptorAsync(harness, "TermDescriptor", "Fall Semester");
        await SeedDescriptorAsync(harness, "CourseAttemptResultDescriptor", "Pass");

        await PostAsync(
            harness,
            "/data/ed-fi/schoolYearTypes",
            new JsonObject
            {
                ["schoolYear"] = SchoolYear,
                ["currentSchoolYear"] = false,
                ["schoolYearDescription"] = $"{SchoolYear}",
            },
            HttpStatusCode.Created,
            HttpStatusCode.OK
        );
        await PostAsync(
            harness,
            "/data/ed-fi/courses",
            new JsonObject
            {
                ["courseCode"] = "C-1",
                ["courseTitle"] = "Course One",
                ["numberOfParts"] = 1,
                ["educationOrganizationReference"] = new JsonObject { ["educationOrganizationId"] = Alpha },
                ["identificationCodes"] = new JsonArray(
                    new JsonObject
                    {
                        ["identificationCode"] = "C-1",
                        ["courseIdentificationSystemDescriptor"] = Uri(CourseSystemType, "LEA course code"),
                    }
                ),
            }
        );

        foreach (string student in new[] { "STU-1", "STU-2" })
        {
            await PostAsync(
                harness,
                "/data/ed-fi/studentAcademicRecords",
                new JsonObject
                {
                    ["studentReference"] = new JsonObject { ["studentUniqueId"] = student },
                    ["educationOrganizationReference"] = new JsonObject
                    {
                        ["educationOrganizationId"] = Alpha,
                    },
                    ["schoolYearTypeReference"] = new JsonObject { ["schoolYear"] = SchoolYear },
                    ["termDescriptor"] = Uri("TermDescriptor", "Fall Semester"),
                }
            );
        }

        JsonObject first = Transcript("STU-1", "ROOT-ORG", RootCatalogUrl);
        first["alternativeCourseIdentificationCodes"] = new JsonArray(
            new JsonObject
            {
                ["identificationCode"] = "ALT-1",
                ["courseIdentificationSystemDescriptor"] = Uri(CourseSystemType, "LEA course code"),
                ["assigningOrganizationIdentificationCode"] = "COLL-ORG",
                ["courseCatalogURL"] = CollectionCatalogUrl,
            }
        );
        await PostAsync(harness, CourseTranscriptsEndpoint, first);
        await PostAsync(
            harness,
            CourseTranscriptsEndpoint,
            Transcript("STU-2", "COLL-ORG", CollectionCatalogUrl)
        );

        static JsonObject Transcript(string student, string organization, string catalogUrl) =>
            new()
            {
                ["courseAttemptResultDescriptor"] = Uri("CourseAttemptResultDescriptor", "Pass"),
                ["courseReference"] = new JsonObject
                {
                    ["courseCode"] = "C-1",
                    ["educationOrganizationId"] = Alpha,
                },
                ["studentAcademicRecordReference"] = new JsonObject
                {
                    ["educationOrganizationId"] = Alpha,
                    ["schoolYear"] = SchoolYear,
                    ["studentUniqueId"] = student,
                    ["termDescriptor"] = Uri("TermDescriptor", "Fall Semester"),
                },
                ["assigningOrganizationIdentificationCode"] = organization,
                ["courseCatalogURL"] = catalogUrl,
            };
    }

    private static async Task PostStudentsAsync(ApiIntegrationHarness harness)
    {
        foreach (string student in new[] { "STU-1", "STU-2" })
        {
            await PostAsync(
                harness,
                StudentsEndpoint,
                new JsonObject
                {
                    ["studentUniqueId"] = student,
                    ["firstName"] = student,
                    ["lastSurname"] = "Student",
                    ["birthDate"] = "2010-01-01",
                }
            );
        }
    }

    private static Task PostSchoolAsync(
        ApiIntegrationHarness harness,
        long schoolId,
        params (string Code, string System)[] codes
    ) =>
        PostAsync(
            harness,
            SchoolsEndpoint,
            new JsonObject
            {
                ["schoolId"] = schoolId,
                ["nameOfInstitution"] = $"School {schoolId}",
                ["educationOrganizationCategories"] = new JsonArray(
                    new JsonObject
                    {
                        ["educationOrganizationCategoryDescriptor"] = Uri(
                            "EducationOrganizationCategoryDescriptor",
                            "School"
                        ),
                    }
                ),
                ["gradeLevels"] = new JsonArray(
                    new JsonObject { ["gradeLevelDescriptor"] = Uri("GradeLevelDescriptor", "Tenth grade") }
                ),
                ["identificationCodes"] = new JsonArray([
                    .. codes.Select(code =>
                        (JsonNode)
                            new JsonObject
                            {
                                ["identificationCode"] = code.Code,
                                [EdOrgSystemParameter] = Uri(EdOrgSystemType, code.System),
                            }
                    ),
                ]),
            }
        );

    private static Task SeedDescriptorAsync(
        ApiIntegrationHarness harness,
        string descriptorType,
        string codeValue
    ) =>
        PostAsync(
            harness,
            $"/data/ed-fi/{char.ToLowerInvariant(descriptorType[0])}{descriptorType[1..]}s",
            new JsonObject
            {
                ["namespace"] = $"{DescriptorNamespace}/{descriptorType}",
                ["codeValue"] = codeValue,
                ["shortDescription"] = codeValue,
            }
        );

    private static async Task PostAsync(
        ApiIntegrationHarness harness,
        string endpoint,
        JsonObject payload,
        params HttpStatusCode[] accepted
    )
    {
        using StringContent content = new(payload.ToJsonString(), Encoding.UTF8, StandardJsonContentType);
        using HttpResponseMessage response = await harness.HttpClient.PostAsync(endpoint, content);
        string body = await response.Content.ReadAsStringAsync();

        response
            .StatusCode.Should()
            .BeOneOf(accepted.Length > 0 ? accepted : [HttpStatusCode.Created], $"POST {endpoint}: {body}");
    }

    // ---------------------------------------------------------------------------------------------
    // Matching
    // ---------------------------------------------------------------------------------------------

    public static async Task It_filters_schools_on_one_collection_element(ApiIntegrationHarness harness)
    {
        await AssertFilteredAsync(harness, SchoolsEndpoint, "identificationCode=A-1", SchoolKey, $"{Alpha}");
        await AssertFilteredAsync(
            harness,
            SchoolsEndpoint,
            "identificationCode=SHARED",
            SchoolKey,
            $"{Alpha}",
            $"{Beta}"
        );

        // Alpha has SHARED and an SEA system, but on different elements.
        await AssertFilteredAsync(
            harness,
            SchoolsEndpoint,
            $"identificationCode=SHARED&{EdOrgSystemParameter}={Escape(Uri(EdOrgSystemType, "SEA"))}",
            SchoolKey,
            $"{Beta}"
        );
        await AssertFilteredAsync(
            harness,
            SchoolsEndpoint,
            $"identificationCode=A-1&{EdOrgSystemParameter}={Escape(Uri(EdOrgSystemType, "NCES"))}",
            SchoolKey
        );

        // Filter names match case-insensitively.
        await AssertFilteredAsync(harness, SchoolsEndpoint, "IDENTIFICATIONCODE=A-1", SchoolKey, $"{Alpha}");
    }

    public static async Task It_returns_a_school_once_when_several_elements_match(
        ApiIntegrationHarness harness
    )
    {
        // Two of Alpha's elements carry SHARED; Alpha is still one result and counts once.
        await AssertFilteredAsync(
            harness,
            SchoolsEndpoint,
            "identificationCode=SHARED",
            SchoolKey,
            $"{Alpha}",
            $"{Beta}"
        );
        await AssertFilteredAsync(
            harness,
            SchoolsEndpoint,
            $"identificationCode=SHARED&{EdOrgSystemParameter}={Escape(Uri(EdOrgSystemType, "LEA"))}",
            SchoolKey,
            $"{Alpha}"
        );
    }

    public static async Task It_filters_staffs_by_assigning_organization_alone_and_with_a_code(
        ApiIntegrationHarness harness
    )
    {
        await AssertFilteredAsync(
            harness,
            StaffsEndpoint,
            "assigningOrganizationIdentificationCode=ORG-B",
            StaffKey,
            "STAFF-1",
            "STAFF-2"
        );
        await AssertFilteredAsync(
            harness,
            StaffsEndpoint,
            "assigningOrganizationIdentificationCode=ORG-B&identificationCode=ST-3",
            StaffKey,
            "STAFF-2"
        );
        await AssertFilteredAsync(
            harness,
            StaffsEndpoint,
            "assigningOrganizationIdentificationCode=ORG-A&identificationCode=ST-2",
            StaffKey
        );
        await AssertFilteredAsync(
            harness,
            StaffsEndpoint,
            $"identificationCode=ST-1&staffIdentificationSystemDescriptor={Escape(Uri(StaffSystemType, "State"))}",
            StaffKey,
            "STAFF-1"
        );
    }

    public static async Task It_filters_student_education_organization_associations_by_code_and_system(
        ApiIntegrationHarness harness
    )
    {
        await AssertFilteredAsync(
            harness,
            AssociationsEndpoint,
            "identificationCode=SC-1",
            StudentKey,
            "STU-1"
        );
        await AssertFilteredAsync(
            harness,
            AssociationsEndpoint,
            $"identificationCode=SC-2&studentIdentificationSystemDescriptor={Escape(Uri(StudentSystemType, "District"))}",
            StudentKey,
            "STU-2"
        );
        await AssertFilteredAsync(
            harness,
            AssociationsEndpoint,
            $"identificationCode=SC-1&studentIdentificationSystemDescriptor={Escape(Uri(StudentSystemType, "District"))}",
            StudentKey
        );
    }

    // ---------------------------------------------------------------------------------------------
    // Descriptors
    // ---------------------------------------------------------------------------------------------

    public static async Task It_returns_an_empty_page_for_an_unresolved_descriptor_uri(
        ApiIntegrationHarness harness
    )
    {
        await AssertFilteredAsync(
            harness,
            SchoolsEndpoint,
            $"{EdOrgSystemParameter}={Escape(Uri(EdOrgSystemType, "Unknown"))}",
            SchoolKey
        );
    }

    public static async Task It_returns_an_empty_page_for_a_descriptor_value_without_a_fragment(
        ApiIntegrationHarness harness
    )
    {
        await AssertFilteredAsync(
            harness,
            SchoolsEndpoint,
            $"{EdOrgSystemParameter}={Escape($"{DescriptorNamespace}/{EdOrgSystemType}")}",
            SchoolKey
        );
    }

    public static async Task It_rejects_a_descriptor_value_containing_nul(ApiIntegrationHarness harness)
    {
        string nulValue = $"{Escape(Uri(EdOrgSystemType, "SEA"))}%00";

        foreach (
            string requestUri in new[]
            {
                $"{SchoolsEndpoint}?{EdOrgSystemParameter}={nulValue}",
                // A filter that alone would empty the page does not hide the invalid value.
                $"{SchoolsEndpoint}?identificationCode=NONE&{EdOrgSystemParameter}={nulValue}",
                $"{SchoolsEndpoint}/partitions?number=2&{EdOrgSystemParameter}={nulValue}",
            }
        )
        {
            using HttpResponseMessage response = await harness.HttpClient.GetAsync(requestUri);
            string body = await response.Content.ReadAsStringAsync();

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, $"GET {requestUri}: {body}");
            JsonNode.Parse(body)!["validationErrors"]![EdOrgSystemPath]![0]!
                .GetValue<string>()
                .Should()
                .Be(DescriptorText.InvalidValueMessage, requestUri);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // CourseTranscript name collision
    // ---------------------------------------------------------------------------------------------

    public static async Task It_keeps_the_root_meaning_of_the_course_transcript_collision_names(
        ApiIntegrationHarness harness
    )
    {
        // As collection filters these would select STU-1; as root filters they select STU-2.
        await AssertFilteredAsync(
            harness,
            CourseTranscriptsEndpoint,
            "assigningOrganizationIdentificationCode=COLL-ORG",
            TranscriptKey,
            "STU-2"
        );
        await AssertFilteredAsync(
            harness,
            CourseTranscriptsEndpoint,
            $"courseCatalogURL={Escape(CollectionCatalogUrl)}",
            TranscriptKey,
            "STU-2"
        );
        await AssertFilteredAsync(
            harness,
            CourseTranscriptsEndpoint,
            "identificationCode=ALT-1&assigningOrganizationIdentificationCode=COLL-ORG",
            TranscriptKey
        );

        // The two collection names it does offer filter the alternative codes.
        await AssertFilteredAsync(
            harness,
            CourseTranscriptsEndpoint,
            $"identificationCode=ALT-1&courseIdentificationSystemDescriptor={Escape(Uri(CourseSystemType, "LEA course code"))}",
            TranscriptKey,
            "STU-1"
        );
        await AssertFilteredAsync(
            harness,
            CourseTranscriptsEndpoint,
            $"identificationCode=ALT-1&courseIdentificationSystemDescriptor={Escape(Uri(CourseSystemType, "State course code"))}",
            TranscriptKey
        );
    }

    // ---------------------------------------------------------------------------------------------
    // Paging, partitions and change-version windows
    // ---------------------------------------------------------------------------------------------

    public static async Task It_walks_only_the_filtered_set_with_page_tokens(ApiIntegrationHarness harness)
    {
        IReadOnlyList<string> shared = await IdsAsync(
            harness,
            $"{SchoolsEndpoint}?identificationCode=SHARED"
        );
        IReadOnlyList<string> nces = await IdsAsync(
            harness,
            $"{SchoolsEndpoint}?{EdOrgSystemParameter}={Escape(Uri(EdOrgSystemType, "NCES"))}"
        );

        shared.Should().HaveCount(2);
        nces.Should().HaveCount(1);

        (await WalkFromTokenAsync(harness, SchoolsEndpoint, EntryPageToken, 1, "&identificationCode=SHARED"))
            .Should()
            .Equal(shared, "Alpha, matched by two elements, is walked once");
        (
            await WalkFromTokenAsync(
                harness,
                SchoolsEndpoint,
                EntryPageToken,
                1,
                $"&{EdOrgSystemParameter}={Escape(Uri(EdOrgSystemType, "NCES"))}"
            )
        )
            .Should()
            .Equal(nces);
    }

    public static async Task It_partitions_only_the_filtered_set(ApiIntegrationHarness harness)
    {
        IReadOnlyList<string> shared = await IdsAsync(
            harness,
            $"{SchoolsEndpoint}?identificationCode=SHARED"
        );
        IReadOnlyList<string> all = await IdsAsync(harness, SchoolsEndpoint);

        all.Should().HaveCount(3, "the unfiltered collection also holds the school without codes");

        IReadOnlyList<string> pageTokens = await ReadPageTokensAsync(
            harness,
            $"{SchoolsEndpoint}/partitions?number=2&identificationCode=SHARED"
        );
        pageTokens.Should().NotBeEmpty();

        List<string> walked = [];
        foreach (string pageToken in pageTokens)
        {
            walked.AddRange(
                await WalkFromTokenAsync(harness, SchoolsEndpoint, pageToken, 1, "&identificationCode=SHARED")
            );
        }

        walked.Should().BeEquivalentTo(shared).And.OnlyHaveUniqueItems();

        (await ReadPageTokensAsync(harness, $"{SchoolsEndpoint}/partitions?number=2&identificationCode=NONE"))
            .Should()
            .BeEmpty("no school matches, so there is nothing to partition");
    }

    public static async Task It_combines_a_change_version_window_with_the_filter(
        ApiIntegrationHarness harness
    )
    {
        long seededThrough = await ReadNewestChangeVersionAsync(harness);
        await PostSchoolAsync(harness, Delta, ("SHARED", "SEA"));

        await AssertFilteredAsync(
            harness,
            SchoolsEndpoint,
            $"identificationCode=SHARED&minChangeVersion={seededThrough + 1}",
            SchoolKey,
            $"{Delta}"
        );
        await AssertFilteredAsync(
            harness,
            SchoolsEndpoint,
            $"identificationCode=SHARED&maxChangeVersion={seededThrough}",
            SchoolKey,
            $"{Alpha}",
            $"{Beta}"
        );
    }

    // ---------------------------------------------------------------------------------------------
    // Warnings and served metadata
    // ---------------------------------------------------------------------------------------------

    public static async Task It_warns_only_where_no_collection_qualifies(ApiIntegrationHarness harness)
    {
        using HttpResponseMessage response = await harness.HttpClient.GetAsync(
            $"{StudentsEndpoint}?identificationCode=SC-1&totalCount=true"
        );
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        JsonNode
            .Parse(body)!
            .AsArray()
            .Should()
            .HaveCount(2, "students has no identification codes to filter");
        AssertWarnsOf(response, "identificationCode");
    }

    public static async Task It_serves_metadata_publishing_the_accepted_parameters(
        ApiIntegrationHarness harness
    )
    {
        JsonNode specification = await GetJsonAsync(harness, ResourcesSpecification);

        foreach ((string path, string endpoint, string[] names) in _publishedParameters)
        {
            foreach (string operationPath in new[] { path, $"{path}/partitions" })
            {
                PublishedQueryNames(specification, operationPath)
                    .Should()
                    .Contain(names, "{0} publishes its identification-code parameters", operationPath);
            }

            foreach (string name in names)
            {
                string value = name.EndsWith("Descriptor", StringComparison.Ordinal)
                    ? Escape(Uri(EdOrgSystemType, "SEA"))
                    : "X";

                using HttpResponseMessage response = await harness.HttpClient.GetAsync(
                    $"{endpoint}?{name}={value}"
                );
                string body = await response.Content.ReadAsStringAsync();

                response.StatusCode.Should().Be(HttpStatusCode.OK, $"{endpoint} {name}: {body}");
                AssertNoWarning(response);
            }
        }

        PublishedQueryNames(specification, "/ed-fi/students")
            .Should()
            .NotContain("identificationCode", "students has no identification-code collection");
    }

    // ---------------------------------------------------------------------------------------------
    // Profiles
    // ---------------------------------------------------------------------------------------------

    public static async Task It_filters_through_a_profile_that_hides_the_collection(
        ApiIntegrationHarness harness
    )
    {
        foreach (
            (string query, long expected) in new[]
            {
                ("identificationCode=A-1", Alpha),
                (
                    $"identificationCode=SHARED&{EdOrgSystemParameter}={Escape(Uri(EdOrgSystemType, "SEA"))}",
                    Beta
                ),
            }
        )
        {
            using HttpRequestMessage request = new(
                HttpMethod.Get,
                $"{SchoolsEndpoint}?{query}&totalCount=true"
            );
            request.Headers.Accept.Add(MediaTypeWithQualityHeaderValue.Parse(ProfileReadableContentType));

            using HttpResponseMessage response = await harness.HttpClient.SendAsync(request);
            string body = await response.Content.ReadAsStringAsync();

            response.StatusCode.Should().Be(HttpStatusCode.OK, body);
            response.Headers.GetValues(TotalCountHeaderName).Single().Should().Be("1", query);
            AssertNoWarning(response);

            JsonArray items = JsonNode.Parse(body)!.AsArray();
            items.Select(item => SchoolKey(item!)).Should().Equal([$"{expected}"], query);
            items[0]!
                .AsObject()
                .Should()
                .NotContainKey("identificationCodes", "the profile hides the collection it filters on");
        }
    }

    public static async Task It_serves_the_parameters_in_the_profile_document(ApiIntegrationHarness harness)
    {
        JsonNode specification = await GetJsonAsync(harness, ProfileResourcesSpecification);

        specification["components"]!["schemas"]!["EdFi_School_readable"]!["properties"]!
            .AsObject()
            .Should()
            .NotContainKey("identificationCodes", "the profile document hides the collection");

        PublishedQueryNames(specification, "/ed-fi/schools")
            .Should()
            .Contain([EdOrgSystemParameter, "identificationCode"]);

        if (specification["paths"]!["/ed-fi/schools/partitions"] is not null)
        {
            PublishedQueryNames(specification, "/ed-fi/schools/partitions")
                .Should()
                .Contain([EdOrgSystemParameter, "identificationCode"]);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Document cache
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Puts the document cache into tracking and seeds the schools, so every write enqueues projection
    /// work, then drains that work so the schools are cache-resident.
    /// </summary>
    public static async Task SeedCacheResidentSchoolsAsync(ApiIntegrationHarness harness)
    {
        await ExecuteNonQueryAsync(
            harness,
            """
            UPDATE "dms"."DocumentCacheState"
            SET "ProjectionLifecycleState" = @lifecycleState,
                "CacheAheadRecoveryRequired" = @cacheAheadRecoveryRequired
            WHERE "StateId" = 1;
            """,
            ("@lifecycleState", "Tracking"),
            ("@cacheAheadRecoveryRequired", false)
        );

        IDocumentCacheProjectionSupervisor supervisor =
            harness.Services.GetRequiredService<IDocumentCacheProjectionSupervisor>();
        await supervisor.RefreshAsync(DocumentCacheTargetRefreshReason.SupervisorTriggered);

        await SeedSchoolsAsync(harness);

        IDocumentCacheProjectionDrainPageProcessor processor =
            harness.Services.GetRequiredService<IDocumentCacheProjectionDrainPageProcessor>();
        DocumentCacheProjectionTargetRuntimeContext targetContext = supervisor
            .CurrentTargetContexts.Should()
            .ContainSingle("the API harness configures one cache target")
            .Subject;

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(60));
        for (int page = 0; page < 100; page++)
        {
            DocumentCacheProjectionDrainPageResult result = await processor.ProcessPageAsync(
                new DocumentCacheProjectionDrainPageRequest(
                    targetContext,
                    DocumentCacheProjectionDrainInvocationKind.Ordinary
                ),
                timeout.Token
            );

            if (result.Outcome == DocumentCacheProjectionDrainPageOutcome.NoEligibleWork)
            {
                return;
            }

            result.Outcome.Should().Be(DocumentCacheProjectionDrainPageOutcome.PageProcessed);
            result.DocumentScopedFailureCount.Should().Be(0);
            result.AdministrativeFailure.Should().BeNull();
        }

        throw new InvalidOperationException("Document cache projection did not drain within 100 pages.");
    }

    public static async Task It_serves_a_filtered_page_from_the_cache(ApiIntegrationHarness harness)
    {
        DocumentCacheReadTelemetryRecorder recorder =
            harness.DocumentCacheReadTelemetryRecorder
            ?? throw new InvalidOperationException("The cache-hit proof requires read telemetry.");

        string requestUri = $"{SchoolsEndpoint}?identificationCode=SHARED&totalCount=true";
        int recordedBefore = recorder.TelemetryRecords.Count;

        using HttpResponseMessage response = await harness.HttpClient.GetAsync(requestUri);
        string body = await response.Content.ReadAsStringAsync();

        string[] events =
        [
            .. recorder
                .TelemetryRecords.Skip(recordedBefore)
                .Select(record => $"{record.EventName}:{record.Outcome}"),
        ];

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        events
            .Select(recorded => recorded.Split(':')[0])
            .Should()
            .Contain(["RecordAttempt", "RecordPageHit"], "the filtered page is served from the cache")
            .And.NotContain(
                ["RecordMiss", "RecordFallback", "RecordCacheUnavailable", "RecordUnexpectedException"],
                "no document on the filtered page falls back. Recorded: {0}",
                string.Join(", ", events)
            );

        JsonArray items = JsonNode.Parse(body)!.AsArray();
        items.Select(item => SchoolKey(item!)).Should().Equal($"{Alpha}", $"{Beta}");
        response.Headers.GetValues(TotalCountHeaderName).Single().Should().Be("2");

        // A cursor walk reads relationally, so the cached page must name the same documents.
        (await WalkFromTokenAsync(harness, SchoolsEndpoint, EntryPageToken, 1, "&identificationCode=SHARED"))
            .Should()
            .Equal(items.Select(item => item!["id"]!.GetValue<string>()));

        (await IdsAsync(harness, SchoolsEndpoint))
            .Should()
            .HaveCount(3, "without the filter the cached collection also returns the school without codes");
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Asserts that a GET-many with <paramref name="query"/> returns exactly <paramref name="expected"/>,
    /// in collection order, with a matching total count and no ignored-parameter warning.
    /// </summary>
    private static async Task AssertFilteredAsync(
        ApiIntegrationHarness harness,
        string endpoint,
        string query,
        Func<JsonNode, string> key,
        params string[] expected
    )
    {
        string requestUri = $"{endpoint}?{query}&totalCount=true";

        using HttpResponseMessage response = await harness.HttpClient.GetAsync(requestUri);
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, $"GET {requestUri}: {body}");

        // Scoped so that a failure reports the items, the count and any warning together.
        using AssertionScope scope = new();

        JsonNode
            .Parse(body)!
            .AsArray()
            .Select(item => key(item!))
            .Should()
            .Equal(expected, "GET {0} filters on the identification codes", requestUri);
        response
            .Headers.GetValues(TotalCountHeaderName)
            .Single()
            .Should()
            .Be($"{expected.Length}", requestUri);
        response.Headers.TryGetValues(
            IgnoredQueryParameterWarning.HeaderName,
            out IEnumerable<string>? warnings
        );
        warnings.Should().BeNull("GET {0} ignores none of its parameters", requestUri);
    }

    private static async Task<IReadOnlyList<string>> IdsAsync(
        ApiIntegrationHarness harness,
        string requestUri
    ) => (await ReadPageAsync(harness, requestUri)).DocumentIds;

    private static async Task<JsonNode> GetJsonAsync(ApiIntegrationHarness harness, string requestUri)
    {
        using HttpResponseMessage response = await harness.HttpClient.GetAsync(requestUri);
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, $"GET {requestUri}: {body}");

        return JsonNode.Parse(body)!;
    }

    private static IReadOnlyList<string> PublishedQueryNames(JsonNode specification, string path) =>
        specification["paths"]?[path]?["get"]?["parameters"] is JsonArray parameters
            ?
            [
                .. parameters
                    .OfType<JsonObject>()
                    .Where(parameter => parameter["in"]?.GetValue<string>() == "query")
                    .Select(parameter => parameter["name"]!.GetValue<string>()),
            ]
            : throw new InvalidOperationException($"The served document has no GET operation at '{path}'.");

    private static async Task ExecuteNonQueryAsync(
        ApiIntegrationHarness harness,
        string sql,
        params (string Name, object Value)[] parameters
    )
    {
        await using DbCommand command = harness.DbConnection.CreateCommand();
        command.CommandText = sql;

        foreach ((string name, object value) in parameters)
        {
            DbParameter parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        await command.ExecuteNonQueryAsync();
    }
}
