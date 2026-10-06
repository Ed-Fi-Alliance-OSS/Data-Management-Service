// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Tests.DmsProjectionE2E.Harness;

/// <summary>
/// The provisioning harness itself, with both services stood in for: a record created by a call that then fails is
/// still deleted, and the failure the call reported is the one that stays visible. Outside the <c>Live</c> namespace,
/// so these run without the Instance stack.
/// </summary>
public static class LiveServicesCleanupTests
{
    private const string Tenant = "Tenant_255901";

    /// <summary>Answers every request from the case's routes, after recording it as "METHOD /path".</summary>
    private sealed class FakeServicesHandler(Func<HttpRequestMessage, HttpResponseMessage?> route)
        : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Requests.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");
            if (request.RequestUri.AbsolutePath == "/connect/token")
            {
                return Task.FromResult(Json(HttpStatusCode.OK, """{"access_token":"admin-token"}"""));
            }

            return Task.FromResult(route(request) ?? new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    /// <summary>Runs one provisioning call that fails, then the registered cleanup, recording both outcomes.</summary>
    public abstract class SetupFailureFixture
    {
        private FakeServicesHandler _handler = null!;

        protected Exception? SetupFailure { get; private set; }

        protected IReadOnlyList<string> CleanupFailures { get; private set; } = [];

        protected IReadOnlyList<string> Requests => _handler.Requests;

        [SetUp]
        public async Task SetUp()
        {
            _handler = new FakeServicesHandler(Route);
            CleanupRegistry cleanup = new();
            using LiveServices services = new(Environment(), cleanup, _handler);

            try
            {
                await ActAsync(services);
            }
            catch (InvalidOperationException exception)
            {
                SetupFailure = exception;
            }

            CleanupFailures = await cleanup.RunAsync();
        }

        protected abstract Task ActAsync(LiveServices services);

        [TearDown]
        public void TearDown() => _handler.Dispose();

        protected abstract HttpResponseMessage? Route(HttpRequestMessage request);

        protected static bool Is(HttpRequestMessage request, HttpMethod method, string path) =>
            request.Method == method && request.RequestUri!.AbsolutePath == path;

        private static ProjectionE2EEnvironment Environment() =>
            new(
                new Uri("http://dms.test/"),
                new Uri("http://cms.test/"),
                "admin",
                "admin-secret",
                "postgresql",
                "Host=db;Database=route2",
                [],
                new Dictionary<string, int> { [Tenant] = 5 }
            );
    }

    public abstract class ClaimSetFixture : SetupFailureFixture
    {
        protected override Task ActAsync(LiveServices services) =>
            services.ImportProjectionClaimSetAsync(Tenant, "ProjectionReaderE2E255901");

        protected abstract HttpResponseMessage Export();

        protected virtual HttpResponseMessage Delete() => new(HttpStatusCode.NoContent);

        protected override HttpResponseMessage? Route(HttpRequestMessage request)
        {
            if (Is(request, HttpMethod.Post, "/v3/claimSets/import"))
            {
                HttpResponseMessage created = new(HttpStatusCode.Created);
                created.Headers.Location = new Uri("http://cms.test/v3/claimSets/42");
                return created;
            }

            if (Is(request, HttpMethod.Get, "/v3/claimSets/42/export"))
            {
                return Export();
            }

            return Is(request, HttpMethod.Delete, "/v3/claimSets/42") ? Delete() : null;
        }
    }

    [TestFixture]
    public class Given_an_imported_claim_set_that_grants_another_claim : ClaimSetFixture
    {
        protected override HttpResponseMessage Export() =>
            Json(
                HttpStatusCode.OK,
                """
                {"resourceClaims":[{"claimName":"http://ed-fi.org/identity/claims/services/identity",
                "actions":[{"name":"Read","enabled":true}]}]}
                """
            );

        [Test]
        public void It_reports_the_failed_grant_check() =>
            SetupFailure!
                .Message.Should()
                .Contain("grants [http://ed-fi.org/identity/claims/services/identity#Read]");

        [Test]
        public void It_deletes_the_claim_set_after_the_failed_check() =>
            Requests.Should().ContainInOrder("GET /v3/claimSets/42/export", "DELETE /v3/claimSets/42");

        [Test]
        public void It_reports_no_cleanup_failure() => CleanupFailures.Should().BeEmpty();
    }

    [TestFixture]
    public class Given_an_imported_claim_set_whose_export_fails : ClaimSetFixture
    {
        protected override HttpResponseMessage Export() => new(HttpStatusCode.InternalServerError);

        [Test]
        public void It_reports_the_failed_export() =>
            SetupFailure!
                .Message.Should()
                .Contain("exporting claim set ProjectionReaderE2E255901 answered 500");

        [Test]
        public void It_deletes_the_claim_set() => Requests.Should().Contain("DELETE /v3/claimSets/42");
    }

    public abstract class DataStoreFixture : SetupFailureFixture
    {
        protected override Task ActAsync(LiveServices services) =>
            services.CreateDataStoreAsync(
                Tenant,
                "Projection Reader E2E missing database",
                "Host=db;Database=missing",
                new Dictionary<string, string> { ["districtId"] = "255901", ["schoolYear"] = "2099" }
            );

        protected virtual HttpResponseMessage Delete() => new(HttpStatusCode.NoContent);

        protected override HttpResponseMessage? Route(HttpRequestMessage request)
        {
            if (Is(request, HttpMethod.Post, "/v3/dataStores"))
            {
                return Json(HttpStatusCode.Created, """{"id":7}""");
            }

            if (Is(request, HttpMethod.Post, "/v3/dataStoreContexts"))
            {
                // The first context is added; the second is rejected.
                return Requests.Count(entry => entry == "POST /v3/dataStoreContexts") == 1
                    ? Json(HttpStatusCode.Created, """{"id":1}""")
                    : Json(HttpStatusCode.BadRequest, """{"title":"rejected"}""");
            }

            return Is(request, HttpMethod.Delete, "/v3/dataStores/7") ? Delete() : null;
        }
    }

    [TestFixture]
    public class Given_a_data_store_whose_second_context_is_rejected : DataStoreFixture
    {
        [Test]
        public void It_reports_the_rejected_context() =>
            SetupFailure!.Message.Should().Contain("adding context schoolYear to data store");

        [Test]
        public void It_deletes_the_data_store_after_the_rejected_context() =>
            Requests
                .Should()
                .ContainInOrder(
                    "POST /v3/dataStores",
                    "POST /v3/dataStoreContexts",
                    "POST /v3/dataStoreContexts",
                    "DELETE /v3/dataStores/7"
                );

        [Test]
        public void It_reports_no_cleanup_failure() => CleanupFailures.Should().BeEmpty();
    }

    [TestFixture]
    public class Given_a_rejected_context_whose_data_store_cannot_be_deleted : DataStoreFixture
    {
        protected override HttpResponseMessage Delete() => new(HttpStatusCode.InternalServerError);

        [Test]
        public void It_still_reports_the_rejected_context_as_the_setup_failure() =>
            SetupFailure!.Message.Should().Contain("adding context schoolYear to data store");

        [Test]
        public void It_reports_the_failed_deletion_separately() =>
            CleanupFailures
                .Should()
                .ContainSingle()
                .Which.Should()
                .Contain("data store 7 in Tenant_255901")
                .And.Contain("answered 500");
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}
