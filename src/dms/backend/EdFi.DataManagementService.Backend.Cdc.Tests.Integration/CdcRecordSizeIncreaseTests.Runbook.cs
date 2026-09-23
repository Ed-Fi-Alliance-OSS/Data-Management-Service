// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.DocumentCacheAdmin.Tests.Integration;
using EdFi.DataManagementService.SchemaTools.Tests.Unit;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;
using Ddl = EdFi.DataManagementService.Backend.Ddl;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

public sealed partial class Given_Cdc_Controller_Record_Size_Increase
{
    private DocumentCacheAdminCliProcessHarness _commandRuntime = null!;
    private string _settingsPath = "";
    private string _acknowledgementPath = "";
    private static bool IsRunbookCase =>
        TestContext.CurrentContext.Test.MethodName!.StartsWith(
            "It_executes_marked_",
            StringComparison.Ordinal
        );

    private static string[] RunbookSchemaFiles()
    {
        if (!IsRunbookCase)
        {
            return null!; // Preserve the original controller fixtures' minimal schema.
        }
        string workspace = DocumentCacheAdminCliFixture.Shared.ApiSchemaDirectory;
        var manifest = ApiSchemaAssetManifestReader.ReadFromFile(
            workspace,
            Path.Combine(workspace, ApiSchemaAssetManifestReader.ManifestFileName)
        );
        return manifest.Projects.Select(p => Path.Combine(workspace, p.SchemaPath)).ToArray();
    }

