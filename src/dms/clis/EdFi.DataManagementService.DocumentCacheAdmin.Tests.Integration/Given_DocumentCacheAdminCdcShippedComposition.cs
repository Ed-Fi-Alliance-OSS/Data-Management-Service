// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using EdFi.DataManagementService.DocumentCacheAdmin;
using FluentAssertions;
using FluentAssertions.Execution;

namespace EdFi.DataManagementService.DocumentCacheAdmin.Tests.Integration;

/// <summary>
/// The cdc verb group driven through the shipped composition: the real CLI process, the real service
/// graph it builds for itself, and a Configuration Service answering for the deployment's data stores.
/// </summary>
/// <remarks>
/// <para>
/// Every other cdc test substitutes something at this seam. The dispatcher's unit tests fake
/// <c>IConnectionStringProvider</c>, and the JSON-contract tests substitute the dispatcher itself, so
/// between them nothing exercises the composition the tool actually ships — which is exactly where a
/// dependency can go unresolved. The gap was not hypothetical: the cdc branch of the executor
/// dispatches straight to the dispatcher without the target-registry refresh the DocumentCache status
/// and mutating branches run first, and the connection string provider reads a cache with no lazy load
/// behind it, so every cdc verb refused on an unloaded tenant and named the wrong cause while both
/// substituted suites stayed green.
/// </para>
/// <para>
/// <c>cdc status</c> is the verb that proves it with no broker, worker, or provider artifact. Against a
/// binding state store that holds nothing, the controller validates the target, reads the store, finds
/// no binding, and composes a status from what it collected — it returns before it opens the instance
/// database connection, before any Kafka describe, and before any Connect request. Everything up to
/// that point is the part of the composition under test: options resolution, provider-setup input
/// derivation from the effective schema, and the instance-database resolution this fixture exists for.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
[Category("PostgresqlIntegration")]
[Category("CdcShippedComposition")]
public sealed class Given_DocumentCacheAdminCdcShippedComposition
{
    private const string DeploymentKey = "local";
    private const string InstanceKey = "ds1";
    private const long Generation = 1;

    /// <summary>
    /// The refusals a cdc verb reports when it cannot reach the instance database of its target. They
    /// are the symptoms this fixture exists to keep from coming back.
    /// </summary>
    private const string InstanceDatabaseUnresolvedCode = "cdcInstanceDatabaseUnresolved";

    private const string DataStoresUnavailableCode = "cdcDataStoresUnavailable";

    /// <summary>
    /// The prose the unresolved-instance-database refusal carries, asserted alongside its code.
    /// </summary>
    /// <remarks>
    /// Both, because either alone is a weak signal. A code can be replaced wholesale — CdcDiagnostic
    /// sanitizes its own code field, and a code that trips a sensitive fragment becomes "redacted" in
    /// stderr and in the shared contract alike, so a run emitting exactly this refusal can carry no
    /// trace of the code at all. The message survives that, and the code is what an operator matches
    /// on, so the pair is asserted rather than either half.
    /// </remarks>
    private const string InstanceDatabaseUnresolvedMessage = "could not resolve the instance database";

    [Test]
    public async Task It_resolves_the_instance_database_for_a_cdc_verb()
    {
        await using DocumentCacheAdminCliTarget target =
            await DocumentCacheAdminCliTarget.CreatePostgresqlAsync();
        await using DocumentCacheAdminCliProcessHarness harness =
            await DocumentCacheAdminCliProcessHarness.CreateAsync(target);
        using TemporaryBindingStateRoot stateRoot = new();

        DocumentCacheAdminCliProcessResult result = await harness.RunAsync(
            CdcStatusArguments(target, stateRoot)
        );

        using AssertionScope scope = new();

        string combinedOutput = string.Join(Environment.NewLine, result.StandardOutput, result.StandardError);

        combinedOutput
            .Should()
            .NotContain(
                InstanceDatabaseUnresolvedMessage,
                "the shipped composition must resolve the instance database of the invocation target"
            );
        combinedOutput.Should().NotContain(InstanceDatabaseUnresolvedCode);
        combinedOutput.Should().NotContain(DataStoresUnavailableCode);
    }

