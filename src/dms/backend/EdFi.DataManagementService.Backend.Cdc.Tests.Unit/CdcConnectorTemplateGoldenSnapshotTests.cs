// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using static EdFi.DataManagementService.Backend.Cdc.Tests.Unit.CdcConnectorTemplateTestData;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Unit;

[TestFixture]
[Parallelizable]
[Category("CdcConnectorTemplateGoldenSnapshot")]
public class Given_CdcConnectorTemplateGoldenSnapshots
{
    [Test]
    public void It_matches_the_postgresql_flat_config_golden_snapshot()
    {
        CdcConnectorTemplateResult result = Render(
            BuildRequest(
                CdcProvider.Postgresql,
                kafkaSecurityProperties: new Dictionary<string, string>
                {
                    ["security.protocol"] = "SASL_SSL",
                    ["sasl.jaas.config"] = "${env:CDC_KAFKA_JAAS_CONFIG}",
                }
            )
        );

        using var _ = new AssertionScope();
        result.Outcome.Should().Be(CdcConnectorTemplateOutcome.Rendered);
        Snapshot(result.Config)
            .Should()
            .Be(
                """
                connector.class=io.debezium.connector.postgresql.PostgresConnector
                database.dbname=edfi_datastore
                database.hostname=postgresql.internal
                database.password=${env:CDC_DATABASE_PASSWORD}
                database.port=5432
                database.user=connector_user
                errors.tolerance=none
                heartbeat.action.query=select 1
                heartbeat.interval.ms=5000
                key.converter=org.apache.kafka.connect.storage.StringConverter
                message.key.columns=dms.DocumentCache:DocumentUuid;dms.Document:DocumentUuid
                name=dms_binding_connector
                plugin.name=pgoutput
                producer.override.acks=all
                producer.override.buffer.memory=67108864
                producer.override.compression.type=none
                producer.override.enable.idempotence=true
                producer.override.max.in.flight.requests.per.connection=5
                producer.override.max.request.size=67108864
                producer.override.partitioner.class=org.edfi.kafka.connect.partitioner.KafkaMurmur2V1Partitioner
                producer.override.retries=2147483647
                producer.override.sasl.jaas.config=${env:CDC_KAFKA_JAAS_CONFIG}
                producer.override.security.protocol=SASL_SSL
                publication.autocreate.mode=disabled
                publication.name=dms_binding_publication
                slot.name=dms_binding_slot
                snapshot.mode=initial
                statistics.metrics.enabled=true
                table.include.list=dms.DocumentCache,dms.Document,dms.CdcHeartbeat
                tasks.max=1
                tombstones.on.delete=false
                topic.delimiter=.
                topic.heartbeat.prefix=__debezium-heartbeat
                topic.naming.strategy=io.debezium.schema.SchemaTopicNamingStrategy
                topic.prefix=dms_binding_connector
                transforms=documentState
                transforms.documentState.progress.topic=edfi.documents.cdc-progress
                transforms.documentState.provider=postgresql
                transforms.documentState.target.topic=edfi.documents
                transforms.documentState.type=org.edfi.kafka.connect.transforms.DocumentState
                unavailable.value.placeholder=__debezium_unavailable_value
                value.converter=org.edfi.kafka.connect.converters.DocumentStateJsonConverter
                value.converter.decimal.format=NUMERIC
                value.converter.schemas.enable=false
                """
            );
        result
            .Config.Keys.Should()
            .NotContain(key => key.StartsWith("topic.creation.", StringComparison.Ordinal));
        result
            .Config.Keys.Should()
            .NotContain(key => key.StartsWith("errors.deadletterqueue.", StringComparison.Ordinal));
        result.Config.Keys.Should().NotContain(key => key.Contains("offset", StringComparison.Ordinal));
    }

