// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;
using System.Text.Json;
using CoreCdc = EdFi.DataManagementService.Core.DocumentCache.Cdc;

namespace EdFi.DataManagementService.Backend.Cdc;

/// <summary>
/// Bounded Connect REST operations. The controller owns workflow locking, live template validation,
/// lifecycle eligibility and journal intent. No automatic mutation retry or config upsert on setup.
/// The supplied client is owned by the host and must disable redirects and automatic write retries.
/// Authentication may be configured on that client; this adapter never logs messages or bodies.
/// </summary>
public sealed class CdcConnectRestAdapter(HttpClient client, TimeProvider timeProvider) : ICdcConnectTransport
{
    /// <summary>Production client defaults; hosts may add credentials without enabling redirects or write retries.</summary>
    public static HttpClient CreateHttpClient() =>
        new(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };

    internal const int MaximumResponseBytes = 4 * 1024 * 1024;
    private static readonly JsonDocumentOptions _jsonOptions = new() { MaxDepth = 32 };

    public CdcConnectRestAdapter(HttpClient client)
        : this(client, TimeProvider.System) { }

    public Task<CdcTransportResult<IReadOnlyDictionary<string, string>>> ReadConfigurationAsync(
        CdcDeploymentRequest request,
        CancellationToken cancellationToken
    ) => ReadAsync(request, Path(request, "/config"), ParseConfiguration, true, cancellationToken);

    public Task<CdcTransportResult<IReadOnlyDictionary<string, string>>> ValidateConfigurationAsync(
        CdcDeploymentRequest request,
        CdcKafkaConnectRegistrationPayload payload,
        CancellationToken cancellationToken
    ) =>
        PassAsync(
            request,
            async token =>
            {
                if (
                    !ValidPayload(request, payload)
                    || !payload.Config.TryGetValue("connector.class", out var connectorClass)
                )
                {
                    return Failure<IReadOnlyDictionary<string, string>>(CdcDeploymentFailure.InvalidInput);
                }
                var response = await SendAsync(
                    request,
                    HttpMethod.Put,
                    "connector-plugins/" + Uri.EscapeDataString(connectorClass) + "/config/validate",
                    JsonSerializer.Serialize(payload.Config),
                    token
                );
                if (response is not CdcTransportResult<JsonElement>.Observed observed)
                {
                    return Transfer<JsonElement, IReadOnlyDictionary<string, string>>(response);
                }
                var root = observed.Value;
                if (
                    !root.TryGetProperty("error_count", out var count)
                    || count.ValueKind != JsonValueKind.Number
                    || !count.TryGetInt32(out int errors)
                    || errors != 0
                    || !root.TryGetProperty("configs", out var configs)
                    || configs.ValueKind != JsonValueKind.Array
                )
                {
                    return Failure<IReadOnlyDictionary<string, string>>(
                        CdcDeploymentFailure.ValidationFailed
                    );
                }
                HashSet<string> names = new(StringComparer.Ordinal);
                foreach (var config in configs.EnumerateArray())
                {
                    if (
                        config.ValueKind != JsonValueKind.Object
                        || !config.TryGetProperty("value", out var value)
                        || !CdcConnectOffsetEvidence.TryString(value, "name", out string name)
                        || !names.Add(name)
                        || !value.TryGetProperty("errors", out var itemErrors)
                        || itemErrors.ValueKind != JsonValueKind.Array
                        || itemErrors.GetArrayLength() != 0
                    )
                    {
                        return Failure<IReadOnlyDictionary<string, string>>(
                            CdcDeploymentFailure.ValidationFailed
                        );
                    }
                }
                // Values can be masked/converted in the validation response; never present them as live config.
                return names.Contains("connector.class")
                    ? new CdcTransportResult<IReadOnlyDictionary<string, string>>.Observed(payload.Config)
                    : Failure<IReadOnlyDictionary<string, string>>(CdcDeploymentFailure.ValidationFailed);
            },
            cancellationToken
        );

