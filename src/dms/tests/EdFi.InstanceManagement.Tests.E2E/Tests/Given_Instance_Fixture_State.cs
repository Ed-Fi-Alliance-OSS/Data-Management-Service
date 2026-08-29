// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.InstanceManagement.Tests.E2E.Management;
using FluentAssertions;

namespace EdFi.InstanceManagement.Tests.E2E.UnitTests;

[TestFixture]
[Category("InstanceFixtureUnit")]
public class Given_A_Valid_Instance_Fixture_Environment
{
    private InstanceFixtureState _state = null!;

    [SetUp]
    public void Setup()
    {
        _state = InstanceFixtureState.Parse(FixtureEnvironmentBuilder.Valid());
    }

    [Test]
    public void It_parses_the_engine()
    {
        _state.DatabaseEngine.Should().Be("postgresql");
    }

    [Test]
    public void It_parses_both_canonical_tenants()
    {
        _state
            .Tenants.Select(t => t.Name)
            .Should()
            .BeEquivalentTo(new List<string> { "Tenant_255901", "Tenant_255902" });
    }

    [Test]
    public void It_parses_every_route_with_ownership()
    {
        _state
            .RoutesForTenant("Tenant_255901")
            .Select(r => r.RouteQualifier)
            .Should()
            .BeEquivalentTo(new List<string> { "255901/2024", "255901/2025", "255901/2026" });
        _state
            .RoutesForTenant("Tenant_255902")
            .Select(r => r.RouteQualifier)
            .Should()
            .BeEquivalentTo(new List<string> { "255902/2024" });
    }

    [Test]
    public void It_maps_each_route_to_its_database_and_data_store()
    {
        _state.TryGetRoute("255901/2024", out var route).Should().BeTrue();
        route.DatabaseOrdinal.Should().Be(1);
        route.DatabaseName.Should().Be("db1");
        route.DataStoreId.Should().Be(201);
        route.DistrictContextId.Should().Be(401);
        route.SchoolYearContextId.Should().Be(402);
    }

    [Test]
    public void It_exposes_the_immutable_fixture_id_guard_sets()
    {
        _state.ApplicationIds.Should().BeEquivalentTo(new List<int> { 301, 302 });
        _state.VendorIds.Should().BeEquivalentTo(new List<int> { 101, 102 });
        _state.DataStoreIds.Should().BeEquivalentTo(new List<int> { 201, 202, 203, 204 });
        _state
            .DataStoreContextIds.Should()
            .BeEquivalentTo(new List<int> { 401, 402, 403, 404, 405, 406, 407, 408 });
    }

    [Test]
    public void It_recognizes_fixture_tenants()
    {
        _state.IsFixtureTenant("Tenant_255901").Should().BeTrue();
        _state.IsFixtureTenant("Tenant_Setup_255901").Should().BeFalse();
    }

    [Test]
    public void It_exposes_the_pre_registered_application_credentials()
    {
        _state.GetTenant("Tenant_255901").ClientKey.Should().Be(FixtureEnvironmentBuilder.Tenant1Key);
        _state.GetTenant("Tenant_255902").ClientSecret.Should().Be(FixtureEnvironmentBuilder.Tenant2Secret);
    }

    [Test]
    public void It_identifies_the_single_route_that_carries_derivatives()
    {
        _state.DerivativeRoutingRoute.RouteQualifier.Should().Be("255901/2026");
        _state.Routes.Count(r => r.HasDerivatives).Should().Be(1);
    }

    [Test]
    public void It_maps_each_derivative_to_the_database_that_serves_it()
    {
        var route = _state.DerivativeRoutingRoute;

        route.GetDerivative(InstanceFixtureDerivativeTypes.ReadReplica).DatabaseName.Should().Be("db1");
        route.GetDerivative(InstanceFixtureDerivativeTypes.Snapshot).DatabaseName.Should().Be("db2");
    }

