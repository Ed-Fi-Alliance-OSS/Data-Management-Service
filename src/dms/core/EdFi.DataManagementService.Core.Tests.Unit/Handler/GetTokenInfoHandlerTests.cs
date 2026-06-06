// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Claims;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Handler;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Profile;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Core.Security.Model;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Handler;

[TestFixture]
public class Given_GetTokenInfoHandler
{
    private const string ClaimSetName = "TestClaimSet";
    private const string ClientId = "test-client";
    private const string TokenId = "test-token-id";
    private const string TokenFromBody = "body-token";

    private IJwtValidationService _jwtValidationService = null!;
    private IClaimSetProvider _claimSetProvider = null!;
    private IProfileService _profileService = null!;
    private ITokenInfoRelationalMappingSetResolver _tokenInfoRelationalMappingSetResolver = null!;

    [SetUp]
    public void Setup()
    {
        _jwtValidationService = A.Fake<IJwtValidationService>();
        _claimSetProvider = A.Fake<IClaimSetProvider>();
        _profileService = A.Fake<IProfileService>();
        _tokenInfoRelationalMappingSetResolver = A.Fake<ITokenInfoRelationalMappingSetResolver>();

        A.CallTo(() => _claimSetProvider.GetAllClaimSets(A<string?>._))
            .Returns([new ClaimSet(ClaimSetName, [])]);

        A.CallTo(() => _profileService.GetOrFetchApplicationProfilesAsync(A<long>._, A<string?>._))
            .Returns(
                Task.FromResult(
                    new CachedApplicationProfiles(
                        new Dictionary<long, string> { [1] = "Writer Profile", [2] = "Reader Profile" }
                    )
                )
            );
    }

