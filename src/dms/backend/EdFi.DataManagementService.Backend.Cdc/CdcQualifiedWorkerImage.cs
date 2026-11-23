// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Frozen;
using System.Text.Json;
using EdFi.DataManagementService.Backend.Ddl;

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
        request.ProviderConnectionProperties.Provider != CdcProvider.SqlServer
        || !request.ProviderSetup.ExpectedSourceInventory.Any(
            CdcConnectorTemplateSharedRules.IsNativeSqlServerDocumentCache
        )
        || (Qualification.MssqlNativeDocumentCache && request.WorkerPolicy.QualifiedImageDigest == Digest);

    private static (string Image, bool MssqlNativeDocumentCache) ReadQualification()
    {
        using var stream = typeof(CdcQualifiedWorkerImage).Assembly.GetManifestResourceStream(
            "EdFi.DataManagementService.Backend.Cdc.CdcQualifiedWorkerImage.json"
        ) ?? throw new InvalidDataException("The shipped CDC image qualification record is unavailable.");
        using var document = JsonDocument.Parse(stream);
        string image = document.RootElement.GetProperty("image").GetString()
            ?? throw new InvalidDataException("The shipped CDC image qualification record is invalid.");
        bool nativeDocumentCache =
            document.RootElement.TryGetProperty("mssqlNativeDocumentCache", out var capability)
            && capability.ValueKind == JsonValueKind.True;
        return (image, nativeDocumentCache);
    }
}
