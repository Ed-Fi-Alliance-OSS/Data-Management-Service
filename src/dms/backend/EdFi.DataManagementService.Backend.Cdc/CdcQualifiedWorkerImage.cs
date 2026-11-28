// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Frozen;
using System.Text.Json;
using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Cdc;

/// <summary>
/// Shipped image qualification, independent of requested deployment configuration. This records
/// packaging/provider qualification only; every live worker still requires deployment inspection.
/// </summary>
public static class CdcQualifiedWorkerImage
{
    private static readonly (string Image, bool MssqlNativeDocumentCache) Qualification = ReadQualification();

    public static string Image { get; } = Qualification.Image;
    public static string Digest { get; } = Image[(Image.LastIndexOf('@') + 1)..];
    internal static IReadOnlySet<string> Images { get; } =
        new[] { Image }.ToFrozenSet(StringComparer.Ordinal);
    internal static IReadOnlySet<string> Digests { get; } =
        new[] { Digest }.ToFrozenSet(StringComparer.Ordinal);

    internal static bool CanServe(CdcDeploymentRequest request) =>
        CanServe(request, Qualification.MssqlNativeDocumentCache, Digest);

    internal static bool CanServe(
        CdcDeploymentRequest request,
        bool mssqlNativeDocumentCache,
        string qualifiedDigest
    ) =>
        request.ProviderConnectionProperties.Provider != CdcProvider.SqlServer
        || !request.ProviderSetup.ExpectedSourceInventory.Any(
            CdcConnectorTemplateSharedRules.IsNativeSqlServerDocumentCache
        )
        || (mssqlNativeDocumentCache && request.WorkerPolicy.QualifiedImageDigest == qualifiedDigest);

    internal static async Task<CdcDeploymentDiagnostic> DiagnoseNativeRefusalAsync(
        CdcDeploymentRequest request,
        TimeProvider clock,
        CancellationToken cancellationToken
    )
    {
        using var deadline = new CancellationTokenSource(request.Timing.CallTimeout, clock);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            deadline.Token
        );
        try
        {
            if (request.ProviderSetup.DatabaseExecutor is not { } executor)
            {
                return new(CdcDeploymentComponent.ProviderSetup, CdcDeploymentFailure.Unavailable);
            }

            var rows = await executor
                .QueryAsync(MssqlDocumentCachePhysicalSchema.ObservedVersionSql, timeout.Token)
                .WaitAsync(timeout.Token);
            if (
                rows.Count != 1
                || !rows[0].TryGetValue("physical_schema_version", out var version)
                || version != MssqlDocumentCachePhysicalSchema.Version
            )
            {
                return new(
                    CdcDeploymentComponent.ProviderSetup,
                    CdcDeploymentFailure.IncompatibleDocumentCachePhysicalSchema
                );
            }

            return new(CdcDeploymentComponent.Worker, CdcDeploymentFailure.NativeDocumentCacheWorkerRequired);
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            return new(CdcDeploymentComponent.ProviderSetup, CdcDeploymentFailure.Timeout);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return CdcDeploymentDiagnostic.FromException(CdcDeploymentComponent.ProviderSetup, exception);
        }
    }

    private static (string Image, bool MssqlNativeDocumentCache) ReadQualification()
    {
        using var stream =
            typeof(CdcQualifiedWorkerImage).Assembly.GetManifestResourceStream(
                "EdFi.DataManagementService.Backend.Cdc.CdcQualifiedWorkerImage.json"
            ) ?? throw new InvalidDataException("The shipped CDC image qualification record is unavailable.");
        using var document = JsonDocument.Parse(stream);
        string image =
            document.RootElement.GetProperty("image").GetString()
            ?? throw new InvalidDataException("The shipped CDC image qualification record is invalid.");
        bool nativeDocumentCache =
            document.RootElement.TryGetProperty("mssqlNativeDocumentCache", out var capability)
            && capability.ValueKind == JsonValueKind.True;
        return (image, nativeDocumentCache);
    }
}
