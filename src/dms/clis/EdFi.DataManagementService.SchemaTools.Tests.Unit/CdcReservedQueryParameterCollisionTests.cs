// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DataManagementService.Backend.Cdc;
using EdFi.DataManagementService.Backend.Cdc.Tests.Unit;
using EdFi.DataManagementService.Core.Startup;
using EdFi.DataManagementService.Core.Validation;
using EdFi.DataManagementService.SchemaTools.Cdc;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using DdlProvider = EdFi.DataManagementService.Backend.Ddl.CdcProvider;
using DdlSetupRequest = EdFi.DataManagementService.Backend.Ddl.CdcProviderSetupRequest;

namespace EdFi.DataManagementService.SchemaTools.Tests.Unit;

/// <summary>
/// The one collision these fixtures are written around, and the names they expect reported back.
/// </summary>
internal static class CdcCollision
{
    internal const string SchemaSource = "extension[0]";
    internal const string FieldName = "pageSize";
    internal const string Sentinel = "private-schema-path-sentinel";

    internal static ApiSchemaNormalizationResult.ReservedQueryParameterCollisionResult Result { get; } =
        new([
            new ApiSchemaNormalizationResult.ReservedQueryParameterCollision(
                SchemaSource,
                "tpdm",
                "candidates",
                FieldName,
                ReservedQueryParameters.All.Single(reserved => reserved.Name == FieldName)
            ),
        ]);
}

/// <summary>
/// Which ApiSchema load failures a CDC command carries far enough to describe, and which stay generic.
/// </summary>
/// <remarks>
/// The CDC path loads ApiSchemas without going through <see cref="Commands.LoadResultErrorHandler"/>,
/// so before DMS-1442 added this arm a reserved query parameter collision reached the operator as
/// "Deployment input is invalid." alone, naming neither the schema nor the field to rename.
/// </remarks>
[TestFixture]
public class Given_A_Cdc_Command_Schema_Load_Result
{
    [Test]
    public void It_refuses_a_reserved_query_parameter_collision_as_its_own_classification()
    {
        Action act = () =>
            CdcCommandConfiguration.RequireNormalizedSchemas(
                new ApiSchemaFileLoadResult.NormalizationFailureResult(CdcCollision.Result)
            );

        act.Should()
            .Throw<CdcReservedQueryParameterCollisionException>()
            .Which.Collision.Should()
            .BeSameAs(CdcCollision.Result);
    }

    /// <summary>
    /// The narrowing the arm depends on. Every other failure keeps the refusal that discloses nothing,
    /// including the other normalization failures, whose details are schema-supplied.
    /// </summary>
    [TestCaseSource(nameof(OtherFailures))]
    public void It_leaves_every_other_load_failure_generic(ApiSchemaFileLoadResult loaded)
    {
        Action act = () => CdcCommandConfiguration.RequireNormalizedSchemas(loaded);

        act.Should().Throw<ArgumentException>().WithMessage("CDC command input is invalid.");
    }

    [Test]
    public void It_returns_the_normalized_nodes_of_a_successful_load()
    {
        var loaded = new ApiSchemaFileLoader(
            new ApiSchemaInputNormalizer(NullLogger<ApiSchemaInputNormalizer>.Instance),
            NullLogger<ApiSchemaFileLoader>.Instance
        ).Load(
            Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "minimal-api-schema.json"),
            []
        );

        CdcCommandConfiguration
            .RequireNormalizedSchemas(loaded)
            .Should()
            .BeSameAs(
                loaded.Should().BeOfType<ApiSchemaFileLoadResult.SuccessResult>().Subject.NormalizedNodes
            );
    }

    private static IEnumerable<TestCaseData> OtherFailures()
    {
        yield return new TestCaseData(
            new ApiSchemaFileLoadResult.FileNotFoundResult(CdcCollision.Sentinel)
        ).SetName("{m}(FileNotFound)");
        yield return new TestCaseData(
            new ApiSchemaFileLoadResult.FileReadErrorResult(CdcCollision.Sentinel, CdcCollision.Sentinel)
        ).SetName("{m}(FileReadError)");
        yield return new TestCaseData(
            new ApiSchemaFileLoadResult.InvalidJsonResult(CdcCollision.Sentinel, CdcCollision.Sentinel)
        ).SetName("{m}(InvalidJson)");
        yield return new TestCaseData(
            new ApiSchemaFileLoadResult.NormalizationFailureResult(
                new ApiSchemaNormalizationResult.MissingOrMalformedProjectSchemaResult(
                    CdcCollision.Sentinel,
                    CdcCollision.Sentinel
                )
            )
        ).SetName("{m}(MalformedProjectSchema)");
        yield return new TestCaseData(
            new ApiSchemaFileLoadResult.NormalizationFailureResult(
                new ApiSchemaNormalizationResult.ProjectEndpointNameCollisionResult([
                    new ApiSchemaNormalizationResult.EndpointNameCollision(
                        CdcCollision.Sentinel,
                        [CdcCollision.Sentinel]
                    ),
                ])
            )
        ).SetName("{m}(EndpointNameCollision)");
    }
}