    [Test]
    public async Task It_uses_token_info_education_organization_lookup_from_request_scope()
    {
        var tokenInfoEducationOrganizationLookup = A.Fake<ITokenInfoEducationOrganizationLookup>();
        IEnumerable<TokenInfoEducationOrganization> educationOrganizationRows =
        [
            new(
                EducationOrganizationId: 255901,
                NameOfInstitution: "Grand Bend School",
                Discriminator: "School",
                AncestorDiscriminator: "LocalEducationAgency",
                AncestorEducationOrganizationId: 255901001
            ),
            new(
                EducationOrganizationId: 255901,
                NameOfInstitution: "Grand Bend School",
                Discriminator: "School",
                AncestorDiscriminator: "School",
                AncestorEducationOrganizationId: 255901
            ),
        ];

        A.CallTo(() =>
                tokenInfoEducationOrganizationLookup.GetEducationOrganizations(
                    A<IReadOnlyCollection<EducationOrganizationId>>.That.Matches(ids =>
                        ids.Single().Value == 255901
                    )
                )
            )
            .Returns(Task.FromResult(educationOrganizationRows));

        var clientAuthorizations = CreateClientAuthorizations([new EducationOrganizationId(255901)]);
        ConfigureJwtValidation(clientAuthorizations);

        RequestInfo requestInfo = CreateRequestInfo(
            clientAuthorizations,
            CreateScopedServiceProvider(tokenInfoEducationOrganizationLookup)
        );

        await Execute(requestInfo);

        requestInfo.FrontendResponse.StatusCode.Should().Be(200);
        JsonNode body = requestInfo.FrontendResponse.Body!;
        body["claim_set"]!["name"]!.GetValue<string>().Should().Be(ClaimSetName);
        body["namespace_prefixes"]!.AsArray()[0]!.GetValue<string>().Should().Be("uri://ed-fi");
        body["assigned_profiles"]!
            .AsArray()
            .Select(profile => profile!.GetValue<string>())
            .Should()
            .Equal("Reader Profile", "Writer Profile");
        body["resources"]!.AsArray().Should().BeEmpty();
        body["services"]!.AsArray().Should().BeEmpty();

        JsonNode educationOrganization = body["education_organizations"]!.AsArray().Single()!;
        educationOrganization["education_organization_id"]!.GetValue<long>().Should().Be(255901);
        educationOrganization["name_of_institution"]!.GetValue<string>().Should().Be("Grand Bend School");
        educationOrganization["type"]!.GetValue<string>().Should().Be("edfi.School");
        educationOrganization["local_education_agency_id"]!.GetValue<long>().Should().Be(255901001);
        educationOrganization["school_id"]!.GetValue<long>().Should().Be(255901);

        A.CallTo(() =>
                tokenInfoEducationOrganizationLookup.GetEducationOrganizations(
                    A<IReadOnlyCollection<EducationOrganizationId>>._
                )
            )
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => _tokenInfoRelationalMappingSetResolver.ResolveAsync(A<RequestInfo>._))
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_returns_empty_education_organizations_without_resolving_lookup_for_empty_claims()
    {
        var clientAuthorizations = CreateClientAuthorizations([]);
        ConfigureJwtValidation(clientAuthorizations);

        RequestInfo requestInfo = CreateRequestInfo(
            clientAuthorizations,
            CreateScopedServiceProvider(tokenInfoEducationOrganizationLookup: null)
        );

        await Execute(requestInfo);

        requestInfo.FrontendResponse.StatusCode.Should().Be(200);
        JsonNode body = requestInfo.FrontendResponse.Body!;
        body["education_organizations"]!.AsArray().Should().BeEmpty();
        body["claim_set"].Should().NotBeNull();
        body["namespace_prefixes"].Should().NotBeNull();
        body["resources"].Should().NotBeNull();
        body["services"].Should().NotBeNull();
        body["assigned_profiles"].Should().NotBeNull();
        A.CallTo(() => _tokenInfoRelationalMappingSetResolver.ResolveAsync(A<RequestInfo>._))
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_resolves_mapping_set_for_relational_lookup()
    {
        var relationalLookup = A.Fake<IRelationalTokenInfoEducationOrganizationLookup>();
        var mappingSet = CreateMappingSet();
        IEnumerable<TokenInfoEducationOrganization> educationOrganizationRows =
        [
            new(
                EducationOrganizationId: 255901,
                NameOfInstitution: "Grand Bend School",
                Discriminator: "School",
                AncestorDiscriminator: "School",
                AncestorEducationOrganizationId: 255901
            ),
        ];

        A.CallTo(() => _tokenInfoRelationalMappingSetResolver.ResolveAsync(A<RequestInfo>._))
            .Returns(new TokenInfoRelationalMappingSetResolutionResult(true, mappingSet));

        A.CallTo(() =>
                relationalLookup.GetEducationOrganizations(
                    A<IReadOnlyCollection<EducationOrganizationId>>.That.Matches(ids =>
                        ids.Single().Value == 255901
                    ),
                    mappingSet
                )
            )
            .Returns(Task.FromResult(educationOrganizationRows));

        var clientAuthorizations = CreateClientAuthorizations([new EducationOrganizationId(255901)]);
        ConfigureJwtValidation(clientAuthorizations);

        RequestInfo requestInfo = CreateRequestInfo(
            clientAuthorizations,
            CreateScopedServiceProvider(relationalLookup)
        );

        await Execute(requestInfo);

        requestInfo.FrontendResponse.StatusCode.Should().Be(200);
        requestInfo.FrontendResponse.Body!["education_organizations"]!.AsArray().Single()!["school_id"]!
            .GetValue<long>()
            .Should()
            .Be(255901);

        A.CallTo(() => _tokenInfoRelationalMappingSetResolver.ResolveAsync(requestInfo))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() =>
                relationalLookup.GetEducationOrganizations(
                    A<IReadOnlyCollection<EducationOrganizationId>>._,
                    mappingSet
                )
            )
            .MustHaveHappenedOnceExactly();
        A.CallTo(() =>
                relationalLookup.GetEducationOrganizations(A<IReadOnlyCollection<EducationOrganizationId>>._)
            )
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_does_not_call_relational_lookup_when_mapping_set_resolution_fails()
    {
        var relationalLookup = A.Fake<IRelationalTokenInfoEducationOrganizationLookup>();
        var clientAuthorizations = CreateClientAuthorizations([new EducationOrganizationId(255901)]);
        ConfigureJwtValidation(clientAuthorizations);

        A.CallTo(() => _tokenInfoRelationalMappingSetResolver.ResolveAsync(A<RequestInfo>._))
            .Returns(new TokenInfoRelationalMappingSetResolutionResult(false, null));

        RequestInfo requestInfo = CreateRequestInfo(
            clientAuthorizations,
            CreateScopedServiceProvider(relationalLookup)
        );

        await Execute(requestInfo);

        A.CallTo(() =>
                relationalLookup.GetEducationOrganizations(
                    A<IReadOnlyCollection<EducationOrganizationId>>._,
                    A<MappingSet>._
                )
            )
            .MustNotHaveHappened();
        A.CallTo(() =>
                relationalLookup.GetEducationOrganizations(A<IReadOnlyCollection<EducationOrganizationId>>._)
            )
            .MustNotHaveHappened();
    }

    private void ConfigureJwtValidation(ClientAuthorizations clientAuthorizations)
    {
        A.CallTo(() =>
                _jwtValidationService.ValidateAndExtractClientAuthorizationsAsync(
                    TokenFromBody,
                    A<CancellationToken>._
                )
            )
            .Returns(
                Task.FromResult<(ClaimsPrincipal?, ClientAuthorizations?)>(
                    (new ClaimsPrincipal(), clientAuthorizations)
                )
            );
    }

    private async Task Execute(RequestInfo requestInfo)
    {
        var handler = new GetTokenInfoHandler(
            NullLogger<GetTokenInfoHandler>.Instance,
            _jwtValidationService,
            _claimSetProvider,
            _profileService,
            _tokenInfoRelationalMappingSetResolver
        );

        await ((IPipelineStep)handler).Execute(requestInfo, () => Task.CompletedTask);
    }

    private static RequestInfo CreateRequestInfo(
        ClientAuthorizations clientAuthorizations,
        IServiceProvider scopedServiceProvider
    )
    {
        return new RequestInfo(
            new FrontendRequest(
                Path: "/oauth/token_info",
                Body: $$"""{"Token":"{{TokenFromBody}}"}""",
                Form: null,
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId("trace-id"),
                RouteQualifiers: []
            ),
            RequestMethod.POST,
            scopedServiceProvider
        )
        {
            ApiSchemaDocuments = new ApiSchemaBuilder()
                .WithStartProject()
                .WithStartResource("Student")
                .WithEndResource()
                .WithEndProject()
                .ToApiSchemaDocuments(),
            ClientAuthorizations = clientAuthorizations,
        };
    }

    private static ServiceProvider CreateScopedServiceProvider(
        ITokenInfoEducationOrganizationLookup? tokenInfoEducationOrganizationLookup
    )
    {
        var services = new ServiceCollection();
        var applicationContextProvider = A.Fake<IApplicationContextProvider>();
        A.CallTo(() => applicationContextProvider.GetApplicationByClientIdAsync(ClientId))
            .Returns(
                Task.FromResult<ApplicationContext?>(
                    new ApplicationContext(
                        Id: 1,
                        ApplicationId: 7,
                        ClientId: ClientId,
                        ClientUuid: Guid.Parse("a650c029-1fc0-4d9a-8844-f9386e35103f"),
                        DataStoreIds: [1]
                    )
                )
            );

        services.AddSingleton(applicationContextProvider);

        if (tokenInfoEducationOrganizationLookup is not null)
        {
            services.AddSingleton(tokenInfoEducationOrganizationLookup);
        }

        return services.BuildServiceProvider();
    }

    private static ClientAuthorizations CreateClientAuthorizations(
        List<EducationOrganizationId> educationOrganizationIds
    )
    {
        return new ClientAuthorizations(
            TokenId: TokenId,
            ClientId: ClientId,
            ClaimSetName: ClaimSetName,
            EducationOrganizationIds: educationOrganizationIds,
            NamespacePrefixes: [new NamespacePrefix("uri://ed-fi")],
            DataStoreIds: [new DataStoreId(1)]
        );
    }

    private static MappingSet CreateMappingSet()
    {
        const string testHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var effectiveSchema = new EffectiveSchemaInfo(
            ApiSchemaFormatVersion: "1.0",
            RelationalMappingVersion: "v1",
            EffectiveSchemaHash: testHash,
            ResourceKeyCount: 0,
            ResourceKeySeedHash: new byte[32],
            SchemaComponentsInEndpointOrder: [],
            ResourceKeysInIdOrder: []
        );

        var modelSet = new DerivedRelationalModelSet(
            EffectiveSchema: effectiveSchema,
            Dialect: SqlDialect.Pgsql,
            ProjectSchemasInEndpointOrder: [],
            ConcreteResourcesInNameOrder: [],
            AbstractIdentityTablesInNameOrder: [],
            AbstractUnionViewsInNameOrder: [],
            IndexesInCreateOrder: [],
            TriggersInCreateOrder: []
        );

        return new MappingSet(
            Key: new MappingSetKey(testHash, SqlDialect.Pgsql, "v1"),
            Model: modelSet,
            WritePlansByResource: new Dictionary<QualifiedResourceName, ResourceWritePlan>(),
            ReadPlansByResource: new Dictionary<QualifiedResourceName, ResourceReadPlan>(),
            ResourceKeyIdByResource: new Dictionary<QualifiedResourceName, short>(),
            ResourceKeyById: new Dictionary<short, ResourceKeyEntry>(),
            SecurableElementColumnPathsByResource: new Dictionary<
                QualifiedResourceName,
                IReadOnlyList<ResolvedSecurableElementPath>
            >()
        );
    }
}
