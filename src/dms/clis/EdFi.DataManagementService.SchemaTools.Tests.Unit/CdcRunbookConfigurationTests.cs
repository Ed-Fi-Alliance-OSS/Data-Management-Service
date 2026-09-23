// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.Cdc;
using EdFi.DataManagementService.Backend.Cdc.Tests.Unit;
using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using EdFi.DataManagementService.SchemaTools.Cdc;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using CoreProvider = EdFi.DataManagementService.Core.DocumentCache.Cdc.CdcProvider;

namespace EdFi.DataManagementService.SchemaTools.Tests.Unit;

[NonParallelizable]
public partial class Given_Cdc_command_configuration
{
    // These settings snippets explicitly require Linux ownership tools and PowerShell, like the
    // existing wrapper/process fixtures. No database, Docker, or deployment settings are touched.
    private async Task<(string SettingsPath, string StatePath)> RunMarkedSettingsAsync(string mutation = "")
    {
        string fixture = Path.Combine(_root, "runbook");
        Directory.CreateDirectory(Path.Combine(fixture, ".local/cdc"));
        Directory.CreateDirectory(Path.Combine(fixture, "eng/docker-compose"));
        var baseline = JsonNode.Parse(
            await File.ReadAllTextAsync(
                Path.Combine(
                    CdcRunbookSnippets.RepositoryRoot,
                    "src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/appsettings.json"
                )
            ),
            documentOptions: new() { CommentHandling = System.Text.Json.JsonCommentHandling.Skip }
        )!;
        baseline["ConfigurationServiceSettings"]!["BaseUrl"] = "http://localhost:8081";
        baseline["ConfigurationServiceSettings"]!["ClientId"] = "runbook-client";
        baseline["ConfigurationServiceSettings"]!["ClientSecret"] = "fixture-only-secret";
        await File.WriteAllTextAsync(
            Path.Combine(fixture, ".local/cdc/dms-base.json"),
            baseline.ToJsonString()
        );
        await File.WriteAllTextAsync(
            Path.Combine(fixture, "eng/docker-compose/.env"),
            "# isolated fixture environment\n"
        );
        string id = provider == CoreProvider.Postgresql ? "cdc-pg-settings" : "cdc-sqlserver-settings";
        string code = CdcRunbookSnippets.Read(id);
        if (mutation == "provider")
        {
            code = code.Replace(
                $"Provider = '{providerToken}'",
                "Provider = 'invalid-provider'",
                StringComparison.Ordinal
            );
        }
        if (mutation == "field")
        {
            code = code.Replace(
                "LagThresholdMilliseconds =",
                "LagThresholdMillisecondz =",
                StringComparison.Ordinal
            );
        }
        if (mutation == "missing")
        {
            code = code.Replace("InstanceKey = 'datastore-1';", "", StringComparison.Ordinal);
        }
        string script = Path.Combine(fixture, "settings-snippet.ps1");
        // Only the declared masked prompt input is supplied by the fixture. The selected snippet
        // itself is unchanged; paths/endpoints/identities remain its documented literal values.
        string connection =
            provider == CoreProvider.Postgresql
                ? "Host=127.0.0.1;Port=5432;Database=edfi_cdc;Username=postgres;Password=fixture-only-secret"
                : "Server=127.0.0.1,1435;Database=edfi_cdc;User Id=sa;Password=fixture-only-secret;Encrypt=true;TrustServerCertificate=true;Command Timeout=180";
        await File.WriteAllTextAsync(
            script,
            "function Read-Host { param([string]$Prompt, [switch]$MaskInput) return '"
                + connection
                + "' }\n"
                + code
        );
        var info = new ProcessStartInfo("pwsh")
        {
            WorkingDirectory = fixture,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { "-NoLogo", "-NoProfile", "-NonInteractive", "-File", script },
        };
        foreach (
            string key in info
                .Environment.Keys.Where(k => k.StartsWith("DMS_CDC__", StringComparison.OrdinalIgnoreCase))
                .ToArray()
        )
        {
            info.Environment.Remove(key);
        }
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
        process.ExitCode.Should().Be(0, await stderr);
        (await stdout).Should().BeEmpty("settings construction must not disclose configuration");
        string name = provider == CoreProvider.Postgresql ? "postgresql" : "sqlserver";
        return (
            Path.Combine(fixture, $".local/cdc/{name}.json"),
            Path.Combine(
                fixture,
                ".local/cdc/state-" + (provider == CoreProvider.Postgresql ? "pg" : "sqlserver")
            )
        );
    }