/// <summary>
/// What a CDC command puts on each of its streams when it is refused for a collision.
/// </summary>
/// <remarks>
/// Driven through the production <see cref="CdcCommandRunner.RunAsync"/>, so the settings load, the
/// controller validation, and the output boundary are the real ones. Only request construction is
/// replaced, because that is what reads the workflow journal, whose local store is POSIX-only; what
/// the seam throws is what request construction itself throws for this schema.
/// </remarks>
[TestFixture]
public class Given_A_Cdc_Command_Refused_For_A_Reserved_Query_Parameter_Collision
{
    private CdcCommandResult _result = null!;
    private string _diagnostics = string.Empty;

    [SetUp]
    public async Task Setup() =>
        (_result, _diagnostics) = await CdcCommandOutput.RunAsync(() =>
            throw new CdcReservedQueryParameterCollisionException(CdcCollision.Result)
        );

    [Test]
    public void It_names_the_schema_the_resource_and_the_field() =>
        _diagnostics
            .Should()
            .Contain("Error: ")
            .And.Contain(CdcCollision.SchemaSource)
            .And.Contain("tpdm/candidates")
            .And.Contain(CdcCollision.FieldName);

    [Test]
    public void It_says_what_the_name_is_taken_for() =>
        _diagnostics.Should().Contain("cursor paging page size");

    [Test]
    public void It_gives_the_rename_and_rebuild_remediation() =>
        _diagnostics.Should().Contain("Rename the colliding property").And.Contain("rebuild the ApiSchema");

    [Test]
    public void It_keeps_the_classification_the_command_already_emitted()
    {
        _result.Succeeded.Should().BeFalse();
        _result.ExitCode.Should().Be(2);
        _result
            .Diagnostics.Should()
            .ContainSingle()
            .Which.Should()
            .Match<CdcDeploymentDiagnostic>(diagnostic =>
                diagnostic.Component == CdcDeploymentComponent.Request
                && diagnostic.Failure == CdcDeploymentFailure.InvalidInput
            );
    }

    /// <summary>The description is operator-facing only; the machine-readable result is unchanged.</summary>
    [Test]
    public void It_puts_no_schema_detail_in_the_emitted_result() =>
        JsonSerializer
            .Serialize(_result, CdcCommandHost.JsonOptions)
            .Should()
            .NotContain(CdcCollision.SchemaSource)
            .And.NotContain("candidates")
            .And.NotContain(CdcCollision.FieldName);
}

/// <summary>
/// The refusal raised when the projection runtime prepares the schema rather than when the request is
/// constructed, which is where every operation that does not prepare eagerly discovers it.
/// </summary>
/// <remarks>
/// Its point is that the runner describes the refusal at the moment preparation raises it, before the
/// exception is handed on. That placement is what covers the operations whose controllers catch it and
/// classify it into a typed diagnostic, which cannot carry a field name: emitting before the rethrow
/// cannot be suppressed by whatever the controller then does with it.
/// </remarks>
/// <remarks>
/// What this fixture does not reach is a controller actually swallowing it. Controller-initiated
/// preparation needs established provenance and live Kafka/Connect, so it belongs to the integration
/// harness. What is pinned here is that the description is written where preparation fails, and once:
/// the exception goes on to reach the runner's own catch as well, and the operator is told a single
/// time. Deleting the emission at the preparation site still passes these, because that second site
/// covers this operation; removing the once-only guard fails, which is what shows the first site runs.
/// </remarks>
[TestFixture]
public class Given_A_Cdc_Command_Refused_While_Preparing_Projection
{
    private string _diagnostics = string.Empty;

    [SetUp]
    public async Task Setup() =>
        (_, _diagnostics) = await CdcCommandOutput.RunAsync(() =>
            CdcCommandOutput.DeferredRequest(() =>
                throw new CdcReservedQueryParameterCollisionException(CdcCollision.Result)
            )
        );

    [Test]
    public void It_names_the_refused_query_field_once() =>
        _diagnostics
            .Should()
            .Contain("Error: ")
            .And.Contain(CdcCollision.SchemaSource)
            .And.Contain(CdcCollision.FieldName)
            .And.Contain("Rename the colliding property");

    /// <summary>A watch pass may retry the preparation that raised it; the operator is told once.</summary>
    [Test]
    public void It_describes_the_refusal_exactly_once() =>
        _diagnostics.Split("Error: ").Length.Should().Be(2);
}

