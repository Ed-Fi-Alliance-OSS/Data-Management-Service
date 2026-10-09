// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Buffers;
using System.Text;
using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Core.EducationOrganizationProjection;

/// <summary>
/// Why a read set is refused as <c>projection-data-invalid</c>. Logged; never sent to the client,
/// which receives one fixed body for every reason.
/// </summary>
internal enum ProjectionDataInvalidReason
{
    /// <summary>Two rows have the same identifier.</summary>
    DuplicateIdentifier,

    /// <summary>A name is not well-formed UTF-16 (it holds an unpaired surrogate).</summary>
    MalformedName,

    /// <summary>A populated reference does not resolve to a row of its expected type.</summary>
    UnresolvedReference,

    /// <summary>Parent local education agency links form a cycle, a self-link included.</summary>
    ParentCycle,
}

/// <summary>
/// The outcome of validating a read set.
/// </summary>
internal abstract record ProjectionSetValidation
{
    private ProjectionSetValidation() { }

    /// <summary>Every check passed; parents can be selected.</summary>
    public sealed record Valid(ValidatedProjectionSet Set) : ProjectionSetValidation;

    /// <summary>A row carries a discriminator outside the four core types: 409 <c>projection-unsupported</c>.</summary>
    public sealed record UnsupportedDiscriminator : ProjectionSetValidation;

    /// <summary>The data contradicts itself: 409 <c>projection-data-invalid</c>.</summary>
    public sealed record DataInvalid(ProjectionDataInvalidReason Reason) : ProjectionSetValidation;
}

/// <summary>
/// A read set that passed every validation rule, with each row's core type resolved.
/// </summary>
internal sealed class ValidatedProjectionSet
{
    internal ValidatedProjectionSet(
        IReadOnlyList<EducationOrganizationProjectionRow> rows,
        ProjectionItemKind[] kinds
    )
    {
        Rows = rows;
        Kinds = kinds;
    }

    /// <summary>The rows in strictly ascending identifier order.</summary>
    public IReadOnlyList<EducationOrganizationProjectionRow> Rows { get; }

    /// <summary>Each row's core type, by row index.</summary>
    public ProjectionItemKind[] Kinds { get; }
}

/// <summary>
/// The whole-set rules of the projection: every page applies them to the complete set before it
/// compares the digest or selects its items, so a contradiction anywhere fails every page.
/// </summary>
/// <remarks>
/// <para>
/// The checks run as separate passes in a fixed order, so the outcome does not depend on where in
/// the set a problem sits: discriminators first (a literal outside the allowlist is a property of the
/// deployed model, not of the data), then identifiers and names, then references, then parent cycles.
/// Every pass is iterative and linear in the number of rows; nothing recurses, so a long chain of
/// local education agencies cannot grow the stack.
/// </para>
/// <para>
/// A reader that returns rows out of identifier order, or a row with a reference its type cannot
/// carry, has broken its contract; that is a defect and throws rather than being reported as data.
/// </para>
/// </remarks>
internal static class ProjectionSetValidator
{
    /// <summary>
    /// The stored discriminator literal of each core type. Nothing else is accepted, and the literal is
    /// never exposed: the response carries <see cref="WireDiscriminator"/> instead.
    /// </summary>
    private static readonly Dictionary<string, ProjectionItemKind> _kindsByStoredDiscriminator = new(
        StringComparer.Ordinal
    )
    {
        ["Ed-Fi:StateEducationAgency"] = ProjectionItemKind.StateEducationAgency,
        ["Ed-Fi:EducationServiceCenter"] = ProjectionItemKind.EducationServiceCenter,
        ["Ed-Fi:LocalEducationAgency"] = ProjectionItemKind.LocalEducationAgency,
        ["Ed-Fi:School"] = ProjectionItemKind.School,
    };

    /// <summary>
    /// The discriminator a response item carries for each core type.
    /// </summary>
    public static string WireDiscriminator(ProjectionItemKind kind) =>
        kind switch
        {
            ProjectionItemKind.StateEducationAgency => "edfi.StateEducationAgency",
            ProjectionItemKind.EducationServiceCenter => "edfi.EducationServiceCenter",
            ProjectionItemKind.LocalEducationAgency => "edfi.LocalEducationAgency",
            ProjectionItemKind.School => "edfi.School",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported projection kind."),
        };

