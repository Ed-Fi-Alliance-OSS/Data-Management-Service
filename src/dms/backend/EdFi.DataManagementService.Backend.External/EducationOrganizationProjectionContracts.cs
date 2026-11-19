// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.External;

/// <summary>
/// The parameters of one complete read of the education-organization projection set.
/// </summary>
/// <param name="MappingSet">The mapping set resolved for the target database.</param>
/// <param name="MaxProjectionRows">
/// The largest set the reader returns. The statement reads at most one row more, and a set of that
/// size is <see cref="EducationOrganizationProjectionSetResult.TooLarge"/>.
/// </param>
/// <param name="ReadLockTimeoutSeconds">How long the read waits for a lock before failing.</param>
/// <param name="ReadCommandTimeoutSeconds">How long the statement may execute before failing.</param>
public sealed record EducationOrganizationProjectionSetReadRequest(
    MappingSet MappingSet,
    int MaxProjectionRows,
    int ReadLockTimeoutSeconds,
    int ReadCommandTimeoutSeconds
);

/// <summary>
/// One stored education organization of a core type, as read, before any validation or parent
/// selection.
/// </summary>
/// <remarks>
/// Each reference the row's type can carry has its own slot, and every slot keeps the stored value
/// whether or not parent precedence would select it. Validation needs the references precedence
/// discards: a dangling or wrong-type reference fails the whole set even when a higher-precedence
/// reference is valid. A slot the row's type cannot carry is <see langword="null"/>.
/// </remarks>
/// <param name="EducationOrganizationId">The education organization's identifier.</param>
/// <param name="Discriminator">
/// The stored discriminator literal, for example <c>Ed-Fi:School</c>. It is never exposed; the
/// handler maps it through a fixed allowlist.
/// </param>
/// <param name="NameOfInstitution">The full name.</param>
/// <param name="ShortNameOfInstitution">The short name, when stored.</param>
/// <param name="LocalEducationAgencyReference">A School's local education agency.</param>
/// <param name="ParentLocalEducationAgencyReference">A local education agency's parent agency.</param>
/// <param name="EducationServiceCenterReference">A local education agency's service center.</param>
/// <param name="StateEducationAgencyReference">
/// A local education agency's or an education service center's state agency.
/// </param>
public sealed record EducationOrganizationProjectionRow(
    long EducationOrganizationId,
    string Discriminator,
    string NameOfInstitution,
    string? ShortNameOfInstitution,
    long? LocalEducationAgencyReference,
    long? ParentLocalEducationAgencyReference,
    long? EducationServiceCenterReference,
    long? StateEducationAgencyReference
);

/// <summary>
/// Why the mapping set cannot be projected. Each is a permanent property of the deployed data model.
/// </summary>
public enum EducationOrganizationProjectionMappingIncompatibilityReason
{
    /// <summary>The model has no abstract EducationOrganization union view.</summary>
    UnionViewMissing,

    /// <summary>The model has more than one abstract EducationOrganization union view.</summary>
    UnionViewAmbiguous,

    /// <summary>The union view does not expose exactly one identity output column.</summary>
    IdentityOutputUnresolved,

    /// <summary>The union view does not expose exactly one discriminator output column.</summary>
    DiscriminatorOutputUnresolved,

    /// <summary>A core type has no arm in the union view.</summary>
    ArmMissing,

    /// <summary>A core type has more than one arm in the union view.</summary>
    ArmAmbiguous,

    /// <summary>A core type's arm does not name a concrete resource the model contains.</summary>
    ConcreteResourceMissing,

    /// <summary>A core type is not stored in relational tables.</summary>
    ResourceNotRelational,

    /// <summary>A core type's arm does not project its identity from a column of its root table.</summary>
    IdentityColumnUnresolved,

    /// <summary>A core type's arm does not project the expected discriminator literal.</summary>
    DiscriminatorUnexpected,

    /// <summary>A required root column is absent.</summary>
    ColumnMissing,

    /// <summary>More than one root column claims the same source path.</summary>
    ColumnAmbiguous,

    /// <summary>A root column's type or nullability does not match what the projection reads.</summary>
    ColumnTypeIncompatible,

    /// <summary>A core reference has no binding on the root table.</summary>
    ReferenceBindingMissing,

    /// <summary>A core reference has more than one binding on the root table.</summary>
    ReferenceBindingAmbiguous,