    private void LoadMarkedSettings(string path)
    {
        ((IDisposable)_settings).Dispose();
        _config = CdcCommandConfiguration.Load(path);
        _settings = _config.Settings;
    }

    [Test]
    [Platform(Exclude = "Win", Reason = "Runbook settings require Linux chmod.")]
    public async Task It_Cdc_runbook_loads_complete_provider_settings_and_renders_the_connector()
    {
        using var isolated = new CdcRunbookEnvironment();
        var (path, state) = await RunMarkedSettingsAsync();
        LoadMarkedSettings(path);
        _config.ValidateControllerSettings();
        _config
            .Target.Should()
            .Be(new CdcTargetIdentity("local", "default", "1", "datastore-1", 1, provider));
        _settings["Cdc:Provider"].Should().Be(providerToken);
        _settings["AppSettings:Datastore"]
            .Should()
            .Be(provider == CoreProvider.Postgresql ? "postgresql" : "mssql");
        _settings["ConfigurationServiceSettings:ClientId"].Should().Be("runbook-client");
        _settings["ConfigurationServiceSettings:ClientSecret"].Should().Be("fixture-only-secret");
        _settings["DataManagement:DocumentCache:Targets:0:DataStoreId"].Should().Be("1");
        _settings["AppSettings:MultiTenancy"].Should().Be("False");
        _config.LagThreshold.Should().Be(5000);
        _settings["Cdc:KafkaBootstrapServers"].Should().Be("dms-kafka1:9092");
        _settings["Cdc:KafkaAdminBootstrapServers"].Should().Be("127.0.0.1:9092");
        _settings["Cdc:SetupPrincipal"].Should().Be(provider == CoreProvider.Postgresql ? "postgres" : "sa");
        _settings["Cdc:Worker:OffsetStorageTopic"].Should().Be("dms-connect-offsets");
        _config.Project.Should().Be("dms-local");
        _config.ComposeFile.Should().Be(Path.Combine(_root, "runbook/eng/docker-compose/kafka-cdc.yml"));
        _config.EnvironmentFile.Should().Be(Path.Combine(_root, "runbook/eng/docker-compose/.env"));
        _config.BrokerSizeOverride.Should().Be(Path.Combine(state, "broker-size.json"));
        _config
            .Timing.CallTimeout.Should()
            .Be(TimeSpan.FromMilliseconds(provider == CoreProvider.Postgresql ? 30000 : 180000));
        _config
            .Timing.WaitTimeout.Should()
            .Be(TimeSpan.FromMilliseconds(provider == CoreProvider.Postgresql ? 300000 : 600000));
        await using (var connection = _config.CreateConnection())
        {
            connection.Database.Should().Be("edfi_cdc");
        }
        var provisioner = A.Fake<ICdcManagedDatabaseProvisioner>();
        A.CallTo(() => provisioner.CreateDatabase()).Returns(true);
        A.CallTo(() => provisioner.ReadSourceFingerprintAsync(A<CancellationToken>._)).Returns(Fingerprint);
        await new CdcManagedDatabaseProvisioning(new LocalCdcWorkflowJournalStore(state)).ProvisionAsync(
            _config.Target,
            provisioner,
            purpose: CdcWorkflowPurpose.InitialCdcProvisioning
        );
        // Wrapper inputs deliberately have no schemas. They cannot be silently used as CLI inputs.
        Func<Task> unstaged = () => RequestAsync(state);
        await unstaged.Should().ThrowAsync<ArgumentException>();
        var settings = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        settings["Cdc"]!["Schemas"] = new JsonArray(
            Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures/minimal-api-schema.json")
        );
        await File.WriteAllTextAsync(path, settings.ToJsonString());
        LoadMarkedSettings(path);
        var request = await RequestAsync(state);
        request.Binding.Provider.Should().Be(provider);
        request.Binding.DataStoreId.Should().Be("1");
        request.ProviderSetup.ExpectedSourceInventory.Should().HaveCount(3);
        request.ConnectEndpoint.Should().Be(new Uri("http://127.0.0.1:8083"));
        request.WorkerMetricsEndpoint.Should().Be(new Uri("http://127.0.0.1:9404/metrics"));
        request.ConnectorPolicy.MaxRecordBytes.Should().Be(10000000);
        request.WorkerPolicy.QualifiedImageDigest.Should().Be(CdcQualifiedWorkerImage.Digest);
        var ddlProvider = CdcConnectorTemplateTestData.ToDdlProvider(provider);
        var evidence = CdcConnectorTemplateTestData.BuildProviderSetupResult(
            ddlProvider,
            boundPhysicalSourceFingerprint: new(CdcSourceFingerprintMetadata.Version, Fingerprint),
            binding: request.Binding
        );
        using var services = new ServiceCollection().AddCdcConnectorTemplates().BuildServiceProvider();
        var rendered = services
            .GetRequiredService<ICdcConnectorTemplateService>()
            .Render(
                CdcConnectorTemplateTestData.BuildRequest(
                    evidence,
                    request.Binding,
                    request.Binding.Generation,
                    request.ProviderConnectionProperties,
                    request.ConnectorPolicy,
                    request.KafkaClientSecurityProperties
                )
            );
        rendered.Diagnostics.Should().BeEmpty();
        rendered
            .Config["database.hostname"]
            .Should()
            .Be(provider == CoreProvider.Postgresql ? "dms-postgresql" : "dms-mssql");
        rendered.Config["database.port"].Should().Be(provider == CoreProvider.Postgresql ? "5432" : "1433");
        rendered
            .Config[provider == CoreProvider.Postgresql ? "database.dbname" : "database.names"]
            .Should()
            .Be("edfi_cdc");
        rendered.Config["database.user"].Should().Be("cdc_reader");
        rendered.Config["database.password"].Should().Be("${env:CDC_DATABASE_PASSWORD}");
        _settings["Cdc:DatabaseConnectorPrincipal"].Should().Be(rendered.Config["database.user"]);
    }

