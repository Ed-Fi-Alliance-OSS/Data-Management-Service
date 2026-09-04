// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.Configuration;

/// <summary>
/// A kind of derivative database the Configuration Service can associate with a parent data store.
/// </summary>
public enum DataStoreDerivativeType
{
    /// <summary>
    /// A replica of the parent database that serves eligible read-only requests.
    /// </summary>
    ReadReplica,

    /// <summary>
    /// A point-in-time copy of the parent database that serves explicitly requested reads.
    /// </summary>
    /// <remarks>
    /// The configured database must be frozen for the duration of a client's paging session.
    /// Change-version-filtered reads served from a snapshot are paged in change-version order, and
    /// that ordering is only safe over data that cannot change while it is being walked: on a source
    /// that continues to apply changes, an update moves a row later within a still-open window, so
    /// the walk returns it twice and its departure shifts another row past a page boundary. Anything
    /// short of frozen belongs under <see cref="ReadReplica" />, which keeps the live paging rule.
    /// Nothing here can verify the guarantee - selection reads the configured type and takes it at
    /// its word - so it is stated as the obligation it is.
    /// </remarks>
    Snapshot,
}

/// <summary>
/// Recognition of the derivative type names the Configuration Service stores.
/// </summary>
internal static class DataStoreDerivativeTypeNames
{
    private const string ReadReplicaName = "ReadReplica";
    private const string SnapshotName = "Snapshot";

    /// <summary>
    /// Recognizes a derivative type name ordinally and exactly. The Configuration Service validates and
    /// stores these two spellings only, so a case variant or a value carrying surrounding whitespace is
    /// an unrecognized type rather than a match.
    /// </summary>
    public static bool TryParseExact(string? value, out DataStoreDerivativeType type)
    {
        // A C# switch over string compares ordinally, which is the exact matching this method promises.
        switch (value)
        {
            case ReadReplicaName:
                type = DataStoreDerivativeType.ReadReplica;
                return true;
            case SnapshotName:
                type = DataStoreDerivativeType.Snapshot;
                return true;
            default:
                type = default;
                return false;
        }
    }
}
