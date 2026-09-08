// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Text.Json;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace EdFi.DataManagementService.DocumentCacheAdmin.Tests.Integration;

/// <summary>
/// Packaged CLI source selection over two real physical databases. The retained generation belongs
/// to the original database; CMS names the replacement. Connect is deliberately unreachable, so
/// successful source validation is observed at the subsequent fence failure, never as cleanup.
/// </summary>
[TestFixture("postgresql")]
[TestFixture("mssql")]
[NonParallelizable]
[Category("CdcRetirementOperator")]
public sealed class Given_DocumentCacheAdminRetirementSourceSelection(string provider)
{
    private const string DeploymentKey = "retirement-source";
    private DocumentCacheAdminCliTarget _current = null!;
    private DocumentCacheAdminCliTarget _original = null!;
    private DocumentCacheAdminCliProcessHarness _harness = null!;
    private string _originalFingerprint = string.Empty;
    private string _currentFingerprint = string.Empty;

    [OneTimeSetUp]
    public async Task Setup()
    {
        _current = await CreateTargetAsync();
        _original = await CreateTargetAsync();
        _harness = await DocumentCacheAdminCliProcessHarness.CreateAsync(_current);
        await _harness.ConfigureCdcBindingStateAsync(DeploymentKey, provider == "mssql" ? "sa" : "postgres");
        // Baseline clones can share the seeded UUID. Give this disposable test source a distinct
        // identity before binding it; this is fixture data, not a source-replacement operation.
        await using (
            DbConnection connection =
                provider == "postgresql"
                    ? new NpgsqlConnection(_original.ConnectionString)
                    : new SqlConnection(_original.ConnectionString)
        )
        {
            await connection.OpenAsync();
            await using DbCommand command = connection.CreateCommand();
            command.CommandText =
                provider == "postgresql"
                    ? "UPDATE dms.\"DataStoreIdentity\" SET \"SourceIdentity\" = @identity WHERE \"DataStoreIdentitySingletonId\" = 1"
                    : "UPDATE [dms].[DataStoreIdentity] SET [SourceIdentity] = @identity WHERE [DataStoreIdentitySingletonId] = 1";
            DbParameter parameter = command.CreateParameter();
            parameter.ParameterName = "identity";
            parameter.Value = Guid.NewGuid();
            command.Parameters.Add(parameter);
            (await command.ExecuteNonQueryAsync()).Should().Be(1);
        }
        _originalFingerprint = await _original.State.ReadPhysicalSourceFingerprintAsync();
        _currentFingerprint = await _current.State.ReadPhysicalSourceFingerprintAsync();
        _originalFingerprint.Should().NotBe(_currentFingerprint);
        CdcProvider engine = provider == "postgresql" ? CdcProvider.Postgresql : CdcProvider.SqlServer;
        CdcArtifactInventory inventory = CdcArtifactNameGenerator
            .Render(
                new(DeploymentKey, DocumentCacheAdminCliProcessHarness.CdcTopicPrefix, "instance", 1, engine)
            )
            .Inventory!;
        CdcBinding binding = new(
            CdcJsonContract.CurrentContractVersion,
            DeploymentKey,
            "default",
            "1",
            "instance",
            1,
            engine,
            _originalFingerprint,
            inventory.ConnectorName,
            inventory.TopicName,
            1,
            CdcTargetValidator.KafkaMurmur2V1PartitionerAlgorithm,
            CdcJsonContract.CurrentContractVersion
        );
        await using ServiceProvider services = new ServiceCollection()
            .AddDmsCdcControlPlane()
            .Configure<CdcBindingStateStoreOptions>(options =>
                options.RootPath = _harness.CdcBindingStatePath
            )
            .BuildServiceProvider();
        (await services.GetRequiredService<ICdcBindingLifecycleService>().CreateBindingIfAbsentAsync(binding))
            .Status.Should()
            .Be(CdcControlPlaneOperationStatus.Succeeded);
    }

