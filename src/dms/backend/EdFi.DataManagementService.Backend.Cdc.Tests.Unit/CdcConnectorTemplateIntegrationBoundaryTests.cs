// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Unit;

[TestFixture]
[Parallelizable]
[Category("CdcConnectorTemplateIntegrationBoundaries")]
public class Given_CdcConnectorTemplateIntegrationBoundaries
{
    private static readonly CdcSourceFingerprint SourceFingerprint = new(
        "cdc-source-fingerprint-v1",
        "physical-source-fingerprint"
    );

    private static readonly IReadOnlyList<ForbiddenSourceToken> ForbiddenSourceTokens =
    [
        new("HttpClient", IsNeverAllowed),
        new("connectors/", IsNeverAllowed),
        new("CREATE PUBLICATION", IsNeverAllowed),
        new("ALTER PUBLICATION", IsNeverAllowed),
        new("sp_cdc_enable_table", IsNeverAllowed),
        new("ACL", IsNeverAllowed),
        new("offset.storage", IsNeverAllowed),
        new("topic.creation", IsAllowedTopicCreationGuardrail),
    ];

    [Test]
    public void It_returns_render_registration_and_artifact_evidence_without_connect_lifecycle_inputs()
    {
        using ServiceProvider serviceProvider = BuildServiceProvider();
        ICdcConnectorTemplateService service =
            serviceProvider.GetRequiredService<ICdcConnectorTemplateService>();
        CdcConnectorTemplateRequest request = BuildRequest(
            CdcProvider.SqlServer,
            new CdcConnectorTemplateArtifactOutputRequest(includeRedactedArtifactPayload: true)
        );

        CdcConnectorTemplateResult result = service.Render(request);

        using var _ = new AssertionScope();
        result.Outcome.Should().Be(CdcConnectorTemplateOutcome.Rendered);
        result.RegistrationPayload.Should().NotBeNull();
        result.RegistrationPayload!.Name.Should().Be(request.ConnectorName);
        result.RegistrationPayload.Config.Should().Equal(result.Config);
        result.RedactedArtifactPayload.Should().NotBeNull();
        result
            .RedactedArtifactPayload!.FileName.Should()
            .Be(new CdcSafeName("cdc-connector-template.sqlserver.manifest.json"));
        result.SchemaHistoryTopicName.Should().Be("edfi.documents.schema-history");
        result.Config.Should().Contain("name", request.ConnectorName.Value);
        result
            .Config.Should()
            .Contain("schema.history.internal.kafka.topic", "edfi.documents.schema-history");
        result
            .Config.Keys.Should()
            .NotContain(key => key.StartsWith("topic.creation.", StringComparison.Ordinal));
        result
            .Config.Keys.Should()
            .NotContain(key => key.Contains("offset.storage", StringComparison.Ordinal));
    }

    [Test]
    public void It_allows_preflight_and_live_validation_from_supplied_read_back_evidence()
    {
        using ServiceProvider serviceProvider = BuildServiceProvider();
        ICdcConnectorTemplateService service =
            serviceProvider.GetRequiredService<ICdcConnectorTemplateService>();
        CdcConnectorTemplateRequest request = BuildRequest(CdcProvider.Postgresql);
        CdcConnectorTemplateResult rendered = service.Render(request);

        CdcConnectorTemplateResult preflight = service.ValidateRegistrationPreflight(
            new CdcConnectorTemplateEffectiveConfigValidationRequest(
                request,
                rendered.Config,
                new CdcConnectorProviderSetupEvidence(
                    bindingGeneration: request.BindingIdentity.BindingGeneration,
                    BuildProviderSetupResult(CdcProvider.Postgresql)
                )
            )
        );
        CdcConnectorTemplateResult liveReadBack = service.ValidateLiveReadBack(
            new CdcConnectorTemplateEffectiveConfigValidationRequest(
                request,
                rendered.Config,
                new CdcConnectorProviderSetupEvidence(
                    bindingGeneration: request.BindingIdentity.BindingGeneration,
                    BuildProviderSetupResult(CdcProvider.Postgresql)
                ),
                new CdcConnectorTemplateSourcePartitionEvidence(
                    new Dictionary<string, string> { ["server"] = request.ConnectorName.Value }
                )
            )
        );

        using var _ = new AssertionScope();
        rendered.Outcome.Should().Be(CdcConnectorTemplateOutcome.Rendered);
        preflight.Outcome.Should().Be(CdcConnectorTemplateOutcome.Rendered);
        preflight.Config.Should().Equal(rendered.Config);
        preflight.Diagnostics.Should().BeEmpty();
        liveReadBack.Outcome.Should().Be(CdcConnectorTemplateOutcome.Rendered);
        liveReadBack.Config.Should().Equal(rendered.Config);
        liveReadBack.Diagnostics.Should().BeEmpty();
    }