    public Task<CdcTransportResult<CdcTransportAcknowledgement>> CreateAsync(
        CdcDeploymentRequest request,
        CdcKafkaConnectRegistrationPayload payload,
        CancellationToken cancellationToken
    ) =>
        PassAsync(
            request,
            async token =>
            {
                if (!ValidPayload(request, payload))
                {
                    return Failure<CdcTransportAcknowledgement>(CdcDeploymentFailure.InvalidInput);
                }
                var before = await ReadConfigurationAsync(request, token);
                if (before is CdcTransportResult<IReadOnlyDictionary<string, string>>.Observed)
                {
                    return Failure<CdcTransportAcknowledgement>(CdcDeploymentFailure.Conflict);
                }
                if (before is not CdcTransportResult<IReadOnlyDictionary<string, string>>.Absent)
                {
                    return Transfer<IReadOnlyDictionary<string, string>, CdcTransportAcknowledgement>(before);
                }
                var effect = await SendAsync(
                    request,
                    HttpMethod.Post,
                    "connectors",
                    JsonSerializer.Serialize(payload),
                    token
                );
                if (IsDefinitiveFailure(effect))
                {
                    return Transfer<JsonElement, CdcTransportAcknowledgement>(effect);
                }
                // Independent call timeout: a timed-out POST may have committed. Never POST a second time.
                return await ReconcileConfigurationAsync(request, payload, token);
            },
            cancellationToken
        );

    public Task<
        CdcTransportResult<CdcTransportAcknowledgement>
    > UpdateConfigurationForRecordSizeIncreaseAsync(
        CdcDeploymentRequest request,
        CdcKafkaConnectRegistrationPayload payload,
        CancellationToken cancellationToken
    ) =>
        PassAsync(
            request,
            async token =>
            {
                if (!ValidPayload(request, payload))
                {
                    return Failure<CdcTransportAcknowledgement>(CdcDeploymentFailure.InvalidInput);
                }
                var before = await ReadConfigurationAsync(request, token);
                if (before is not CdcTransportResult<IReadOnlyDictionary<string, string>>.Observed)
                {
                    return Transfer<IReadOnlyDictionary<string, string>, CdcTransportAcknowledgement>(before);
                }
                var stopped = await ReadStatusAsync(request, token);
                if (stopped is not CdcTransportResult<CdcConnectStatus>.Observed status)
                {
                    return Transfer<CdcConnectStatus, CdcTransportAcknowledgement>(stopped);
                }
                if (!status.Value.IsStopped)
                {
                    return Failure<CdcTransportAcknowledgement>(CdcDeploymentFailure.ValidationFailed);
                }
                if (
                    !IsSizeOnlyIncrease(
                        ((CdcTransportResult<IReadOnlyDictionary<string, string>>.Observed)before).Value,
                        payload.Config
                    )
                )
                {
                    return Failure<CdcTransportAcknowledgement>(CdcDeploymentFailure.ValidationFailed);
                }
                // This explicit controller-authorized operation is the only PUT config surface. Connect's
                // API is inherently an upsert, so the state-root controller lock must exclude managed deletion.
                var effect = await SendAsync(
                    request,
                    HttpMethod.Put,
                    Path(request, "/config"),
                    JsonSerializer.Serialize(payload.Config),
                    token
                );
                if (IsDefinitiveFailure(effect))
                {
                    return Transfer<JsonElement, CdcTransportAcknowledgement>(effect);
                }
                return await ReconcileConfigurationAsync(request, payload, token);
            },
            cancellationToken
        );

    public Task<CdcTransportResult<CdcConnectStatus>> ReadStatusAsync(
        CdcDeploymentRequest request,
        CancellationToken cancellationToken
    ) =>
        ReadAsync(
            request,
            Path(request, "/status"),
            root => ParseStatus(request, root),
            true,
            cancellationToken
        );

    public async Task<CdcTransportResult<CoreCdc.CdcConnectorRuntimeObservation>> ReadRuntimeAsync(
        CdcDeploymentRequest request,
        CancellationToken cancellationToken
    )
    {
        var result = await ReadStatusAsync(request, cancellationToken);
        return result is CdcTransportResult<CdcConnectStatus>.Observed status
            ? new CdcTransportResult<CoreCdc.CdcConnectorRuntimeObservation>.Observed(status.Value.Runtime)
            : Transfer<CdcConnectStatus, CoreCdc.CdcConnectorRuntimeObservation>(result);
    }

