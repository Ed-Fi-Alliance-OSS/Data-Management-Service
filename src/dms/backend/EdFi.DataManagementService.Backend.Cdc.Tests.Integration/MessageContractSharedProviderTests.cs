// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;
using System.Text.Json;
using EdFi.DataManagementService.Backend.Cdc.Tests.Unit;
using EdFi.DataManagementService.Backend.Ddl;
using FluentAssertions;
using FluentAssertions.Execution;
using CoreCdc = EdFi.DataManagementService.Core.DocumentCache.Cdc;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

[TestFixture(CdcProvider.Postgresql)]
[TestFixture(CdcProvider.SqlServer)]
[Category("CdcMessageContract")]
public sealed class Given_MessageContractProviderAttachment(CdcProvider provider)
{
    private CdcDeploymentRequest _request = null!;

    [SetUp]
    public void Setup() =>
        _request = CdcDeploymentRequestTestData.Request(provider, endpoint: "http://attached-connect:18083/");

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_checks_the_effective_include_list_at_the_supplied_endpoint(bool includeWork)
    {
        using var handler = new ConfigurationHandler(includeWork);
        using var client = new HttpClient(handler);
        var observer = new MessageContractProviderObserver(
            _request.Binding,
            () => throw new AssertionException("Configuration inspection must not open the database.")
        );
        var fences = new MessageContractProviderFences(
            observer,
            _request,
            new CdcConnectRestAdapter(client, TimeProvider.System)
        );
        using var scope = new AssertionScope();
        await fences.AssertConnectorIncludeListAsync(CancellationToken.None);
        string[] failures = scope.Discard();
        handler
            .RequestUri.Should()
            .Be(new Uri(_request.ConnectEndpoint, $"connectors/{_request.Binding.ConnectorName}/config"));
        failures.Length.Should().Be(includeWork ? 1 : 0);
    }

    [TestCase("CdcHeartbeat", false)]
    [TestCase("DocumentProjectionWork", false)]
    [TestCase("CdcHeartbeat", true)]
    public void It_classifies_retained_progress_without_source_observer_instrumentation(
        string table,
        bool wrongBinding
    )
    {
        var record = Record(
            JsonSerializer.SerializeToUtf8Bytes(
                new
                {
                    source = new
                    {
                        schema = "dms",
                        table,
                        name = wrongBinding ? "other-binding" : _request.Binding.ConnectorName,
                    },
                }
            )
        );
        using var scope = new AssertionScope();
        var kind = MessageContractProgressAssertions.AssertHeartbeat(_request.Binding, record);
        string[] failures = scope.Discard();
        failures.Length.Should().Be(table == "CdcHeartbeat" && !wrongBinding ? 0 : 1);
        kind.Should().Be(MessageContractProgressKind.RelationalHeartbeat);
    }

    [Test]
    public void It_classifies_native_progress_from_the_retained_record()
    {
        var kind = MessageContractProgressAssertions.AssertHeartbeat(
            _request.Binding,
            Record("{\"ts_ms\":23}"u8.ToArray())
        );
        kind.Should().Be(MessageContractProgressKind.NativeHeartbeat);
    }

    private MessageContractKafkaRecord Record(byte[] value) =>
        new(
            CoreCdc
                .CdcArtifactNameGenerator.RecoverFromBinding(_request.Binding)
                .Inventory!.ProgressTopicName,
            0,
            42,
            MessageContractKafkaBytes.From(Encoding.UTF8.GetBytes("cdc-progress")),
            MessageContractKafkaBytes.From(value),
            [],
            0
        );

    private sealed class ConfigurationHandler(bool includeWork) : HttpMessageHandler
    {
        public Uri RequestUri { get; private set; } = null!;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            RequestUri = request.RequestUri!;
            string includeList =
                @"dms\.DocumentCache,dms\.Document,dms\.CdcHeartbeat"
                + (includeWork ? @",dms\.DocumentProjectionWork" : "");
            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        JsonSerializer.Serialize(
                            new Dictionary<string, string> { ["table.include.list"] = includeList }
                        )
                    ),
                }
            );
        }
    }
}
