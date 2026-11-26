// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Middleware;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Telemetry;
using EdFi.DataManagementService.Core.Tests.Unit.ApiSchema;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using static EdFi.DataManagementService.Core.Tests.Unit.TestHelper;

namespace EdFi.DataManagementService.Core.Tests.Unit.Middleware;

/// <summary>
/// Identification-code query parameters on GET-many and partition requests over the packaged Data
/// Standard 5.2 schema: they become filters on the collection item path, are validated and reported like
/// root filters, and stay ignored, with a warning, where a resource has no identification-code collection.
/// </summary>
[TestFixture]
[Parallelizable]
public class IdentificationCodeQueryValidationTests
{
    private const string SchoolCodePath = "$.identificationCodes[*].identificationCode";
    private const string SchoolSystemPath =
        "$.identificationCodes[*].educationOrganizationIdentificationSystemDescriptor";

    private static readonly Lazy<ApiSchemaDocuments> _apiSchemaDocuments = new(() =>
        new ApiSchemaDocuments(
            new ApiSchemaDocumentNodes(
                PackagedApiSchemaContract.LoadPackagedRootNode("DataStandard52ApiSchemaPackageRoot"),
                []
            ),
            NullLogger.Instance
        )
    );

    private static RequestInfo RequestInfoFor(
        string endpointName,
        ResourcePathOperation operation,
        params (string Key, string Value)[] queryParameters
    )
    {
        FrontendRequest frontendRequest = new(
            Path: $"/ed-fi/{endpointName}",
            Body: null,
            Form: null,
            Headers: [],
            QueryParameters: queryParameters.ToDictionary(
                static parameter => parameter.Key,
                static parameter => parameter.Value,
                StringComparer.Ordinal
            ),
            TraceId: new TraceId(""),
            RouteQualifiers: []
        );

        RequestInfo requestInfo = new(
            frontendRequest,
            RequestMethod.GET,
            ServiceProviderWithEffectiveTarget()
        )
        {
            ApiSchemaDocuments = _apiSchemaDocuments.Value,
            PathComponents = new(
                ProjectEndpointName: new("ed-fi"),
                EndpointName: new(endpointName),
                Operation: operation
            ),
        };
        requestInfo.ProjectSchema = requestInfo.ApiSchemaDocuments.FindProjectSchemaForProjectNamespace(
            new("ed-fi")
        )!;
        requestInfo.ResourceSchema = new ResourceSchema(
            requestInfo.ProjectSchema.FindResourceSchemaNodeByEndpointName(new(endpointName))!
        );

        return requestInfo;
    }

    private static string? WarningOf(RequestInfo requestInfo) =>
        requestInfo.FrontendResponse.Headers.TryGetValue(
            IgnoredQueryParameterWarning.HeaderName,
            out string? value
        )
            ? value
            : null;

    private static (string Name, string[] Paths, string Value, string Type)[] ElementsOf(
        RequestInfo requestInfo
    ) =>
        [
            .. requestInfo.QueryElements.Select(static element =>
                (
                    element.QueryFieldName,
                    element.DocumentPaths.Select(static path => path.Value).ToArray(),
                    element.Value,
                    element.Type
                )
            ),
        ];