    [Test]
    public void It_points_each_derivative_at_a_database_another_route_owns()
    {
        // This is what lets a scenario seed the replica and the snapshot through the ordinary API
        // instead of writing to them directly.
        foreach (var derivative in _state.DerivativeRoutingRoute.Derivatives)
        {
            _state
                .Routes.Should()
                .Contain(
                    r => r.DatabaseOrdinal == derivative.DatabaseOrdinal,
                    $"the {derivative.DerivativeType} database must be reachable through its own route"
                );
        }
    }

    [Test]
    public void It_leaves_every_other_route_without_derivatives()
    {
        _state
            .Routes.Where(r => r.RouteQualifier != "255901/2026")
            .Should()
            .OnlyContain(r => !r.HasDerivatives);
    }
}

[TestFixture]
[Category("InstanceFixtureUnit")]
public class Given_An_Invalid_Instance_Fixture_Environment
{
    private static void ParseShouldThrow(Dictionary<string, string?> environment)
    {
        var act = () => InstanceFixtureState.Parse(environment);
        act.Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void It_fails_when_the_manifest_is_missing()
    {
        ParseShouldThrow(FixtureEnvironmentBuilder.With("INSTANCE_E2E_ROUTE_MANIFEST", null));
    }

    [Test]
    public void It_fails_when_the_manifest_is_not_valid_json()
    {
        ParseShouldThrow(FixtureEnvironmentBuilder.With("INSTANCE_E2E_ROUTE_MANIFEST", "{ not json"));
    }

    [Test]
    public void It_fails_when_the_manifest_has_the_wrong_route_count()
    {
        var manifest = """
            [
              {"tenant":"Tenant_255901","districtId":"255901","schoolYear":"2024","databaseOrdinal":1,"databaseName":"db1","dataStoreId":201,"districtContextId":401,"schoolYearContextId":402}
            ]
            """;
        ParseShouldThrow(FixtureEnvironmentBuilder.With("INSTANCE_E2E_ROUTE_MANIFEST", manifest));
    }

    [Test]
    public void It_fails_when_a_manifest_database_name_disagrees_with_the_ordinal_variable()
    {
        ParseShouldThrow(FixtureEnvironmentBuilder.With("INSTANCE_E2E_DATABASE_1_NAME", "unexpected"));
    }

    [Test]
    public void It_fails_when_the_engine_is_unsupported()
    {
        ParseShouldThrow(FixtureEnvironmentBuilder.With("INSTANCE_E2E_DATABASE_ENGINE", "oracle"));
    }

    [Test]
    public void It_fails_when_a_tenant_variable_is_missing()
    {
        ParseShouldThrow(FixtureEnvironmentBuilder.With("INSTANCE_E2E_FIXTURE_TENANT_2_CLIENT_KEY", null));
    }

    [Test]
    public void It_fails_when_a_vendor_id_is_not_an_integer()
    {
        ParseShouldThrow(
            FixtureEnvironmentBuilder.With("INSTANCE_E2E_FIXTURE_TENANT_1_VENDOR_ID", "not-a-number")
        );
    }

    [Test]
    public void It_fails_when_the_declared_data_store_ids_disagree_with_the_manifest()
    {
        ParseShouldThrow(FixtureEnvironmentBuilder.With("INSTANCE_E2E_FIXTURE_DATASTORE_IDS", "201,202,999"));
    }

    [Test]
    public void It_fails_when_the_manifest_references_an_undeclared_tenant()
    {
        var manifest = FixtureEnvironmentBuilder.RouteManifestJson.Replace(
            "Tenant_255902",
            "Tenant_Unknown",
            StringComparison.Ordinal
        );
        ParseShouldThrow(FixtureEnvironmentBuilder.With("INSTANCE_E2E_ROUTE_MANIFEST", manifest));
    }

    [Test]
    public void It_fails_when_the_manifest_carries_an_application_secret()
    {
        var manifest = FixtureEnvironmentBuilder.RouteManifestJson.Replace(
            "\"db1\"",
            $"\"{FixtureEnvironmentBuilder.Tenant1Secret}\"",
            StringComparison.Ordinal
        );
        // The database-name mismatch would also fail, so align the ordinal variable to isolate the secret check.
        var environment = FixtureEnvironmentBuilder.With("INSTANCE_E2E_ROUTE_MANIFEST", manifest);
        environment["INSTANCE_E2E_DATABASE_1_NAME"] = FixtureEnvironmentBuilder.Tenant1Secret;
        ParseShouldThrow(environment);
    }

    /// <summary>
    /// The derivative entries of the canonical manifest, exactly as the build orchestration writes them.
    /// Each test below rewrites one of them into a single specific invalid arrangement.
    /// </summary>
    private const string ReplicaEntry =
        "{\"derivativeType\":\"ReadReplica\",\"databaseOrdinal\":1,\"databaseName\":\"db1\"}";

    private const string SnapshotEntry =
        "{\"derivativeType\":\"Snapshot\",\"databaseOrdinal\":2,\"databaseName\":\"db2\"}";

    private static Dictionary<string, string?> WithRewrittenManifest(string find, string replace)
    {
        var manifest = FixtureEnvironmentBuilder.RouteManifestJson.Replace(
            find,
            replace,
            StringComparison.Ordinal
        );

        // A rewrite that matched nothing would leave the canonical manifest in place, and the test would
        // then assert against a valid arrangement rather than the invalid one it names.
        manifest.Should().NotBe(FixtureEnvironmentBuilder.RouteManifestJson, "the rewrite must apply");

        return FixtureEnvironmentBuilder.With("INSTANCE_E2E_ROUTE_MANIFEST", manifest);
    }

    [Test]
    public void It_fails_when_no_route_carries_derivatives()
    {
        // Without a derivative arrangement the routing scenarios would pass trivially against the
        // primary, so an unregistered arrangement must fail loudly instead.
        ParseShouldThrow(WithRewrittenManifest(ReplicaEntry + "," + SnapshotEntry, string.Empty));
    }

    [Test]
    public void It_fails_when_a_second_route_also_carries_derivatives()
    {
        // A replica on another route would silently serve that route's reads from a different database.
        ParseShouldThrow(
            WithRewrittenManifest(
                "\"schoolYearContextId\":406,\"derivatives\":[]",
                "\"schoolYearContextId\":406,\"derivatives\":[" + SnapshotEntry + "]"
            )
        );
    }

    [Test]
    public void It_fails_when_a_derivative_points_at_its_own_parent_database()
    {
        // Such an arrangement would make a derivative-routed read indistinguishable from a primary read.
        ParseShouldThrow(
            WithRewrittenManifest(
                ReplicaEntry,
                "{\"derivativeType\":\"ReadReplica\",\"databaseOrdinal\":4,\"databaseName\":\"db4\"}"
            )
        );
    }

    [Test]
    public void It_fails_when_both_derivatives_point_at_the_same_database()
    {
        // Snapshot precedence could not be proven if the two targets were the same database.
        ParseShouldThrow(
            WithRewrittenManifest(
                SnapshotEntry,
                "{\"derivativeType\":\"Snapshot\",\"databaseOrdinal\":1,\"databaseName\":\"db1\"}"
            )
        );
    }

    [Test]
    public void It_fails_when_a_derivative_type_is_unknown()
    {
        ParseShouldThrow(
            WithRewrittenManifest(
                SnapshotEntry,
                "{\"derivativeType\":\"Mirror\",\"databaseOrdinal\":2,\"databaseName\":\"db2\"}"
            )
        );
    }

    [Test]
    public void It_fails_when_a_derivative_database_name_disagrees_with_the_ordinal_variable()
    {
        ParseShouldThrow(
            WithRewrittenManifest(
                ReplicaEntry,
                "{\"derivativeType\":\"ReadReplica\",\"databaseOrdinal\":1,\"databaseName\":\"unexpected\"}"
            )
        );
    }
}
