// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Middleware;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using static EdFi.DataManagementService.Core.Tests.Unit.TestHelper;

namespace EdFi.DataManagementService.Core.Tests.Unit.Middleware;

[TestFixture]
public class RefactoredDuplicateValidationIntegrationTests
{
    /// <summary>
    /// Schema that has both ArrayUniquenessConstraints and DocumentReferenceArrays
    /// to test the complete integration between the two new middlewares
    /// </summary>
    internal static ApiSchemaDocuments CombinedValidationSchemaDocuments()
    {
        var result = new ApiSchemaBuilder()
            .WithStartProject()
            .WithStartResource("Assessment")
            .WithStartDocumentPathsMapping()
            .WithDocumentPathReference(
                "ClassPeriod",
                [
                    new("$.classPeriodName", "$.classPeriods[*].classPeriodReference.classPeriodName"),
                    new("$.schoolId", "$.classPeriods[*].classPeriodReference.schoolId"),
                ]
            )
            .WithDocumentPathDescriptor(
                "AssessmentItemResultDescriptor",
                "$.items[*].assessmentItemResultDescriptor"
            )
            .WithEndDocumentPathsMapping()
            .WithStartArrayUniquenessConstraints()
            .WithArrayUniquenessConstraints(
                [
                    "$.performanceLevels[*].assessmentReportingMethodDescriptor",
                    "$.performanceLevels[*].performanceLevelDescriptor",
                ]
            )
            .WithArrayUniquenessConstraints(
                [
                    "$.items[*].assessmentItemReference.assessmentIdentifier",
                    "$.items[*].assessmentItemReference.identificationCode",
                    "$.items[*].assessmentItemReference.namespace",
                ]
            )
            .WithEndArrayUniquenessConstraints()
            .WithEndResource()
            .WithEndProject()
            .ToApiSchemaDocuments();

        return result;
    }

    internal PipelineContext CombinedValidationContext(FrontendRequest frontendRequest, RequestMethod method)
    {
        PipelineContext context = new(frontendRequest, method)
        {
            ApiSchemaDocuments = CombinedValidationSchemaDocuments(),
            PathComponents = new(
                ProjectNamespace: new("ed-fi"),
                EndpointName: new("assessments"),
                DocumentUuid: No.DocumentUuid
            ),
        };
        context.ProjectSchema = context.ApiSchemaDocuments.FindProjectSchemaForProjectNamespace(
            new("ed-fi")
        )!;
        context.ResourceSchema = new ResourceSchema(
            context.ProjectSchema.FindResourceSchemaNodeByEndpointName(new("assessments")) ?? new JsonObject()
        );

        if (context.FrontendRequest.Body != null)
        {
            var body = JsonNode.Parse(context.FrontendRequest.Body);
            if (body != null)
            {
                context.ParsedBody = body;
            }
        }

        return context;
    }

    internal static BuildResourceInfoMiddleware BuildResourceInfo()
    {
        return new BuildResourceInfoMiddleware(NullLogger.Instance, new List<string>());
    }

    internal static ExtractDocumentInfoMiddleware ExtractDocument()
    {
        return new ExtractDocumentInfoMiddleware(NullLogger.Instance);
    }

    internal static ArrayUniquenessValidationMiddleware ArrayUniquenessMiddleware()
    {
        return new ArrayUniquenessValidationMiddleware(NullLogger.Instance);
    }

    internal static DuplicateReferenceValidationMiddleware DuplicateReferenceMiddleware()
    {
        return new DuplicateReferenceValidationMiddleware(NullLogger.Instance);
    }

    internal static DisallowDuplicateReferencesMiddleware OriginalMiddleware()
    {
        return new DisallowDuplicateReferencesMiddleware(NullLogger.Instance);
    }

