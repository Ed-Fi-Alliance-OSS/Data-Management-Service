// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;
using FluentAssertions;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Cdc.Control.Tests.Unit;

[TestFixture]
[Parallelizable]
[Category("CdcControlOptions")]
public class Given_CdcControlOptionsTests
{
    private static readonly CdcControlOptionsValidator Validator = new();

    [Test]
    public void It_accepts_a_fully_supplied_configuration()
    {
        ValidateOptionsResult result = Validate(ValidOptions());

        result.Succeeded.Should().BeTrue(result.FailureMessage);
    }

    [Test]
    public void It_reports_every_failure_rather_than_only_the_first()
    {
        CdcControlOptions options = ValidOptions();
        options.DeploymentKey = string.Empty;
        options.InstanceKey = string.Empty;
        options.Generation = 0;

        ValidateOptionsResult result = Validate(options);

        result.Failures.Should().HaveCount(3);
    }

    [TestCaseSource(nameof(RequiredTextCases))]
    public void It_requires_each_mandatory_text_setting(string settingName, Action<CdcControlOptions> clear)
    {
        CdcControlOptions options = ValidOptions();
        clear(options);

        AssertFailsWith(options, settingName);
    }

    private static IEnumerable<TestCaseData> RequiredTextCases()
    {
        yield return Case(nameof(CdcControlOptions.DeploymentKey), options => options.DeploymentKey = "   ");
        yield return Case(nameof(CdcControlOptions.InstanceKey), options => options.InstanceKey = "");
        yield return Case(nameof(CdcControlOptions.TopicPrefix), options => options.TopicPrefix = "");
        // The two database principals are required whether or not an authorizer is enabled: every cdc
        // verb runs a provider-setup pass as the setup principal, granting the source objects to the
        // connector principal. Only the Connect worker principal is ACL-conditional.
        yield return Case(
            nameof(CdcControlOptions.SetupPrincipal),
            options => options.SetupPrincipal = "   "
        );
        yield return Case(
            nameof(CdcControlOptions.ConnectorPrincipal),
            options => options.ConnectorPrincipal = "   "
        );
        yield return Case(
            nameof(CdcControlOptions.KafkaBootstrapServers),
            options => options.KafkaBootstrapServers = ""
        );
        yield return Case(
            nameof(CdcControlOptions.ConnectWorkerKey),
            options => options.ConnectWorkerKey = ""
        );
        yield return Case(
            nameof(CdcControlOptions.ConnectOffsetStorageTopic),
            options => options.ConnectOffsetStorageTopic = ""
        );
    }

    [TestCase(0)]
    [TestCase(-1)]
    public void It_requires_a_positive_generation(long generation)
    {
        CdcControlOptions options = ValidOptions();
        options.Generation = generation;

        AssertFailsWith(options, nameof(CdcControlOptions.Generation));
    }

    [TestCase(0)]
    [TestCase(-3)]
    public void It_requires_a_positive_partition_count(int partitionCount)
    {
        CdcControlOptions options = ValidOptions();
        options.PartitionCount = partitionCount;

        AssertFailsWith(options, nameof(CdcControlOptions.PartitionCount));
    }

    [TestCase("")]
    [TestCase("localhost:8083")]
    [TestCase("/connectors")]
    [TestCase("ftp://localhost:8083")]
    public void It_requires_an_absolute_http_connect_base_uri(string connectBaseUri)
    {
        CdcControlOptions options = ValidOptions();
        options.ConnectBaseUri = connectBaseUri;

        AssertFailsWith(options, nameof(CdcControlOptions.ConnectBaseUri));
    }

    [TestCase("http://localhost:8083")]
    [TestCase("https://connect.example.org")]
    public void It_accepts_an_absolute_http_connect_base_uri(string connectBaseUri)
    {
        CdcControlOptions options = ValidOptions();
        options.ConnectBaseUri = connectBaseUri;

        Validate(options).Succeeded.Should().BeTrue();
    }

    [TestCase("connect-metrics.internal:8778")]
    [TestCase("ftp://connect-metrics.internal:8778")]
    public void It_rejects_a_metrics_bridge_that_is_not_an_absolute_http_uri(string connectMetricsBaseUri)
    {
        CdcControlOptions options = ValidOptions();
        options.ConnectMetricsBaseUri = connectMetricsBaseUri;

        AssertFailsWith(options, nameof(CdcControlOptions.ConnectMetricsBaseUri));
    }

