// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Security;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Core.Security.Model;
using EdFi.DataManagementService.Tests.Integration.Doubles;
using EdFi.DataManagementService.Tests.Integration.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace EdFi.DataManagementService.Tests.Integration.Scenarios;

/// <summary>
/// A POST authorizes against Create when its target is new and against Update when its target exists, so a
/// client granted only one of the two can do only that one.
/// </summary>
internal static class PostActionAuthorizationScenario
{
    public const string AuthorizedPrefix = "uri://ns1.org/";
    private const string UnauthorizedNamespace = "uri://other.example/AcademicSubjectDescriptor";
    private const string AuthorizedNamespace = "uri://ns1.org/AcademicSubjectDescriptor";
    private const string NullableResourcesEndpoint = "/data/authz/authorizationNullableResources";
    private const string AcademicSubjectDescriptorsEndpoint = "/data/ed-fi/academicSubjectDescriptors";

    private static readonly string[] _noFurther =
    [
        AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired,
    ];
    private static readonly string[] _namespaceBased = [AuthorizationStrategyNameConstants.NamespaceBased];

    public static IReadOnlyList<string> ConfiguredPrefixes { get; } = [AuthorizedPrefix];

    public static IClaimSetProvider CreateClaimSetProvider(FixtureContext fixture) =>
        new SwitchablePostActionGrants(fixture);

    public static async Task It_creates_but_refuses_every_update_for_a_create_only_client(
        ApiIntegrationHarness harness
    )
    {
        Grants(harness).Set(create: _noFurther, update: null);

        using HttpResponseMessage created = await PostAsync(
            harness,
            NullableResourcesEndpoint,
            NullableBody("Original")
        );
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        string location = GetLocationPath(created);
        var (originalBody, originalEtag) = await GetAsync(harness, location);

        using HttpResponseMessage changed = await PostAsync(
            harness,
            NullableResourcesEndpoint,
            NullableBody("Changed")
        );
        await AssertActionDeniedAsync(changed, "Update", "AuthorizationNullableResource");

        using HttpResponseMessage identical = await PostAsync(
            harness,
            NullableResourcesEndpoint,
            NullableBody("Original")
        );
        await AssertActionDeniedAsync(identical, "Update", "AuthorizationNullableResource");

        var (currentBody, currentEtag) = await GetAsync(harness, location);
        currentBody["name"]!.GetValue<string>().Should().Be("Original");
        currentBody.ToJsonString().Should().Be(originalBody.ToJsonString());
        currentEtag.Should().Be(originalEtag);
    }

    public static async Task It_refuses_a_create_but_updates_for_an_update_only_client(
        ApiIntegrationHarness harness
    )
    {
        Grants(harness).Set(create: null, update: _noFurther);

        using HttpResponseMessage refused = await PostAsync(
            harness,
            NullableResourcesEndpoint,
            NullableBody("Original")
        );
        await AssertActionDeniedAsync(refused, "Create", "AuthorizationNullableResource");
        (await GetManyAsync(harness, NullableResourcesEndpoint)).Should().BeEmpty();

        Grants(harness).Set(create: _noFurther, update: _noFurther);
        using HttpResponseMessage seeded = await PostAsync(
            harness,
            NullableResourcesEndpoint,
            NullableBody("Original")
        );
        seeded.StatusCode.Should().Be(HttpStatusCode.Created, await seeded.Content.ReadAsStringAsync());
        string location = GetLocationPath(seeded);

        Grants(harness).Set(create: null, update: _noFurther);
        using HttpResponseMessage changed = await PostAsync(
            harness,
            NullableResourcesEndpoint,
            NullableBody("Changed")
        );
        changed.StatusCode.Should().Be(HttpStatusCode.OK, await changed.Content.ReadAsStringAsync());
        var (changedBody, changedEtag) = await GetAsync(harness, location);
        changedBody["name"]!.GetValue<string>().Should().Be("Changed");

        using HttpResponseMessage identical = await PostAsync(
            harness,
            NullableResourcesEndpoint,
            NullableBody("Changed")
        );
        identical.StatusCode.Should().Be(HttpStatusCode.OK, await identical.Content.ReadAsStringAsync());
        identical.TryReadRawEtag(out string identicalEtag).Should().BeTrue();
        identicalEtag.Should().Be(changedEtag);
        (await GetAsync(harness, location)).Etag.Should().Be(changedEtag);
    }

