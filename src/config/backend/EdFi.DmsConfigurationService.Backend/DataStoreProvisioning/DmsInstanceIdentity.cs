// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;

/// <summary>
/// Engine-level evidence that a name still resolves to the physical database the operation knows
/// (D-13). It is detection and preservation, not atomicity: every selected-target mutation re-verifies
/// it immediately before executing and is skipped when it differs. The evidence is name-independent
/// because the staging database keeps its identity through the rename into the target name.
/// </summary>
public abstract record DmsInstanceIdentity
{
    /// <summary>
    /// Whether <paramref name="other"/> names the same physical database by the stable evidence alone:
    /// PostgreSQL <c>oid</c> and claim; SQL Server <c>database_id</c> and primary-file tag, ignoring the
    /// <c>create_date</c> snapshot that changes on rename.
    /// </summary>
    public bool IsSameStableInstance(DmsInstanceIdentity other) =>
        (this, other) switch
        {
            (Postgresql mine, Postgresql theirs) => mine.Oid == theirs.Oid
                && string.Equals(mine.Claim, theirs.Claim, StringComparison.Ordinal),
            (Mssql mine, Mssql theirs) => mine.DatabaseId == theirs.DatabaseId
                && string.Equals(mine.PrimaryFileTag, theirs.PrimaryFileTag, StringComparison.Ordinal),
            _ => false,
        };

    /// <summary><c>pg_database.oid</c> (stable across rename) plus the claim comment written at creation.</summary>
    public sealed record Postgresql(uint Oid, string Claim) : DmsInstanceIdentity;

    /// <summary>
    /// <c>database_id</c> plus the operation tag carried by the primary file's <c>physical_name</c>, both
    /// unaffected by rename (F-P17); <c>database_id</c> reuse after a drop is detected by the tag.
    /// <see cref="CreateDateSnapshot"/> changes on rename and is only a guard for commands issued after the
    /// snapshot was taken; it is excluded from <see cref="IsSameStableInstance"/>.
    /// </summary>
    public sealed record Mssql(int DatabaseId, string PrimaryFileTag, DateTimeOffset CreateDateSnapshot)
        : DmsInstanceIdentity
    {
        /// <summary>The identity after a rename, with <c>create_date</c> re-captured (6.4 step 10).</summary>
        public Mssql WithCreateDateSnapshot(DateTimeOffset createDateSnapshot) =>
            this with
            {
                CreateDateSnapshot = createDateSnapshot,
            };
    }

    /// <summary>
    /// A SQL Server staging name reserved for the database the restore will create (6.4): nothing exists on the
    /// server yet, so the only evidence that a database later found under the staging name is this operation's is
    /// the file name its primary file carries, <c>&lt;stage&gt;__&lt;op&gt;.mdf</c> (D-11, D-13). The orchestrator
    /// replaces it with the restored instance; until then cleanup drops a database under the staging name only
    /// when its primary file carries <see cref="PrimaryFileName"/>. It is never the same stable instance as
    /// anything.
    /// </summary>
    public sealed record MssqlReservation(string PrimaryFileName) : DmsInstanceIdentity
    {
        /// <summary>
        /// Whether <paramref name="instance"/> is a SQL Server database whose primary file's last path segment,
        /// under either path convention, is exactly <see cref="PrimaryFileName"/>.
        /// </summary>
        public bool IsRealizedBy(DmsInstanceIdentity instance) =>
            instance is Mssql restored && IsPrimaryFile(restored.PrimaryFileTag);

        /// <summary>Whether <paramref name="physicalName"/> names a file called <see cref="PrimaryFileName"/>.</summary>
        public bool IsPrimaryFile(string physicalName)
        {
            ArgumentNullException.ThrowIfNull(physicalName);
            int separator = physicalName.LastIndexOfAny(['/', '\\']);
            return separator >= 0
                && string.Equals(physicalName[(separator + 1)..], PrimaryFileName, StringComparison.Ordinal);
        }
    }
}