    public Task<CdcTransportResult<JsonElement>> ReadOffsetsAsync(
        CdcDeploymentRequest request,
        CancellationToken cancellationToken
    ) =>
        // An offsets-route 404 may mean the worker lacks this API. It is never offset absence.
        ReadAsync(
            request,
            Path(request, "/offsets"),
            root =>
                root.TryGetProperty("offsets", out var offsets) && offsets.ValueKind == JsonValueKind.Array
                    ? root
                    : throw new JsonException(),
            false,
            cancellationToken
        );

    public async Task<CdcTransportResult<CdcConnectOffsetEvidence>> ReadOffsetEvidenceAsync(
        CdcDeploymentRequest request,
        CancellationToken cancellationToken
    )
    {
        var response = await ReadOffsetsAsync(request, cancellationToken);
        return response is CdcTransportResult<JsonElement>.Observed observed
            ? new CdcTransportResult<CdcConnectOffsetEvidence>.Observed(
                CdcConnectOffsetEvidence.Parse(request, observed.Value)
            )
            : Transfer<JsonElement, CdcConnectOffsetEvidence>(response);
    }

    public Task<CdcTransportResult<CdcTransportAcknowledgement>> StopAsync(
        CdcDeploymentRequest request,
        CancellationToken cancellationToken
    ) => ChangeStateAsync(request, HttpMethod.Put, "/stop", stopped: true, cancellationToken);

    public Task<CdcTransportResult<CdcTransportAcknowledgement>> ResumeAsync(
        CdcDeploymentRequest request,
        CancellationToken cancellationToken
    ) => ChangeStateAsync(request, HttpMethod.Put, "/resume", stopped: false, cancellationToken);

    public Task<CdcTransportResult<CdcTransportAcknowledgement>> RestartAsync(
        CdcDeploymentRequest request,
        CancellationToken cancellationToken
    ) =>
        ChangeStateAsync(
            request,
            HttpMethod.Post,
            "/restart?includeTasks=true&onlyFailed=false",
            stopped: false,
            cancellationToken,
            requireAcknowledgement: true
        );

    private Task<CdcTransportResult<CdcTransportAcknowledgement>> ChangeStateAsync(
        CdcDeploymentRequest request,
        HttpMethod method,
        string suffix,
        bool stopped,
        CancellationToken cancellationToken,
        bool requireAcknowledgement = false
    ) =>
        PassAsync(
            request,
            async token =>
            {
                var effect = await SendAsync(request, method, Path(request, suffix), "", token);
                // RUNNING alone cannot prove that an uncertain restart was accepted. Let the
                // lifecycle controller reconcile any transition from its pre-request observation.
                if (
                    IsDefinitiveFailure(effect)
                    || (requireAcknowledgement && effect is not CdcTransportResult<JsonElement>.Observed)
                )
                {
                    return Transfer<JsonElement, CdcTransportAcknowledgement>(effect);
                }
                while (true)
                {
                    var current = await ReadStatusAsync(request, token);
                    if (current is not CdcTransportResult<CdcConnectStatus>.Observed status)
                    {
                        return Transfer<CdcConnectStatus, CdcTransportAcknowledgement>(current);
                    }
                    if (stopped ? status.Value.IsStopped : status.Value.IsRunning)
                    {
                        // Running is current state only, never proof of a new task/process incarnation.
                        return Acknowledged();
                    }
                    await Task.Delay(request.Timing.PollInterval, timeProvider, token);
                }
            },
            cancellationToken
        );