    /// <summary>
    /// The unresolved-instance-database refusal keeps a code an operator can actually match on.
    /// </summary>
    /// <remarks>
    /// <c>CdcDiagnostic</c> sanitizes its own <c>code</c>, replacing anything that trips a sensitive
    /// fragment with <c>redacted</c> — in the shared JSON contract as well as in stderr. The fragment
    /// list includes <c>connectionstring</c>, so the obvious name for this refusal silently erased
    /// itself and left the operator matching on nothing. The code is asserted to survive its own
    /// sanitizer rather than only to exist at the call site.
    /// </remarks>
    [Test]
    public void It_keeps_a_matchable_code_on_the_unresolved_instance_database_refusal()
    {
        CdcDiagnostic diagnostic = new(
            InstanceDatabaseUnresolvedCode,
            CdcDiagnosticCategory.SourceMismatch,
            CdcDiagnosticSeverity.Error,
            CdcDiagnosticComponent.ProviderSetup,
            DateTimeOffset.UnixEpoch,
            "CDC operation could not resolve the instance database for the invocation target.",
            retryable: false,
            observed: "absent"
        );

        diagnostic.Code.Should().Be(InstanceDatabaseUnresolvedCode);
    }

    /// <summary>
    /// The verb runs far enough to produce its shared contract, which is what says the composition
    /// resolved rather than merely avoiding one diagnostic.
    /// </summary>
    /// <remarks>
    /// A status over an empty binding state store is a legitimate status: the target is a CDC target,
    /// nothing is bound to it, and the readiness that follows from no binding is the answer. So the
    /// exit code is a status verdict rather than a refusal, and exactly one CDC contract document
    /// reaches stdout — the surface every cdc verb owes its caller.
    /// </remarks>
    [Test]
    public async Task It_reports_a_cdc_status_contract_for_a_target_with_no_binding()
    {
        await using DocumentCacheAdminCliTarget target =
            await DocumentCacheAdminCliTarget.CreatePostgresqlAsync();
        await using DocumentCacheAdminCliProcessHarness harness =
            await DocumentCacheAdminCliProcessHarness.CreateAsync(target);
        using TemporaryBindingStateRoot stateRoot = new();

        DocumentCacheAdminCliProcessResult result = await harness.RunAsync(
            CdcStatusArguments(target, stateRoot)
        );

        using AssertionScope scope = new();

        result.ExitCode.Should().Be(DocumentCacheAdminExitCodes.Success);
        result
            .StandardError.Should()
            .NotContain("\"contractVersion\"")
            .And.NotContain(target.ConnectionString)
            .And.NotContain(harness.SecretFromEnvironment);
        result.StandardOutput.TrimEnd().Should().NotContain("\n");
        result
            .StandardOutput.Should()
            .NotContain(target.ConnectionString)
            .And.NotContain(harness.SecretFromEnvironment);

        JsonNode contract = ReadSingleContract(result);
        contract["contractVersion"]!.GetValue<int>().Should().Be(CdcJsonContract.CurrentContractVersion);
        contract["readiness"]!.GetValue<string>().Should().Be("notReady");
        JsonArray targets = contract["targets"]!.AsArray();
        targets.Should().ContainSingle();
        JsonNode observed = targets[0]!;
        observed["targetIdentity"]!["tenantKey"]!.GetValue<string>().Should().Be("default");
        observed["targetIdentity"]!["dataStoreId"]!.GetValue<string>().Should().Be("1");
        observed["targetIdentity"]!["provider"]!.GetValue<string>().Should().Be("postgresql");
        observed["targetIdentity"]!["generation"]!.GetValue<long>().Should().Be(Generation);
        observed["binding"]!["state"]!.GetValue<string>().Should().Be("notSatisfied");
        observed["binding"]!["category"]!.GetValue<string>().Should().Be("bindingMissing");
        await TestContext.Out.WriteLineAsync(
            JsonSerializer.Serialize(
                new
                {
                    result.ExitCode,
                    result.StandardOutput,
                    result.StandardError,
                }
            )
        );
    }

