// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Profile;
using EdFi.DataManagementService.Core.Security;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_Descriptor_Read_Contracts
{
    private static readonly QualifiedResourceName _descriptorResource = new("Ed-Fi", "SchoolTypeDescriptor");

    [Test]
    public void It_carries_descriptor_get_inputs_without_widening_the_public_get_contract()
    {
        var mappingSet = RelationalAccessTestData.CreateMappingSet(
            new QualifiedResourceName("Ed-Fi", "Student")
        );
        var descriptorResourceModel = mappingSet.GetConcreteResourceModelOrThrow(_descriptorResource);
        AuthorizationStrategyEvaluator[] authorizationStrategyEvaluators =
        [
            new AuthorizationStrategyEvaluator(
                AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired,
                [],
                FilterOperator.Or
            ),
        ];
        var readableProfileProjectionContext = CreateReadableProfileProjectionContext();
        var traceId = new TraceId("trace-id");
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));

        var request = new DescriptorGetByIdRequest(
            mappingSet,
            descriptorResourceModel,
            _descriptorResource,
            documentUuid,
            RelationalGetRequestReadMode.StoredDocument,
            authorizationStrategyEvaluators,
            readableProfileProjectionContext,
            traceId
        );

        request.Should().NotBeAssignableTo<IGetRequest>();
        request.MappingSet.Should().BeSameAs(mappingSet);
        request.DescriptorResourceModel.Should().BeSameAs(descriptorResourceModel);
        request.Resource.Should().Be(_descriptorResource);
        request.DocumentUuid.Should().Be(documentUuid);
        request.ReadMode.Should().Be(RelationalGetRequestReadMode.StoredDocument);
        request.AuthorizationStrategyEvaluators.Should().BeSameAs(authorizationStrategyEvaluators);
        request.ReadableProfileProjectionContext.Should().BeSameAs(readableProfileProjectionContext);
        request.TraceId.Should().Be(traceId);
    }

    [Test]
    public void It_carries_descriptor_query_inputs_without_widening_the_public_query_contract()
    {
        var mappingSet = RelationalAccessTestData.CreateMappingSet(
            new QualifiedResourceName("Ed-Fi", "Student")
        );
        var descriptorResourceModel = mappingSet.GetConcreteResourceModelOrThrow(_descriptorResource);
        QueryElement[] queryElements =
        [
            new QueryElement(
                "namespace",
                [new JsonPath("$.namespace")],
                "uri://ed-fi.org/SchoolTypeDescriptor",
                "string"
            ),
        ];
        var paginationParameters = new PaginationParameters(
            Limit: 25,
            Offset: 10,
            TotalCount: true,
            MaximumPageSize: 500
        );
        AuthorizationStrategyEvaluator[] authorizationStrategyEvaluators =
        [
            new AuthorizationStrategyEvaluator(
                AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired,
                [],
                FilterOperator.Or
            ),
        ];
        var readableProfileProjectionContext = CreateReadableProfileProjectionContext();
        var traceId = new TraceId("trace-id");

        var request = new DescriptorQueryRequest(
            mappingSet,
            descriptorResourceModel,
            _descriptorResource,
            queryElements,
            paginationParameters,
            authorizationStrategyEvaluators,
            readableProfileProjectionContext,
            traceId
        );

        request.Should().NotBeAssignableTo<IQueryRequest>();
        request.MappingSet.Should().BeSameAs(mappingSet);
        request.DescriptorResourceModel.Should().BeSameAs(descriptorResourceModel);
        request.Resource.Should().Be(_descriptorResource);
        request.QueryElements.Should().BeSameAs(queryElements);
        request.PaginationParameters.Should().BeSameAs(paginationParameters);
        request.AuthorizationStrategyEvaluators.Should().BeSameAs(authorizationStrategyEvaluators);
        request.ReadableProfileProjectionContext.Should().BeSameAs(readableProfileProjectionContext);
        request.TraceId.Should().Be(traceId);
    }

    private static ReadableProfileProjectionContext CreateReadableProfileProjectionContext() =>
        new(
            new ContentTypeDefinition(
                MemberSelection.IncludeOnly,
                [new PropertyRule("namespace"), new PropertyRule("codeValue")],
                [],
                [],
                []
            ),
            new HashSet<string>(StringComparer.Ordinal) { "namespace", "codeValue", "id" }
        );
}