    public Task<CdcTransportResult<CdcTransportAcknowledgement>> DeleteOffsetsAsync(
        CdcDeploymentRequest request,
        CancellationToken cancellationToken
    ) =>
        PassAsync(
            request,
            async token =>
            {
                var before = await ReadStatusAsync(request, token);
                if (before is not CdcTransportResult<CdcConnectStatus>.Observed stopped)
                {
                    return Transfer<CdcConnectStatus, CdcTransportAcknowledgement>(before);
                }
                if (!stopped.Value.IsStopped)
                {
                    return Failure<CdcTransportAcknowledgement>(CdcDeploymentFailure.ValidationFailed);
                }
                var effect = await SendAsync(
                    request,
                    HttpMethod.Delete,
                    Path(request, "/offsets"),
                    "",
                    token
                );
                if (IsDefinitiveFailure(effect))
                {
                    return Transfer<JsonElement, CdcTransportAcknowledgement>(effect);
                }
                var offsets = await ReadOffsetEvidenceAsync(request, token);
                if (offsets is not CdcTransportResult<CdcConnectOffsetEvidence>.Observed observed)
                {
                    return Transfer<CdcConnectOffsetEvidence, CdcTransportAcknowledgement>(offsets);
                }
                var after = await ReadStatusAsync(request, token);
                if (after is not CdcTransportResult<CdcConnectStatus>.Observed last)
                {
                    return Transfer<CdcConnectStatus, CdcTransportAcknowledgement>(after);
                }
                return observed.Value.State == CdcConnectOffsetState.Missing && last.Value.IsStopped
                    ? Acknowledged()
                    : Failure<CdcTransportAcknowledgement>(CdcDeploymentFailure.ValidationFailed);
            },
            cancellationToken
        );

    public Task<CdcTransportResult<CdcTransportAcknowledgement>> DeleteAsync(
        CdcDeploymentRequest request,
        CancellationToken cancellationToken
    ) =>
        PassAsync(
            request,
            async token =>
            {
                var before = await ReadConfigurationAsync(request, token);
                if (before is CdcTransportResult<IReadOnlyDictionary<string, string>>.Absent)
                {
                    return new CdcTransportResult<CdcTransportAcknowledgement>.Absent();
                }
                if (before is not CdcTransportResult<IReadOnlyDictionary<string, string>>.Observed)
                {
                    return Transfer<IReadOnlyDictionary<string, string>, CdcTransportAcknowledgement>(before);
                }
                var status = await ReadStatusAsync(request, token);
                if (status is not CdcTransportResult<CdcConnectStatus>.Observed stopped)
                {
                    return Transfer<CdcConnectStatus, CdcTransportAcknowledgement>(status);
                }
                if (!stopped.Value.IsStopped)
                {
                    return Failure<CdcTransportAcknowledgement>(CdcDeploymentFailure.ValidationFailed);
                }
                var effect = await SendAsync(request, HttpMethod.Delete, Path(request, ""), "", token);
                if (IsDefinitiveFailure(effect))
                {
                    return Transfer<JsonElement, CdcTransportAcknowledgement>(effect);
                }
                while (true)
                {
                    var current = await ReadConfigurationAsync(request, token);
                    if (current is CdcTransportResult<IReadOnlyDictionary<string, string>>.Absent)
                    {
                        return new CdcTransportResult<CdcTransportAcknowledgement>.Absent();
                    }
                    if (current is not CdcTransportResult<IReadOnlyDictionary<string, string>>.Observed)
                    {
                        return Transfer<IReadOnlyDictionary<string, string>, CdcTransportAcknowledgement>(
                            current
                        );
                    }
                    await Task.Delay(request.Timing.PollInterval, timeProvider, token);
                }
            },
            cancellationToken
        );