    /// <summary>
    /// The bridge's port is fixed by the Connect image entrypoint, so a deployment that publishes it
    /// on the Connect host needs no override.
    /// </summary>
    [TestCase("")]
    [TestCase("http://connect-metrics.internal:8778")]
    public void It_accepts_an_absent_or_absolute_http_metrics_bridge(string connectMetricsBaseUri)
    {
        CdcControlOptions options = ValidOptions();
        options.ConnectMetricsBaseUri = connectMetricsBaseUri;

        Validate(options).Succeeded.Should().BeTrue();
    }

    [TestCase("")]
    [TestCase("Local ")]
    [TestCase("staging")]
    [TestCase("locally")]
    public void It_rejects_an_unknown_durability_profile(string durabilityProfile)
    {
        CdcControlOptions options = ValidOptions();
        options.DurabilityProfile = durabilityProfile;

        AssertFailsWith(options, nameof(CdcControlOptions.DurabilityProfile));
    }

    [TestCase("local", CdcDurabilityProfile.Local)]
    [TestCase("LOCAL", CdcDurabilityProfile.Local)]
    [TestCase("production", CdcDurabilityProfile.Production)]
    [TestCase("Production", CdcDurabilityProfile.Production)]
    public void It_parses_each_defined_durability_profile(string value, CdcDurabilityProfile expected)
    {
        CdcControlOptions
            .TryParseDurabilityProfile(value, out CdcDurabilityProfile? parsed)
            .Should()
            .BeTrue();

        parsed.Should().Be(expected);
    }

    [TestCase(0)]
    [TestCase(-1)]
    public void It_requires_max_record_bytes_because_it_has_no_default(int maxRecordBytes)
    {
        CdcControlOptions options = ValidOptions();
        options.MaxRecordBytes = maxRecordBytes;

        ValidateOptionsResult result = Validate(options);

        result.Succeeded.Should().BeFalse();
        result
            .Failures.Should()
            .ContainSingle(failure =>
                failure.Contains(nameof(CdcControlOptions.MaxRecordBytes), StringComparison.Ordinal)
                && failure.Contains("no default", StringComparison.Ordinal)
            );
    }

    [Test]
    public void It_rejects_a_producer_buffer_below_the_shared_minimum()
    {
        CdcControlOptions options = ValidOptions();
        options.MaxRecordBytes = 1_048_576;
        options.ProducerBufferBytes = CdcConnectorTemplateDeploymentPolicy.MinimumProducerBufferBytes - 1;

        AssertFailsWith(options, nameof(CdcControlOptions.ProducerBufferBytes));
    }

    [Test]
    public void It_rejects_a_producer_buffer_below_a_larger_max_record_size()
    {
        CdcControlOptions options = ValidOptions();
        options.MaxRecordBytes = CdcConnectorTemplateDeploymentPolicy.MinimumProducerBufferBytes * 2;
        options.ProducerBufferBytes = CdcConnectorTemplateDeploymentPolicy.MinimumProducerBufferBytes;

        AssertFailsWith(options, nameof(CdcControlOptions.ProducerBufferBytes));
    }

    [Test]
    public void It_accepts_a_producer_buffer_at_the_derived_minimum()
    {
        CdcControlOptions options = ValidOptions();
        options.MaxRecordBytes = CdcConnectorTemplateDeploymentPolicy.MinimumProducerBufferBytes * 2;
        options.ProducerBufferBytes = CdcConnectorTemplateDeploymentPolicy.MinimumProducerBufferBytes * 2;

        Validate(options).Succeeded.Should().BeTrue();
    }

    [Test]
    public void It_accepts_an_omitted_producer_buffer()
    {
        CdcControlOptions options = ValidOptions();
        options.ProducerBufferBytes = null;

        Validate(options).Succeeded.Should().BeTrue();
    }

    [Test]
    public void It_requires_a_positive_lag_threshold()
    {
        CdcControlOptions options = ValidOptions();
        options.LagThreshold = TimeSpan.Zero;

        AssertFailsWith(options, nameof(CdcControlOptions.LagThreshold));
    }

    [Test]
    public void It_rejects_a_non_positive_heartbeat_interval_when_supplied()
    {
        CdcControlOptions options = ValidOptions();
        options.HeartbeatInterval = TimeSpan.Zero;

        AssertFailsWith(options, nameof(CdcControlOptions.HeartbeatInterval));
    }