/// <summary>
/// The same command refused for any other invalid input, which stays generic.
/// </summary>
[TestFixture]
public class Given_A_Cdc_Command_Refused_For_Other_Invalid_Input
{
    private CdcCommandResult _result = null!;
    private string _diagnostics = string.Empty;

    [SetUp]
    public async Task Setup() =>
        (_result, _diagnostics) = await CdcCommandOutput.RunAsync(() =>
            throw new ArgumentException(CdcCollision.Sentinel)
        );

    [Test]
    public void It_describes_nothing_beyond_the_typed_diagnostic()
    {
        _diagnostics.Should().NotContain("Error: ").And.NotContain(CdcCollision.Sentinel);
        _result
            .Diagnostics.Should()
            .ContainSingle()
            .Which.Message.Should()
            .Be("Deployment input is invalid.");
    }

    [Test]
    public void It_emits_the_same_classification_and_exit_code()
    {
        _result.ExitCode.Should().Be(2);
        _result
            .Diagnostics.Should()
            .ContainSingle()
            .Which.Should()
            .Match<CdcDeploymentDiagnostic>(diagnostic =>
                diagnostic.Component == CdcDeploymentComponent.Request
                && diagnostic.Failure == CdcDeploymentFailure.InvalidInput
            );
    }
}

/// <summary>
/// Runs the production runner against settings valid enough to reach request construction, and returns
/// the result it emits together with everything written to the writer the host supplies as stderr.
/// </summary>
internal static class CdcCommandOutput
{
    internal static async Task<(CdcCommandResult Result, string Diagnostics)> RunAsync(
        Func<CdcDeploymentRequest> onRequest
    )
    {
        string root = Path.Combine(Path.GetTempPath(), "cdc-collision-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string settingsPath = Path.Combine(root, "settings.txt");
            await File.WriteAllTextAsync(settingsPath, JsonSerializer.Serialize(Settings));
            var runner = new CdcCommandRunner(A.Fake<IApiSchemaFileLoader>(), null!)
            {
                CreateRequest = (_, _, _, _, _, _, _, _) => Task.FromResult(onRequest()),
            };
            using var progress = new StringWriter();

            var result = await runner.RunAsync(
                new(CdcCommandOperation.Validate, settingsPath, root, 1, 0, false, "", false),
                progress,
                default
            );

            return (result, progress.ToString());
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>
    /// A request that prepares its provider setup lazily and fails when it does, which is the shape
    /// request construction returns for every operation that does not prepare projection eagerly.
    /// </summary>
    internal static CdcDeploymentRequest DeferredRequest(Func<DdlSetupRequest> providerSetup)
    {
        var template = CdcConnectorTemplateTestData.BuildRequest(DdlProvider.Postgresql);
        return CdcDeploymentRequest.CreateDeferred(
            template.Binding,
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:Dms"] = "Host=private-source-host;Password=private",
                        ["DocumentCache:Targets:0:DataStoreId"] = "1",
                    }
                )
                .Build(),
            providerSetup,
            new Uri("http://connect:8083/"),
            new Uri("http://connect:9404/metrics"),
            template.DeploymentPolicy,
            CdcDeploymentRequestTestData.Worker(),
            new(
                DdlProvider.Postgresql,
                new Dictionary<string, string>(template.ProviderConnectionProperties.Properties)
                {
                    ["database.password"] = "${env:DATABASE_PASSWORD}",
                    ["database.hostname"] = "private-source-host",
                }
            ),
            template.KafkaClientSecurityProperties,
            new(TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(1))
        );
    }

    /// <summary>
    /// The smallest settings set that passes controller validation and reaches request construction.
    /// </summary>
    private static Dictionary<string, string> Settings { get; } =
        new()
        {
            ["Cdc:KafkaAdminBootstrapServers"] = "localhost:9092",
            ["AppSettings:Datastore"] = "postgresql",
            ["Cdc:Provider"] = "postgresql",
            ["Cdc:LagThresholdMilliseconds"] = "5000",
            ["Cdc:DeploymentKey"] = "local",
            ["Cdc:DataStoreId"] = "42",
            ["Cdc:InstanceKey"] = "datastore-42",
            ["Cdc:Generation"] = "1",
            ["Cdc:DurabilityProfile"] = "LocalSingleBroker",
            ["Cdc:AuthorizationProfile"] = "AuthorizationDisabledLocal",
            ["Cdc:Compose:Project"] = "cdc-test",
            ["Cdc:Compose:File"] = "/compose.json",
            ["Cdc:Compose:EnvironmentFile"] = "/environment",
            ["Cdc:Compose:BrokerSizeOverrideFile"] = "/state/broker-size.json",
            ["Cdc:SetupConnectionString"] = "Host=localhost;Database=cdc;Username=setup;Password=setup",
        };
}