    [Test]
    public void It_keeps_connect_rest_provider_mutation_topic_acl_and_offset_lifecycle_code_out_of_scope()
    {
        SourceTokenMatch[] forbiddenMatches = EnumerateCdcSourceTokenMatches()
            .Where(match => !match.Token.IsAllowed(match.FilePath, match.Line))
            .ToArray();

        forbiddenMatches.Should().BeEmpty();
    }

    [Test]
    public void It_documents_that_the_pinned_runtime_must_supply_the_murmur2_partitioner_class()
    {
        using ServiceProvider serviceProvider = BuildServiceProvider();
        ICdcConnectorTemplateService service =
            serviceProvider.GetRequiredService<ICdcConnectorTemplateService>();

        CdcConnectorTemplateResult result = service.Render(BuildRequest(CdcProvider.Postgresql));

        using var _ = new AssertionScope();
        result
            .Config.Should()
            .Contain(
                "producer.override.partitioner.class",
                "org.edfi.kafka.connect.partitioner.KafkaMurmur2V1Partitioner"
            );
        CdcSourceFiles()
            .Select(File.ReadAllText)
            .Should()
            .NotContain(
                source => source.Contains("class KafkaMurmur2V1Partitioner", StringComparison.Ordinal),
                "DMS-1321 emits the pinned-image class name, but DMS-1322 owns packaging the implementation"
            );
    }

    private static ServiceProvider BuildServiceProvider() =>
        new ServiceCollection().AddCdcConnectorTemplates().BuildServiceProvider();

    private static CdcConnectorTemplateRequest BuildRequest(
        CdcProvider provider,
        CdcConnectorTemplateArtifactOutputRequest? artifactOutput = null
    ) =>
        new(
            BuildBinding(provider),
            new CdcConnectorProviderSetupEvidence(bindingGeneration: 7, BuildProviderSetupResult(provider)),
            new CdcConnectorTemplateDeploymentPolicy(
                "broker-1:9092,broker-2:9092",
                maxRecordBytes: 67_108_864,
                heartbeatInterval: TimeSpan.FromSeconds(5),
                sqlServerPollInterval: provider == CdcProvider.SqlServer ? TimeSpan.FromSeconds(2) : null
            ),
            new CdcProviderConnectionProperties(provider, BuildProviderConnectionProperties(provider)),
            CdcKafkaClientSecurityProperties.Empty,
            artifactOutput
        );

    private static CdcConnectorTemplateBindingIdentity BuildBinding(CdcProvider provider) =>
        new(
            provider,
            new CdcSafeName("dms_binding_connector"),
            "edfi.documents",
            bindingGeneration: 7,
            SourceFingerprint
        );

    private static IReadOnlyDictionary<string, string> BuildProviderConnectionProperties(
        CdcProvider provider
    ) =>
        provider switch
        {
            CdcProvider.Postgresql => new Dictionary<string, string>
            {
                ["database.hostname"] = "postgresql.internal",
                ["database.port"] = "5432",
                ["database.user"] = "connector_user",
                ["database.password"] = "${env:CDC_DATABASE_PASSWORD}",
                ["database.dbname"] = "edfi_datastore",
            },
            CdcProvider.SqlServer => new Dictionary<string, string>
            {
                ["database.hostname"] = "sqlserver.internal",
                ["database.port"] = "1433",
                ["database.user"] = "connector_user",
                ["database.password"] = "${env:CDC_DATABASE_PASSWORD}",
                ["database.names"] = "edfi_datastore",
            },
            _ => throw new ArgumentOutOfRangeException(
                nameof(provider),
                provider,
                "Unsupported CDC provider."
            ),
        };

    private static CdcProviderSetupResult BuildProviderSetupResult(CdcProvider provider) =>
        new(
            Provider: provider,
            Mode: CdcProviderSetupMode.InitialCreateOrExactMatch,
            Outcome: CdcProviderSetupOutcome.CreatedOrMatched,
            BoundPhysicalSourceFingerprint: SourceFingerprint,
            ObservedSourceFingerprint: SourceFingerprint,
            ArtifactInventory: BuildArtifactInventory(provider),
            GrantInventory: [],
            SourceTableInventory: BuildRequiredSourceTableInventory(provider),
            ExpectedMessageKeyColumns: BuildExpectedMessageKeyColumns(),
            HeartbeatActionQuery: new CdcHeartbeatActionQuery("select 1", "sha256-safe"),
            ProviderHistoryObservations: [],
            ManifestPayload: null,
            Diagnostics: []
        );

