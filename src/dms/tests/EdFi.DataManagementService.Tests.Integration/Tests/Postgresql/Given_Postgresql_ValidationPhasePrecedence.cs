// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Postgresql;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace EdFi.DataManagementService.Tests.Integration.Tests.Postgresql;

/// <summary>
/// The endpoint-validation phase answers before the database-validation phase, so when both would
/// fail the request receives the endpoint verdict rather than a database availability error. None of
/// this depends on a derivative or on a routing header: it is the observable consequence of the phase
/// ordering alone, which is why it needs coverage of its own.
///
/// Each concrete fixture breaks exactly one database-validation stage and then drives every hoisted
/// verdict against it, so the matrix is stage by verdict rather than a single spot check.
///
/// The two stages that live in the database are broken before the host boots, which is required rather
/// than incidental: startup instance validation reads the fingerprint and the resource-key seed and
/// caches both verdicts permanently for the primary, so a database mutated after startup would still be
/// served from those cached verdicts and every assertion below would pass vacuously. Every fixture
/// asserts a control proving its stage really does fail, so that failure mode stays detectable.
/// </summary>
public abstract class PostgresqlValidationPhasePrecedenceTestBase : PostgresqlApiIntegrationTestBase
{
    /// <summary>A resource the ApiSchema does not define, so endpoint validation rejects it.</summary>
    private const string UnknownResourceEndpoint = "/data/ed-fi/nonexistentThings";

    /// <summary>A resource the ApiSchema does define, so a request for it clears endpoint validation.</summary>
    private const string StudentsEndpoint = "/data/ed-fi/students";

    private const string ItemPath = "/data/ed-fi/students/00000000-0000-0000-0000-000000000001";

    /// <summary>
    /// The status every stage of the database phase produces. Each test below asserts both that the
    /// response is not this and that it is the hoisted verdict's own status, because the point is which
    /// of two failures answers rather than merely that something failed.
    /// </summary>
    private const HttpStatusCode DatabaseValidationStatus = HttpStatusCode.ServiceUnavailable;

    /// <summary>
    /// Owned by the fixture and handed to the registration, so the request host and the test share one
    /// object. Capturing a service out of the factory container instead does not work: the instance the
    /// request pipeline resolves is not the one the capture observes.
    /// </summary>
    private sealed class HealthSwitch
    {
        public bool IsBroken { get; private set; }

        public void Break() => IsBroken = true;

        public void Restore() => IsBroken = false;
    }

    private readonly HealthSwitch _apiSchemaSwitch = new();
    private readonly HealthSwitch _mappingSetSwitch = new();

    /// <summary>
    /// NUnit reuses one fixture instance across its tests, so a switch broken by one test must be
    /// restored or every later test in the fixture inherits the breakage. This has to run in teardown
    /// rather than setup: the base class builds the host in its own [SetUp], which NUnit runs before a
    /// derived [SetUp], so a switch left broken would already have failed host startup by then. The
    /// switches are restored rather than replaced because the host registration captured these exact
    /// objects.
    /// </summary>
    [TearDown]
    public void RestoreSwitches()
    {
        _apiSchemaSwitch.Restore();
        _mappingSetSwitch.Restore();
    }

    protected override FixtureKey Fixture => FixtureKey.ProfileRootOnlyMerge;

    /// <summary>
    /// The SQL that breaks this fixture's stage, applied to the freshly provisioned database before the
    /// host starts, or null when the fixture breaks a stage that does not live in the database.
    /// </summary>
    protected virtual string? BreakLaterStageSql => null;

    /// <summary>
    /// Breaks a stage that cannot be broken before the host boots. Called at the start of every test,
    /// after the base setup has completed, so startup runs entirely against healthy services.
    /// </summary>
    protected virtual void BreakLaterStageAfterSetup() { }

    protected override async Task<string> LeaseDatabaseAsync(FixtureContext fixture)
    {
        string leasedConnectionString = await base.LeaseDatabaseAsync(fixture);

        if (BreakLaterStageSql is { } sql)
        {
            await using NpgsqlConnection connection = new(leasedConnectionString);
            await connection.OpenAsync();
            await using NpgsqlCommand command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }

        return leasedConnectionString;
    }

