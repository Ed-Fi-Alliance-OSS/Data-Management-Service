// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Canonical executable relationship authorization check shape, excluding runtime claim values.
/// </summary>
public sealed record RelationshipAuthorizationExecutableShape
{
    private RelationshipAuthorizationExecutableShape(
        IReadOnlyList<RelationshipAuthorizationCheckSpec> checkSpecs,
        RelationshipAuthorizationSqlShapeKey sqlShapeKey
    )
    {
        CheckSpecs = checkSpecs;
        SqlShapeKey = sqlShapeKey;
    }

    public IReadOnlyList<RelationshipAuthorizationCheckSpec> CheckSpecs { get; }

    public RelationshipAuthorizationSqlShapeKey SqlShapeKey { get; }

    public static RelationshipAuthorizationExecutableShape Create(
        IReadOnlyList<RelationshipAuthorizationCheckSpec> checkSpecs
    )
    {
        ArgumentNullException.ThrowIfNull(checkSpecs);

        return new RelationshipAuthorizationExecutableShape(
            checkSpecs,
            RelationshipAuthorizationSqlShapeKey.Create(checkSpecs)
        );
    }
}

public sealed class RelationshipAuthorizationSqlShapeKey : IEquatable<RelationshipAuthorizationSqlShapeKey>
{
    private readonly ShapeToken[] _shapeTokens;
    private readonly int _hashCode;

    private RelationshipAuthorizationSqlShapeKey(ShapeToken[] shapeTokens)
    {
        _shapeTokens = shapeTokens;
        _hashCode = BuildHashCode();
    }

    public static RelationshipAuthorizationSqlShapeKey Create(
        IReadOnlyList<RelationshipAuthorizationCheckSpec> checkSpecs
    )
    {
        ArgumentNullException.ThrowIfNull(checkSpecs);

        return new RelationshipAuthorizationSqlShapeKey(BuildShapeTokens(checkSpecs));
    }

    public bool Equals(RelationshipAuthorizationSqlShapeKey? other)
    {
        if (ReferenceEquals(this, other))
        {
            return true;
        }

        return other is not null
            && _hashCode == other._hashCode
            && _shapeTokens.AsSpan().SequenceEqual(other._shapeTokens);
    }

    public override bool Equals(object? obj) =>
        obj is RelationshipAuthorizationSqlShapeKey other && Equals(other);

    public override int GetHashCode() => _hashCode;

    private int BuildHashCode()
    {
        HashCode hashCode = new();
        AddShapeTokenSequenceHashCodes(ref hashCode, _shapeTokens);

        return hashCode.ToHashCode();
    }

    private static ShapeToken[] BuildShapeTokens(IReadOnlyList<RelationshipAuthorizationCheckSpec> checkSpecs)
    {
        var builder = new ShapeTokenBuilder(Math.Max(32, checkSpecs.Count * 24));
        builder.Add(CreateNumberToken(ShapeTokenKind.CheckSpecCount, checkSpecs.Count));

        for (var index = 0; index < checkSpecs.Count; index++)
        {
            AddCheckSpecShapeTokens(ref builder, checkSpecs[index]);
        }

        return builder.ToArray();
    }

    private static void AddCheckSpecShapeTokens(
        ref ShapeTokenBuilder builder,
        RelationshipAuthorizationCheckSpec checkSpec
    )
    {
        builder.Add(
            new ShapeToken(
                ShapeTokenKind.CheckSpecStrategy,
                checkSpec.ConfiguredStrategy.StrategyName,
                null,
                checkSpec.ConfiguredStrategy.RawConfiguredIndex,
                checkSpec.RelationshipLocalOrder
            )
        );
        builder.Add(
            CreateNumberToken(
                ShapeTokenKind.CheckSpecSource,
                (int)checkSpec.Direction,
                (int)checkSpec.ValueSource
            )
        );
        builder.Add(CreateNumberToken(ShapeTokenKind.SubjectCount, checkSpec.Subjects.Count));

        for (var index = 0; index < checkSpec.Subjects.Count; index++)
        {
            AddSubjectShapeTokens(ref builder, checkSpec.Subjects[index]);
        }

        AddCheckTargetShapeTokens(ref builder, checkSpec.CheckTarget);
    }

    private static void AddSubjectShapeTokens(
        ref ShapeTokenBuilder builder,
        RelationshipAuthorizationSubject subject
    )
    {
        builder.Add(CreateResourceToken(ShapeTokenKind.SubjectResource, subject.Resource));
        builder.Add(CreateTableToken(ShapeTokenKind.SubjectTable, subject.Table));
        builder.Add(CreateColumnToken(ShapeTokenKind.SubjectColumn, subject.Column));
        AddAuthObjectShapeTokens(ref builder, subject.AuthObject);
        builder.Add(CreateNumberToken(ShapeTokenKind.ContributorCount, subject.Contributors.Count));

        for (var index = 0; index < subject.Contributors.Count; index++)
        {
            var contributor = subject.Contributors[index];
            builder.Add(
                new ShapeToken(
                    ShapeTokenKind.Contributor,
                    contributor.JsonPath,
                    contributor.ReadableName,
                    (int)contributor.Kind,
                    contributor.ContributionOrder
                )
            );
        }

        AddPersonMetadataShapeTokens(ref builder, subject.PersonMetadata);
    }