    [Test]
    public void It_rejects_a_non_positive_sql_server_poll_interval_when_supplied()
    {
        CdcControlOptions options = ValidOptions();
        options.SqlServerPollInterval = TimeSpan.FromSeconds(-1);

        AssertFailsWith(options, nameof(CdcControlOptions.SqlServerPollInterval));
    }

    [Test]
    public void It_accepts_omitted_optional_intervals()
    {
        CdcControlOptions options = ValidOptions();
        options.HeartbeatInterval = null;
        options.SqlServerPollInterval = null;

        Validate(options).Succeeded.Should().BeTrue();
    }

    [Test]
    public void It_accepts_an_empty_consumer_list()
    {
        CdcControlOptions options = ValidOptions();
        options.AclsEnabled = true;
        options.ConnectorPrincipal = "User:connector";
        options.ConnectWorkerPrincipal = "User:worker";
        options.Consumers = new List<CdcConsumerOptions>();

        ValidateOptionsResult result = Validate(options);

        result.Succeeded.Should().BeTrue(result.FailureMessage);
    }

    [Test]
    public void It_rejects_a_blank_consumer_principal()
    {
        CdcControlOptions options = ValidOptions();
        options.Consumers = [Consumer("User:reader", "reader-group"), Consumer("  ", "other-group")];

        AssertFailsWith(options, nameof(CdcControlOptions.Consumers));
    }

    [Test]
    public void It_rejects_a_blank_consumer_group()
    {
        CdcControlOptions options = ValidOptions();
        options.Consumers = [Consumer("User:reader", "")];

        AssertFailsWith(options, nameof(CdcControlOptions.Consumers));
    }

    [Test]
    public void It_rejects_one_principal_granted_more_than_one_consumer_group()
    {
        CdcControlOptions options = ValidOptions();
        options.Consumers = [Consumer("User:reader", "group-a"), Consumer("User:reader", "group-b")];

        ValidateOptionsResult result = Validate(options);

        result.Succeeded.Should().BeFalse();
        result
            .Failures.Should()
            .ContainSingle(failure =>
                failure.Contains("more than one consumer group", StringComparison.Ordinal)
            );
    }

    [Test]
    public void It_rejects_consumers_sharing_one_consumer_group()
    {
        CdcControlOptions options = ValidOptions();
        options.Consumers = [Consumer("User:reader", "shared"), Consumer("User:other", "shared")];

        ValidateOptionsResult result = Validate(options);

        result.Succeeded.Should().BeFalse();
        result
            .Failures.Should()
            .ContainSingle(failure => failure.Contains("share one consumer group", StringComparison.Ordinal));
    }

    private static CdcConsumerOptions Consumer(string principal, string consumerGroup) =>
        new() { Principal = principal, ConsumerGroup = consumerGroup };

    [Test]
    public void It_requires_acl_principals_when_acls_are_enabled()
    {
        CdcControlOptions options = ValidOptions();
        options.AclsEnabled = true;
        options.ConnectorPrincipal = string.Empty;
        options.ConnectWorkerPrincipal = string.Empty;

        ValidateOptionsResult result = Validate(options);

        result.Succeeded.Should().BeFalse();
        result.Failures.Should().HaveCount(2);
    }

    /// <summary>
    /// Only the Connect worker principal is ACL-conditional. The connector principal is named by the
    /// provider-setup pass every verb runs, so a deployment with no authorizer still requires it —
    /// which is what <see cref="CdcProviderSetupInputsFactory"/> refuses every verb without.
    /// </summary>
    [Test]
    public void It_requires_only_the_worker_principal_conditionally_when_acls_are_disabled()
    {
        CdcControlOptions options = ValidOptions();
        options.AclsEnabled = false;
        options.ConnectWorkerPrincipal = string.Empty;

        Validate(options).Succeeded.Should().BeTrue();
    }

    [Test]
    public void It_requires_the_connector_principal_when_acls_are_disabled()
    {
        CdcControlOptions options = ValidOptions();
        options.AclsEnabled = false;
        options.ConnectorPrincipal = string.Empty;
        options.ConnectWorkerPrincipal = string.Empty;

        AssertFailsWith(options, nameof(CdcControlOptions.ConnectorPrincipal));
    }