    [TestFixture]
    [Parallelizable]
    public class Given_A_Get_Many_Request_With_Identification_Code_Parameters
        : IdentificationCodeQueryValidationTests
    {
        /// <summary>
        /// Runs the live GET-many validation step and records whether the request reached the query.
        /// </summary>
        private static async Task<(RequestInfo RequestInfo, bool QueryRan)> GetManyAsync(
            string endpointName,
            params (string Key, string Value)[] queryParameters
        )
        {
            RequestInfo requestInfo = RequestInfoFor(
                endpointName,
                ResourcePathOperation.Collection.Instance,
                queryParameters
            );
            bool queryRan = false;

            await ValidateQueryMiddlewareTests
                .Middleware()
                .Execute(
                    requestInfo,
                    () =>
                    {
                        queryRan = true;
                        requestInfo.FrontendResponse = new FrontendResponse(
                            StatusCode: 200,
                            Body: null,
                            Headers: []
                        );
                        return Task.CompletedTask;
                    }
                );

            return (requestInfo, queryRan);
        }

        [Test]
        public async Task It_filters_on_the_collection_item_paths_without_a_warning()
        {
            var (requestInfo, queryRan) = await GetManyAsync(
                "schools",
                ("identificationCode", "C-100"),
                ("educationOrganizationIdentificationSystemDescriptor", "uri://ed-fi.org/X#NCES")
            );

            queryRan.Should().BeTrue();
            WarningOf(requestInfo).Should().BeNull();
            ElementsOf(requestInfo)
                .Should()
                .BeEquivalentTo(
                    [
                        ("identificationCode", new[] { SchoolCodePath }, "C-100", "string"),
                        (
                            "educationOrganizationIdentificationSystemDescriptor",
                            new[] { SchoolSystemPath },
                            "uri://ed-fi.org/X#NCES",
                            "string"
                        ),
                    ],
                    options => options.WithStrictOrdering()
                );
        }

        /// <summary>
        /// The element keeps the name as the client spelled it, as for every filter; the backend resolves
        /// it case-insensitively.
        /// </summary>
        [TestCase("IDENTIFICATIONCODE")]
        [TestCase("IdentificationCode")]
        public async Task It_matches_the_parameter_name_case_insensitively(string parameterName)
        {
            var (requestInfo, queryRan) = await GetManyAsync("schools", (parameterName, "C-100"));

            queryRan.Should().BeTrue();
            WarningOf(requestInfo).Should().BeNull();
            ElementsOf(requestInfo)
                .Should()
                .BeEquivalentTo([(parameterName, new[] { SchoolCodePath }, "C-100", "string")]);
        }

        [Test]
        public async Task It_rejects_a_value_over_the_item_max_length_keyed_by_the_item_path()
        {
            var tooLong = new string('x', 61);

            var (requestInfo, queryRan) = await GetManyAsync("schools", ("identificationCode", tooLong));

            queryRan.Should().BeFalse();
            requestInfo.FrontendResponse.StatusCode.Should().Be(400);
            requestInfo.FrontendResponse.Body!["validationErrors"]!
                .AsObject()
                .Select(static error => error.Key)
                .Should()
                .Equal(SchoolCodePath);
            requestInfo.FrontendResponse.Body!["validationErrors"]![SchoolCodePath]!
                .AsArray()
                .Select(static message => message!.GetValue<string>())
                .Should()
                .Equal($"The value '{tooLong}' is not valid for identificationCode.");
        }

        [Test]
        public async Task It_passes_a_value_at_the_item_max_length_to_the_query()
        {
            var atBound = new string('x', 60);

            var (requestInfo, queryRan) = await GetManyAsync("schools", ("identificationCode", atBound));

            queryRan.Should().BeTrue();
            ElementsOf(requestInfo)
                .Should()
                .BeEquivalentTo([("identificationCode", new[] { SchoolCodePath }, atBound, "string")]);
        }

        /// <summary>
        /// An empty value is passed on as an empty-string filter, exactly as an empty root string filter is.
        /// </summary>
        [Test]
        public async Task It_passes_an_empty_value_to_the_query_as_a_root_string_filter_is()
        {
            var (requestInfo, queryRan) = await GetManyAsync(
                "schools",
                ("nameOfInstitution", ""),
                ("identificationCode", ""),
                ("educationOrganizationIdentificationSystemDescriptor", "")
            );

            queryRan.Should().BeTrue();
            WarningOf(requestInfo).Should().BeNull();
            ElementsOf(requestInfo)
                .Select(static element => (element.Name, element.Value, element.Type))
                .Should()
                .Equal(
                    ("nameOfInstitution", "", "string"),
                    ("identificationCode", "", "string"),
                    ("educationOrganizationIdentificationSystemDescriptor", "", "string")
                );
        }

        [Test]
        public async Task It_ignores_and_reports_the_parameter_on_a_resource_without_an_identification_code_collection()
        {
            var (requestInfo, queryRan) = await GetManyAsync("students", ("identificationCode", "X"));

            queryRan.Should().BeTrue();
            requestInfo.QueryElements.Should().BeEmpty();
            WarningOf(requestInfo).Should().Be("Ignored query parameters: identificationCode");
        }

        [TestCase("courseCatalogURL", "$.courseCatalogURL")]
        [TestCase("assigningOrganizationIdentificationCode", "$.assigningOrganizationIdentificationCode")]
        public async Task It_keeps_the_root_path_of_a_course_transcript_name_the_mapping_already_has(
            string queryFieldName,
            string rootPath
        )
        {
            var (requestInfo, queryRan) = await GetManyAsync("courseTranscripts", (queryFieldName, "X"));

            queryRan.Should().BeTrue();
            WarningOf(requestInfo).Should().BeNull();
            ElementsOf(requestInfo)
                .Should()
                .BeEquivalentTo([(queryFieldName, new[] { rootPath }, "X", "string")]);
        }

        [Test]
        public async Task It_filters_a_course_transcript_on_its_alternative_course_identification_codes()
        {
            var (requestInfo, queryRan) = await GetManyAsync(
                "courseTranscripts",
                ("identificationCode", "X")
            );

            queryRan.Should().BeTrue();
            WarningOf(requestInfo).Should().BeNull();
            ElementsOf(requestInfo)
                .Should()
                .BeEquivalentTo([
                    (
                        "identificationCode",
                        new[] { "$.alternativeCourseIdentificationCodes[*].identificationCode" },
                        "X",
                        "string"
                    ),
                ]);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Partition_Request_With_Identification_Code_Parameters
        : IdentificationCodeQueryValidationTests
    {
        private static async Task<RequestInfo> PartitionsAsync(
            params (string Key, string Value)[] queryParameters
        )
        {
            RequestInfo requestInfo = RequestInfoFor(
                "schools",
                ResourcePathOperation.Partitions.Instance,
                queryParameters
            );

            IPipelineStep middleware = new ValidatePartitionQueryMiddleware(
                NullLogger.Instance,
                12,
                NoOpCollectionPagingTelemetry.Instance,
                _useLegacyDocumentIdOrderingForChangeQueries: false
            );

            await middleware.Execute(requestInfo, NullNext);

            return requestInfo;
        }

        [Test]
        public async Task It_accepts_the_parameters_as_filters_without_a_warning()
        {
            var requestInfo = await PartitionsAsync(
                ("identificationCode", "C-100"),
                ("EducationOrganizationIdentificationSystemDescriptor", "uri://ed-fi.org/X#NCES")
            );

            WarningOf(requestInfo).Should().BeNull();
            ElementsOf(requestInfo)
                .Should()
                .BeEquivalentTo(
                    [
                        ("identificationCode", new[] { SchoolCodePath }, "C-100", "string"),
                        (
                            "EducationOrganizationIdentificationSystemDescriptor",
                            new[] { SchoolSystemPath },
                            "uri://ed-fi.org/X#NCES",
                            "string"
                        ),
                    ],
                    options => options.WithStrictOrdering()
                );
        }

        [Test]
        public async Task It_rejects_a_value_over_the_item_max_length_keyed_by_the_item_path()
        {
            var requestInfo = await PartitionsAsync(("identificationCode", new string('x', 61)));

            requestInfo.FrontendResponse.StatusCode.Should().Be(400);
            requestInfo.FrontendResponse.Body!["validationErrors"]!
                .AsObject()
                .Select(static error => error.Key)
                .Should()
                .Equal(SchoolCodePath);
        }
    }
}