    /// <summary>
    /// Wraps the two services these fixtures must be able to fail on demand. Both wrappers delegate
    /// every member to the originally registered implementation while their switch is healthy, so the
    /// host boots and behaves normally, and both are registered unconditionally because a switch that
    /// is never flipped changes nothing.
    /// </summary>
    protected override void ConfigureAdditionalServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        Decorate<IApiSchemaProvider>(
            services,
            inner => new SwitchableApiSchemaProvider(inner, _apiSchemaSwitch)
        );
        Decorate<IMappingSetProvider>(
            services,
            inner => new SwitchableMappingSetProvider(inner, _mappingSetSwitch)
        );
    }

    /// <summary>
    /// Replaces the last registration for <typeparamref name="TService"/> with a singleton that wraps
    /// whatever that registration would have produced. The original descriptor is preserved and used to
    /// build the inner instance, so the production type never has to be named and the wrapper cannot
    /// recurse into itself.
    /// </summary>
    private static void Decorate<TService>(IServiceCollection services, Func<TService, TService> wrap)
        where TService : class
    {
        ServiceDescriptor original = services.Last(descriptor => descriptor.ServiceType == typeof(TService));
        services.Remove(original);

        services.AddSingleton<TService>(serviceProvider =>
        {
            var inner = (TService)(
                original.ImplementationInstance
                ?? original.ImplementationFactory?.Invoke(serviceProvider)
                ?? ActivatorUtilities.CreateInstance(serviceProvider, original.ImplementationType!)
            );

            return wrap(inner);
        });
    }

    /// <summary>Makes the ApiSchema report itself invalid from this point on.</summary>
    private void InvalidateTheApiSchema() => _apiSchemaSwitch.Break();

    /// <summary>Makes request-time mapping-set resolution fail from this point on.</summary>
    protected void StopResolvingMappingSets() => _mappingSetSwitch.Break();

    private static StringContent JsonBody(string json) => new(json, Encoding.UTF8, "application/json");

    [Test]
    public async Task It_answers_503_when_the_request_reaches_the_broken_later_stage()
    {
        BreakLaterStageAfterSetup();

        using HttpResponseMessage response = await Harness.HttpClient.GetAsync(StudentsEndpoint);

        response.StatusCode.Should().Be(DatabaseValidationStatus);
    }

    [Test]
    public async Task It_answers_the_endpoint_404_for_an_unroutable_request()
    {
        BreakLaterStageAfterSetup();

        using HttpResponseMessage response = await Harness.HttpClient.GetAsync(UnknownResourceEndpoint);

        response.StatusCode.Should().NotBe(DatabaseValidationStatus);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task It_answers_the_route_semantics_405_for_a_collection_delete()
    {
        BreakLaterStageAfterSetup();

        using HttpResponseMessage response = await Harness.HttpClient.DeleteAsync(StudentsEndpoint);

        response.StatusCode.Should().NotBe(DatabaseValidationStatus);
        response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
    }

    [Test]
    public async Task It_answers_the_route_semantics_405_for_a_collection_put()
    {
        BreakLaterStageAfterSetup();

        using HttpResponseMessage response = await Harness.HttpClient.PutAsync(
            StudentsEndpoint,
            JsonBody("{}")
        );

        response.StatusCode.Should().NotBe(DatabaseValidationStatus);
        response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
    }

    [Test]
    public async Task It_answers_the_route_semantics_405_for_an_item_post()
    {
        BreakLaterStageAfterSetup();

        using HttpResponseMessage response = await Harness.HttpClient.PostAsync(ItemPath, JsonBody("{}"));

        response.StatusCode.Should().NotBe(DatabaseValidationStatus);
        response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
    }

    /// <summary>
    /// The ApiSchema verdict is the first step of the endpoint phase, so it answers ahead of the whole
    /// database phase. A known resource is used rather than an unknown one, to isolate this from the
    /// endpoint 404 that would otherwise also apply.
    /// </summary>
    [Test]
    public async Task It_answers_the_api_schema_failure_when_the_schema_is_invalid()
    {
        BreakLaterStageAfterSetup();
        InvalidateTheApiSchema();

        using HttpResponseMessage response = await Harness.HttpClient.GetAsync(StudentsEndpoint);

        response.StatusCode.Should().NotBe(DatabaseValidationStatus);
        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
    }

    /// <summary>
    /// Reports the schema invalid once its switch is broken, and otherwise behaves exactly like the
    /// registered provider, so host startup loads and validates the real schema.
    /// </summary>
    private sealed class SwitchableApiSchemaProvider(IApiSchemaProvider inner, HealthSwitch health)
        : IApiSchemaProvider
    {
        public ApiSchemaDocumentNodes GetApiSchemaNodes() => inner.GetApiSchemaNodes();

        public Guid SchemaLoadId => inner.SchemaLoadId;

        public bool IsSchemaValid => !health.IsBroken && inner.IsSchemaValid;

        public List<ApiSchemaFailure> ApiSchemaFailures => inner.ApiSchemaFailures;
    }

    /// <summary>
    /// Delegates to the registered provider until its switch is broken, after which request-time
    /// resolution fails before the inner provider or its cache is consulted. Startup backend mapping
    /// initialization therefore completes normally through the real provider.
    /// </summary>
    private sealed class SwitchableMappingSetProvider(IMappingSetProvider inner, HealthSwitch health)
        : IMappingSetProvider
    {
        private const string Unavailable = "Mapping set deliberately unavailable for a precedence test.";

        public Task<MappingSet> GetOrCreateAsync(MappingSetKey key, CancellationToken cancellationToken) =>
            health.IsBroken
                ? throw new MappingSetUnavailableException(Unavailable, [Unavailable])
                : inner.GetOrCreateAsync(key, cancellationToken);
    }
}

/// <summary>
/// Fingerprint validation fails: the provisioned database has no dms.EffectiveSchema row at all, which
/// is the unprovisioned-database case.
/// </summary>
public sealed class Given_Postgresql_Precedence_Over_Fingerprint_Failure
    : PostgresqlValidationPhasePrecedenceTestBase
{
    protected override string BreakLaterStageSql => """DELETE FROM dms."EffectiveSchema";""";
}

/// <summary>
/// Fingerprint validation succeeds and resource-key validation then fails: the recorded effective
/// schema hash still matches what the process expects, so the fingerprint is valid, while the recorded
/// resource-key seed hash no longer matches the seed the process computed.
/// </summary>
public sealed class Given_Postgresql_Precedence_Over_Resource_Key_Mismatch
    : PostgresqlValidationPhasePrecedenceTestBase
{
    protected override string BreakLaterStageSql =>
        """UPDATE dms."EffectiveSchema" SET "ResourceKeySeedHash" = decode(repeat('ab', 32), 'hex');""";
}

/// <summary>
/// Fingerprint and resource-key validation both succeed and mapping-set resolution then fails, which is
/// the third and last edge the phase reorder moves. The database stays healthy; the switchable provider
/// is what stops resolving, and only after startup has completed through the real one.
/// </summary>
public sealed class Given_Postgresql_Precedence_Over_Mapping_Set_Failure
    : PostgresqlValidationPhasePrecedenceTestBase
{
    protected override void BreakLaterStageAfterSetup() => StopResolvingMappingSets();
}