    private static void AddAuthObjectShapeTokens(
        ref ShapeTokenBuilder builder,
        RelationshipAuthorizationAuthObject authObject
    )
    {
        builder.Add(CreateTableToken(ShapeTokenKind.AuthObjectTable, authObject.Name));
        builder.Add(
            CreateColumnToken(ShapeTokenKind.AuthObjectSubjectValueColumn, authObject.SubjectValueColumn)
        );
        builder.Add(
            CreateColumnToken(
                ShapeTokenKind.AuthObjectClaimEducationOrganizationIdColumn,
                authObject.ClaimEducationOrganizationIdColumn
            )
        );
        builder.Add(
            CreateNumberToken(ShapeTokenKind.AuthObjectFlags, authObject.AllowsDirectClaimMatch ? 1 : 0)
        );
        builder.Add(CreateTextToken(ShapeTokenKind.AuthObjectFailureHint, authObject.FailureHint));
    }

    private static void AddPersonMetadataShapeTokens(
        ref ShapeTokenBuilder builder,
        RelationshipAuthorizationPersonSubjectMetadata? metadata
    )
    {
        if (metadata is null)
        {
            builder.Add(CreateNumberToken(ShapeTokenKind.PersonMetadata, -1));
            return;
        }

        builder.Add(CreateNumberToken(ShapeTokenKind.PersonMetadata, (int)metadata.PersonKind));
        builder.Add(CreateNumberToken(ShapeTokenKind.PersonPath, (int)metadata.Path.Kind));
        builder.Add(CreateNumberToken(ShapeTokenKind.PersonPathStepCount, metadata.Path.Steps.Count));

        for (var index = 0; index < metadata.Path.Steps.Count; index++)
        {
            AddColumnPathStepShapeTokens(ref builder, metadata.Path.Steps[index]);
        }

        builder.Add(CreateTableToken(ShapeTokenKind.StoredAnchorTable, metadata.StoredAnchor.RootTable));
        builder.Add(
            CreateColumnToken(
                ShapeTokenKind.StoredAnchorDocumentIdColumn,
                metadata.StoredAnchor.RootDocumentIdColumn
            )
        );

        if (metadata.ProposedAnchor is not { } proposedAnchor)
        {
            builder.Add(CreateNumberToken(ShapeTokenKind.ProposedAnchor, -1));
            return;
        }

        builder.Add(CreateNumberToken(ShapeTokenKind.ProposedAnchor, (int)proposedAnchor.Kind));
        AddProposedBindingShapeTokens(ref builder, proposedAnchor.Binding);
    }

    private static void AddColumnPathStepShapeTokens(ref ShapeTokenBuilder builder, ColumnPathStep step)
    {
        builder.Add(CreateTableToken(ShapeTokenKind.PersonPathSourceTable, step.SourceTable));
        builder.Add(CreateColumnToken(ShapeTokenKind.PersonPathSourceColumn, step.SourceColumnName));
        AddNullableTableToken(ref builder, ShapeTokenKind.PersonPathTargetTable, step.TargetTable);
        AddNullableColumnToken(ref builder, ShapeTokenKind.PersonPathTargetColumn, step.TargetColumnName);
    }

