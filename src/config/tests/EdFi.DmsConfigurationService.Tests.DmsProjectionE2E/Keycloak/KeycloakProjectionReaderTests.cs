// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection;
using FluentAssertions;
using FailureCode = EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection.EducationOrganizationProjectionFailureCode;
using FailureStage = EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection.EducationOrganizationProjectionStage;

namespace EdFi.DmsConfigurationService.Tests.DmsProjectionE2E.Keycloak;

/// <summary>
/// The production reader against the single-tenant DMS E2E stack in Keycloak mode, through real endpoints only. It is
/// outside the <c>Live</c> namespace, so the Instance fixture never runs for it, and the Instance run filters its
/// category out.
/// </summary>
public static class KeycloakProjectionReaderTests
{
    [TestFixture]
    [Category("KeycloakReader")]
    public class Given_the_production_reader_against_the_keycloak_dms_stack
    {
        private const string ClaimSetName = "ProjectionReaderKeycloakE2E";

        private readonly CleanupRegistry _cleanup = new();
        private KeycloakReaderE2EEnvironment _environment = null!;
        private LiveServices? _services;
        private Uri _advertisedTokenUrl = null!;
        private Uri _advertisedProjectionUrl = null!;
        private Type _readerType = null!;
        private IReadOnlyList<ObservedRequest> _requests = [];
        private EducationOrganizationProjectionReadResult _projectionRead = null!;
        private EducationOrganizationProjectionReadResult _resourceOnlyRead = null!;

        [OneTimeSetUp]
        public async Task SetUpAsync()
        {
            _environment = KeycloakReaderE2EEnvironment.Read();
            _services = new LiveServices(_environment.ForLiveServices(), _cleanup);
            const string Tenant = LiveServices.SingleTenant;

            // A data store of this fixture's own on the stack's E2E database, a projection-only application, a
            // resource-only one that seeds, and the hierarchy, all through CMS and DMS; CMS creates each
            // application's Keycloak client.
            int dataStoreId = await _services.CreateDataStoreAsync(
                Tenant,
                "Projection Reader Keycloak E2E",
                _environment.DataStoreConnectionString,
                new Dictionary<string, string>()
            );
            int vendorId = await _services.CreateVendorAsync(
                Tenant,
                "Projection Reader Keycloak E2E",
                ProjectionHierarchySeed.NamespacePrefix
            );
            await _services.ImportProjectionClaimSetAsync(Tenant, ClaimSetName);
            ClientCredentials projection = await _services.CreateApplicationAsync(
                Tenant,
                "Projection Reader Keycloak E2E",
                ClaimSetName,
                dataStoreIds: [],
                vendorId
            );
            ClientCredentials resourceOnly = await _services.CreateApplicationAsync(
                Tenant,
                "Projection Reader Keycloak E2E seeder",
                "EdFiSandbox",
                [dataStoreId],
                vendorId
            );
            await _services.ReloadClaimSetsAsync(Tenant);
            await ProjectionHierarchySeed.SeedAsync(
                _services,
                await _services.ClientTokenAsync(resourceOnly),
                new FixtureRoute(Tenant, "", "", dataStoreId)
            );

            (_advertisedTokenUrl, _advertisedProjectionUrl) = await DiscoveryUrlsAsync(
                _environment.DmsBaseUrl
            );
            IReadOnlyDictionary<string, string> contexts = await _services.DataStoreContextsAsync(
                Tenant,
                dataStoreId
            );

            using (KeycloakReaderHost host = new(_environment.DmsBaseUrl, projection))
            {
                _readerType = host.ReaderType;
                _projectionRead = await host.ReadAsync(dataStoreId, contexts);
                _requests = host.Requests;
            }

            using KeycloakReaderHost resourceOnlyHost = new(_environment.DmsBaseUrl, resourceOnly);
            _resourceOnlyRead = await resourceOnlyHost.ReadAsync(dataStoreId, contexts);
        }

        [OneTimeTearDown]
        public async Task TearDownAsync()
        {
            IReadOnlyList<string> failures = await _cleanup.RunAsync();
            _services?.Dispose();

            if (failures.Count > 0)
            {
                Assert.Fail(
                    "DMS projection reader Keycloak E2E cleanup failed: " + string.Join("; ", failures)
                );
            }
        }

        private IEnumerable<ObservedRequest> Pages => _requests.Skip(2);

        [Test]
        public void It_resolves_the_production_reader() =>
            _readerType.Should().Be<EducationOrganizationProjectionReader>();

        [Test]
        public void It_sends_one_discovery_get_one_token_post_to_the_advertised_url_and_then_only_pages()
        {
            _requests.Should().HaveCount(2 + 4, "seven items at two a page are four pages");
            (_requests[0].Method, _requests[0].Url).Should().Be((HttpMethod.Get, _environment.DmsBaseUrl));
            (_requests[1].Method, _requests[1].Url).Should().Be((HttpMethod.Post, _advertisedTokenUrl));
            Pages
                .Should()
                .OnlyContain(page =>
                    page.Method == HttpMethod.Get
                    && new Uri(page.Url.GetLeftPart(UriPartial.Path)) == _advertisedProjectionUrl
                );
        }

        [Test]
        public void It_reads_every_page_with_a_token_keycloak_issued()
        {
            Pages.Should().NotBeEmpty();
            Pages.Select(page => page.BearerIssuer).Should().AllBe(_environment.ExpectedIssuer);
        }

        [Test]
        public void It_reads_the_seeded_hierarchy_with_its_parents() =>
            _projectionRead
                .Should()
                .BeOfType<EducationOrganizationProjectionReadResult.Success>()
                .Subject.Items.Should()
                .Equal(ProjectionHierarchySeed.Items);

        [Test]
        public void It_refuses_a_resource_only_credential_at_the_page_stage() =>
            _resourceOnlyRead
                .Should()
                .BeOfType<EducationOrganizationProjectionReadResult.Failure>()
                .Subject.Detail.Should()
                .BeEquivalentTo(
                    new
                    {
                        Code = FailureCode.Forbidden,
                        Stage = FailureStage.Page,
                        HttpStatus = 403,
                        PagesRead = 0,
                    }
                );

        /// <summary>The token and projection URLs DMS's own Discovery document advertises, read outside the reader.</summary>
        private static async Task<(Uri Token, Uri Projection)> DiscoveryUrlsAsync(Uri dmsBaseUrl)
        {
            using HttpClient client = new();
            JsonNode discovery = JsonNode.Parse(await client.GetStringAsync(dmsBaseUrl))!;
            return (
                new Uri(discovery["urls"]!["oauth"]!.GetValue<string>()),
                new Uri(discovery["urls"]!["educationOrganizationProjection"]!.GetValue<string>())
            );
        }
    }
}