    [Test]
    public void It_requires_a_supplied_dms_base_url_to_be_an_absolute_http_url()
    {
        CdcControlOptions options = ValidOptions();
        options.DmsBaseUrl = "dms.example.org";

        AssertFailsWith(options, nameof(CdcControlOptions.DmsBaseUrl));
    }

    /// <summary>
    /// Projection-status access is a precondition of the verbs that read the running projector's
    /// report — enable, status, restart — and of nothing else. Requiring it to resolve the options made
    /// it a precondition of every verb, so a deployment that could not mint an operator token could not
    /// retire a binding either; and retirement matters most when the DMS that mints the token is gone.
    /// The collector refuses for itself, so the verbs that need these still fail closed.
    /// </summary>
    [Test]
    public void It_admits_options_that_configure_no_projection_status_access()
    {
        CdcControlOptions options = ValidOptions();
        options.DmsBaseUrl = string.Empty;
        options.DmsBearerToken = string.Empty;

        new CdcControlOptionsValidator().Validate(Options.DefaultName, options).Failed.Should().BeFalse();
    }

    [TestCaseSource(nameof(TimeoutCases))]
    public void It_requires_every_step_timeout_to_be_positive(
        string settingName,
        Action<CdcControlTimeoutOptions> clear
    )
    {
        CdcControlOptions options = ValidOptions();
        clear(options.Timeouts);

        AssertFailsWith(options, settingName);
    }

    private static IEnumerable<TestCaseData> TimeoutCases()
    {
        yield return TimeoutCase(
            nameof(CdcControlTimeoutOptions.EligibilityProbe),
            timeouts => timeouts.EligibilityProbe = TimeSpan.Zero
        );
        yield return TimeoutCase(
            nameof(CdcControlTimeoutOptions.KafkaAdmin),
            timeouts => timeouts.KafkaAdmin = TimeSpan.Zero
        );
        yield return TimeoutCase(
            nameof(CdcControlTimeoutOptions.ConnectRequest),
            timeouts => timeouts.ConnectRequest = TimeSpan.FromSeconds(-1)
        );
        yield return TimeoutCase(
            nameof(CdcControlTimeoutOptions.StatusEndpoint),
            timeouts => timeouts.StatusEndpoint = TimeSpan.Zero
        );
        yield return TimeoutCase(
            nameof(CdcControlTimeoutOptions.ProviderSetup),
            timeouts => timeouts.ProviderSetup = TimeSpan.Zero
        );
        yield return TimeoutCase(
            nameof(CdcControlTimeoutOptions.ProjectionCaughtUp),
            timeouts => timeouts.ProjectionCaughtUp = TimeSpan.Zero
        );
        yield return TimeoutCase(
            nameof(CdcControlTimeoutOptions.ProviderBarrier),
            timeouts => timeouts.ProviderBarrier = TimeSpan.Zero
        );
        yield return TimeoutCase(
            nameof(CdcControlTimeoutOptions.PollInterval),
            timeouts => timeouts.PollInterval = TimeSpan.Zero
        );
    }

    [Test]
    public void It_projects_a_validated_configuration_onto_the_connector_template_deployment_policy()
    {
        CdcControlOptions options = ValidOptions();

        CdcConnectorTemplateDeploymentPolicy policy = options.ToDeploymentPolicy();

        policy.KafkaBootstrapServers.Should().Be(options.KafkaBootstrapServers);
        policy.MaxRecordBytes.Should().Be(options.MaxRecordBytes);
        policy.ProducerBufferBytes.Should().Be(options.ProducerBufferBytes);
        policy.HeartbeatInterval.Should().Be(options.HeartbeatInterval);
        policy.SqlServerPollInterval.Should().Be(options.SqlServerPollInterval);
    }

    [Test]
    public void It_copies_property_bags_so_later_option_mutation_cannot_change_a_projection()
    {
        CdcControlOptions options = ValidOptions();
        options.ProviderConnectionProperties["database.hostname"] = "source-host";
        options.KafkaClientSecurityProperties["security.protocol"] = "PLAINTEXT";

        CdcProviderConnectionProperties providerProperties = options.ToProviderConnectionProperties(
            CdcProvider.Postgresql
        );
        CdcKafkaClientSecurityProperties securityProperties = options.ToKafkaClientSecurityProperties();
        options.ProviderConnectionProperties["database.hostname"] = "changed";
        options.KafkaClientSecurityProperties.Clear();

        providerProperties.Provider.Should().Be(CdcProvider.Postgresql);
        providerProperties.Properties.Should().Contain("database.hostname", "source-host");
        securityProperties.Properties.Should().Contain("security.protocol", "PLAINTEXT");
    }