    private async Task<CdcTransportResult<CdcTransportAcknowledgement>> ReconcileConfigurationAsync(
        CdcDeploymentRequest request,
        CdcKafkaConnectRegistrationPayload payload,
        CancellationToken token
    )
    {
        var after = await ReadConfigurationAsync(request, token);
        if (after is not CdcTransportResult<IReadOnlyDictionary<string, string>>.Observed observed)
        {
            // Absence following a write does not prove it cannot still commit later.
            return after is CdcTransportResult<IReadOnlyDictionary<string, string>>.Absent
                ? Failure<CdcTransportAcknowledgement>(CdcDeploymentFailure.Unavailable)
                : Transfer<IReadOnlyDictionary<string, string>, CdcTransportAcknowledgement>(after);
        }
        // Preserve masked secrets on reads; never replace them with the submitted secret/reference.
        // If exact read-back is unavailable the controller must live-validate and reconcile on retry.
        return
            payload.Config.All(pair =>
                observed.Value.TryGetValue(pair.Key, out var value) && value == pair.Value
            )
            && observed.Value.All(pair =>
                payload.Config.ContainsKey(pair.Key) || (pair.Key == "name" && pair.Value == payload.Name)
            )
            ? Acknowledged()
            : Failure<CdcTransportAcknowledgement>(CdcDeploymentFailure.ValidationFailed);
    }

    private static bool IsSizeOnlyIncrease(
        IReadOnlyDictionary<string, string> before,
        IReadOnlyDictionary<string, string> after
    )
    {
        if (!before.Keys.Order().SequenceEqual(after.Keys.Order()))
        {
            return false;
        }
        int changes = 0;
        foreach (var pair in after)
        {
            string old = before[pair.Key];
            if (old == pair.Value)
            {
                continue;
            }
            if (
                CdcConnectorTemplateInputValidator.IsSecretBearingRenderedProperty(pair.Key)
                && CdcConnectorTemplateEffectiveConfigValidator.IsAcceptedSecretReadBack(
                    pair.Value,
                    old,
                    CdcConnectorTemplateSourcePhase.LiveReadBack
                )
            )
            {
                continue;
            }
            if (
                pair.Key is not ("producer.override.max.request.size" or "producer.override.buffer.memory")
                || !long.TryParse(
                    old,
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out long previous
                )
                || !long.TryParse(
                    pair.Value,
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out long requested
                )
                || previous <= 0
                || requested <= previous
            )
            {
                return false;
            }
            changes++;
        }
        return changes <= 1;
    }

    private static bool ValidPayload(
        CdcDeploymentRequest request,
        CdcKafkaConnectRegistrationPayload payload
    ) =>
        payload.Name == request.Binding.ConnectorName
        && (!payload.Config.TryGetValue("name", out var name) || name == payload.Name)
        && payload.Config.All(pair =>
            CdcConnectorTemplateInputValidator.HasExternalizedSecretReferenceIfRequired(pair.Key, pair.Value)
        );

    private static IReadOnlyDictionary<string, string> ParseConfiguration(JsonElement root)
    {
        Dictionary<string, string> result = new(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (
                property.Value.ValueKind != JsonValueKind.String
                || !result.TryAdd(property.Name, property.Value.GetString()!)
            )
            {
                throw new JsonException();
            }
        }
        if (result.Count == 0)
        {
            throw new JsonException();
        }
        return result;
    }

    private CdcConnectStatus ParseStatus(CdcDeploymentRequest request, JsonElement root)
    {
        if (
            !CdcConnectOffsetEvidence.TryString(root, "name", out var name)
            || name != request.Binding.ConnectorName
            || !root.TryGetProperty("connector", out var connector)
            || connector.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("tasks", out var tasks)
            || tasks.ValueKind != JsonValueKind.Array
        )
        {
            throw new JsonException();
        }
        var connectorState = State(connector);
        List<CdcConnectTaskStatus> taskStates = [];
        HashSet<int> ids = [];
        foreach (var task in tasks.EnumerateArray())
        {
            if (
                task.ValueKind != JsonValueKind.Object
                || !task.TryGetProperty("id", out var id)
                || id.ValueKind != JsonValueKind.Number
                || !id.TryGetInt32(out int taskId)
                || taskId < 0
                || !ids.Add(taskId)
            )
            {
                throw new JsonException();
            }
            taskStates.Add(new(taskId, State(task), WorkerId(task)));
        }
        if (taskStates.Count == 1 && taskStates[0].Id != 0)
        {
            throw new JsonException();
        }
        bool failed =
            connectorState == CoreCdc.CdcConnectorRuntimeState.Failed
            || taskStates.Exists(task => task.State == CoreCdc.CdcConnectorRuntimeState.Failed);
        return new(
            new(
                CoreCdc.CdcJsonContract.CurrentContractVersion,
                Guid.NewGuid().ToString("N"),
                timeProvider.GetUtcNow(),
                request.TargetIdentity,
                request.Binding.Provider,
                request.Binding.PhysicalSourceFingerprint,
                request.Binding.ConnectorName,
                connectorState,
                taskStates.Count,
                taskStates.Count(task => task.State == CoreCdc.CdcConnectorRuntimeState.Running),
                taskStates.Count == 1 ? taskStates[0].State : CoreCdc.CdcConnectorRuntimeState.Unknown,
                CoreCdc.CdcConnectorSnapshotState.Unknown,
                failed ? "connect-runtime-failed" : null,
                null,
                []
            ),
            WorkerId(connector),
            taskStates.AsReadOnly()
        );
    }

