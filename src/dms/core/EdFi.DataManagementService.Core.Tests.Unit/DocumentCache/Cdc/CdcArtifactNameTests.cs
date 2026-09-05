// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.DocumentCache.Cdc;

[TestFixture]
[Parallelizable]
[Category("CdcArtifactName")]
public class Given_CdcArtifactNameGenerator
{
    [Test]
    public void It_renders_complete_postgresql_inventory_from_design_formulas()
    {
        CdcArtifactNameResult result = CdcArtifactNameGenerator.Render(
            new("dms-local", "edfi.dms", "data-store-1", 1, CdcProvider.Postgresql)
        );

        result.Succeeded.Should().BeTrue();
        CdcArtifactInventory inventory = result.Inventory!;
        inventory.ConnectorName.Should().Be("dms-local-data-store-1-g1");
        inventory.TopicName.Should().Be("edfi.dms.instance.data-store-1-g1.documents.v1");
        inventory
            .ProgressTopicName.Should()
            .Be("edfi.dms.instance.data-store-1-g1.documents.v1.cdc-progress");
        inventory.SchemaHistoryTopicName.Should().BeNull();
        inventory
            .PostgresqlPublicationName.Should()
            .Be("edfi_dms_dms_local_data_store_1_g1_56c4668b1b24_pub");
        inventory
            .PostgresqlLogicalSlotName.Should()
            .Be("edfi_dms_dms_local_data_store_1_g1_56c4668b1b24_slot");
        inventory.SqlServerCdcGatingRoleName.Should().BeNull();
        inventory
            .GovernedArtifacts.Should()
            .Equal(
                new CdcGovernedArtifactName(
                    CdcGovernedArtifactKind.KafkaConnectConnector,
                    inventory.ConnectorName
                ),
                new CdcGovernedArtifactName(
                    CdcGovernedArtifactKind.ConnectSourceOffsets,
                    inventory.ConnectorName
                ),
                new CdcGovernedArtifactName(CdcGovernedArtifactKind.PublicTopic, inventory.TopicName),
                new CdcGovernedArtifactName(
                    CdcGovernedArtifactKind.ProgressTopic,
                    inventory.ProgressTopicName
                ),
                new CdcGovernedArtifactName(CdcGovernedArtifactKind.PublicTopicAcls, inventory.TopicName),
                new CdcGovernedArtifactName(
                    CdcGovernedArtifactKind.ProgressTopicAcls,
                    inventory.ProgressTopicName
                ),
                new CdcGovernedArtifactName(
                    CdcGovernedArtifactKind.PostgresqlPublication,
                    inventory.PostgresqlPublicationName!
                ),
                new CdcGovernedArtifactName(
                    CdcGovernedArtifactKind.PostgresqlLogicalSlot,
                    inventory.PostgresqlLogicalSlotName!
                )
            );
    }

