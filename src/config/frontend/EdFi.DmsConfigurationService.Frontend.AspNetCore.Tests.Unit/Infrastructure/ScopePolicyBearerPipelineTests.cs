// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.DataModel.Model.Authorization;
using FluentAssertions;
using Microsoft.IdentityModel.Tokens;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Infrastructure;

/// <summary>
/// Signed tokens with scope claims in several shapes, sent through the default <c>Bearer</c> scheme, the real
/// authentication and authorization middleware and the real route policies of the profile endpoints.
/// </summary>
public class ScopePolicyBearerPipelineTests
{
    [TestFixture]
    public class Given_Signed_Scope_Policy_Tokens
    {
        private const string ReadOnly = "edfi_admin_api/readonly_access";

        private static Dictionary<string, object> Claims(object? scope, bool withRole = true)
        {
            Dictionary<string, object> claims = [];
            if (scope is not null)
            {
                claims["scope"] = scope;
            }

            if (withRole)
            {
                claims[BearerPipelineHost.RoleClaimType] = BearerPipelineHost.ConfigServiceRole;
            }

            return claims;
        }

        private sealed record Outcome(
            HttpStatusCode Status,
            JsonObject? Payload,
            JsonObject? Body,
            string[] ValidatedScopeClaims
        );

        private BearerPipelineHost _host = null!;
        private readonly Dictionary<string, Outcome> _outcomes = [];

        private async Task SendAsync(
            string name,
            IReadOnlyDictionary<string, object> claims,
            HttpMethod? method = null,
            string path = "/v3/profiles/7"
        )
        {
            string token = _host.Store.Mint("key-1", authorizationClaims: claims).Token;
            string payloadJson = Encoding.UTF8.GetString(Base64UrlEncoder.DecodeBytes(token.Split('.')[1]));

            using HttpResponseMessage response = await _host.SendAsync(
                new AuthenticationHeaderValue("Bearer", token),
                path,
                method
            );
            string content = await response.Content.ReadAsStringAsync();

            _outcomes[name] = new Outcome(
                response.StatusCode,
                JsonNode.Parse(payloadJson)!.AsObject(),
                response.StatusCode == HttpStatusCode.OK ? JsonNode.Parse(content)!.AsObject() : null,
                [.. _host.LastValidatedClaims.Where(c => c.Type == "scope").Select(c => c.Value)]
            );
        }

        [SetUp]
        public async Task Act()
        {
            _host = new BearerPipelineHost();
            _host.Store.AddKey("key-1");
            await _host.StartWarmAsync();

            await SendAsync("array, required last", Claims(new[] { "unrelated", ReadOnly }));
            await SendAsync("array, required first", Claims(new[] { ReadOnly, "unrelated" }));
            await SendAsync("singleton string", Claims($"unrelated {ReadOnly}"));
            await SendAsync("array element holding a list", Claims(new[] { $"unrelated {ReadOnly}" }));
            await SendAsync("admin scope control", Claims(AuthorizationScopes.AdminScope.Name));
            await SendAsync("no scope", Claims(null));
            await SendAsync("empty array", Claims(Array.Empty<string>()));
            await SendAsync("empty string", Claims(""));
            await SendAsync("wrong case", Claims(new[] { "unrelated", ReadOnly.ToUpperInvariant() }));
            await SendAsync("prefix only", Claims(new[] { "unrelated", "edfi_admin_api/readonly" }));
            await SendAsync("scope without role", Claims(new[] { "unrelated", ReadOnly }, withRole: false));
            await SendAsync(
                "readonly POST",
                Claims(new[] { "unrelated", ReadOnly }),
                HttpMethod.Post,
                "/v3/profiles/"
            );
        }

        [TearDown]
        public void TearDown() => _host.Dispose();

        private void ShouldBeAllowedWith(string name, params string[] validatedScopeClaims)
        {
            Outcome outcome = _outcomes[name];
            outcome.Status.Should().Be(HttpStatusCode.OK);
            outcome.Body!["id"]!.GetValue<int>().Should().Be(7);
            outcome.ValidatedScopeClaims.Should().Equal(validatedScopeClaims);
        }

        private void ShouldBeForbiddenWith(string name, params string[] validatedScopeClaims)
        {
            Outcome outcome = _outcomes[name];
            outcome.Status.Should().Be(HttpStatusCode.Forbidden);
            outcome.ValidatedScopeClaims.Should().Equal(validatedScopeClaims);
        }

        [Test]
        public void It_signs_the_scope_array_as_an_array()
        {
            JsonNode scope = _outcomes["array, required last"].Payload!["scope"]!;
            scope.Should().BeAssignableTo<JsonArray>();
            scope.AsArray().Select(value => value!.GetValue<string>()).Should().Equal("unrelated", ReadOnly);
        }

        [Test]
        public void It_signs_the_singleton_scope_as_one_string()
        {
            JsonNode scope = _outcomes["singleton string"].Payload!["scope"]!;
            scope.Should().BeAssignableTo<JsonValue>();
            scope.GetValue<string>().Should().Be($"unrelated {ReadOnly}");
        }

        [Test]
        public void It_signs_the_list_element_inside_an_array()
        {
            JsonNode scope = _outcomes["array element holding a list"].Payload!["scope"]!;
            scope
                .AsArray()
                .Select(value => value!.GetValue<string>())
                .Should()
                .Equal($"unrelated {ReadOnly}");
        }

        [Test]
        public void It_allows_a_readonly_scope_placed_last_in_the_array() =>
            ShouldBeAllowedWith("array, required last", "unrelated", ReadOnly);

        [Test]
        public void It_allows_a_readonly_scope_placed_first_in_the_array() =>
            ShouldBeAllowedWith("array, required first", ReadOnly, "unrelated");

        [Test]
        public void It_allows_a_readonly_scope_in_a_space_separated_string() =>
            ShouldBeAllowedWith("singleton string", $"unrelated {ReadOnly}");

        [Test]
        public void It_allows_a_readonly_scope_in_a_list_inside_an_array_element() =>
            ShouldBeAllowedWith("array element holding a list", $"unrelated {ReadOnly}");

        [Test]
        public void It_allows_the_admin_scope_control() =>
            ShouldBeAllowedWith("admin scope control", AuthorizationScopes.AdminScope.Name);

        [Test]
        public void It_forbids_a_token_without_a_scope() => ShouldBeForbiddenWith("no scope");

        [Test]
        public void It_forbids_an_empty_scope_array() => ShouldBeForbiddenWith("empty array");

        [Test]
        public void It_forbids_an_empty_scope_string() => ShouldBeForbiddenWith("empty string", "");

        [Test]
        public void It_forbids_a_scope_that_differs_in_case() =>
            ShouldBeForbiddenWith("wrong case", "unrelated", ReadOnly.ToUpperInvariant());

        [Test]
        public void It_forbids_a_scope_that_is_only_a_prefix() =>
            ShouldBeForbiddenWith("prefix only", "unrelated", "edfi_admin_api/readonly");

        [Test]
        public void It_forbids_the_right_scope_without_the_service_role() =>
            ShouldBeForbiddenWith("scope without role", "unrelated", ReadOnly);

        [Test]
        public void It_forbids_a_readonly_scope_on_a_write_route() =>
            ShouldBeForbiddenWith("readonly POST", "unrelated", ReadOnly);
    }
}