    /// <summary>
    /// Applies every rule to the whole set.
    /// </summary>
    public static ProjectionSetValidation Validate(
        IReadOnlyList<EducationOrganizationProjectionRow> rows,
        IProjectionProcessingObserver observer,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(observer);

        // One counter across the passes, so checkpoints advance monotonically through the stage.
        int rowsProcessed = 0;

        void Checkpoint()
        {
            rowsProcessed++;
            ProjectionProcessing.Checkpoint(
                observer,
                ProjectionProcessingStage.Validation,
                rowsProcessed,
                cancellationToken
            );
        }

        var kinds = new ProjectionItemKind[rows.Count];

        for (int index = 0; index < rows.Count; index++)
        {
            Checkpoint();
            EducationOrganizationProjectionRow row = rows[index];

            if (!_kindsByStoredDiscriminator.TryGetValue(row.Discriminator, out ProjectionItemKind kind))
            {
                return new ProjectionSetValidation.UnsupportedDiscriminator();
            }

            EnsureSlotsFit(row, kind);
            kinds[index] = kind;
        }

        var indexById = new Dictionary<long, int>(rows.Count);

        for (int index = 0; index < rows.Count; index++)
        {
            Checkpoint();
            EducationOrganizationProjectionRow row = rows[index];

            if (index > 0 && row.EducationOrganizationId < rows[index - 1].EducationOrganizationId)
            {
                throw new InvalidOperationException(
                    "The projection set reader returned rows out of ascending identifier order."
                );
            }

            if (!indexById.TryAdd(row.EducationOrganizationId, index))
            {
                return new ProjectionSetValidation.DataInvalid(
                    ProjectionDataInvalidReason.DuplicateIdentifier
                );
            }

            if (!IsWellFormed(row.NameOfInstitution) || !IsWellFormed(row.ShortNameOfInstitution))
            {
                return new ProjectionSetValidation.DataInvalid(ProjectionDataInvalidReason.MalformedName);
            }
        }

        for (int index = 0; index < rows.Count; index++)
        {
            Checkpoint();

            if (!ReferencesResolve(rows[index], kinds[index], indexById, kinds))
            {
                return new ProjectionSetValidation.DataInvalid(
                    ProjectionDataInvalidReason.UnresolvedReference
                );
            }
        }

        if (HasParentCycle(rows, kinds, indexById, Checkpoint))
        {
            return new ProjectionSetValidation.DataInvalid(ProjectionDataInvalidReason.ParentCycle);
        }

        return new ProjectionSetValidation.Valid(new ValidatedProjectionSet(rows, kinds));
    }

    /// <summary>
    /// Selects each row's parent by the precedence of its type. Every reference was validated first,
    /// so whichever is selected names another row of the expected type.
    /// </summary>
    public static IReadOnlyList<ProjectionItem> SelectParents(
        ValidatedProjectionSet set,
        IProjectionProcessingObserver observer,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(observer);

        var items = new ProjectionItem[set.Rows.Count];

        for (int index = 0; index < items.Length; index++)
        {
            ProjectionProcessing.Checkpoint(
                observer,
                ProjectionProcessingStage.Precedence,
                index + 1,
                cancellationToken
            );

            EducationOrganizationProjectionRow row = set.Rows[index];
            ProjectionItemKind kind = set.Kinds[index];

            long? parentId = kind switch
            {
                ProjectionItemKind.School => row.LocalEducationAgencyReference,
                ProjectionItemKind.LocalEducationAgency => row.ParentLocalEducationAgencyReference
                    ?? row.EducationServiceCenterReference
                    ?? row.StateEducationAgencyReference,
                ProjectionItemKind.EducationServiceCenter => row.StateEducationAgencyReference,
                _ => null,
            };

            items[index] = new ProjectionItem(
                row.EducationOrganizationId,
                kind,
                row.NameOfInstitution,
                row.ShortNameOfInstitution,
                parentId
            );
        }

        return items;
    }