    [Test]
    public void It_renders_complete_sql_server_inventory_and_isolates_generation()
    {
        CdcArtifactNameResult result = CdcArtifactNameGenerator.Render(
            new("dms-local", "edfi.dms", "data-store-1", 7, CdcProvider.SqlServer)
        );
        CdcArtifactNameResult nextGenerationResult = CdcArtifactNameGenerator.Render(
            new("dms-local", "edfi.dms", "data-store-1", 8, CdcProvider.SqlServer)
        );

        result.Succeeded.Should().BeTrue();
        nextGenerationResult.Succeeded.Should().BeTrue();
        CdcArtifactInventory inventory = result.Inventory!;
        inventory.ConnectorName.Should().Be("dms-local-data-store-1-g7");
        inventory.TopicName.Should().Be("edfi.dms.instance.data-store-1-g7.documents.v1");
        inventory
            .ProgressTopicName.Should()
            .Be("edfi.dms.instance.data-store-1-g7.documents.v1.cdc-progress");
        inventory
            .SchemaHistoryTopicName.Should()
            .Be("edfi.dms.instance.data-store-1-g7.documents.v1.schema-history");
        inventory
            .SqlServerCdcGatingRoleName.Should()
            .Be("edfi_dms_dms_local_data_store_1_g7_3ac400fa3057_cdc_reader");
        inventory
            .SqlServerCaptureInstanceDocumentName.Should()
            .Be("edfi_dms_dms_local_data_store_1_g7_3ac400fa3057_document");
        inventory
            .SqlServerCaptureInstanceDocumentCacheName.Should()
            .Be("edfi_dms_dms_local_data_store_1_g7_3ac400fa3057_documentcache");
        inventory
            .SqlServerCaptureInstanceCdcHeartbeatName.Should()
            .Be("edfi_dms_dms_local_data_store_1_g7_3ac400fa3057_cdcheartbeat");
        inventory.PostgresqlPublicationName.Should().BeNull();
        inventory
            .GovernedArtifacts.Select(artifact => artifact.Kind)
            .Should()
            .Equal(
                CdcGovernedArtifactKind.KafkaConnectConnector,
                CdcGovernedArtifactKind.ConnectSourceOffsets,
                CdcGovernedArtifactKind.PublicTopic,
                CdcGovernedArtifactKind.ProgressTopic,
                CdcGovernedArtifactKind.PublicTopicAcls,
                CdcGovernedArtifactKind.ProgressTopicAcls,
                CdcGovernedArtifactKind.SqlServerCdcGatingRole,
                CdcGovernedArtifactKind.SqlServerCaptureInstanceDocument,
                CdcGovernedArtifactKind.SqlServerCaptureInstanceDocumentCache,
                CdcGovernedArtifactKind.SqlServerCaptureInstanceCdcHeartbeat,
                CdcGovernedArtifactKind.SchemaHistoryTopic,
                CdcGovernedArtifactKind.SchemaHistoryTopicAcls
            );
        nextGenerationResult.Inventory!.ConnectorName.Should().NotBe(inventory.ConnectorName);
        nextGenerationResult.Inventory!.TopicName.Should().NotBe(inventory.TopicName);
        nextGenerationResult
            .Inventory!.SqlServerCaptureInstanceDocumentName.Should()
            .NotBe(inventory.SqlServerCaptureInstanceDocumentName);
    }

    [Test]
    public void It_keeps_provider_artifact_names_that_are_at_their_exact_limits()
    {
        CdcArtifactInventory postgresqlPublicationLimit = Render(
            new(new string('a', 16), "x", new string('b', 17), 1, CdcProvider.Postgresql)
        );
        CdcArtifactInventory postgresqlSlotLimit = Render(
            new(new string('a', 16), "x", new string('b', 16), 1, CdcProvider.Postgresql)
        );
        CdcArtifactInventory sqlServerRoleLimit = Render(
            new(new string('a', 45), "x", new string('b', 46), 1, CdcProvider.SqlServer)
        );
        CdcArtifactInventory sqlServerDocumentLimit = Render(
            new(new string('a', 32), "x", new string('b', 33), 1, CdcProvider.SqlServer)
        );
        CdcArtifactInventory sqlServerDocumentCacheLimit = Render(
            new(new string('a', 30), "x", new string('b', 30), 1, CdcProvider.SqlServer)
        );
        CdcArtifactInventory sqlServerCdcHeartbeatLimit = Render(
            new(new string('a', 30), "x", new string('b', 31), 1, CdcProvider.SqlServer)
        );

        postgresqlPublicationLimit.PostgresqlPublicationName.Should().HaveLength(63);
        postgresqlPublicationLimit.PostgresqlPublicationName.Should().EndWith("_pub");
        postgresqlSlotLimit.PostgresqlLogicalSlotName.Should().HaveLength(63);
        postgresqlSlotLimit.PostgresqlLogicalSlotName.Should().EndWith("_slot");
        sqlServerRoleLimit.SqlServerCdcGatingRoleName.Should().HaveLength(128);
        sqlServerRoleLimit.SqlServerCdcGatingRoleName.Should().EndWith("_cdc_reader");
        sqlServerDocumentLimit.SqlServerCaptureInstanceDocumentName.Should().HaveLength(100);
        sqlServerDocumentLimit.SqlServerCaptureInstanceDocumentName.Should().EndWith("_document");
        sqlServerDocumentCacheLimit.SqlServerCaptureInstanceDocumentCacheName.Should().HaveLength(100);
        sqlServerDocumentCacheLimit
            .SqlServerCaptureInstanceDocumentCacheName.Should()
            .EndWith("_documentcache");
        sqlServerCdcHeartbeatLimit.SqlServerCaptureInstanceCdcHeartbeatName.Should().HaveLength(100);
        sqlServerCdcHeartbeatLimit.SqlServerCaptureInstanceCdcHeartbeatName.Should().EndWith("_cdcheartbeat");
    }