    private static void AddCheckTargetShapeTokens(
        ref ShapeTokenBuilder builder,
        RelationshipAuthorizationCheckTarget target
    )
    {
        switch (target)
        {
            case RelationshipAuthorizationCheckTarget.Stored stored:
                builder.Add(CreateNumberToken(ShapeTokenKind.CheckTargetStored, 0));
                builder.Add(CreateTableToken(ShapeTokenKind.CheckTargetRootTable, stored.RootTable));
                builder.Add(
                    CreateColumnToken(ShapeTokenKind.CheckTargetDocumentIdColumn, stored.DocumentIdColumn)
                );
                break;
            case RelationshipAuthorizationCheckTarget.Proposed proposed:
                builder.Add(CreateNumberToken(ShapeTokenKind.CheckTargetProposed, 0));
                builder.Add(CreateTableToken(ShapeTokenKind.CheckTargetRootTable, proposed.RootTable));
                builder.Add(
                    CreateNumberToken(
                        ShapeTokenKind.CheckTargetBindingCount,
                        proposed.SubjectBindingsInOrder.Count
                    )
                );

                for (var index = 0; index < proposed.SubjectBindingsInOrder.Count; index++)
                {
                    AddProposedBindingShapeTokens(ref builder, proposed.SubjectBindingsInOrder[index]);
                }
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(target), target, null);
        }
    }

    private static void AddProposedBindingShapeTokens(
        ref ShapeTokenBuilder builder,
        RelationshipAuthorizationProposedValueBinding binding
    )
    {
        builder.Add(CreateTableToken(ShapeTokenKind.ProposedBindingTable, binding.Table));
        builder.Add(CreateColumnToken(ShapeTokenKind.ProposedBindingColumn, binding.Column));
        builder.Add(
            new ShapeToken(
                ShapeTokenKind.ProposedBinding,
                binding.LogicalKey,
                binding.ParameterSeed,
                binding.BindingIndex,
                0
            )
        );
    }

    private static void AddNullableTableToken(
        ref ShapeTokenBuilder builder,
        ShapeTokenKind kind,
        DbTableName? table
    )
    {
        if (table is not { } tableValue)
        {
            builder.Add(CreateNullToken(kind));
            return;
        }

        builder.Add(CreateTableToken(kind, tableValue));
    }

    private static void AddNullableColumnToken(
        ref ShapeTokenBuilder builder,
        ShapeTokenKind kind,
        DbColumnName? column
    )
    {
        if (column is not { } columnValue)
        {
            builder.Add(CreateNullToken(kind));
            return;
        }

        builder.Add(CreateColumnToken(kind, columnValue));
    }

    private static ShapeToken CreateNullToken(ShapeTokenKind kind) => new(kind, null, null, 0, 0);

    private static ShapeToken CreateNumberToken(ShapeTokenKind kind, int number1, int number2 = 0) =>
        new(kind, null, null, number1, number2);

    private static ShapeToken CreateTextToken(ShapeTokenKind kind, string? text) =>
        new(kind, text, null, 0, 0);

    private static ShapeToken CreateResourceToken(ShapeTokenKind kind, QualifiedResourceName resource) =>
        new(kind, resource.ProjectName, resource.ResourceName, 0, 0);

    private static ShapeToken CreateTableToken(ShapeTokenKind kind, DbTableName table) =>
        new(kind, table.Schema.Value, table.Name, 0, 0);

    private static ShapeToken CreateColumnToken(ShapeTokenKind kind, DbColumnName column) =>
        new(kind, column.Value, null, 0, 0);

    private static void AddShapeTokenSequenceHashCodes(
        ref HashCode hashCode,
        IReadOnlyList<ShapeToken> values
    )
    {
        hashCode.Add(values.Count);

        foreach (var value in values)
        {
            hashCode.Add(value);
        }
    }

    private enum ShapeTokenKind
    {
        CheckSpecCount,
        CheckSpecStrategy,
        CheckSpecSource,
        SubjectCount,
        SubjectResource,
        SubjectTable,
        SubjectColumn,
        AuthObjectTable,
        AuthObjectSubjectValueColumn,
        AuthObjectClaimEducationOrganizationIdColumn,
        AuthObjectFlags,
        AuthObjectFailureHint,
        ContributorCount,
        Contributor,
        PersonMetadata,
        PersonPath,
        PersonPathStepCount,
        PersonPathSourceTable,
        PersonPathSourceColumn,
        PersonPathTargetTable,
        PersonPathTargetColumn,
        StoredAnchorTable,
        StoredAnchorDocumentIdColumn,
        ProposedAnchor,
        ProposedBindingTable,
        ProposedBindingColumn,
        ProposedBinding,
        CheckTargetStored,
        CheckTargetProposed,
        CheckTargetRootTable,
        CheckTargetDocumentIdColumn,
        CheckTargetBindingCount,
    }

    private readonly record struct ShapeToken(
        ShapeTokenKind Kind,
        string? Text1,
        string? Text2,
        int Number1,
        int Number2
    );

    private struct ShapeTokenBuilder
    {
        private ShapeToken[] _tokens;
        private int _count;

        public ShapeTokenBuilder(int initialCapacity)
        {
            _tokens = new ShapeToken[initialCapacity];
            _count = 0;
        }

        public void Add(ShapeToken token)
        {
            if (_count == _tokens.Length)
            {
                Grow();
            }

            _tokens[_count] = token;
            _count++;
        }

        public ShapeToken[] ToArray()
        {
            if (_count == _tokens.Length)
            {
                return _tokens;
            }

            ShapeToken[] tokens = new ShapeToken[_count];
            Array.Copy(_tokens, tokens, _count);
            return tokens;
        }

        private void Grow()
        {
            var newCapacity = _tokens.Length == 0 ? 16 : _tokens.Length * 2;
            Array.Resize(ref _tokens, newCapacity);
        }
    }
}