    public static async Task It_refuses_a_post_neither_action_permits_with_the_create_denial(
        ApiIntegrationHarness harness
    )
    {
        Grants(harness).Set(create: null, update: null);

        using HttpResponseMessage response = await PostAsync(
            harness,
            NullableResourcesEndpoint,
            NullableBody("Original")
        );

        await AssertActionDeniedAsync(response, "Create", "AuthorizationNullableResource");
        (await GetManyAsync(harness, NullableResourcesEndpoint)).Should().BeEmpty();
    }

    public static async Task It_answers_an_update_granted_without_strategies_only_for_an_existing_target(
        ApiIntegrationHarness harness
    )
    {
        Grants(harness).Set(create: _noFurther, update: []);

        using HttpResponseMessage created = await PostAsync(
            harness,
            NullableResourcesEndpoint,
            NullableBody("Original")
        );
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());

        using HttpResponseMessage changed = await PostAsync(
            harness,
            NullableResourcesEndpoint,
            NullableBody("Changed")
        );

        string resourceClaimUri =
            $"{Conventions.EdFiOdsResourceClaimBaseUri}/authz/authorizationnullableresource";
        await AssertSecurityConfigurationAsync(
            changed,
            [
                SecurityConfigurationFailureMessages.NoAuthorizationStrategies(
                    "Update",
                    [resourceClaimUri],
                    resourceClaimUri
                ),
            ]
        );
        (await GetAsync(harness, GetLocationPath(created))).Body["name"]!
            .GetValue<string>()
            .Should()
            .Be("Original");
    }

    public static async Task It_applies_the_update_namespace_check_only_to_an_existing_descriptor(
        ApiIntegrationHarness harness
    )
    {
        // Created under the create policy, which does not check namespaces, so the stored value lies outside
        // the client's prefixes.
        Grants(harness).Set(create: _noFurther, update: _namespaceBased);
        using HttpResponseMessage created = await PostAsync(
            harness,
            AcademicSubjectDescriptorsEndpoint,
            DescriptorBody(UnauthorizedNamespace, "Original")
        );
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var (_, originalEtag) = await GetAsync(harness, GetLocationPath(created));

        using HttpResponseMessage changed = await PostAsync(
            harness,
            AcademicSubjectDescriptorsEndpoint,
            DescriptorBody(UnauthorizedNamespace, "Changed")
        );

        await AssertNamespaceMismatchAsync(
            changed,
            "Access to the requested data could not be authorized. The existing 'Namespace' value of the data does not start with any of the caller's associated namespace prefixes ('uri://ns1.org/')."
        );
        var (body, etag) = await GetAsync(harness, GetLocationPath(created));
        body["shortDescription"]!.GetValue<string>().Should().Be("Original");
        etag.Should().Be(originalEtag);
    }

    public static async Task It_applies_the_create_namespace_check_only_to_a_new_descriptor(
        ApiIntegrationHarness harness
    )
    {
        Grants(harness).Set(create: _namespaceBased, update: _noFurther);

        using HttpResponseMessage refused = await PostAsync(
            harness,
            AcademicSubjectDescriptorsEndpoint,
            DescriptorBody(UnauthorizedNamespace, "Original")
        );
        await AssertNamespaceMismatchAsync(
            refused,
            "Access to the requested data could not be authorized. The 'Namespace' value of the data does not start with any of the caller's associated namespace prefixes ('uri://ns1.org/')."
        );
        (await GetManyAsync(harness, AcademicSubjectDescriptorsEndpoint))
            .Should()
            .NotContain(descriptor => descriptor["namespace"]!.GetValue<string>() == UnauthorizedNamespace);

        // An existing descriptor outside the prefixes is updated, because the update policy checks nothing.
        Grants(harness).Set(create: _noFurther, update: _noFurther);
        using HttpResponseMessage seeded = await PostAsync(
            harness,
            AcademicSubjectDescriptorsEndpoint,
            DescriptorBody(UnauthorizedNamespace, "Original")
        );
        seeded.StatusCode.Should().Be(HttpStatusCode.Created, await seeded.Content.ReadAsStringAsync());

        Grants(harness).Set(create: _namespaceBased, update: _noFurther);
        using HttpResponseMessage changed = await PostAsync(
            harness,
            AcademicSubjectDescriptorsEndpoint,
            DescriptorBody(UnauthorizedNamespace, "Changed")
        );
        changed.StatusCode.Should().Be(HttpStatusCode.OK, await changed.Content.ReadAsStringAsync());
        (await GetAsync(harness, GetLocationPath(seeded))).Body["shortDescription"]!
            .GetValue<string>()
            .Should()
            .Be("Changed");
    }

    public static async Task It_creates_but_refuses_updates_to_a_descriptor_for_a_create_only_client(
        ApiIntegrationHarness harness
    )
    {
        Grants(harness).Set(create: _noFurther, update: null);

        using HttpResponseMessage created = await PostAsync(
            harness,
            AcademicSubjectDescriptorsEndpoint,
            DescriptorBody(AuthorizedNamespace, "Original")
        );
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var (_, originalEtag) = await GetAsync(harness, GetLocationPath(created));

        using HttpResponseMessage changed = await PostAsync(
            harness,
            AcademicSubjectDescriptorsEndpoint,
            DescriptorBody(AuthorizedNamespace, "Changed")
        );
        await AssertActionDeniedAsync(changed, "Update", "AcademicSubjectDescriptor");

        var (body, etag) = await GetAsync(harness, GetLocationPath(created));
        body["shortDescription"]!.GetValue<string>().Should().Be("Original");
        etag.Should().Be(originalEtag);
    }

    public static async Task It_refuses_a_descriptor_create_but_updates_for_an_update_only_client(
        ApiIntegrationHarness harness
    )
    {
        Grants(harness).Set(create: null, update: _noFurther);
        using HttpResponseMessage refused = await PostAsync(
            harness,
            AcademicSubjectDescriptorsEndpoint,
            DescriptorBody(AuthorizedNamespace, "Original")
        );
        await AssertActionDeniedAsync(refused, "Create", "AcademicSubjectDescriptor");

        Grants(harness).Set(create: _noFurther, update: _noFurther);
        using HttpResponseMessage seeded = await PostAsync(
            harness,
            AcademicSubjectDescriptorsEndpoint,
            DescriptorBody(AuthorizedNamespace, "Original")
        );
        seeded.StatusCode.Should().Be(HttpStatusCode.Created, await seeded.Content.ReadAsStringAsync());

        Grants(harness).Set(create: null, update: _noFurther);
        using HttpResponseMessage changed = await PostAsync(
            harness,
            AcademicSubjectDescriptorsEndpoint,
            DescriptorBody(AuthorizedNamespace, "Changed")
        );
        changed.StatusCode.Should().Be(HttpStatusCode.OK, await changed.Content.ReadAsStringAsync());
        (await GetAsync(harness, GetLocationPath(seeded))).Body["shortDescription"]!
            .GetValue<string>()
            .Should()
            .Be("Changed");
    }

    public static async Task It_keeps_put_on_the_update_action(ApiIntegrationHarness harness)
    {
        Grants(harness).Set(create: _noFurther, update: _noFurther);
        using HttpResponseMessage seeded = await PostAsync(
            harness,
            NullableResourcesEndpoint,
            NullableBody("Original")
        );
        seeded.StatusCode.Should().Be(HttpStatusCode.Created, await seeded.Content.ReadAsStringAsync());
        string location = GetLocationPath(seeded);
        string id = location.Split('/', StringSplitOptions.RemoveEmptyEntries)[^1];

        Grants(harness).Set(create: _noFurther, update: null);
        using HttpResponseMessage createOnlyPut = await PutAsync(
            harness,
            location,
            NullableBody("Changed", id)
        );
        await AssertActionDeniedAsync(createOnlyPut, "Update", "AuthorizationNullableResource");

        Grants(harness).Set(create: null, update: _noFurther);
        using HttpResponseMessage updateOnlyPut = await PutAsync(
            harness,
            location,
            NullableBody("Changed", id)
        );
        updateOnlyPut
            .StatusCode.Should()
            .Be(HttpStatusCode.NoContent, await updateOnlyPut.Content.ReadAsStringAsync());
        (await GetAsync(harness, location)).Body["name"]!.GetValue<string>().Should().Be("Changed");
    }

    public static async Task It_keeps_the_put_stored_namespace_check_on_the_update_action(
        ApiIntegrationHarness harness
    )
    {
        Grants(harness).Set(create: _noFurther, update: _noFurther);
        using HttpResponseMessage seeded = await PostAsync(
            harness,
            AcademicSubjectDescriptorsEndpoint,
            DescriptorBody(UnauthorizedNamespace, "Original")
        );
        seeded.StatusCode.Should().Be(HttpStatusCode.Created, await seeded.Content.ReadAsStringAsync());
        string location = GetLocationPath(seeded);
        string id = location.Split('/', StringSplitOptions.RemoveEmptyEntries)[^1];

        Grants(harness).Set(create: _noFurther, update: _namespaceBased);
        using HttpResponseMessage put = await PutAsync(
            harness,
            location,
            DescriptorBody(UnauthorizedNamespace, "Changed", id)
        );

        await AssertNamespaceMismatchAsync(
            put,
            "Access to the requested data could not be authorized. The existing 'Namespace' value of the data does not start with any of the caller's associated namespace prefixes ('uri://ns1.org/')."
        );
        (await GetAsync(harness, location)).Body["shortDescription"]!
            .GetValue<string>()
            .Should()
            .Be("Original");
    }

    private static SwitchablePostActionGrants Grants(ApiIntegrationHarness harness) =>
        (SwitchablePostActionGrants)harness.Services.GetRequiredService<IClaimSetProvider>();

    private static JsonObject NullableBody(string name, string? id = null)
    {
        var body = new JsonObject { ["authorizationNullableId"] = 1099, ["name"] = name };

        if (id is not null)
        {
            body["id"] = id;
        }

        return body;
    }

    private static JsonObject DescriptorBody(string @namespace, string shortDescription, string? id = null)
    {
        var body = new JsonObject
        {
            ["namespace"] = @namespace,
            ["codeValue"] = "PostActionSubject",
            ["shortDescription"] = shortDescription,
        };

        if (id is not null)
        {
            body["id"] = id;
        }

        return body;
    }

    private static Task<HttpResponseMessage> PostAsync(
        ApiIntegrationHarness harness,
        string endpoint,
        JsonObject body
    ) => SendAsync(harness, HttpMethod.Post, endpoint, body);

    private static Task<HttpResponseMessage> PutAsync(
        ApiIntegrationHarness harness,
        string location,
        JsonObject body
    ) => SendAsync(harness, HttpMethod.Put, location, body);

    private static async Task<HttpResponseMessage> SendAsync(
        ApiIntegrationHarness harness,
        HttpMethod method,
        string endpoint,
        JsonObject body
    )
    {
        using var request = new HttpRequestMessage(method, endpoint)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
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

    private static async Task<IReadOnlyList<JsonObject>> GetManyAsync(
        ApiIntegrationHarness harness,
        string endpoint
    )
    {
        using HttpResponseMessage response = await harness.HttpClient.GetAsync(endpoint);
        string body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);

        return [.. JsonNode.Parse(body)!.AsArray().Select(static item => item!.AsObject())];
    }

    private static string GetLocationPath(HttpResponseMessage response) =>
        response.Headers.Location!.IsAbsoluteUri
            ? response.Headers.Location.AbsolutePath
            : response.Headers.Location.OriginalString;

    private static async Task AssertActionDeniedAsync(
        HttpResponseMessage response,
        string actionName,
        string resourceClaimName
    )
    {
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden, body);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");

        JsonObject problem = JsonNode.Parse(body)!.AsObject();
        problem["type"]!
            .GetValue<string>()
            .Should()
            .Be("urn:ed-fi:api:security:authorization:access-denied:action");
        problem["title"]!.GetValue<string>().Should().Be("Authorization Denied");
        problem["status"]!.GetValue<int>().Should().Be(403);
        problem["errors"]!
            .AsArray()
            .Select(static error => error!.GetValue<string>())
            .Should()
            .Equal(
                $"The API client's assigned claim set (currently '{ExternalDoublesConstants.SmokeClaimSetName}') must grant permission of the '{actionName}' action on one of the following resource claims: {resourceClaimName}"
            );
    }

    private static async Task AssertNamespaceMismatchAsync(
        HttpResponseMessage response,
        string expectedDetail
    )
    {
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden, body);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");

        JsonObject problem = JsonNode.Parse(body)!.AsObject();
        problem["type"]!
            .GetValue<string>()
            .Should()
            .Be("urn:ed-fi:api:security:authorization:namespace:access-denied:namespace-mismatch");
        problem["detail"]!.GetValue<string>().Should().Be(expectedDetail);
    }

    private static async Task AssertSecurityConfigurationAsync(
        HttpResponseMessage response,
        IReadOnlyList<string> expectedErrors
    )
    {
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError, body);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");

        JsonObject problem = JsonNode.Parse(body)!.AsObject();
        problem["type"]!.GetValue<string>().Should().Be(SecurityConfigurationProblemDetails.Type);
        problem["detail"]!.GetValue<string>().Should().Be(SecurityConfigurationProblemDetails.Detail);
        problem["errors"]!
            .AsArray()
            .Select(static error => error!.GetValue<string>())
            .Should()
            .Equal(expectedErrors);
    }

    /// <summary>
    /// The claim set under test. Read and Delete are always granted, as is every action on resources the
    /// scenario does not write; Create and Update on the written resources are whatever the test last set,
    /// where <see langword="null"/> leaves the action ungranted. The middleware reads the claim set on every
    /// request, so a test can seed with both actions and then narrow them.
    /// </summary>
    private sealed class SwitchablePostActionGrants : IClaimSetProvider
    {
        private readonly ConfigurableClaimSetProvider _inner;
        private IReadOnlyList<string>? _create = _noFurther;
        private IReadOnlyList<string>? _update = _noFurther;

        public SwitchablePostActionGrants(FixtureContext fixture) =>
            _inner = new ConfigurableClaimSetProvider(fixture, Resolve);

        public void Set(IReadOnlyList<string>? create, IReadOnlyList<string>? update)
        {
            _create = create;
            _update = update;
        }

        public Task<IList<ClaimSet>> GetAllClaimSets(string? tenant = null) => _inner.GetAllClaimSets(tenant);

        private IReadOnlyList<string>? Resolve(QualifiedResourceName resource, string action) =>
            IsWrittenResource(resource)
                ? action switch
                {
                    "Create" => _create,
                    "Update" => _update,
                    _ => _noFurther,
                }
                : _noFurther;

        private static bool IsWrittenResource(QualifiedResourceName resource) =>
            (
                string.Equals(resource.ProjectName, "Authz", StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    resource.ResourceName,
                    "AuthorizationNullableResource",
                    StringComparison.Ordinal
                )
            )
            || (
                string.Equals(resource.ProjectName, "Ed-Fi", StringComparison.OrdinalIgnoreCase)
                && string.Equals(resource.ResourceName, "AcademicSubjectDescriptor", StringComparison.Ordinal)
            );
    }
}