    [TestCase("provider")]
    [TestCase("field")]
    [TestCase("missing")]
    [Platform(Exclude = "Win", Reason = "Runbook settings require Linux chmod.")]
    public async Task It_Cdc_runbook_rejects_broken_copied_settings_despite_ambient_overrides(string mutation)
    {
        using var restore = new CdcRunbookEnvironment();
        Environment.SetEnvironmentVariable("DMS_CDC__Cdc__Provider", providerToken);
        Environment.SetEnvironmentVariable("DMS_CDC__Cdc__LagThresholdMilliseconds", "5000");
        Environment.SetEnvironmentVariable("DMS_CDC__Cdc__InstanceKey", "datastore-1");
        using (var isolated = new CdcRunbookEnvironment())
        {
            var (path, _) = await RunMarkedSettingsAsync(mutation);
            LoadMarkedSettings(path);
            Action validate = _config.ValidateControllerSettings;
            validate.Should().Throw<ArgumentException>();
        }
        Environment.GetEnvironmentVariable("DMS_CDC__Cdc__Provider").Should().Be(providerToken);
        Environment.GetEnvironmentVariable("DMS_CDC__Cdc__LagThresholdMilliseconds").Should().Be("5000");
        Environment.GetEnvironmentVariable("DMS_CDC__Cdc__InstanceKey").Should().Be("datastore-1");
    }