    private static string WorkerId(JsonElement root) =>
        CdcConnectOffsetEvidence.TryString(root, "worker_id", out string worker) ? worker : "";

    private static CoreCdc.CdcConnectorRuntimeState State(JsonElement root)
    {
        if (!CdcConnectOffsetEvidence.TryString(root, "state", out string state))
        {
            throw new JsonException();
        }
        return state switch
        {
            "RUNNING" => CoreCdc.CdcConnectorRuntimeState.Running,
            "PAUSED" => CoreCdc.CdcConnectorRuntimeState.Paused,
            "STOPPED" => CoreCdc.CdcConnectorRuntimeState.Stopped,
            "FAILED" => CoreCdc.CdcConnectorRuntimeState.Failed,
            "UNASSIGNED" => CoreCdc.CdcConnectorRuntimeState.Unassigned,
            _ => CoreCdc.CdcConnectorRuntimeState.Unknown,
        };
    }

    private async Task<CdcTransportResult<T>> ReadAsync<T>(
        CdcDeploymentRequest request,
        string path,
        Func<JsonElement, T> parse,
        bool absenceAllowed,
        CancellationToken token
    )
        where T : notnull
    {
        var result = await SendAsync(request, HttpMethod.Get, path, "", token);
        if (result is CdcTransportResult<JsonElement>.Absent && !absenceAllowed)
        {
            return Failure<T>(CdcDeploymentFailure.Unavailable);
        }
        if (result is not CdcTransportResult<JsonElement>.Observed observed)
        {
            return Transfer<JsonElement, T>(result);
        }
        try
        {
            return new CdcTransportResult<T>.Observed(parse(observed.Value));
        }
        catch (Exception exception)
            when (exception is JsonException or InvalidOperationException or FormatException)
        {
            return Failure<T>(CdcDeploymentFailure.ValidationFailed);
        }
    }