    [Test]
    public void It_truncates_provider_artifacts_with_literal_artifact_kind_hashes()
    {
        string deploymentKey = new('a', 24);
        string instanceKey = new('b', 23);
        string sqlServerDeploymentKey = new('a', 45);
        string sqlServerInstanceKey = new('b', 45);
        string postgresqlPrefix = ProviderPrefix(deploymentKey, instanceKey, 1);
        string sqlServerPrefix = ProviderPrefix(sqlServerDeploymentKey, sqlServerInstanceKey, 1);
        string postgresqlPublication = $"{postgresqlPrefix}_pub";
        string postgresqlSlot = $"{postgresqlPrefix}_slot";
        string sqlServerCapture = $"{sqlServerPrefix}_documentcache";

        CdcArtifactInventory postgresqlInventory = Render(
            new(deploymentKey, "x", instanceKey, 1, CdcProvider.Postgresql)
        );
        CdcArtifactInventory sqlServerInventory = Render(
            new(sqlServerDeploymentKey, "x", sqlServerInstanceKey, 1, CdcProvider.SqlServer)
        );

        postgresqlInventory
            .PostgresqlPublicationName.Should()
            .Be(Truncate("postgresql-publication", postgresqlPublication, 63));
        postgresqlInventory
            .PostgresqlLogicalSlotName.Should()
            .Be(Truncate("postgresql-logical-slot", postgresqlSlot, 63));
        postgresqlInventory
            .PostgresqlPublicationName.Should()
            .NotBe(postgresqlInventory.PostgresqlLogicalSlotName);
        sqlServerInventory
            .SqlServerCaptureInstanceDocumentCacheName.Should()
            .Be(Truncate("sqlserver-capture-instance-documentcache", sqlServerCapture, 100));
        sqlServerInventory.SqlServerCaptureInstanceDocumentCacheName.Should().HaveLength(100);
    }

    [Test]
    public void It_discriminates_provider_artifacts_before_separator_normalization()
    {
        string[] instanceKeys = ["data-store-1", "data_store_1", "data.store.1"];

        CdcArtifactInventory[] postgresqlInventories = instanceKeys
            .Select(instanceKey =>
                Render(new("dms-local", "edfi.dms", instanceKey, 1, CdcProvider.Postgresql))
            )
            .ToArray();
        CdcArtifactInventory[] sqlServerInventories = instanceKeys
            .Select(instanceKey =>
                Render(new("dms-local", "edfi.dms", instanceKey, 1, CdcProvider.SqlServer))
            )
            .ToArray();

        postgresqlInventories
            .Select(inventory => inventory.PostgresqlPublicationName)
            .Should()
            .OnlyHaveUniqueItems();
        postgresqlInventories
            .Select(inventory => inventory.PostgresqlLogicalSlotName)
            .Should()
            .OnlyHaveUniqueItems();
        sqlServerInventories
            .Select(inventory => inventory.SqlServerCdcGatingRoleName)
            .Should()
            .OnlyHaveUniqueItems();
        sqlServerInventories
            .Select(inventory => inventory.SqlServerCaptureInstanceDocumentName)
            .Should()
            .OnlyHaveUniqueItems();
        sqlServerInventories
            .Select(inventory => inventory.SqlServerCaptureInstanceDocumentCacheName)
            .Should()
            .OnlyHaveUniqueItems();
        sqlServerInventories
            .Select(inventory => inventory.SqlServerCaptureInstanceCdcHeartbeatName)
            .Should()
            .OnlyHaveUniqueItems();

        postgresqlInventories[0]
            .PostgresqlPublicationName.Should()
            .Contain($"g1_{ProviderDiscriminator("dms-local", "data-store-1", 1)}_pub");
        postgresqlInventories[1]
            .PostgresqlPublicationName.Should()
            .Contain($"g1_{ProviderDiscriminator("dms-local", "data_store_1", 1)}_pub");
        postgresqlInventories[2]
            .PostgresqlPublicationName.Should()
            .Contain($"g1_{ProviderDiscriminator("dms-local", "data.store.1", 1)}_pub");

        Render(new("dms-local", "edfi.dms", "data-store-1", 1, CdcProvider.Postgresql))
            .PostgresqlPublicationName.Should()
            .Be(postgresqlInventories[0].PostgresqlPublicationName);
    }