    [TestCase("")]
    [TestCase("missing")]
    [TestCase("unknown")]
    [TestCase("scope")]
    [TestCase("confirmation")]
    public async Task It_Cdc_runbook_reads_the_marked_no_consumers_acknowledgement(string mutation)
    {
        var request = await RequestAsync();
        var identity = request.Binding.ToCompleteBindingIdentity();
        Guid operation = Guid.NewGuid();
        var inputs = new Dictionary<string, string>
        {
            ["<increase-operation-uuid>"] = operation.ToString(),
            ["<binding-deployment-key>"] = identity.DeploymentKey,
            ["<binding-tenant-key>"] = identity.TenantKey,
            ["<binding-data-store-id>"] = identity.DataStoreId,
            ["<binding-instance-key>"] = identity.InstanceKey,
            ["<binding-provider>"] = identity.Provider.ToString(),
            ["<binding-physical-source-fingerprint>"] = identity.PhysicalSourceFingerprint,
            ["<binding-connector-name>"] = identity.ConnectorName,
            ["<binding-topic-name>"] = identity.TopicName,
            ["<operator-token>"] = "fixture-operator",
        };
        var json = JsonNode.Parse(CdcRunbookSnippets.Read("cdc-size-no-consumers", "json"))!;
        foreach (var property in json.AsObject().ToArray())
        {
            if (
                property.Value is JsonValue value
                && value.TryGetValue<string>(out var text)
                && inputs.TryGetValue(text, out var replacement)
            )
            {
                json[property.Key] = replacement;
            }
        }
        foreach (var property in json["bindingIdentity"]!.AsObject().ToArray())
        {
            if (property.Value is JsonValue value && value.TryGetValue<string>(out var text))
            {
                json["bindingIdentity"]![property.Key] = inputs[text];
            }
        }
        json["bindingIdentity"]!["generation"] = identity.Generation;
        if (mutation == "missing")
        {
            json.AsObject().Remove("noConsumers");
        }
        if (mutation == "unknown")
        {
            json["invocationId"] = "not-permitted";
        }
        if (mutation == "scope")
        {
            json["bindingIdentity"]!["physicalSourceFingerprint"] = "sha256:" + new string('b', 64);
        }
        string path = Path.Combine(_root, "runbook-ack.json");
        await File.WriteAllTextAsync(path, json.ToJsonString());
        var invocation = new CdcCommandInvocation(
            CdcCommandOperation.IncreaseRecordSize,
            "",
            _root,
            1,
            0,
            false,
            path,
            mutation != "confirmation"
        );
        if (mutation.Length > 0)
        {
            Func<Task> read = () => CdcCommandRunner.ReadAcknowledgementAsync(invocation, request, default);
            if (mutation is "missing" or "unknown")
            {
                await read.Should().ThrowAsync<System.Text.Json.JsonException>();
            }
            else
            {
                await read.Should().ThrowAsync<ArgumentException>();
            }
            return;
        }
        var parsed = await CdcCommandRunner.ReadAcknowledgementAsync(invocation, request, default);
        parsed.OperationId.Should().Be(operation);
        parsed.BindingIdentity.Should().Be(identity);
        parsed.NoConsumers.Should().BeTrue();
        parsed.Consumers.Should().BeEmpty();
        parsed.PreviousMaxRecordBytes.Should().Be(request.ConnectorPolicy.MaxRecordBytes);
        parsed.RequestedMaxRecordBytes.Should().Be(20000000);
        parsed.RequestedProducerBufferBytes.Should().Be(33554432);
        parsed.OperatorIdentity.Should().Be("fixture-operator");
    }
}