    [TestCase("")]
    [TestCase("default")]
    public async Task It_requires_the_cli_default_tenant_translation_for_a_cdc_target(string tenantKey)
    {
        await using DocumentCacheAdminCliTarget target =
            await DocumentCacheAdminCliTarget.CreatePostgresqlAsync();
        await using DocumentCacheAdminCliProcessHarness harness =
            await DocumentCacheAdminCliProcessHarness.CreateAsync(target);
        using TemporaryBindingStateRoot stateRoot = new();
        List<string> arguments = [.. CdcStatusArguments(target, stateRoot)];
        arguments.AddRange(["--tenant-key", tenantKey]);

        DocumentCacheAdminCliProcessResult result = await harness.RunAsync(arguments.ToArray());

        await TestContext.Out.WriteLineAsync(
            JsonSerializer.Serialize(
                new
                {
                    tenantKey,
                    result.ExitCode,
                    result.StandardOutput,
                    result.StandardError,
                }
            )
        );
        if (tenantKey.Length == 0)
        {
            result.ExitCode.Should().Be(0);
            CdcJsonContract.Deserialize<CdcStatus>(result.StandardOutput).Succeeded.Should().BeTrue();
        }
        else
        {
            result
                .ExitCode.Should()
                .Be(
                    DocumentCacheAdminExitCodes.ArgumentError,
                    "the record spelling default is reserved and is not the CLI default tenant key"
                );
            result
                .StandardOutput.Should()
                .BeEmpty("an invalid tenant argument must not produce CDC target status");
            result.StandardError.Should().Contain("omit --tenant-key or pass an empty string");
        }
    }

    private static string[] CdcStatusArguments(
        DocumentCacheAdminCliTarget target,
        TemporaryBindingStateRoot stateRoot
    ) =>
        [
            DocumentCacheAdminCommandSurface.CdcCommandName,
            DocumentCacheAdminCommandSurface.CdcStatusVerbName,
            DocumentCacheAdminCommandSurface.DataStoreIdOptionName,
            target.DataStoreId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            DocumentCacheAdminCommandSurface.DeploymentKeyOptionName,
            DeploymentKey,
            DocumentCacheAdminCommandSurface.InstanceKeyOptionName,
            InstanceKey,
            DocumentCacheAdminCommandSurface.GenerationOptionName,
            Generation.ToString(System.Globalization.CultureInfo.InvariantCulture),
            // Endpoints nothing is listening on. The status stops at the empty binding state store
            // before it would reach either, and naming reachable ones would make this fixture depend
            // on a broker and a worker it has no assertion about.
            DocumentCacheAdminCommandSurface.KafkaBootstrapServersOptionName,
            "localhost:9092",
            DocumentCacheAdminCommandSurface.ConnectBaseUrlOptionName,
            "http://localhost:8083",
            DocumentCacheAdminCommandSurface.MaxRecordBytesOptionName,
            "1048576",
            DocumentCacheAdminCommandSurface.DurabilityProfileOptionName,
            CdcControlDurabilityProfileLocal,
            DocumentCacheAdminCommandSurface.CdcBindingStatePathOptionName,
            stateRoot.Path,
            DocumentCacheAdminCommandSurface.JsonOptionName,
        ];

    private const string CdcControlDurabilityProfileLocal = "local";

    /// <summary>
    /// The one CDC contract document a cdc verb puts on stdout in <c>--json</c> mode.
    /// </summary>
    private static JsonNode ReadSingleContract(DocumentCacheAdminCliProcessResult result)
    {
        string[] documents = result
            .StandardOutput.Split(
                '\n',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
            )
            .Where(line => line.StartsWith('{'))
            .ToArray();

        documents
            .Should()
            .ContainSingle(
                $"exactly one CDC contract document belongs on stdout. stdout: {result.StandardOutput}; stderr: {result.StandardError}"
            );

        try
        {
            return JsonNode.Parse(documents[0])
                ?? throw new InvalidOperationException("CDC contract document parsed as null.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                $"CDC contract document was not valid JSON: {exception.Message}",
                exception
            );
        }
    }

    /// <summary>
    /// A binding state store root of this run's own, so the fixture never reads or writes a developer's
    /// real store and every run starts from "nothing has ever been bound".
    /// </summary>
    private sealed class TemporaryBindingStateRoot : IDisposable
    {
        public TemporaryBindingStateRoot()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"dms-cdc-composition-state-{Guid.NewGuid():N}"
            );
            if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(Path);
            }
            else
            {
                Directory.CreateDirectory(
                    Path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                );
            }
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch (IOException)
            {
                // Best-effort cleanup; the root is under the OS temp directory.
            }
            catch (UnauthorizedAccessException)
            {
                // Best-effort cleanup; the root is under the OS temp directory.
            }
        }
    }
}