    [Test]
    public void It_rejects_kafka_and_connect_artifact_names_over_the_limit_without_truncating()
    {
        CdcArtifactNameResult connectorResult = CdcArtifactNameGenerator.Render(
            new(new string('a', 130), "x", new string('b', 120), 1, CdcProvider.Postgresql)
        );
        CdcArtifactNameResult schemaHistoryResult = CdcArtifactNameGenerator.Render(
            new("dms-local", new string('a', 230), "data-store-1", 1, CdcProvider.SqlServer)
        );

        connectorResult.Succeeded.Should().BeFalse();
        connectorResult.Inventory.Should().BeNull();
        connectorResult
            .Diagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.Path == "$.deploymentKey"
                && diagnostic.Message.Contains("connectorName", StringComparison.Ordinal)
            );
        schemaHistoryResult.Succeeded.Should().BeFalse();
        schemaHistoryResult.Inventory.Should().BeNull();
        schemaHistoryResult
            .Diagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.Path == "$.topicPrefix"
                && diagnostic.Message.Contains("schemaHistoryTopicName", StringComparison.Ordinal)
            );
    }

    private static CdcArtifactInventory Render(CdcArtifactNameInput input)
    {
        CdcArtifactNameResult result = CdcArtifactNameGenerator.Render(input);

        result.Succeeded.Should().BeTrue();
        return result.Inventory!;
    }

    /// <summary>
    /// The ACL isolation rule asks whether a topic a consumer principal holds a grant on belongs to
    /// this target at all, because a guarded source replacement retains the generation it supersedes
    /// and a stable consumer reads both. Every case below is one the rule must get right: another
    /// generation of this target is this target's, and a progress topic, another instance's topic, and
    /// anything not named for a generation are not.
    /// </summary>
    [TestCase("edfi.dms.instance.data-store-1-g1.documents.v1", true)]
    [TestCase("edfi.dms.instance.data-store-1-g2.documents.v1", true)]
    [TestCase("edfi.dms.instance.data-store-1-g17.documents.v1", true)]
    [TestCase("edfi.dms.instance.data-store-1-g1.documents.v1.cdc-progress", false)]
    [TestCase("edfi.dms.instance.data-store-1-g1.documents.v1.schema-history", false)]
    [TestCase("edfi.dms.instance.data-store-2-g1.documents.v1", false)]
    [TestCase("edfi.dms.instance.data-store-1-g.documents.v1", false)]
    [TestCase("edfi.dms.instance.data-store-1-g0.documents.v1", false)]
    [TestCase("edfi.dms.instance.data-store-1-gx.documents.v1", false)]
    [TestCase("edfi.dms.instance.data-store-1-g-1.documents.v1", false)]
    [TestCase("connect-offsets", false)]
    public void It_recognizes_only_this_targets_public_topics(string topicName, bool expected)
    {
        CdcArtifactNameGenerator
            .IsTargetPublicTopicName("edfi.dms", "data-store-1", topicName)
            .Should()
            .Be(expected);
    }

    private static string Truncate(string artifactKind, string untruncatedName, int limit)
    {
        if (untruncatedName.Length <= limit)
        {
            return untruncatedName;
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{artifactKind}\0{untruncatedName}"));
        string suffix = $"_{Convert.ToHexString(hash).ToLowerInvariant()[..12]}";
        return $"{untruncatedName[..(limit - suffix.Length)]}{suffix}";
    }

    private static string ProviderPrefix(string deploymentKey, string instanceKey, long generation) =>
        $"edfi_dms_{ToProviderSafeToken(deploymentKey)}_{ToProviderSafeToken(instanceKey)}_g{generation.ToString(CultureInfo.InvariantCulture)}_{ProviderDiscriminator(deploymentKey, instanceKey, generation)}";

    private static string ToProviderSafeToken(string value) => value.Replace('.', '_').Replace('-', '_');

    private static string ProviderDiscriminator(string deploymentKey, string instanceKey, long generation)
    {
        byte[] hash = SHA256.HashData(
            Encoding.UTF8.GetBytes(
                $"{deploymentKey}\0{instanceKey}\0{generation.ToString(CultureInfo.InvariantCulture)}"
            )
        );

        return Convert.ToHexString(hash).ToLowerInvariant()[..12];
    }
}