    private async Task PrepareRunbookAsync()
    {
        _settingsPath = Path.Combine(_fixture.Infrastructure.StateRoot, "size-settings.json");
        _acknowledgementPath = Path.Combine(_fixture.Infrastructure.StateRoot, "size-acknowledgement.json");
        _commandRuntime = await DocumentCacheAdminCliProcessHarness.CreateAsync(
            DocumentCacheAdminCliTarget.ForManagedSource(
                provider == Ddl.CdcProvider.SqlServer,
                _fixture.ConnectionString
            )
        );
        var settings = new ConfigurationBuilder().AddJsonFile(_commandRuntime.SettingsPath).Build();
        using var lifetime = (IDisposable)settings;
        var runtimeSettings = settings
            .AsEnumerable()
            .Where(p => p.Value is not null)
            .ToDictionary(p => p.Key, p => p.Value!);
        runtimeSettings["ConfigurationServiceSettings:ClientSecret"] = _commandRuntime.SecretFromEnvironment;
        runtimeSettings["Cdc:Timing:CallMilliseconds"] = "180000";
        runtimeSettings["Cdc:Timing:WaitMilliseconds"] = "300000";
        await CdcRunbookLiveCommands.WriteSettingsAsync(
            _fixture,
            provider,
            _settingsPath,
            Token,
            runtimeSettings
        );
        var images = new Dictionary<string, string>();
        foreach (string role in new[] { "provider", "broker", "connect" })
        {
            var inspected = await new DockerCli().RunAsync(
                [
                    "inspect",
                    _fixture.Infrastructure.Resources.ControllerProject + "-" + role,
                    "--format",
                    "{{.Image}}",
                ],
                Token
            );
            string image = inspected.StandardOutput.Trim();
            image.Should().MatchRegex("^sha256:[a-f0-9]{64}$");
            images[role] = image;
        }
        _evidence.Add(new { Profile = "LocalSingleBroker/AuthorizationDisabledLocal", Images = images });
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_executes_marked_record_size_increase_with_explicit_inventory(bool consumers)
    {
        await WriteAcknowledgementAsync(consumers);
        string originalSettings = await File.ReadAllTextAsync(_settingsPath, Token);
        var before = await CaptureLimitsAsync("marked-before-missing-confirmation");
        var rejected = await InvokeSizeAsync("cdc-size-increase", confirm: false);
        rejected.GetProperty("exitCode").GetInt32().Should().Be(2);
        (await _fixture.JournalAsync(Token)).HasPendingRecordSizeIncrease.Should().BeFalse();
        (await CaptureLimitsAsync("marked-missing-confirmation")).Should().BeEquivalentTo(before);
        var result = await InvokeSizeAsync("cdc-size-increase");
        await AssertRunbookCompleteAsync(result, originalSettings, 1, consumers);
    }

    [Test]
    public async Task It_executes_marked_record_size_retry_after_partial_broker_change()
    {
        // Existing controller fault seam cancels after a real Compose broker update. The
        // subsequent public CLI process has no test hook and must reconcile retained intent.
        using var interrupted = CancellationTokenSource.CreateLinkedTokenSource(Token);
        bool reached = false;
        _fixture.Hooks.OnBoundary = e =>
        {
            if (e.Boundary == CdcControllerBoundary.Rollout && e.Edge == CdcControllerEdge.After)
            {
                reached = true;
                interrupted.Cancel();
                interrupted.Token.ThrowIfCancellationRequested();
            }
        };
        await FluentActions
            .Awaiting(() => IncreaseAsync(token: interrupted.Token, consumers: true))
            .Should()
            .ThrowAsync<OperationCanceledException>();
        reached.Should().BeTrue();
        _fixture.Hooks.OnBoundary = _ => { };
        var partial = await CaptureLimitsAsync("marked-retry-interrupted");
        partial.Brokers.Brokers.Should().OnlyContain(b => b.ReplicaFetchMaxBytes >= Ceiling);
        partial.Topic.Should().Be(_scope.PreviousMaxRecordBytes);
        File.Exists(_fixture.Infrastructure.Resources.ControllerSizeOverride).Should().BeTrue();
        await RequirePendingAcrossOrdinaryCommandsAsync();
        await WriteAcknowledgementAsync(
            consumers: true,
            revision: "revision-2",
            evidence: "capacity-evidence-2"
        );
        string originalSettings = await File.ReadAllTextAsync(_settingsPath, Token);
        var rejected = await InvokeSizeAsync("cdc-size-retry", confirm: false);
        rejected.GetProperty("exitCode").GetInt32().Should().Be(2);
        (await _fixture.JournalAsync(Token)).HasPendingRecordSizeIncrease.Should().BeTrue();
        (await CaptureLimitsAsync("marked-retry-missing-confirmation")).Should().BeEquivalentTo(partial);
        var result = await InvokeSizeAsync("cdc-size-retry");
        await AssertRunbookCompleteAsync(result, originalSettings, 2, consumers: true);
    }

    private async Task WriteAcknowledgementAsync(
        bool consumers,
        string revision = "revision-1",
        string evidence = "capacity-evidence-1"
    )
    {
        string id = consumers ? "cdc-size-consumers" : "cdc-size-no-consumers";
        var identity = _scope.BindingIdentity;
        var replacements = new Dictionary<string, string>
        {
            ["<increase-operation-uuid>"] = _scope.OperationId.ToString(),
            ["<binding-deployment-key>"] = identity.DeploymentKey,
            ["<binding-tenant-key>"] = identity.TenantKey,
            ["<binding-data-store-id>"] = identity.DataStoreId,
            ["<binding-instance-key>"] = identity.InstanceKey,
            ["<binding-provider>"] = identity.Provider.ToString(),
            ["<binding-physical-source-fingerprint>"] = identity.PhysicalSourceFingerprint,
            ["<binding-connector-name>"] = identity.ConnectorName,
            ["<binding-topic-name>"] = identity.TopicName,
            ["<operator-token>"] = "rollout-operator",
            ["<consumer-deployment-token>"] = "consumer-a",
            ["<consumer-revision-token>"] = revision,
            ["<consumer-owner-token>"] = "consumer-owner",
            ["<consumer-evidence-token>"] = evidence,
        };
        string text = CdcRunbookExamples.Read(id, "json");
        foreach (var item in replacements)
        {
            text = text.Replace(
                "\"" + item.Key + "\"",
                JsonSerializer.Serialize(item.Value),
                StringComparison.Ordinal
            );
        }
        // Literal placeholder replacement must not silently accept an unbound input.
        text.Should().NotContain("<");
        var json = JsonNode.Parse(text)!;
        json["bindingIdentity"]!["generation"] = identity.Generation;
        json["previousMaxRecordBytes"] = _scope.PreviousMaxRecordBytes;
        json["requestedMaxRecordBytes"] = Ceiling;
        json["requestedProducerBufferBytes"] = Buffer;
        await File.WriteAllTextAsync(_acknowledgementPath, json.ToJsonString(), Token);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(_acknowledgementPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        _evidence.Add(new { AcknowledgementSnippetId = id, Acknowledgement = json });
    }

    private async Task<JsonElement> InvokeSizeAsync(string id, bool confirm = true)
    {
        var result = await CdcRunbookLiveCommands.InvokeAsync(
            id,
            _settingsPath,
            _fixture.Infrastructure.StateRoot,
            Token,
            new Dictionary<string, string> { ["<acknowledgement-path>"] = _acknowledgementPath },
            confirm
        );
        _evidence.Add(
            new
            {
                SnippetId = id,
                ConfirmConsumerCapacity = confirm,
                Result = result,
            }
        );
        return result;
    }

    private async Task AssertRunbookCompleteAsync(
        JsonElement result,
        string originalSettings,
        int acknowledgementCount,
        bool consumers
    )
    {
        result.GetProperty("exitCode").GetInt32().Should().Be(0, "{0}", result.GetRawText());
        result.GetProperty("succeeded").GetBoolean().Should().BeTrue();
        var data = result.GetProperty("data");
        data.GetProperty("succeeded").GetBoolean().Should().BeTrue();
        data.GetProperty("ready").GetBoolean().Should().BeTrue();
        data.GetProperty("operationId").GetGuid().Should().Be(_scope.OperationId);
        var journal = await _fixture.JournalAsync(Token);
        journal.HasPendingRecordSizeIncrease.Should().BeFalse();
        var acknowledgements = journal
            .Operations.Single(o => o.OperationId == _scope.OperationId)
            .RecordSizeIncrease.Single()
            .Acknowledgements;
        acknowledgements.Should().HaveCount(acknowledgementCount);
        acknowledgements.Select(a => a.InvocationId).Distinct().Should().HaveCount(acknowledgementCount);
        acknowledgements[^1].NoConsumers.Should().Be(!consumers);
        if (consumers)
        {
            acknowledgements[^1]
                .Consumers.Single()
                .Revision.Should()
                .Be(acknowledgementCount == 2 ? "revision-2" : "revision-1");
            acknowledgements[^1]
                .Consumers.Single()
                .EvidenceReference.Should()
                .Be(acknowledgementCount == 2 ? "capacity-evidence-2" : "capacity-evidence-1");
        }
        var limits = await CaptureLimitsAsync("marked-complete");
        limits.Topic.Should().Be(Ceiling);
        limits.Request.Should().Be(Ceiling);
        limits.ProducerBuffer.Should().Be(Buffer);
        limits
            .Brokers.Brokers.Should()
            .OnlyContain(b =>
                b.SocketRequestMaxBytes >= CdcDeploymentKafkaPolicy.MinimumBrokerRequestBytes(Ceiling)
                && b.ReplicaFetchMaxBytes >= Ceiling
                && b.ReplicaFetchResponseMaxBytes >= Ceiling
            );
        string overrideBefore = await File.ReadAllTextAsync(
            _fixture.Infrastructure.Resources.ControllerSizeOverride,
            Token
        );
        (await File.ReadAllTextAsync(_settingsPath, Token))
            .Should()
            .Be(originalSettings, "the CLI does not rewrite retained settings");
        var updated = JsonNode.Parse(originalSettings)!;
        updated["Cdc:MaxRecordBytes"] = Ceiling.ToString();
        updated["Cdc:ProducerBufferBytes"] = Buffer.ToString();
        await File.WriteAllTextAsync(_settingsPath, updated.ToJsonString(), Token);
        // Same two operator-owned updates documented after success; original identity retained.
        var inspection = await CdcRunbookLiveCommands.InvokeAsync(
            "cdc-validate",
            _settingsPath,
            _fixture.Infrastructure.StateRoot,
            Token
        );
        inspection.GetProperty("exitCode").GetInt32().Should().Be(1);
        inspection.GetProperty("data").GetProperty("preStartEligible").GetBoolean().Should().BeTrue();
        inspection.GetProperty("data").GetProperty("publicationReady").GetBoolean().Should().BeFalse();
        inspection
            .GetProperty("diagnostics")
            .EnumerateArray()
            .Should()
            .Contain(d =>
                d.GetProperty("component").GetString() == "Projection"
                && d.GetProperty("failure").GetString() == "Unavailable"
            );
        (await File.ReadAllTextAsync(_fixture.Infrastructure.Resources.ControllerSizeOverride, Token))
            .Should()
            .Be(overrideBefore);
        _evidence.Add(
            new
            {
                Acknowledgements = acknowledgements,
                SettingsUnchangedByCommand = true,
                UpdatedSettingsFields = new[] { "Cdc:MaxRecordBytes", "Cdc:ProducerBufferBytes" },
                BrokerOverrideRetained = true,
                Validation = inspection,
            }
        );
        AssertNoOffsetReset();
    }
}
