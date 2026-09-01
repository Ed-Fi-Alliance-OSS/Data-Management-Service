// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.External.Backend;

/// <summary>
/// Which physical database a request uses: the parent data store itself, or one of its derivatives.
/// </summary>
public enum EffectiveTargetKind
{
    /// <summary>The parent data store's own database.</summary>
    Primary,

    /// <summary>A replica of the parent database, serving eligible read-only requests.</summary>
    ReadReplica,

    /// <summary>A point-in-time copy of the parent database, serving explicitly requested reads.</summary>
    Snapshot,
}

/// <summary>
/// The single target every database operation in a request uses: its kind, plus the connection string
/// exactly as configured.
///
/// The connection string here is deliberately the configured value and never a provider-realized form.
/// Realizing one means asking a provider to parse it, and a value that is present and non-blank but
/// provider-invalid must stay selectable and fail at the connection-acquisition boundary rather than
/// during configuration load or target selection. Parsing therefore belongs only inside an acquisition
/// implementation, immediately before the connection is constructed.
/// </summary>
public sealed record EffectiveDataStoreTarget
{
    public EffectiveDataStoreTarget(EffectiveTargetKind Kind, string ConnectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ConnectionString);

        this.Kind = Kind;
        this.ConnectionString = ConnectionString;
    }

    public EffectiveTargetKind Kind { get; }

    public string ConnectionString { get; }

    /// <summary>
    /// The parent data store's own database. Named rather than constructed inline at call sites so a
    /// reader that is primary-only by design says so.
    /// </summary>
    public static EffectiveDataStoreTarget Primary(string connectionString) =>
        new(EffectiveTargetKind.Primary, connectionString);
}
