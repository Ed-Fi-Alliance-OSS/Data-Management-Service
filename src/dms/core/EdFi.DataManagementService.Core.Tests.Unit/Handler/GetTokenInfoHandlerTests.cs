// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Claims;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Handler;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Profile;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Core.Security.Model;
using EdFi.DataManagementService.Core.Tests.Unit.TestSupport;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
    private MappingSet _mappingSet = null!;

    [SetUp]
    public void Setup()
    {
        _jwtValidationService = A.Fake<IJwtValidationService>();
        _claimSetProvider = A.Fake<IClaimSetProvider>();
        _profileService = A.Fake<IProfileService>();
        _tokenInfoRelationalMappingSetResolver = A.Fake<ITokenInfoRelationalMappingSetResolver>();
        _mappingSet = CreateMappingSet();

        A.CallTo(() => _claimSetProvider.GetAllClaimSets(A<string?>._, A<CancellationToken>._))
            .Returns([new ClaimSet(ClaimSetName, [])]);

        A.CallTo(() => _profileService.GetOrFetchApplicationProfilesAsync(A<long>._, A<string?>._))
            .Returns(
                Task.FromResult(
                    new CachedApplicationProfiles(
                        new Dictionary<long, string> { [1] = "Writer Profile", [2] = "Reader Profile" }
                    )
                )
            );

        A.CallTo(() => _tokenInfoRelationalMappingSetResolver.ResolveAsync(A<RequestInfo>._))
            .Returns(new TokenInfoRelationalMappingSetResolutionResult(true, _mappingSet));
    }

    [Test]
    public async Task It_passes_the_requests_cancellation_token_to_the_claim_set_and_application_context_providers()
    {
        using var cancellationSource = new CancellationTokenSource();
        CancellationToken capturedClaimSetToken = default;
        CancellationToken capturedApplicationContextToken = default;

        A.CallTo(() => _claimSetProvider.GetAllClaimSets(A<string?>._, A<CancellationToken>._))
            .Invokes((string? _, CancellationToken token) => capturedClaimSetToken = token)
            .Returns([new ClaimSet(ClaimSetName, [])]);

        var applicationContextProvider = A.Fake<IApplicationContextProvider>();
        A.CallTo(() =>
                applicationContextProvider.GetApplicationByClientIdAsync(
                    ClientId,
                    tenant: null,
                    A<CancellationToken>._
                )
            )
            .Invokes(
                (string _, string? _, CancellationToken token) => capturedApplicationContextToken = token
            )
            .Returns(
                Task.FromResult<ApplicationContextResult>(
                    new ApplicationContextResult.Success(
                        new ApplicationContext(
                            Id: 1,
                            ApplicationId: 7,
                            ClientId: ClientId,
                            ClientUuid: Guid.Parse("a650c029-1fc0-4d9a-8844-f9386e35103f"),
                            DataStoreIds: [1],
                            CreatorOwnershipTokenId: null,
                            OwnershipTokenIds: []
                        )
                    )
                )
            );

        var services = new ServiceCollection();
        services.AddSingleton(applicationContextProvider);
        using var scopedServiceProvider = services.BuildServiceProvider();

        // No education organization ids, so the handler never needs the relational lookup that
        // GetAuthorizedEducationOrganizations resolves from request scope.
        var clientAuthorizations = CreateClientAuthorizations([]);
        ConfigureJwtValidation(clientAuthorizations);

        RequestInfo requestInfo = CreateRequestInfo(
            clientAuthorizations,
            scopedServiceProvider,
            cancellationSource.Token
        );

        await Execute(requestInfo);

        requestInfo.FrontendResponse.StatusCode.Should().Be(200);
        capturedClaimSetToken.Should().Be(cancellationSource.Token);
        capturedApplicationContextToken.Should().Be(cancellationSource.Token);
    }

    [Test]
    public async Task It_uses_relational_token_info_education_organization_lookup_from_request_scope()
    {
        var relationalLookup = A.Fake<IRelationalTokenInfoEducationOrganizationLookup>();
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
                relationalLookup.GetEducationOrganizations(
                    A<IReadOnlyCollection<EducationOrganizationId>>.That.Matches(ids =>
                        ids.Single().Value == 255901
                    ),
                    _mappingSet
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
                relationalLookup.GetEducationOrganizations(
                    A<IReadOnlyCollection<EducationOrganizationId>>._,
                    _mappingSet
                )
            )
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => _tokenInfoRelationalMappingSetResolver.ResolveAsync(requestInfo))
            .MustHaveHappenedOnceExactly();
    }

    [Test]
    public async Task It_preserves_full_response_contract_while_using_relational_token_info_lookup()
    {
        var relationalLookup = A.Fake<IRelationalTokenInfoEducationOrganizationLookup>();
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

        A.CallTo(() =>
                relationalLookup.GetEducationOrganizations(
                    A<IReadOnlyCollection<EducationOrganizationId>>.That.Matches(ids =>
                        ids.Single().Value == 255901
                    ),
                    _mappingSet
                )
            )
            .Returns(Task.FromResult(educationOrganizationRows));

        A.CallTo(() => _claimSetProvider.GetAllClaimSets(A<string?>._, A<CancellationToken>._))
            .Returns([
                new ClaimSet(
                    ClaimSetName,
                    [
                        new($"{Conventions.EdFiOdsResourceClaimBaseUri}/ed-fi/Student", "Create", []),
                        new($"{Conventions.EdFiOdsResourceClaimBaseUri}/ed-fi/Student", "Read", []),
                        new($"{Conventions.EdFiOdsServiceClaimBaseUri}/identity", "Read", []),
                    ]
                ),
            ]);

        var clientAuthorizations = CreateClientAuthorizations([new EducationOrganizationId(255901)]);
        ConfigureJwtValidation(clientAuthorizations);

        RequestInfo requestInfo = CreateRequestInfo(
            clientAuthorizations,
            CreateScopedServiceProvider(relationalLookup),
            CreateApiSchemaDocumentsWithStudentAndStaff()
        );

        await Execute(requestInfo);

        requestInfo.FrontendResponse.StatusCode.Should().Be(200);
        JsonNode body = requestInfo.FrontendResponse.Body!;
        body["active"]!.GetValue<bool>().Should().BeTrue();
        body["client_id"]!.GetValue<string>().Should().Be(ClientId);
        body["claim_set"]!["name"]!.GetValue<string>().Should().Be(ClaimSetName);
        body["namespace_prefixes"]!
            .AsArray()
            .Select(namespacePrefix => namespacePrefix!.GetValue<string>())
            .Should()
            .Equal("uri://ed-fi");
        body["assigned_profiles"]!
            .AsArray()
            .Select(profile => profile!.GetValue<string>())
            .Should()
            .Equal("Reader Profile", "Writer Profile");

        JsonNode educationOrganization = body["education_organizations"]!.AsArray().Single()!;
        educationOrganization["education_organization_id"]!.GetValue<long>().Should().Be(255901);
        educationOrganization["name_of_institution"]!.GetValue<string>().Should().Be("Grand Bend School");
        educationOrganization["type"]!.GetValue<string>().Should().Be("edfi.School");
        educationOrganization["school_id"]!.GetValue<long>().Should().Be(255901);

        JsonNode resource = body["resources"]!.AsArray().Single()!;
        resource["resource"]!.GetValue<string>().Should().Be("/ed-fi/students");
        resource["operations"]!
            .AsArray()
            .Select(operation => operation!.GetValue<string>())
            .Should()
            .Equal("Create", "Read");

        JsonNode service = body["services"]!.AsArray().Single()!;
        service["service"]!.GetValue<string>().Should().Be("identity");
        service["operations"]!
            .AsArray()
            .Select(operation => operation!.GetValue<string>())
            .Should()
            .Equal("Read");

        A.CallTo(() =>
                relationalLookup.GetEducationOrganizations(
                    A<IReadOnlyCollection<EducationOrganizationId>>._,
                    _mappingSet
                )
            )
            .MustHaveHappenedOnceExactly();
    }

    [Test]
    public async Task It_formats_relational_ed_fi_discriminators_using_existing_token_info_names()
    {
        var relationalLookup = A.Fake<IRelationalTokenInfoEducationOrganizationLookup>();
        IEnumerable<TokenInfoEducationOrganization> educationOrganizationRows =
        [
            new(
                EducationOrganizationId: 255901,
                NameOfInstitution: "Grand Bend School",
                Discriminator: "Ed-Fi:School",
                AncestorDiscriminator: "Ed-Fi:LocalEducationAgency",
                AncestorEducationOrganizationId: 255901001
            ),
            new(
                EducationOrganizationId: 255901,
                NameOfInstitution: "Grand Bend School",
                Discriminator: "Ed-Fi:School",
                AncestorDiscriminator: "Ed-Fi:School",
                AncestorEducationOrganizationId: 255901
            ),
        ];

        A.CallTo(() =>
                relationalLookup.GetEducationOrganizations(
                    A<IReadOnlyCollection<EducationOrganizationId>>._,
                    _mappingSet
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
        JsonNode educationOrganization = requestInfo.FrontendResponse.Body!["education_organizations"]!
            .AsArray()
            .Single()!;
        educationOrganization["type"]!.GetValue<string>().Should().Be("edfi.School");
        educationOrganization["local_education_agency_id"]!.GetValue<long>().Should().Be(255901001);
        educationOrganization["school_id"]!.GetValue<long>().Should().Be(255901);
    }

    [Test]
    public async Task It_formats_extension_relational_discriminators_using_project_endpoint_name()
    {
        var relationalLookup = A.Fake<IRelationalTokenInfoEducationOrganizationLookup>();
        IEnumerable<TokenInfoEducationOrganization> educationOrganizationRows =
        [
            new(
                EducationOrganizationId: 900001,
                NameOfInstitution: "Sample Custom Education Organization",
                Discriminator: "Sample:CustomEducationOrganization",
                AncestorDiscriminator: "Ed-Fi:LocalEducationAgency",
                AncestorEducationOrganizationId: 255901001
            ),
            new(
                EducationOrganizationId: 900001,
                NameOfInstitution: "Sample Custom Education Organization",
                Discriminator: "Sample:CustomEducationOrganization",
                AncestorDiscriminator: "Sample:CustomEducationOrganization",
                AncestorEducationOrganizationId: 900001
            ),
        ];

        A.CallTo(() =>
                relationalLookup.GetEducationOrganizations(
                    A<IReadOnlyCollection<EducationOrganizationId>>._,
                    _mappingSet
                )
            )
            .Returns(Task.FromResult(educationOrganizationRows));

        var clientAuthorizations = CreateClientAuthorizations([new EducationOrganizationId(900001)]);
        ConfigureJwtValidation(clientAuthorizations);

        RequestInfo requestInfo = CreateRequestInfo(
            clientAuthorizations,
            CreateScopedServiceProvider(relationalLookup),
            CreateApiSchemaDocumentsWithSampleExtension()
        );

        await Execute(requestInfo);

        requestInfo.FrontendResponse.StatusCode.Should().Be(200);
        JsonNode educationOrganization = requestInfo.FrontendResponse.Body!["education_organizations"]!
            .AsArray()
            .Single()!;
        educationOrganization["type"]!.GetValue<string>().Should().Be("sample.CustomEducationOrganization");
        educationOrganization["local_education_agency_id"]!.GetValue<long>().Should().Be(255901001);
        educationOrganization["custom_education_organization_id"]!.GetValue<long>().Should().Be(900001);
    }

    [Test]
    public async Task It_returns_empty_education_organizations_without_resolving_lookup_for_empty_claims()
    {
        var clientAuthorizations = CreateClientAuthorizations([]);
        ConfigureJwtValidation(clientAuthorizations);

        RequestInfo requestInfo = CreateRequestInfo(
            clientAuthorizations,
            CreateScopedServiceProvider(relationalLookup: null)
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
    public async Task It_marks_the_successful_response_as_no_store()
    {
        var clientAuthorizations = CreateClientAuthorizations([]);
        ConfigureJwtValidation(clientAuthorizations);

        RequestInfo requestInfo = CreateRequestInfo(
            clientAuthorizations,
            CreateScopedServiceProvider(relationalLookup: null)
        );

        await Execute(requestInfo);

        requestInfo.FrontendResponse.StatusCode.Should().Be(200);
        requestInfo.FrontendResponse.Headers["Cache-Control"].Should().Be("no-store");
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
    }

    [Test]
    public async Task It_requires_relational_lookup_registration_for_education_organization_claims()
    {
        var clientAuthorizations = CreateClientAuthorizations([new EducationOrganizationId(255901)]);
        ConfigureJwtValidation(clientAuthorizations);

        RequestInfo requestInfo = CreateRequestInfo(
            clientAuthorizations,
            CreateScopedServiceProvider(relationalLookup: null)
        );

        Func<Task> action = async () => await Execute(requestInfo);

        await action.Should().ThrowAsync<InvalidOperationException>();
    }

    [Test]
    public async Task It_returns_401_when_application_context_is_not_found()
    {
        var clientAuthorizations = CreateClientAuthorizations([]);
        ConfigureJwtValidation(clientAuthorizations);
        RequestInfo requestInfo = CreateRequestInfo(
            clientAuthorizations,
            CreateScopedServiceProvider(null, new ApplicationContextResult.NotFound())
        );

        await Execute(requestInfo);

        requestInfo.FrontendResponse.StatusCode.Should().Be(401);
    }

    [Test]
    public async Task It_returns_503_when_application_context_is_unavailable()
    {
        var clientAuthorizations = CreateClientAuthorizations([]);
        ConfigureJwtValidation(clientAuthorizations);
        RequestInfo requestInfo = CreateRequestInfo(
            clientAuthorizations,
            CreateScopedServiceProvider(null, new ApplicationContextResult.Unavailable())
        );

        await Execute(requestInfo);

        requestInfo.FrontendResponse.StatusCode.Should().Be(503);
    }

    private static ClaimSet StudentClaimSet(string name, string action) =>
        new(name, [new($"{Conventions.EdFiOdsResourceClaimBaseUri}/ed-fi/Student", action, [])]);

    /// <summary>
    /// Authorizations shaped as JWT extraction produces them for '|'-separated scope values: the
    /// claim-set name is set only for one distinct scope value.
    /// </summary>
    private static ClientAuthorizations CreateScopedClientAuthorizations(string scopeValueList)
    {
        string[] scopeValues = scopeValueList.Split('|');
        return CreateClientAuthorizations([]) with
        {
            ClaimSetName = scopeValues.Length == 1 ? scopeValues[0] : "",
            ScopeValues = scopeValues,
        };
    }

    private void ConfigureClaimSets(params ClaimSet[] claimSets) =>
        A.CallTo(() => _claimSetProvider.GetAllClaimSets(A<string?>._, A<CancellationToken>._))
            .Returns(claimSets.ToList());

    private static (
        IServiceProvider ScopedServiceProvider,
        IApplicationContextProvider Provider
    ) CreateRecordingApplicationContext()
    {
        var applicationContextProvider = A.Fake<IApplicationContextProvider>();
        var services = new ServiceCollection();
        services.AddSingleton(applicationContextProvider);
        return (services.BuildServiceProvider(), applicationContextProvider);
    }

    private static string[] Errors(RequestInfo requestInfo) =>
        [.. requestInfo.FrontendResponse.Body!["errors"]!.AsArray().Select(node => node!.GetValue<string>())];

    [TestCase("Alpha|profile")]
    [TestCase("profile|Alpha")]
    public async Task It_reports_only_the_one_recognized_claim_set_from_multiple_scope_values(
        string scopeValueList
    )
    {
        // Beta grants Create but is not in the token, so it contributes nothing.
        ConfigureClaimSets(StudentClaimSet("Alpha", "Read"), StudentClaimSet("Beta", "Create"));
        ClientAuthorizations clientAuthorizations = CreateScopedClientAuthorizations(scopeValueList);
        ConfigureJwtValidation(clientAuthorizations);
        RequestInfo requestInfo = CreateRequestInfo(
            clientAuthorizations,
            CreateScopedServiceProvider(relationalLookup: null)
        );

        await Execute(requestInfo);

        requestInfo.FrontendResponse.StatusCode.Should().Be(200);
        JsonNode body = requestInfo.FrontendResponse.Body!;
        body["claim_set"]!["name"]!.GetValue<string>().Should().Be("Alpha");
        JsonNode resource = body["resources"]!.AsArray().Single()!;
        resource["operations"]!
            .AsArray()
            .Select(operation => operation!.GetValue<string>())
            .Should()
            .Equal("Read");
        requestInfo.ClientAuthorizations.ClaimSetName.Should().Be("Alpha");
        clientAuthorizations.ClaimSetName.Should().BeEmpty();
        A.CallTo(() => _claimSetProvider.GetAllClaimSets(A<string?>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Test]
    public async Task It_reports_the_metadata_spelling_when_case_variants_select_one_claim_set()
    {
        ConfigureClaimSets(StudentClaimSet("ALPHA", "Read"));
        ClientAuthorizations clientAuthorizations = CreateScopedClientAuthorizations("alpha|Alpha");
        ConfigureJwtValidation(clientAuthorizations);
        RequestInfo requestInfo = CreateRequestInfo(
            clientAuthorizations,
            CreateScopedServiceProvider(relationalLookup: null)
        );

        await Execute(requestInfo);

        requestInfo.FrontendResponse.StatusCode.Should().Be(200);
        requestInfo.FrontendResponse.Body!["claim_set"]!["name"]!.GetValue<string>().Should().Be("ALPHA");
    }

    [TestCase("Alpha|Beta")]
    [TestCase("Beta|Alpha")]
    [TestCase("profile|Beta|Alpha")]
    public async Task It_returns_403_when_scope_values_identify_multiple_claim_sets(string scopeValueList)
    {
        ConfigureClaimSets(StudentClaimSet("Alpha", "Read"), StudentClaimSet("Beta", "Create"));
        ClientAuthorizations clientAuthorizations = CreateScopedClientAuthorizations(scopeValueList);
        ConfigureJwtValidation(clientAuthorizations);
        var (scopedServiceProvider, applicationContextProvider) = CreateRecordingApplicationContext();
        RequestInfo requestInfo = CreateRequestInfo(clientAuthorizations, scopedServiceProvider);

        await Execute(requestInfo);

        requestInfo.FrontendResponse.StatusCode.Should().Be(403);
        requestInfo.FrontendResponse.ContentType.Should().Be("application/problem+json");
        requestInfo.FrontendResponse.Headers.Should().BeEmpty();
        Errors(requestInfo).Should().Equal("The token identifies multiple claim sets.");
        A.CallTo(applicationContextProvider).MustNotHaveHappened();
        A.CallTo(_profileService).MustNotHaveHappened();
        A.CallTo(() => _claimSetProvider.GetAllClaimSets(A<string?>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Test]
    public async Task It_keeps_401_when_no_scope_value_matches_a_claim_set()
    {
        ConfigureClaimSets(StudentClaimSet("Alpha", "Read"));
        ClientAuthorizations clientAuthorizations = CreateScopedClientAuthorizations("profile|Gamma");
        ConfigureJwtValidation(clientAuthorizations);
        var (scopedServiceProvider, applicationContextProvider) = CreateRecordingApplicationContext();
        RequestInfo requestInfo = CreateRequestInfo(clientAuthorizations, scopedServiceProvider);

        await Execute(requestInfo);

        requestInfo.FrontendResponse.StatusCode.Should().Be(401);
        requestInfo.FrontendResponse.Body!["detail"]!
            .GetValue<string>()
            .Should()
            .Be("The client's ClaimSet was not found.");
        A.CallTo(applicationContextProvider).MustNotHaveHappened();
    }

    [Test]
    public async Task It_returns_a_security_configuration_500_for_duplicate_matching_claim_set_names()
    {
        ConfigureClaimSets(StudentClaimSet("Alpha", "Read"), StudentClaimSet("alpha", "Create"));
        ClientAuthorizations clientAuthorizations = CreateScopedClientAuthorizations("Alpha");
        ConfigureJwtValidation(clientAuthorizations);
        var (scopedServiceProvider, applicationContextProvider) = CreateRecordingApplicationContext();
        RequestInfo requestInfo = CreateRequestInfo(clientAuthorizations, scopedServiceProvider);

        await Execute(requestInfo);

        requestInfo.FrontendResponse.StatusCode.Should().Be(500);
        requestInfo.FrontendResponse.ContentType.Should().Be("application/problem+json");
        requestInfo.FrontendResponse.Body!["type"]!
            .GetValue<string>()
            .Should()
            .Be("urn:ed-fi:api:system:configuration:security");
        Errors(requestInfo).Should().Equal("The security metadata contains duplicate claim-set names.");
        A.CallTo(applicationContextProvider).MustNotHaveHappened();
    }

    [Test]
    public async Task It_lets_a_claim_set_provider_failure_propagate_for_multiple_scope_values()
    {
        A.CallTo(() => _claimSetProvider.GetAllClaimSets(A<string?>._, A<CancellationToken>._))
            .Throws(new InvalidOperationException("simulated failure"));
        ClientAuthorizations clientAuthorizations = CreateScopedClientAuthorizations("Alpha|Beta");
        ConfigureJwtValidation(clientAuthorizations);
        var (scopedServiceProvider, _) = CreateRecordingApplicationContext();
        RequestInfo requestInfo = CreateRequestInfo(clientAuthorizations, scopedServiceProvider);

        Func<Task> act = () => Execute(requestInfo);

        await act.Should().ThrowAsync<InvalidOperationException>();
        requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
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

    [TestCase("pro\r\nfile|Gamma", "Alpha", "No ClaimSet matching Scope", "Scope", "profile Gamma", 401)]
    [TestCase("Alpha|Beta", "Alpha|Beta", "multiple claim sets", "Scope", "Alpha Beta", 403)]
    [TestCase(
        "Alpha\r\n",
        "Alpha\r\n|alpha\r\n|Beta",
        "duplicate claim-set names",
        "DuplicateNames",
        "Alpha alpha",
        500
    )]
    public async Task It_logs_sanitized_resolution_details_without_reflecting_them_in_errors(
        string scopes,
        string metadataNames,
        string diagnostic,
        string property,
        string expected,
        int statusCode
    )
    {
        A.CallTo(() => _claimSetProvider.GetAllClaimSets(A<string?>._, A<CancellationToken>._))
            .Returns(metadataNames.Split('|').Select(name => new ClaimSet(name, [])).ToList());
        ClientAuthorizations authorizations = CreateScopedClientAuthorizations(scopes);
        ConfigureJwtValidation(authorizations);
        var (services, _) = CreateRecordingApplicationContext();
        RequestInfo requestInfo = CreateRequestInfo(authorizations, services);
        var logger = new RecordingLogger<GetTokenInfoHandler>();

        await Execute(requestInfo, logger);

        logger
            .Records.Should()
            .ContainSingle(record =>
                record.Message.Contains(diagnostic) && Equals(record.Properties[property], expected)
            );
        requestInfo.FrontendResponse.StatusCode.Should().Be(statusCode);
        requestInfo
            .FrontendResponse.Body!.ToJsonString()
            .Should()
            .NotContain("Alpha")
            .And.NotContain("Gamma");
    }

    private Task Execute(RequestInfo requestInfo) =>
        Execute(requestInfo, NullLogger<GetTokenInfoHandler>.Instance);

    [Test]
    public async Task It_bounds_scope_diagnostics_after_removing_control_characters()
    {
        ClientAuthorizations authorizations = CreateScopedClientAuthorizations(
            new string('\r', 1024) + new string('x', 1030)
        );
        ConfigureJwtValidation(authorizations);
        var (services, _) = CreateRecordingApplicationContext();
        RequestInfo requestInfo = CreateRequestInfo(authorizations, services);
        var logger = new RecordingLogger<GetTokenInfoHandler>();

        await Execute(requestInfo, logger);

        logger
            .Records.Should()
            .ContainSingle(record =>
                record.Message.Contains("No ClaimSet matching Scope")
                && Equals(record.Properties["Scope"], new string('x', 1024) + "...[truncated]")
            );
        requestInfo.FrontendResponse.StatusCode.Should().Be(401);
    }

    private async Task Execute(RequestInfo requestInfo, ILogger<GetTokenInfoHandler> logger)
    {
        var handler = new GetTokenInfoHandler(
            logger,
            _jwtValidationService,
            _claimSetProvider,
            _profileService,
            _tokenInfoRelationalMappingSetResolver
        );

        await ((IPipelineStep)handler).Execute(requestInfo, () => Task.CompletedTask);
    }

    private static RequestInfo CreateRequestInfo(
        ClientAuthorizations clientAuthorizations,
        IServiceProvider scopedServiceProvider,
        CancellationToken cancellationToken = default
    )
    {
        return CreateRequestInfo(
            clientAuthorizations,
            scopedServiceProvider,
            CreateDefaultApiSchemaDocuments(),
            cancellationToken
        );
    }

    private static RequestInfo CreateRequestInfo(
        ClientAuthorizations clientAuthorizations,
        IServiceProvider scopedServiceProvider,
        ApiSchemaDocuments apiSchemaDocuments,
        CancellationToken cancellationToken = default
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
            scopedServiceProvider,
            cancellationToken
        )
        {
            ApiSchemaDocuments = apiSchemaDocuments,
            ClientAuthorizations = clientAuthorizations,
        };
    }

    private static ApiSchemaDocuments CreateDefaultApiSchemaDocuments()
    {
        return new ApiSchemaBuilder()
            .WithStartProject()
            .WithStartResource("Student")
            .WithEndResource()
            .WithEndProject()
            .ToApiSchemaDocuments();
    }

    private static ApiSchemaDocuments CreateApiSchemaDocumentsWithStudentAndStaff()
    {
        return new ApiSchemaBuilder()
            .WithStartProject()
            .WithStartResource("Student")
            .WithEndResource()
            .WithStartResource("Staff")
            .WithEndResource()
            .WithEndProject()
            .ToApiSchemaDocuments();
    }

    private static ApiSchemaDocuments CreateApiSchemaDocumentsWithSampleExtension()
    {
        return new ApiSchemaBuilder()
            .WithStartProject()
            .WithStartResource("Student")
            .WithEndResource()
            .WithEndProject()
            .WithStartProject("Sample")
            .WithStartResource("CustomEducationOrganization")
            .WithEndResource()
            .WithEndProject()
            .ToApiSchemaDocuments();
    }

    private static ServiceProvider CreateScopedServiceProvider(
        IRelationalTokenInfoEducationOrganizationLookup? relationalLookup,
        ApplicationContextResult? applicationContextResult = null
    )
    {
        var services = new ServiceCollection();
        var applicationContextProvider = A.Fake<IApplicationContextProvider>();
        A.CallTo(() =>
                applicationContextProvider.GetApplicationByClientIdAsync(
                    ClientId,
                    tenant: null,
                    A<CancellationToken>._
                )
            )
            .Returns(
                Task.FromResult(
                    applicationContextResult
                        ?? new ApplicationContextResult.Success(
                            new ApplicationContext(
                                Id: 1,
                                ApplicationId: 7,
                                ClientId: ClientId,
                                ClientUuid: Guid.Parse("a650c029-1fc0-4d9a-8844-f9386e35103f"),
                                DataStoreIds: [1],
                                CreatorOwnershipTokenId: null,
                                OwnershipTokenIds: []
                            )
                        )
                )
            );

        services.AddSingleton(applicationContextProvider);

        if (relationalLookup is not null)
        {
            services.AddSingleton(relationalLookup);
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
        )
        {
            ScopeValues = [ClaimSetName],
        };
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