    [TestCase("missingConfirmation", 64)]
    [TestCase("literalDefault", 64)]
    [TestCase("unsetSource", 10)]
    [TestCase("currentSource", 10)]
    [TestCase("originalSource", 12)]
    public async Task It_checks_operator_authority_and_the_original_source_before_cleanup(
        string scenario,
        int expectedExitCode
    )
    {
        string variableName = $"DMS_RETIREMENT_SOURCE_{Guid.NewGuid():N}";
        Dictionary<string, string> recordsBefore = ReadRecords();
        DocumentCacheAdminCliLifecycleState currentBefore = await _current.State.ReadLifecycleAsync();
        DocumentCacheAdminCliLifecycleState originalBefore = await _original.State.ReadLifecycleAsync();
        List<string> arguments =
        [
            "cdc",
            "retire",
            "--data-store-id",
            "1",
            "--deployment-key",
            DeploymentKey,
            "--instance-key",
            "instance",
            "--generation",
            "1",
            "--tenant-key",
            scenario == "literalDefault" ? "default" : "",
            "--source-connection-variable",
            variableName,
            "--connector-already-absent",
            "--kafka-bootstrap-servers",
            "127.0.0.1:1",
            "--connect-base-url",
            "http://127.0.0.1:1",
            "--max-record-bytes",
            "1048576",
            "--durability-profile",
            "local",
            "--cdc-binding-state-path",
            _harness.CdcBindingStatePath,
            "--json",
        ];
        if (scenario != "missingConfirmation")
        {
            arguments.AddRange(["--confirm", "cdcBindingRetirement"]);
        }
        DocumentCacheAdminCliProcessResult result;
        try
        {
            if (scenario != "unsetSource")
            {
                Environment.SetEnvironmentVariable(
                    variableName,
                    scenario == "currentSource" ? _current.ConnectionString : _original.ConnectionString
                );
            }
            result = await _harness.RunAsync(arguments.ToArray());
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, null);
        }
        result.ExitCode.Should().Be(expectedExitCode, "{0}", result.StandardError);
        result.StandardOutput.Should().BeEmpty("no attempt completed cleanup or produced a proof");
        string expectedDiagnostic = scenario switch
        {
            "missingConfirmation" => "cdcBindingRetirement",
            "literalDefault" => "--tenant-key",
            "unsetSource" => "cdcSourceConnectionVariableUnresolved",
            "currentSource" => CdcRetirementDiagnosticCodes.RefusedNoMutation,
            _ => CdcRetirementDiagnosticCodes.IncompleteRetryable,
        };
        result.StandardError.Should().Contain(expectedDiagnostic);
        if (scenario == "currentSource")
        {
            result.StandardError.Should().Contain("physical source");
        }
        if (scenario == "originalSource")
        {
            result
                .StandardError.Should()
                .Contain("could not stop the connector")
                .And.NotContain(CdcRetirementDiagnosticCodes.RefusedNoMutation);
        }
        result
            .StandardError.Should()
            .NotContain(_original.ConnectionString)
            .And.NotContain(_current.ConnectionString)
            .And.NotContain(_harness.SecretFromEnvironment);
        ReadRecords().Should().BeEquivalentTo(recordsBefore);
        (await _current.State.ReadLifecycleAsync()).Should().Be(currentBefore);
        (await _original.State.ReadLifecycleAsync()).Should().Be(originalBefore);
        (await _current.State.ReadPhysicalSourceFingerprintAsync()).Should().Be(_currentFingerprint);
        (await _original.State.ReadPhysicalSourceFingerprintAsync()).Should().Be(_originalFingerprint);
        string artifact = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            $"retirement-source-{provider}-{scenario}.json"
        );
        await File.WriteAllTextAsync(
            artifact,
            JsonSerializer.Serialize(
                new
                {
                    provider,
                    scenario,
                    boundary = "packaged CLI; two real databases; synthetic retained binding; unreachable Connect and broker",
                    arguments = arguments.Select(value =>
                        value switch
                        {
                            var path when path == _harness.CdcBindingStatePath => "<disposable-state-root>",
                            var variable when variable == variableName => "DMS_RETAINED_SOURCE",
                            _ => value,
                        }
                    ),
                    result.ExitCode,
                    result.StandardOutput,
                    result.StandardError,
                    originalFingerprint = _originalFingerprint,
                    currentFingerprint = _currentFingerprint,
                    currentBefore,
                    originalBefore,
                    recordsBefore,
                    recordsAfter = ReadRecords(),
                }
            )
        );
        TestContext.AddTestAttachment(artifact);
    }

    [OneTimeTearDown]
    public async Task TearDown()
    {
        if (_harness is not null)
        {
            await _harness.DisposeAsync();
        }
        if (_original is not null)
        {
            await _original.DisposeAsync();
        }
        if (_current is not null)
        {
            await _current.DisposeAsync();
        }
    }

    private Task<DocumentCacheAdminCliTarget> CreateTargetAsync() =>
        provider == "postgresql"
            ? DocumentCacheAdminCliTarget.CreatePostgresqlAsync()
            : Given_DocumentCacheAdminMssqlRebuildOnline.CreateReadyMssqlTargetAsync();

    private Dictionary<string, string> ReadRecords() =>
        Directory
            .GetFiles(_harness.CdcBindingStatePath, "*.json", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(_harness.CdcBindingStatePath, path), File.ReadAllText);
}