    /// <summary>
    /// A slot the row's type cannot carry is always null in what the reader returns; a value there
    /// means the read and the validation disagree about the row's shape.
    /// </summary>
    private static void EnsureSlotsFit(EducationOrganizationProjectionRow row, ProjectionItemKind kind)
    {
        bool fits = kind switch
        {
            ProjectionItemKind.School => row.ParentLocalEducationAgencyReference is null
                && row.EducationServiceCenterReference is null
                && row.StateEducationAgencyReference is null,
            ProjectionItemKind.LocalEducationAgency => row.LocalEducationAgencyReference is null,
            ProjectionItemKind.EducationServiceCenter => row.LocalEducationAgencyReference is null
                && row.ParentLocalEducationAgencyReference is null
                && row.EducationServiceCenterReference is null,
            _ => row.LocalEducationAgencyReference is null
                && row.ParentLocalEducationAgencyReference is null
                && row.EducationServiceCenterReference is null
                && row.StateEducationAgencyReference is null,
        };

        if (!fits)
        {
            throw new InvalidOperationException(
                "The projection set reader returned a reference that the row's type cannot carry."
            );
        }
    }

    private static bool ReferencesResolve(
        EducationOrganizationProjectionRow row,
        ProjectionItemKind kind,
        Dictionary<long, int> indexById,
        ProjectionItemKind[] kinds
    )
    {
        bool Resolves(long? reference, ProjectionItemKind expected) =>
            reference is not long id
            || (indexById.TryGetValue(id, out int target) && kinds[target] == expected);

        return kind switch
        {
            ProjectionItemKind.School => Resolves(
                row.LocalEducationAgencyReference,
                ProjectionItemKind.LocalEducationAgency
            ),
            ProjectionItemKind.LocalEducationAgency => Resolves(
                row.ParentLocalEducationAgencyReference,
                ProjectionItemKind.LocalEducationAgency
            )
                && Resolves(row.EducationServiceCenterReference, ProjectionItemKind.EducationServiceCenter)
                && Resolves(row.StateEducationAgencyReference, ProjectionItemKind.StateEducationAgency),
            ProjectionItemKind.EducationServiceCenter => Resolves(
                row.StateEducationAgencyReference,
                ProjectionItemKind.StateEducationAgency
            ),
            _ => true,
        };
    }

    /// <summary>
    /// Each local education agency has at most one parent agency, so following parent links from any
    /// agency either ends or enters a cycle. A three-colour walk with an explicit path list visits each
    /// agency once: an agency met again while still on the current path closes a cycle, and a path
    /// that ends, or reaches an agency an earlier walk finished, is cycle-free. A self-link is a cycle
    /// of length one.
    /// <para>
    /// One walk can follow every agency in the set, and marking its path finished visits them all
    /// again, so both loops take a checkpoint per agency, as does the loop over starting rows.
    /// </para>
    /// </summary>
    private static bool HasParentCycle(
        IReadOnlyList<EducationOrganizationProjectionRow> rows,
        ProjectionItemKind[] kinds,
        Dictionary<long, int> indexById,
        Action checkpoint
    )
    {
        const byte Unvisited = 0;
        const byte OnPath = 1;
        const byte Finished = 2;

        var state = new byte[rows.Count];
        var path = new List<int>();

        for (int start = 0; start < rows.Count; start++)
        {
            checkpoint();

            if (kinds[start] != ProjectionItemKind.LocalEducationAgency || state[start] != Unvisited)
            {
                continue;
            }

            int current = start;
            bool cycle = false;

            while (true)
            {
                checkpoint();

                if (state[current] == OnPath)
                {
                    cycle = true;
                    break;
                }

                if (state[current] == Finished)
                {
                    break;
                }

                state[current] = OnPath;
                path.Add(current);

                // References were validated, so a populated parent is another local education agency
                // of the set.
                if (rows[current].ParentLocalEducationAgencyReference is not long parentId)
                {
                    break;
                }

                current = indexById[parentId];
            }

            if (cycle)
            {
                return true;
            }

            foreach (int visited in path)
            {
                checkpoint();
                state[visited] = Finished;
            }

            path.Clear();
        }

        return false;
    }

    /// <summary>
    /// Whether every surrogate is part of a high-low pair. A null value is well formed.
    /// </summary>
    private static bool IsWellFormed(string? value)
    {
        if (value is null)
        {
            return true;
        }

        ReadOnlySpan<char> remaining = value;

        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(remaining, out _, out int consumed) != OperationStatus.Done)
            {
                return false;
            }

            remaining = remaining[consumed..];
        }

        return true;
    }
}