    private static IReadOnlyList<CdcProviderArtifactObservation> BuildArtifactInventory(
        CdcProvider provider
    ) =>
        provider switch
        {
            CdcProvider.Postgresql =>
            [
                new(
                    CdcProviderArtifactKind.PostgresqlPublication,
                    new CdcSafeName("dms_binding_publication"),
                    CdcProviderArtifactState.Matched,
                    new Dictionary<string, string>()
                ),
                new(
                    CdcProviderArtifactKind.PostgresqlReplicationSlot,
                    new CdcSafeName("dms_binding_slot"),
                    CdcProviderArtifactState.Matched,
                    new Dictionary<string, string>()
                ),
            ],
            CdcProvider.SqlServer =>
            [
                new(
                    CdcProviderArtifactKind.SqlServerCaptureInstance,
                    new CdcSafeName("dms_binding_document_cache_capture"),
                    CdcProviderArtifactState.Matched,
                    new Dictionary<string, string>()
                ),
                new(
                    CdcProviderArtifactKind.SqlServerCaptureInstance,
                    new CdcSafeName("dms_binding_document_capture"),
                    CdcProviderArtifactState.Matched,
                    new Dictionary<string, string>()
                ),
                new(
                    CdcProviderArtifactKind.SqlServerCaptureInstance,
                    new CdcSafeName("dms_binding_cdc_heartbeat_capture"),
                    CdcProviderArtifactState.Matched,
                    new Dictionary<string, string>()
                ),
            ],
            _ => throw new ArgumentOutOfRangeException(
                nameof(provider),
                provider,
                "Unsupported CDC provider."
            ),
        };

    private static IReadOnlyList<CdcSourceTableInventory> BuildRequiredSourceTableInventory(
        CdcProvider provider
    ) =>
        [
            BuildSourceTable(
                provider,
                CdcSourceTableKind.DocumentCache,
                "DocumentCache",
                [BuildColumn(provider, "DocumentUuid")]
            ),
            BuildSourceTable(
                provider,
                CdcSourceTableKind.Document,
                "Document",
                [BuildColumn(provider, "DocumentUuid")]
            ),
            BuildSourceTable(
                provider,
                CdcSourceTableKind.CdcHeartbeat,
                "CdcHeartbeat",
                [
                    BuildColumn(provider, "HeartbeatId"),
                    BuildColumn(provider, "HeartbeatSequence", 2),
                    BuildColumn(provider, "HeartbeatAt", 3),
                ]
            ),
        ];

    private static CdcSourceTableInventory BuildSourceTable(
        CdcProvider provider,
        CdcSourceTableKind tableKind,
        string tableName,
        IReadOnlyList<CdcSourceColumnInventory> columns
    ) =>
        new(
            tableKind,
            new DbTableName(new DbSchemaName("dms"), tableName),
            provider == CdcProvider.Postgresql ? $"\"dms\".\"{tableName}\"" : $"[dms].[{tableName}]",
            columns
        );

    private static CdcSourceColumnInventory BuildColumn(
        CdcProvider provider,
        string columnName,
        int ordinal = 1
    ) =>
        new(
            new DbColumnName(columnName),
            provider == CdcProvider.Postgresql ? $"\"{columnName}\"" : $"[{columnName}]",
            ordinal,
            provider == CdcProvider.Postgresql ? "text" : "nvarchar(max)",
            IsNullable: false
        );

    private static IReadOnlyList<CdcExpectedMessageKeyColumns> BuildExpectedMessageKeyColumns() =>
        [
            new(CdcSourceTableKind.DocumentCache, [new DbColumnName("DocumentUuid")]),
            new(CdcSourceTableKind.Document, [new DbColumnName("DocumentUuid")]),
        ];

    private static IReadOnlyList<SourceTokenMatch> EnumerateCdcSourceTokenMatches() =>
        CdcSourceFiles()
            .SelectMany(filePath =>
                File.ReadLines(filePath)
                    .Select((line, index) => new { Line = line, LineNumber = index + 1 })
                    .SelectMany(line =>
                        ForbiddenSourceTokens
                            .Where(token => line.Line.Contains(token.Value, StringComparison.Ordinal))
                            .Select(token => new SourceTokenMatch(
                                Path.GetRelativePath(FindRepositoryRoot(), filePath),
                                line.LineNumber,
                                token,
                                line.Line.Trim()
                            ))
                    )
            )
            .ToArray();

    private static IReadOnlyList<string> CdcSourceFiles()
    {
        string sourceDirectory = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "dms",
            "backend",
            "EdFi.DataManagementService.Backend.Cdc"
        );

        return Directory.EnumerateFiles(sourceDirectory, "*.cs", SearchOption.AllDirectories).ToArray();
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "src", "dms", "EdFi.DataManagementService.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find repository root from the test directory.");
    }

    private static bool IsNeverAllowed(string filePath, string line) => false;

    private static bool IsAllowedTopicCreationGuardrail(string filePath, string line) =>
        Path.GetFileName(filePath) == "CdcConnectorTemplateInputValidation.cs"
        && line.Contains("\"topic.creation.\"", StringComparison.Ordinal);

    private sealed record ForbiddenSourceToken(string Value, Func<string, string, bool> IsAllowed);

    private sealed record SourceTokenMatch(
        string FilePath,
        int LineNumber,
        ForbiddenSourceToken Token,
        string Line
    );
}