    [Test]
    public void It_rejects_a_null_options_instance()
    {
        Action validation = () => Validator.Validate(name: null, options: null!);

        validation.Should().Throw<ArgumentNullException>();
    }

    private static ValidateOptionsResult Validate(CdcControlOptions options) =>
        Validator.Validate(name: null, options);

    /// <summary>
    /// The two Kafka security dictionaries configure different client implementations that do not share
    /// a configuration vocabulary. The connector's are Java Kafka client properties for the client the
    /// rendered connector runs inside the Connect worker; the control plane reaches the broker through
    /// librdkafka, which defines none of them and throws on an unknown property name when the client is
    /// built. The client is built lazily on first resolution, so without this the deployment starts
    /// cleanly and fails inside whichever verb happens to touch Kafka first.
    /// </summary>
    [TestCase("sasl.jaas.config")]
    [TestCase("ssl.truststore.location")]
    [TestCase("ssl.keystore.certificate.chain")]
    public void It_refuses_an_admin_client_security_property_librdkafka_does_not_define(
        string javaPropertyName
    )
    {
        CdcControlOptions options = ValidOptions();
        options.KafkaAdminClientSecurityProperties[javaPropertyName] = "value";

        AssertFailsWith(options, javaPropertyName);
    }

    [TestCase("security.protocol", "SASL_SSL")]
    [TestCase("sasl.username", "cdc-control")]
    [TestCase("sasl.password", "secret")]
    [TestCase("ssl.ca.location", "/etc/ssl/ca.pem")]
    public void It_accepts_an_admin_client_security_property_librdkafka_defines(
        string propertyName,
        string value
    )
    {
        CdcControlOptions options = ValidOptions();
        options.KafkaAdminClientSecurityProperties[propertyName] = value;

        Validate(options).Succeeded.Should().BeTrue();
    }

    /// <summary>
    /// An empty value is a property librdkafka would be handed as blank, which is not a configuration
    /// the deployment can have meant.
    /// </summary>
    [Test]
    public void It_refuses_an_empty_admin_client_security_property_value()
    {
        CdcControlOptions options = ValidOptions();
        options.KafkaAdminClientSecurityProperties["sasl.password"] = "   ";

        AssertFailsWith(options, "sasl.password");
    }

    /// <summary>
    /// Empty is what every PLAINTEXT deployment uses, which is all of local and E2E.
    /// </summary>
    [Test]
    public void It_accepts_no_admin_client_security_properties_at_all()
    {
        CdcControlOptions options = ValidOptions();
        options.KafkaClientSecurityProperties["sasl.jaas.config"] = "value";

        Validate(options).Succeeded.Should().BeTrue();
    }

    private static void AssertFailsWith(CdcControlOptions options, string settingName)
    {
        ValidateOptionsResult result = Validate(options);

        result.Succeeded.Should().BeFalse();
        result
            .Failures.Should()
            .ContainSingle(failure => failure.Contains(settingName, StringComparison.Ordinal));
    }

    private static TestCaseData Case(string settingName, Action<CdcControlOptions> clear) =>
        new TestCaseData(settingName, clear).SetArgDisplayNames(settingName);

    private static TestCaseData TimeoutCase(string settingName, Action<CdcControlTimeoutOptions> clear) =>
        new TestCaseData(settingName, clear).SetArgDisplayNames(settingName);

    private static CdcControlOptions ValidOptions() =>
        new()
        {
            DeploymentKey = "deployment",
            InstanceKey = "instance",
            TopicPrefix = "edfi.documents.instance",
            SetupPrincipal = "setup_principal",
            ConnectorPrincipal = "connector_principal",
            Generation = 7,
            PartitionCount = 3,
            KafkaBootstrapServers = "localhost:9092",
            ConnectBaseUri = "http://localhost:8083",
            ConnectWorkerKey = "worker",
            ConnectOffsetStorageTopic = "connect-offsets",
            DurabilityProfile = CdcControlOptions.LocalDurabilityProfile,
            MaxRecordBytes = 4_194_304,
            LagThreshold = TimeSpan.FromSeconds(30),
            DmsBaseUrl = "http://localhost:8080",
            DmsBearerToken = "token",
        };
}