    private async Task<CdcTransportResult<JsonElement>> SendAsync(
        CdcDeploymentRequest request,
        HttpMethod method,
        string path,
        string body,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(request.Timing.CallTimeout);
        var token = timeout.Token;
        try
        {
            var endpoint = new Uri($"{request.ConnectEndpoint.AbsoluteUri.TrimEnd('/')}/{path}");
            using HttpRequestMessage message = new(method, endpoint);
            message.Headers.Accept.ParseAdd("application/json");
            if (body.Length > 0)
            {
                message.Content = new StringContent(body, Encoding.UTF8, "application/json");
            }
            using var response = await client
                .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token)
                .WaitAsync(token);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new CdcTransportResult<JsonElement>.Absent();
            }
            if (!response.IsSuccessStatusCode)
            {
                return Failure<JsonElement>(
                    response.StatusCode switch
                    {
                        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                            CdcDeploymentFailure.AuthenticationFailed,
                        HttpStatusCode.Conflict => CdcDeploymentFailure.Conflict,
                        HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout =>
                            CdcDeploymentFailure.Timeout,
                        HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity =>
                            CdcDeploymentFailure.ValidationFailed,
                        _ => CdcDeploymentFailure.Unavailable,
                    }
                );
            }
            // Mutation response bodies are neither authority nor diagnostics. Always reconcile separately.
            if (method != HttpMethod.Get && !path.EndsWith("/config/validate", StringComparison.Ordinal))
            {
                return new CdcTransportResult<JsonElement>.Observed(default);
            }
            if (response.Content.Headers.ContentLength > MaximumResponseBytes)
            {
                return Failure<JsonElement>(CdcDeploymentFailure.ValidationFailed);
            }
            await using var stream = await response.Content.ReadAsStreamAsync(token).WaitAsync(token);
            using MemoryStream buffer = new();
            byte[] chunk = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(chunk, token).AsTask().WaitAsync(token)) != 0)
            {
                if (buffer.Length + read > MaximumResponseBytes)
                {
                    return Failure<JsonElement>(CdcDeploymentFailure.ValidationFailed);
                }
                await buffer.WriteAsync(chunk.AsMemory(0, read), token);
            }
            using var json = JsonDocument.Parse(buffer.ToArray(), _jsonOptions);
            if (
                json.RootElement.ValueKind != JsonValueKind.Object
                || HasDuplicateProperties(json.RootElement)
            )
            {
                return Failure<JsonElement>(CdcDeploymentFailure.ValidationFailed);
            }
            token.ThrowIfCancellationRequested();
            return new CdcTransportResult<JsonElement>.Observed(json.RootElement.Clone());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure<JsonElement>(CdcDeploymentFailure.Timeout);
        }
        catch (JsonException)
        {
            return Failure<JsonElement>(CdcDeploymentFailure.ValidationFailed);
        }
        catch (Exception exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new CdcTransportResult<JsonElement>.Unavailable(
                CdcDeploymentDiagnostic.FromException(CdcDeploymentComponent.Connect, exception)
            );
        }
    }

    private static async Task<CdcTransportResult<T>> PassAsync<T>(
        CdcDeploymentRequest request,
        Func<CancellationToken, Task<CdcTransportResult<T>>> action,
        CancellationToken cancellationToken
    )
        where T : notnull
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(request.Timing.WaitTimeout);
        try
        {
            return await action(timeout.Token).WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure<T>(CdcDeploymentFailure.Timeout);
        }
        catch (Exception exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new CdcTransportResult<T>.Unavailable(
                CdcDeploymentDiagnostic.FromException(CdcDeploymentComponent.Connect, exception)
            );
        }
    }

    private static bool HasDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.Ordinal);
            return element
                .EnumerateObject()
                .Any(property => !names.Add(property.Name) || HasDuplicateProperties(property.Value));
        }
        return element.ValueKind == JsonValueKind.Array
            && element.EnumerateArray().Any(HasDuplicateProperties);
    }

    private static string Path(CdcDeploymentRequest request, string suffix) =>
        "connectors/" + Uri.EscapeDataString(request.Binding.ConnectorName) + suffix;

    private static CdcTransportResult<CdcTransportAcknowledgement> Acknowledged() =>
        new CdcTransportResult<CdcTransportAcknowledgement>.Observed(new());

    private static CdcTransportResult<T> Failure<T>(CdcDeploymentFailure failure)
        where T : notnull =>
        new CdcTransportResult<T>.Unavailable(new(CdcDeploymentComponent.Connect, failure));

    private static bool IsDefinitiveFailure(CdcTransportResult<JsonElement> result) =>
        result is CdcTransportResult<JsonElement>.Unavailable unavailable
        && unavailable.Diagnostic.Failure
            is CdcDeploymentFailure.AuthenticationFailed
                or CdcDeploymentFailure.ValidationFailed;

    private static CdcTransportResult<TOut> Transfer<TIn, TOut>(CdcTransportResult<TIn> result)
        where TIn : notnull
        where TOut : notnull =>
        result switch
        {
            CdcTransportResult<TIn>.Absent => new CdcTransportResult<TOut>.Absent(),
            CdcTransportResult<TIn>.Unavailable unavailable => new CdcTransportResult<TOut>.Unavailable(
                unavailable.Diagnostic
            ),
            _ => Failure<TOut>(CdcDeploymentFailure.Unavailable),
        };
}