    [TestFixture]
    public class Given_Document_With_ArrayUniquenessConstraint_Violation_Should_Stop_Early
        : RefactoredDuplicateValidationIntegrationTests
    {
        private PipelineContext _refactoredContext = No.PipelineContext();
        private PipelineContext _originalContext = No.PipelineContext();

        [SetUp]
        public async Task Setup()
        {
            // Document that violates ArrayUniquenessConstraints but also has potential reference duplicates
            string jsonBody = """
                {
                 "assessmentIdentifier": "01774fa3-06f1-47fe-8801-c8b1e65057f2",
                 "namespace": "uri://ed-fi.org/Assessment/Assessment.xml",
                 "assessmentTitle": "3rd Grade Reading 1st Six Weeks 2021-2022",
                 "performanceLevels": [
                   {
                     "performanceLevelDescriptor": "uri://ed-fi.org/PerformanceLevelDescriptor#Advanced",
                     "assessmentReportingMethodDescriptor": "uri://ed-fi.org/AssessmentReportingMethodDescriptor#Scale score",
                     "minimumScore": "23",
                     "maximumScore": "26"
                   },
                   {
                     "performanceLevelDescriptor": "uri://ed-fi.org/PerformanceLevelDescriptor#Advanced",
                     "assessmentReportingMethodDescriptor": "uri://ed-fi.org/AssessmentReportingMethodDescriptor#Scale score",
                     "minimumScore": "27",
                     "maximumScore": "30"
                   }
                 ],
                 "classPeriods": [
                   {
                     "classPeriodReference": {
                       "classPeriodName": "01 - Traditional",
                       "schoolId": 1
                     }
                   },
                   {
                     "classPeriodReference": {
                       "classPeriodName": "01 - Traditional",
                       "schoolId": 1
                     }
                   }
                 ]
                }
                """;

            FrontendRequest frontEndRequest = new(
                Path: "ed-fi/assessments",
                Body: jsonBody,
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId(""),
                new ClientAuthorizations(
                    TokenId: "",
                    ClaimSetName: "",
                    EducationOrganizationIds: [],
                    NamespacePrefixes: []
                )
            );

            // Test refactored pipeline
            _refactoredContext = CombinedValidationContext(frontEndRequest, RequestMethod.POST);
            await BuildResourceInfo().Execute(_refactoredContext, NullNext);
            await ExtractDocument().Execute(_refactoredContext, NullNext);

            // ArrayUniqueness should run first and detect the performanceLevels violation
            await ArrayUniquenessMiddleware().Execute(_refactoredContext, NullNext);

            // If ArrayUniqueness found an error, don't run DuplicateReference
            if (_refactoredContext.FrontendResponse.StatusCode == 200)
            {
                await DuplicateReferenceMiddleware().Execute(_refactoredContext, NullNext);
            }

            // Test original middleware for comparison
            _originalContext = CombinedValidationContext(frontEndRequest, RequestMethod.POST);
            await BuildResourceInfo().Execute(_originalContext, NullNext);
            await ExtractDocument().Execute(_originalContext, NullNext);
            await OriginalMiddleware().Execute(_originalContext, NullNext);
        }

        [Test]
        public void Both_Return_Same_Status_Code()
        {
            _refactoredContext.FrontendResponse.StatusCode.Should().Be(400);
            _originalContext.FrontendResponse.StatusCode.Should().Be(400);
            _refactoredContext
                .FrontendResponse.StatusCode.Should()
                .Be(_originalContext.FrontendResponse.StatusCode);
        }

        [Test]
        public void Both_Return_Same_Error_Message()
        {
            var refactoredError = _refactoredContext.FrontendResponse.Body?.ToJsonString();
            var originalError = _originalContext.FrontendResponse.Body?.ToJsonString();

            refactoredError.Should().Be(originalError);

            // Both should report the ArrayUniquenessConstraint violation (not the reference duplicate)
            refactoredError.Should().Contain("performanceLevels");
            refactoredError.Should().Contain("2nd item");
        }
    }

    [TestFixture]
    public class Given_Document_With_Only_Reference_Duplicates : RefactoredDuplicateValidationIntegrationTests
    {
        private PipelineContext _refactoredContext = No.PipelineContext();
        private PipelineContext _originalContext = No.PipelineContext();

        [SetUp]
        public async Task Setup()
        {
            // Document that has reference duplicates but no ArrayUniquenessConstraint violations
            string jsonBody = """
                {
                 "assessmentIdentifier": "01774fa3-06f1-47fe-8801-c8b1e65057f2",
                 "namespace": "uri://ed-fi.org/Assessment/Assessment.xml",
                 "assessmentTitle": "3rd Grade Reading 1st Six Weeks 2021-2022",
                 "performanceLevels": [
                   {
                     "performanceLevelDescriptor": "uri://ed-fi.org/PerformanceLevelDescriptor#Advanced",
                     "assessmentReportingMethodDescriptor": "uri://ed-fi.org/AssessmentReportingMethodDescriptor#Scale score",
                     "minimumScore": "23",
                     "maximumScore": "26"
                   },
                   {
                     "performanceLevelDescriptor": "uri://ed-fi.org/PerformanceLevelDescriptor#Proficient",
                     "assessmentReportingMethodDescriptor": "uri://ed-fi.org/AssessmentReportingMethodDescriptor#Raw score",
                     "minimumScore": "15",
                     "maximumScore": "22"
                   }
                 ],
                 "classPeriods": [
                   {
                     "classPeriodReference": {
                       "classPeriodName": "01 - Traditional",
                       "schoolId": 1
                     }
                   },
                   {
                     "classPeriodReference": {
                       "classPeriodName": "01 - Traditional",
                       "schoolId": 1
                     }
                   }
                 ]
                }
                """;

            FrontendRequest frontEndRequest = new(
                Path: "ed-fi/assessments",
                Body: jsonBody,
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId(""),
                new ClientAuthorizations(
                    TokenId: "",
                    ClaimSetName: "",
                    EducationOrganizationIds: [],
                    NamespacePrefixes: []
                )
            );

            // Test refactored pipeline
            _refactoredContext = CombinedValidationContext(frontEndRequest, RequestMethod.POST);
            await BuildResourceInfo().Execute(_refactoredContext, NullNext);
            await ExtractDocument().Execute(_refactoredContext, NullNext);
            await ArrayUniquenessMiddleware().Execute(_refactoredContext, NullNext);
            await DuplicateReferenceMiddleware().Execute(_refactoredContext, NullNext);

            // Test original middleware for comparison
            _originalContext = CombinedValidationContext(frontEndRequest, RequestMethod.POST);
            await BuildResourceInfo().Execute(_originalContext, NullNext);
            await ExtractDocument().Execute(_originalContext, NullNext);
            await OriginalMiddleware().Execute(_originalContext, NullNext);
        }