    [Test]
    public void It_matches_the_sqlserver_flat_config_golden_snapshot()
    {
        CdcConnectorTemplateResult result = Render(
            BuildRequest(
                CdcProvider.SqlServer,
                providerConnectionProperties: new Dictionary<string, string>(
                    BuildSqlServerConnectionProperties()
                )
                {
                    ["driver.encrypt"] = "true",
                    ["driver.trustServerCertificate"] = "true",
                    ["driver.trustStorePassword"] = "${env:CDC_SQLSERVER_TRUSTSTORE_PASSWORD}",
                },
                kafkaSecurityProperties: new Dictionary<string, string>
                {
                    ["security.protocol"] = "SASL_SSL",
                    ["sasl.jaas.config"] = "${env:CDC_KAFKA_JAAS_CONFIG}",
                }
            )
        );

        using var _ = new AssertionScope();
        result.Outcome.Should().Be(CdcConnectorTemplateOutcome.Rendered);
        Snapshot(result.Config)
            .Should()
            .Be(
                """
                connector.class=io.debezium.connector.sqlserver.SqlServerConnector
                database.hostname=sqlserver.internal
                database.names=edfi_datastore
                database.password=${env:CDC_DATABASE_PASSWORD}
                database.port=1433
                database.user=connector_user
                driver.encrypt=true
                driver.trustServerCertificate=true
                driver.trustStorePassword=${env:CDC_SQLSERVER_TRUSTSTORE_PASSWORD}
                errors.tolerance=none
                heartbeat.action.query=select 1
                heartbeat.interval.ms=5000
                include.schema.changes=false
                key.converter=org.apache.kafka.connect.storage.StringConverter
                message.key.columns=dms.DocumentCache:DocumentUuid;dms.Document:DocumentUuid
                name=dms_binding_connector
                poll.interval.ms=2000
                producer.override.acks=all
                producer.override.buffer.memory=67108864
                producer.override.compression.type=none
                producer.override.enable.idempotence=true
                producer.override.max.in.flight.requests.per.connection=5
                producer.override.max.request.size=67108864
                producer.override.partitioner.class=org.edfi.kafka.connect.partitioner.KafkaMurmur2V1Partitioner
                producer.override.retries=2147483647
                producer.override.sasl.jaas.config=${env:CDC_KAFKA_JAAS_CONFIG}
                producer.override.security.protocol=SASL_SSL
                schema.history.internal.consumer.sasl.jaas.config=${env:CDC_KAFKA_JAAS_CONFIG}
                schema.history.internal.consumer.security.protocol=SASL_SSL
                schema.history.internal.kafka.bootstrap.servers=broker-1:9092,broker-2:9092
                schema.history.internal.kafka.topic=edfi.documents.schema-history
                schema.history.internal.producer.acks=all
                schema.history.internal.producer.enable.idempotence=true
                schema.history.internal.producer.max.in.flight.requests.per.connection=1
                schema.history.internal.producer.retries=2147483647
                schema.history.internal.producer.sasl.jaas.config=${env:CDC_KAFKA_JAAS_CONFIG}
                schema.history.internal.producer.security.protocol=SASL_SSL
                snapshot.mode=initial
                statistics.metrics.enabled=true
                table.include.list=dms.DocumentCache,dms.Document,dms.CdcHeartbeat
                tasks.max=1
                time.precision.mode=isostring
                tombstones.on.delete=false
                topic.delimiter=.
                topic.heartbeat.prefix=__debezium-heartbeat
                topic.naming.strategy=io.debezium.schema.SchemaTopicNamingStrategy
                topic.prefix=dms_binding_connector
                transforms=documentState
                transforms.documentState.progress.topic=edfi.documents.cdc-progress
                transforms.documentState.provider=sqlserver
                transforms.documentState.target.topic=edfi.documents
                transforms.documentState.type=org.edfi.kafka.connect.transforms.DocumentState
                unavailable.value.placeholder=__debezium_unavailable_value
                value.converter=org.edfi.kafka.connect.converters.DocumentStateJsonConverter
                value.converter.decimal.format=NUMERIC
                value.converter.schemas.enable=false
                """
            );
        result
            .Config.Keys.Should()
            .NotContain(key => key.StartsWith("topic.creation.", StringComparison.Ordinal));
        result
            .Config.Keys.Should()
            .NotContain(key => key.StartsWith("errors.deadletterqueue.", StringComparison.Ordinal));
        result
            .Config.Keys.Should()
            .NotContain(key => key.StartsWith("database.history.", StringComparison.Ordinal));
        result.Config.Keys.Should().NotContain(key => key.Contains("offset", StringComparison.Ordinal));
    }

    private static CdcConnectorTemplateResult Render(CdcConnectorTemplateRequest request)
    {
        using ServiceProvider serviceProvider = new ServiceCollection()
            .AddCdcConnectorTemplates()
            .BuildServiceProvider();

        ICdcConnectorTemplateService service =
            serviceProvider.GetRequiredService<ICdcConnectorTemplateService>();

        return service.Render(request);
    }

    private static string Snapshot(IReadOnlyDictionary<string, string> config) =>
        string.Join("\n", config.Select(property => $"{property.Key}={property.Value}"));
}
