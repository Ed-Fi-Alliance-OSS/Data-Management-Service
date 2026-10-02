// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Identity;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace EdFi.DataManagementService.Tests.Integration.Plugins.Identity;

/// <summary>
/// F6 at the provider boundary: the context equality rules HTTP routing cannot produce - qualifier
/// value case, qualifier name case, dictionary insertion order and comparer, missing and extra
/// qualifiers, a null tenant, and client ids that differ only by case - are exercised by calling the
/// resolved <see cref="IIdentityService"/> with crafted <see cref="IdentityRequestContext"/> values.
/// </summary>
/// <remarks>
/// The host has no deliberate-throw variant, so resolving the service from a test scope is only an
/// extra, harmless activation. The client whose id differs only by case is granted the namespace, so
/// its denial for a job is ownership. A client with a case-distinct id and no grant is denied the
/// namespace itself, which shows grants compare the client id exactly.
/// </remarks>
[Category("PluginIntegration")]
public sealed class Given_CraftedRequestContextsReachTheIdentityProvider
{
    private const string FixturePlugin = "Acme.IdentityFixture";
    private const string Tenant = IdentityTestClients.TenantOne;
    private const string AlphaDistrict = "Abc";

    private IdentityPluginHost? _host;
    private IServiceScope? _scope;
    private IIdentityService _service = null!;
    private string _district = string.Empty;
    private string _personId = string.Empty;
    private string _alphaPersonId = string.Empty;
    private string _token = string.Empty;

    [OneTimeSetUp]
    public async Task Setup()
    {
        _district = IdentityTestClients.DistrictsOf(Tenant)[0];

        IdentityFixtureSettings fixture = new IdentityFixtureSettings()
            .Namespace("ns-a", (Tenant, _district))
            .Namespace("ns-alpha", (Tenant, AlphaDistrict))
            .Grant(IdentityTestClients.ClientA.ClientId, Tenant, "ns-a")
            .Grant(IdentityTestClients.ClientA.ClientId, Tenant, "ns-alpha")
            .Grant(IdentityTestClients.CaseVariantOfA.ClientId, Tenant, "ns-a")
            .With("IdentityFixture:PollsUntilComplete", "0");
        _host = IdentityPluginHost.Create(
            [FixturePlugin],
            allowed: FixturePlugin,
            fixtureSettings: fixture.Settings
        );

        // Starts the host; the scope below then resolves the plugin's service from the real container.
        _host.TryBoot().Should().BeNull();
        _scope = _host.Factory.Services.CreateScope();
        _service = _scope.ServiceProvider.GetRequiredService<IIdentityService>();

        IdentityResult created = await _service.CreateAsync(
            new JsonObject { ["LastSurname"] = "Rivera" },
            Context(),
            CancellationToken.None
        );
        _personId = created.Payload!.GetValue<string>();

        IdentityResult alphaCreated = await _service.CreateAsync(
            new JsonObject { ["LastSurname"] = "Alpha" },
            Context(qualifiers: Qualifiers(AlphaDistrict)),
            CancellationToken.None
        );
        _alphaPersonId = alphaCreated.Payload!.GetValue<string>();

        IdentityAsyncResult accepted = await _service.FindAsync(
            ["~fixture:async"],
            Context(),
            CancellationToken.None
        );
        _token = accepted.RequestToken!;
    }

