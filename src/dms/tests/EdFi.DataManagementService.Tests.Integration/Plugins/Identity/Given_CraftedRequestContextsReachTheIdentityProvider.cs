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
/// namespace itself, which shows grants compare the client id exactly. The job-binding contexts (a
/// missing qualifier, an extra qualifier, another district) are mapped to the same granted namespace as
/// the issuing context, so only job binding can deny them, and each one also polls a job issued under
/// itself. The namespace-selection cases use contexts no namespace maps. A null tenant context is a
/// namespace-selection case only, because mapping it would remove the unmapped null-tenant control.
/// </remarks>
[Category("PluginIntegration")]
public sealed class Given_CraftedRequestContextsReachTheIdentityProvider
{
    private const string FixturePlugin = "Acme.IdentityFixture";
    private const string Tenant = IdentityTestClients.TenantOne;
    private const string AlphaDistrict = "Abc";
    private const string OtherDistrict = "255999";

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
            .NamespaceWithQualifiers(
                "ns-a",
                (Tenant, Qualifiers()),
                (Tenant, MissingSchoolYear()),
                (Tenant, WithCampus("1")),
                (Tenant, Qualifiers(OtherDistrict))
            )
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

    private IReadOnlyDictionary<string, string> MissingSchoolYear() =>
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["districtId"] = _district };

    private IReadOnlyDictionary<string, string> WithCampus(string campus) =>
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["districtId"] = _district,
            ["schoolYear"] = IdentityTestClients.SchoolYear,
            ["campusId"] = campus,
        };

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
            ["schoolYear"] = IdentityTestClients.SchoolYear,
        };

        (await GetStatusAsync(Context(qualifiers: missing))).Should().Be(IdentityResultStatus.NotFound);
    }

    [Test]
    public async Task It_denies_a_context_with_an_extra_qualifier()
    {
        (await GetStatusAsync(Context(qualifiers: WithCampus("2"))))
            .Should()
            .Be(IdentityResultStatus.NotFound);
    }

    [Test]
    public async Task It_denies_a_context_with_a_different_qualifier_value()
    {
        (await GetStatusAsync(Context(qualifiers: Qualifiers("255998"))))
            .Should()
            .Be(IdentityResultStatus.NotFound);
    }

    [Test]
    public async Task It_selects_no_namespace_for_a_context_with_no_tenant_because_a_missing_tenant_equals_only_a_missing_tenant()
    {
        // A namespace-selection denial: no namespace maps a context without a tenant, so neither a
        // lookup nor a job poll gets as far as job binding.
        (await GetStatusAsync(Context(tenant: null)))
            .Should()
            .Be(IdentityResultStatus.NotFound);
        (await ResultsStatusAsync(Context(tenant: null))).Should().Be(IdentityResultStatus.NotFound);
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
    public async Task It_reaches_the_namespace_from_each_job_binding_context()
    {
        // The control that these contexts are mapped and granted, so job binding alone decides them.
        foreach (IdentityRequestContext context in JobBindingContexts())
        {
            (await GetStatusAsync(context)).Should().Be(IdentityResultStatus.Success);
        }
    }

    [Test]
    public async Task It_polls_a_job_under_the_context_that_issued_it_for_each_job_binding_context()
    {
        foreach (IdentityRequestContext context in JobBindingContexts())
        {
            IdentityAsyncResult accepted = await _service.FindAsync(
                ["~fixture:async"],
                context,
                CancellationToken.None
            );

            (await _service.ResultsAsync(accepted.RequestToken!, context, CancellationToken.None))
                .Status.Should()
                .Be(IdentityResultStatus.Success);
        }
    }

    [Test]
    public async Task It_denies_the_job_to_a_granted_context_missing_a_qualifier()
    {
        (await ResultsStatusAsync(Context(qualifiers: MissingSchoolYear())))
            .Should()
            .Be(IdentityResultStatus.NotFound);
    }

    [Test]
    public async Task It_denies_the_job_to_a_granted_context_adding_a_qualifier()
    {
        (await ResultsStatusAsync(Context(qualifiers: WithCampus("1"))))
            .Should()
            .Be(IdentityResultStatus.NotFound);
    }

    [Test]
    public async Task It_denies_the_job_to_a_granted_context_changing_a_qualifier_value()
    {
        (await ResultsStatusAsync(Context(qualifiers: Qualifiers(OtherDistrict))))
            .Should()
            .Be(IdentityResultStatus.NotFound);
    }

    private IEnumerable<IdentityRequestContext> JobBindingContexts() =>
        [
            Context(qualifiers: MissingSchoolYear()),
            Context(qualifiers: WithCampus("1")),
            Context(qualifiers: Qualifiers(OtherDistrict)),
        ];

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