        [Test]
        public void Both_Return_Same_Status_Code()
        {
            _refactoredContext.FrontendResponse.StatusCode.Should().Be(400);
            _originalContext.FrontendResponse.StatusCode.Should().Be(400);
            _refactoredContext
                .FrontendResponse.StatusCode.Should()
                .Be(_originalContext.FrontendResponse.StatusCode);
        }

        [Test]
        public void Both_Return_Same_Error_Message()
        {
            var refactoredError = _refactoredContext.FrontendResponse.Body?.ToJsonString();
            var originalError = _originalContext.FrontendResponse.Body?.ToJsonString();

            refactoredError.Should().Be(originalError);

            // Both should report the reference duplicate
            refactoredError.Should().Contain("classPeriods");
            refactoredError.Should().Contain("2nd item");
        }
    }

    [TestFixture]
    public class Given_Valid_Document_With_No_Duplicates : RefactoredDuplicateValidationIntegrationTests
    {
        private PipelineContext _refactoredContext = No.PipelineContext();
        private PipelineContext _originalContext = No.PipelineContext();

        [SetUp]
        public async Task Setup()
        {
            // Document with no violations
            string jsonBody = """
                {
                 "assessmentIdentifier": "01774fa3-06f1-47fe-8801-c8b1e65057f2",
                 "namespace": "uri://ed-fi.org/Assessment/Assessment.xml",
                 "assessmentTitle": "3rd Grade Reading 1st Six Weeks 2021-2022",
                 "performanceLevels": [
                   {
                     "performanceLevelDescriptor": "uri://ed-fi.org/PerformanceLevelDescriptor#Advanced",
                     "assessmentReportingMethodDescriptor": "uri://ed-fi.org/AssessmentReportingMethodDescriptor#Scale score",
                     "minimumScore": "23",
                     "maximumScore": "26"
                   },
                   {
                     "performanceLevelDescriptor": "uri://ed-fi.org/PerformanceLevelDescriptor#Proficient",
                     "assessmentReportingMethodDescriptor": "uri://ed-fi.org/AssessmentReportingMethodDescriptor#Raw score",
                     "minimumScore": "15",
                     "maximumScore": "22"
                   }
                 ],
                 "classPeriods": [
                   {
                     "classPeriodReference": {
                       "classPeriodName": "01 - Traditional",
                       "schoolId": 1
                     }
                   },
                   {
                     "classPeriodReference": {
                       "classPeriodName": "02 - Block",
                       "schoolId": 1
                     }
                   }
                 ]
                }
                """;

            FrontendRequest frontEndRequest = new(
                Path: "ed-fi/assessments",
                Body: jsonBody,
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId(""),
                new ClientAuthorizations(
                    TokenId: "",
                    ClaimSetName: "",
                    EducationOrganizationIds: [],
                    NamespacePrefixes: []
                )
            );

            // Test refactored pipeline
            _refactoredContext = CombinedValidationContext(frontEndRequest, RequestMethod.POST);
            await BuildResourceInfo().Execute(_refactoredContext, NullNext);
            await ExtractDocument().Execute(_refactoredContext, NullNext);
            await ArrayUniquenessMiddleware().Execute(_refactoredContext, NullNext);
            await DuplicateReferenceMiddleware().Execute(_refactoredContext, NullNext);

            // Test original middleware for comparison
            _originalContext = CombinedValidationContext(frontEndRequest, RequestMethod.POST);
            await BuildResourceInfo().Execute(_originalContext, NullNext);
            await ExtractDocument().Execute(_originalContext, NullNext);
            await OriginalMiddleware().Execute(_originalContext, NullNext);
        }

        [Test]
        public void Both_Continue_To_Next_Middleware()
        {
            _refactoredContext.FrontendResponse.Should().Be(No.FrontendResponse);
            _originalContext.FrontendResponse.Should().Be(No.FrontendResponse);
        }
    }
}