    /// <summary>
    /// A core reference binding targets another resource, or does not store exactly the target's
    /// identifier.
    /// </summary>
    ReferenceBindingIncompatible,
}

/// <summary>
/// A typed reason the mapping set cannot be projected.
/// </summary>
/// <param name="Reason">The reason.</param>
/// <param name="ResourceName">
/// The logical core resource name the reason concerns (for example <c>School</c>), when it concerns
/// one. Never a physical table or column name.
/// </param>
public sealed record EducationOrganizationProjectionMappingIncompatibility(
    EducationOrganizationProjectionMappingIncompatibilityReason Reason,
    string? ResourceName = null
);

/// <summary>
/// Why the target database's physical schema rejected the compiled read.
/// </summary>
public enum EducationOrganizationProjectionSchemaIncompatibilityReason
{
    /// <summary>The statement named a schema object the database does not have.</summary>
    SchemaObjectMissing,

    /// <summary>The database could not apply the statement to a column of an incompatible type.</summary>
    DataTypeIncompatible,

    /// <summary>A returned value could not be read as the type the row requires.</summary>
    MaterializationTypeMismatch,
}

/// <summary>
/// The stage of the read in which a transient failure occurred.
/// </summary>
public enum EducationOrganizationProjectionReadStage
{
    /// <summary>Opening the connection.</summary>
    Acquire,

    /// <summary>Beginning the transaction and applying its session settings.</summary>
    Prepare,

    /// <summary>Executing the statement and reading every row.</summary>
    Execute,

    /// <summary>Committing the transaction.</summary>
    Commit,
}

/// <summary>
/// The outcome of one complete read of the education-organization projection set. Only caller
/// cancellation and defects are thrown.
/// </summary>
public abstract record EducationOrganizationProjectionSetResult
{
    private EducationOrganizationProjectionSetResult() { }

    /// <summary>
    /// Every core row, in ascending identifier order, read inside one committed transaction.
    /// </summary>
    public sealed record Set(IReadOnlyList<EducationOrganizationProjectionRow> Rows)
        : EducationOrganizationProjectionSetResult;

    /// <summary>
    /// The set has more rows than <paramref name="MaxProjectionRows"/>. No row is returned.
    /// </summary>
    public sealed record TooLarge(int MaxProjectionRows) : EducationOrganizationProjectionSetResult;

    /// <summary>
    /// The mapping set cannot be projected. Permanent.
    /// </summary>
    public sealed record MappingIncompatible(
        EducationOrganizationProjectionMappingIncompatibility Incompatibility
    ) : EducationOrganizationProjectionSetResult;

    /// <summary>
    /// The database rejected the read because of its physical schema or stored types. Permanent.
    /// </summary>
    /// <param name="Reason">The reason.</param>
    /// <param name="ProviderCode">The provider's error code (SQLSTATE or error number), when one exists.</param>
    public sealed record SchemaIncompatible(
        EducationOrganizationProjectionSchemaIncompatibilityReason Reason,
        string? ProviderCode
    ) : EducationOrganizationProjectionSetResult;

    /// <summary>
    /// The read failed in a way that may succeed on retry. Transient.
    /// </summary>
    /// <param name="Stage">The stage that failed.</param>
    /// <param name="Describe">The exception type name and provider code; never a message.</param>
    public sealed record TargetUnavailable(EducationOrganizationProjectionReadStage Stage, string Describe)
        : EducationOrganizationProjectionSetResult;

    /// <summary>
    /// Applies the row cap to a fully read set: rows read under a limit of
    /// <see cref="RowLimitFor"/> are a <see cref="Set"/> when they fit within
    /// <paramref name="maxProjectionRows"/> and <see cref="TooLarge"/> otherwise.
    /// </summary>
    public static EducationOrganizationProjectionSetResult FromCappedRows(
        IReadOnlyList<EducationOrganizationProjectionRow> rows,
        int maxProjectionRows
    )
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxProjectionRows);

        return rows.Count > maxProjectionRows ? new TooLarge(maxProjectionRows) : new Set(rows);
    }

    /// <summary>
    /// The statement row limit for a cap: one more than the cap, so that a set larger than the cap is
    /// detected without reading all of it.
    /// </summary>
    public static int RowLimitFor(int maxProjectionRows)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxProjectionRows);
        ArgumentOutOfRangeException.ThrowIfEqual(maxProjectionRows, int.MaxValue);

        return maxProjectionRows + 1;
    }
}