    private IReadOnlyDictionary<string, string> Qualifiers(string? district = null) =>
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["districtId"] = district ?? _district,
            ["schoolYear"] = IdentityTestClients.SchoolYear,
        };

    private IdentityRequestContext Context(
        string? tenant = Tenant,
        IReadOnlyDictionary<string, string>? qualifiers = null,
        string? clientId = null,
        string traceId = "trace-original"
    ) =>
        new()
        {
            Tenant = tenant,
            RouteQualifiers = qualifiers ?? Qualifiers(),
            ClientId = clientId ?? IdentityTestClients.ClientA.ClientId,
            TraceId = traceId,
        };

    [OneTimeTearDown]
    public async Task TearDown()
    {
        _scope?.Dispose();

        if (_host is not null)
        {
            await _host.DisposeAsync();
            _host = null;
        }
    }

    private async Task<IdentityResultStatus> GetStatusAsync(
        IdentityRequestContext context,
        string? id = null
    ) => (await _service.GetByIdAsync(id ?? _personId, context, CancellationToken.None)).Status;

    private async Task<IdentityResultStatus> ResultsStatusAsync(IdentityRequestContext context) =>
        (await _service.ResultsAsync(_token, context, CancellationToken.None)).Status;

    private static IReadOnlyDictionary<string, string> Reversed(string district) =>
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["schoolYear"] = IdentityTestClients.SchoolYear,
            ["districtId"] = district,
        };

    private static IReadOnlyDictionary<string, string> OrdinalComparer(
        string districtName,
        string yearName,
        string district
    ) =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [yearName] = IdentityTestClients.SchoolYear,
            [districtName] = district,
        };

    [Test]
    public async Task It_resolves_the_namespace_under_the_original_context()
    {
        (await GetStatusAsync(Context())).Should().Be(IdentityResultStatus.Success);
    }

    [Test]
    public async Task It_resolves_the_namespace_when_the_tenant_differs_only_by_case()
    {
        (await GetStatusAsync(Context(tenant: "TENANT-ONE"))).Should().Be(IdentityResultStatus.Success);
    }

    [Test]
    public async Task It_resolves_the_namespace_when_the_qualifiers_were_inserted_in_another_order()
    {
        (await GetStatusAsync(Context(qualifiers: Reversed(_district))))
            .Should()
            .Be(IdentityResultStatus.Success);
    }

    [Test]
    public async Task It_resolves_the_namespace_when_the_qualifier_names_differ_only_by_case_in_a_case_sensitive_dictionary()
    {
        (await GetStatusAsync(Context(qualifiers: OrdinalComparer("DISTRICTID", "SchoolYear", _district))))
            .Should()
            .Be(IdentityResultStatus.Success);
    }

    [Test]
    public async Task It_resolves_the_namespace_when_a_qualifier_value_differs_only_by_case()
    {
        IdentityRequestContext upper = Context(qualifiers: Qualifiers("ABC"));
        IdentityRequestContext lower = Context(qualifiers: Qualifiers("abc"));

        (await GetStatusAsync(upper, _alphaPersonId)).Should().Be(IdentityResultStatus.Success);
        (await GetStatusAsync(lower, _alphaPersonId)).Should().Be(IdentityResultStatus.Success);
    }

    [Test]
    public async Task It_denies_a_context_missing_a_qualifier()
    {
        IReadOnlyDictionary<string, string> missing = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase
        )
        {
            ["districtId"] = _district,
        };

        (await GetStatusAsync(Context(qualifiers: missing))).Should().Be(IdentityResultStatus.NotFound);
    }

    [Test]
    public async Task It_denies_a_context_with_an_extra_qualifier()
    {
        IReadOnlyDictionary<string, string> extra = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase
        )
        {
            ["districtId"] = _district,
            ["schoolYear"] = IdentityTestClients.SchoolYear,
            ["campusId"] = "1",
        };

        (await GetStatusAsync(Context(qualifiers: extra))).Should().Be(IdentityResultStatus.NotFound);
    }

    [Test]
    public async Task It_denies_a_context_with_a_different_qualifier_value()
    {
        (await GetStatusAsync(Context(qualifiers: Qualifiers("255999"))))
            .Should()
            .Be(IdentityResultStatus.NotFound);
    }

    [Test]
    public async Task It_denies_a_context_with_no_tenant_because_a_missing_tenant_equals_only_a_missing_tenant()
    {
        (await GetStatusAsync(Context(tenant: null))).Should().Be(IdentityResultStatus.NotFound);
    }

    [Test]
    public async Task It_denies_a_client_whose_id_differs_only_by_case_when_nothing_grants_it()
    {
        (await GetStatusAsync(Context(clientId: "Identity-Client-A")))
            .Should()
            .Be(IdentityResultStatus.NotFound);
    }

    [Test]
    public async Task It_polls_the_job_under_equivalent_contexts_of_the_same_client()
    {
        (await ResultsStatusAsync(Context(tenant: "TENANT-ONE"))).Should().Be(IdentityResultStatus.Success);
        (await ResultsStatusAsync(Context(qualifiers: Reversed(_district))))
            .Should()
            .Be(IdentityResultStatus.Success);
        (await ResultsStatusAsync(Context(traceId: "trace-other"))).Should().Be(IdentityResultStatus.Success);
    }

    [Test]
    public async Task It_denies_the_job_to_a_context_missing_or_adding_a_qualifier_or_changing_a_value()
    {
        IReadOnlyDictionary<string, string> missing = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase
        )
        {
            ["districtId"] = _district,
        };
        IReadOnlyDictionary<string, string> extra = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase
        )
        {
            ["districtId"] = _district,
            ["schoolYear"] = IdentityTestClients.SchoolYear,
            ["campusId"] = "1",
        };

        (await ResultsStatusAsync(Context(qualifiers: missing))).Should().Be(IdentityResultStatus.NotFound);
        (await ResultsStatusAsync(Context(qualifiers: extra))).Should().Be(IdentityResultStatus.NotFound);
        (await ResultsStatusAsync(Context(qualifiers: Qualifiers("255999"))))
            .Should()
            .Be(IdentityResultStatus.NotFound);
        (await ResultsStatusAsync(Context(tenant: null))).Should().Be(IdentityResultStatus.NotFound);
    }

    [Test]
    public async Task It_denies_the_job_to_the_granted_client_whose_id_differs_only_by_case()
    {
        // Granted the namespace, so the person is readable; the job still belongs to the exact client id.
        (await GetStatusAsync(Context(clientId: IdentityTestClients.CaseVariantOfA.ClientId)))
            .Should()
            .Be(IdentityResultStatus.Success);
        (await ResultsStatusAsync(Context(clientId: IdentityTestClients.CaseVariantOfA.ClientId)))
            .Should()
            .Be(IdentityResultStatus.NotFound);
    }
}
