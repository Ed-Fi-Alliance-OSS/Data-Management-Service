// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;

namespace EdFi.DataManagementService.Backend.ChangeQueries;

internal sealed record TrackedChangeAuthorizationSql(
    IReadOnlyList<string> Predicates,
    IReadOnlyList<RelationalParameter> Parameters
)
{
    public static readonly TrackedChangeAuthorizationSql None = new([], []);
}

/// <summary>
/// Emits ReadChanges authorization SQL predicate fragments and parameters for tracked-change queries
/// from a <see cref="ReadChangesAuthorizationPlan"/>. Subjects AND within a strategy, strategies OR
/// across, NamespaceBased AND-ed with the relationship OR-group, custom views AND-ed after both, in CMS
/// order. Every custom-view predicate is correlated on the tracked-change alias and reads only its
/// <c>Old*</c> columns and <c>DocumentId</c> system column, so the same text serves the <c>/deletes</c>
/// filtered subquery and the <c>/keyChanges</c> <c>FilteredChanges</c> CTE.
/// </summary>
internal static class TrackedChangeAuthorizationSqlEmitter
{
    // Custom views expose the basis DocumentId under this fixed column name (auth.md §"View-based
    // authorization strategy"); the tracked-change side always names its system column the same way.
    private static readonly DbColumnName _documentIdColumn = new("DocumentId");
    private static readonly DbTableName _descriptorTable = new(new DbSchemaName("dms"), "Descriptor");
    private static readonly DbColumnName _descriptorNamespaceColumn = new("Namespace");
    private static readonly DbColumnName _descriptorCodeValueColumn = new("CodeValue");
    private static readonly DbColumnName _descriptorDiscriminatorColumn = new("Discriminator");

    // Distinct from TrackedChangeQueryPlanner's @DescriptorDiscriminator{n} names, which the deletes query
    // binds for its recreated-row suppression join in the same command.
    private const string CustomViewDescriptorDiscriminatorParameterPrefix =
        "@CustomViewDescriptorDiscriminator";
    private const string CustomViewDescriptorDiscriminatorQualifiedParameterPrefix =
        "@CustomViewDescriptorDiscriminatorQualified";

    private const string BasisAlias = "b";
    private const string ProbeAlias = "t";
    private const string DescriptorAlias = "d";
    private const string UnionAlias = "basis";

    public static TrackedChangeAuthorizationSql Emit(
        ReadChangesAuthorizationPlan plan,
        SqlDialect dialect,
        string alias,
        IRelationalParameterConfigurator parameterConfigurator
    )
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(parameterConfigurator);

        List<string> predicates = [];
        List<RelationalParameter> parameters = [];

        if (
            plan.NamespaceCheck is { } namespaceCheck
            && plan.NamespaceParameterization is { } namespaceParameterization
        )
        {
            predicates.Add(
                BuildNamespacePredicate(alias, namespaceCheck, namespaceParameterization, dialect)
            );
            parameters.AddRange(BuildNamespaceParameters(namespaceParameterization, dialect));
        }

        if (plan.RelationshipChecks.Count > 0 && plan.ClaimParameterization is { } claimParameterization)
        {
            string claimFilter = BuildClaimFilterSql(dialect, claimParameterization);
            string orGroup = string.Join(
                " OR ",
                plan.RelationshipChecks.Select(check =>
                    "("
                    + string.Join(
                        " AND ",
                        check.Subjects.Select(subject =>
                            BuildSubjectPredicate(dialect, alias, subject, claimFilter)
                        )
                    )
                    + ")"
                )
            );
            predicates.Add("(" + orGroup + ")");
            parameters.AddRange(BuildClaimParameters(claimParameterization, parameterConfigurator));
        }

        // Custom views are AND filters in CMS order (the plan already orders them). Descriptor discriminator
        // parameters are numbered across the whole plan so two checks never bind the same name.
        var descriptorParameterIndex = 0;
        foreach (ReadChangesCustomViewCheckSpec check in plan.CustomViewChecks)
        {
            predicates.Add(
                BuildCustomViewPredicate(dialect, alias, check, parameters, ref descriptorParameterIndex)
            );
        }

        return new TrackedChangeAuthorizationSql(predicates, parameters);
    }

    private static string BuildCustomViewPredicate(
        SqlDialect dialect,
        string alias,
        ReadChangesCustomViewCheckSpec check,
        List<RelationalParameter> parameters,
        ref int descriptorParameterIndex
    )
    {
        return check.Basis switch
        {
            ReadChangesCustomViewBasis.StoredDocumentId stored => BuildViewMembership(
                dialect,
                check.View,
                $"{alias}.{Quote(dialect, stored.TrackedColumn)}"
            ),
            ReadChangesCustomViewBasis.LiveSeek seek => BuildLiveSeekPredicate(
                dialect,
                alias,
                check.View,
                seek,
                parameters,
                ref descriptorParameterIndex
            ),
            ReadChangesCustomViewBasis.DescriptorSeek seek => BuildDescriptorSeekPredicate(
                dialect,
                alias,
                check.View,
                seek,
                parameters,
                ref descriptorParameterIndex
            ),
            _ => throw new InvalidOperationException(
                $"Unsupported ReadChanges custom-view basis '{check.Basis.GetType().Name}' for strategy "
                    + $"'{check.ConfiguredStrategy.StrategyName}'."
            ),
        };
    }

    /// <summary>
    /// Live seek: the basis root table (or abstract union view) is sought by the paired identity columns and
    /// its <c>DocumentId</c> tested against the view. Without probe arms the seek collapses to a direct
    /// <c>EXISTS</c> over the basis table; with them the live arm is <c>UNION</c>-ed with one arm per basis
    /// tracked-change table so a deleted basis is still found.
    /// </summary>
    private static string BuildLiveSeekPredicate(
        SqlDialect dialect,
        string alias,
        DbTableName view,
        ReadChangesCustomViewBasis.LiveSeek seek,
        List<RelationalParameter> parameters,
        ref int descriptorParameterIndex
    )
    {
        List<string> liveJoins = [];
        List<string> liveConditions =
        [
            .. seek.KeyPairs.Select(pair =>
                $"{BasisAlias}.{Quote(dialect, pair.BasisColumn)} = {alias}.{Quote(dialect, pair.TrackedOldColumn)}"
            ),
        ];

        for (var i = 0; i < seek.DescriptorKeyPairs.Count; i++)
        {
            ReadChangesCustomViewDescriptorKeyPair pair = seek.DescriptorKeyPairs[i];
            string descriptorAlias = $"{DescriptorAlias}{i}";
            (string discriminatorParameter, string qualifiedDiscriminatorParameter) =
                BindDescriptorDiscriminator(
                    pair.DescriptorResource,
                    parameters,
                    ref descriptorParameterIndex
                );

            liveJoins.Add(
                $"LEFT JOIN {Quote(dialect, _descriptorTable)} {descriptorAlias} ON "
                    + BuildDescriptorMatch(
                        dialect,
                        alias,
                        descriptorAlias,
                        _descriptorDiscriminatorColumn,
                        _descriptorNamespaceColumn,
                        _descriptorCodeValueColumn,
                        discriminatorParameter,
                        qualifiedDiscriminatorParameter,
                        pair.TrackedOldNamespaceColumn,
                        pair.TrackedOldCodeValueColumn
                    )
            );
            liveConditions.Add(
                $"{BasisAlias}.{Quote(dialect, pair.BasisFkColumn)} = {descriptorAlias}.{Quote(dialect, _documentIdColumn)}"
            );
        }

        if (liveConditions.Count == 0)
        {
            throw new InvalidOperationException(
                $"ReadChanges custom-view live seek of '{seek.BasisTable}' has no identity key pairs to seek by."
            );
        }

        string liveFrom =
            $"{Quote(dialect, seek.BasisTable)} {BasisAlias}"
            + string.Concat(liveJoins.Select(static join => " " + join));
        string liveWhere = string.Join(" AND ", liveConditions);
        string basisDocumentId = $"{BasisAlias}.{Quote(dialect, seek.BasisDocumentIdColumn)}";

        if (seek.ProbeArms.Count == 0)
        {
            return $"EXISTS (SELECT 1 FROM {liveFrom} WHERE {liveWhere} AND {BuildViewMembership(dialect, view, basisDocumentId)})";
        }

        List<string> arms =
        [
            $"SELECT {basisDocumentId} AS {Quote(dialect, _documentIdColumn)} FROM {liveFrom} WHERE {liveWhere}",
            .. seek.ProbeArms.Select(arm => BuildProbeArm(dialect, alias, arm)),
        ];

        return BuildUnionMembership(dialect, view, arms);
    }

    private static string BuildProbeArm(SqlDialect dialect, string alias, ReadChangesCustomViewProbeArm arm)
    {
        List<string> conditions =
        [
            .. arm.KeyPairs.Select(pair =>
                $"{ProbeAlias}.{Quote(dialect, pair.BasisColumn)} = {alias}.{Quote(dialect, pair.TrackedOldColumn)}"
            ),
            .. arm.DescriptorKeyPairs.SelectMany(pair =>
                new[]
                {
                    $"{ProbeAlias}.{Quote(dialect, pair.BasisOldNamespaceColumn)} = {alias}.{Quote(dialect, pair.TrackedOldNamespaceColumn)}",
                    $"{ProbeAlias}.{Quote(dialect, pair.BasisOldCodeValueColumn)} = {alias}.{Quote(dialect, pair.TrackedOldCodeValueColumn)}",
                }
            ),
        ];

        if (conditions.Count == 0)
        {
            throw new InvalidOperationException(
                $"ReadChanges custom-view probe arm over '{arm.BasisTrackedChangeTable}' has no identity key pairs to seek by."
            );
        }

        return $"SELECT {ProbeAlias}.{Quote(dialect, arm.BasisDocumentIdColumn)} FROM {Quote(dialect, arm.BasisTrackedChangeTable)} {ProbeAlias} "
            + $"WHERE {string.Join(" AND ", conditions)}";
    }

    /// <summary>
    /// Descriptor basis: descriptors share <c>dms.Descriptor</c>, so the tombstone's old Namespace/CodeValue
    /// seek that table under the basis descriptor's discriminator; the optional probe arm reads the shared
    /// descriptor tracked-change table under the same discriminator parameters.
    /// </summary>
    private static string BuildDescriptorSeekPredicate(
        SqlDialect dialect,
        string alias,
        DbTableName view,
        ReadChangesCustomViewBasis.DescriptorSeek seek,
        List<RelationalParameter> parameters,
        ref int descriptorParameterIndex
    )
    {
        (string discriminatorParameter, string qualifiedDiscriminatorParameter) = BindDescriptorDiscriminator(
            seek.DescriptorResource,
            parameters,
            ref descriptorParameterIndex
        );
        string liveMatch = BuildDescriptorMatch(
            dialect,
            alias,
            DescriptorAlias,
            _descriptorDiscriminatorColumn,
            _descriptorNamespaceColumn,
            _descriptorCodeValueColumn,
            discriminatorParameter,
            qualifiedDiscriminatorParameter,
            seek.TrackedOldNamespaceColumn,
            seek.TrackedOldCodeValueColumn
        );
        string liveFrom = $"{Quote(dialect, _descriptorTable)} {DescriptorAlias}";
        string descriptorDocumentId = $"{DescriptorAlias}.{Quote(dialect, _documentIdColumn)}";

        if (seek.ProbeArm is not { } probeArm)
        {
            return $"EXISTS (SELECT 1 FROM {liveFrom} WHERE {liveMatch} AND {BuildViewMembership(dialect, view, descriptorDocumentId)})";
        }

        string probeMatch = BuildDescriptorMatch(
            dialect,
            alias,
            ProbeAlias,
            probeArm.DiscriminatorColumn,
            probeArm.OldNamespaceColumn,
            probeArm.OldCodeValueColumn,
            discriminatorParameter,
            qualifiedDiscriminatorParameter,
            seek.TrackedOldNamespaceColumn,
            seek.TrackedOldCodeValueColumn
        );

        return BuildUnionMembership(
            dialect,
            view,
            [
                $"SELECT {descriptorDocumentId} AS {Quote(dialect, _documentIdColumn)} FROM {liveFrom} WHERE {liveMatch}",
                $"SELECT {ProbeAlias}.{Quote(dialect, probeArm.DocumentIdColumn)} FROM {Quote(dialect, probeArm.DescriptorTrackedChangeTable)} {ProbeAlias} WHERE {probeMatch}",
            ]
        );
    }

    // <alias>.Discriminator IN (@p, @pq) AND <alias>.Namespace = c.OldNs AND <alias>.CodeValue = c.OldCv —
    // the shape TrackedChangeQueryPlanner.BuildDescriptorIdentityJoin uses for the recreated-row join.
    private static string BuildDescriptorMatch(
        SqlDialect dialect,
        string alias,
        string descriptorAlias,
        DbColumnName discriminatorColumn,
        DbColumnName namespaceColumn,
        DbColumnName codeValueColumn,
        string discriminatorParameter,
        string qualifiedDiscriminatorParameter,
        DbColumnName trackedOldNamespaceColumn,
        DbColumnName trackedOldCodeValueColumn
    ) =>
        $"{descriptorAlias}.{Quote(dialect, discriminatorColumn)} IN ({discriminatorParameter}, {qualifiedDiscriminatorParameter})"
        + $" AND {descriptorAlias}.{Quote(dialect, namespaceColumn)} = {alias}.{Quote(dialect, trackedOldNamespaceColumn)}"
        + $" AND {descriptorAlias}.{Quote(dialect, codeValueColumn)} = {alias}.{Quote(dialect, trackedOldCodeValueColumn)}";

    private static (
        string DiscriminatorParameter,
        string QualifiedDiscriminatorParameter
    ) BindDescriptorDiscriminator(
        QualifiedResourceName descriptorResource,
        List<RelationalParameter> parameters,
        ref int descriptorParameterIndex
    )
    {
        string discriminatorParameter =
            $"{CustomViewDescriptorDiscriminatorParameterPrefix}{descriptorParameterIndex}";
        string qualifiedDiscriminatorParameter =
            $"{CustomViewDescriptorDiscriminatorQualifiedParameterPrefix}{descriptorParameterIndex}";
        descriptorParameterIndex++;

        parameters.Add(new RelationalParameter(discriminatorParameter, descriptorResource.ResourceName));
        parameters.Add(
            new RelationalParameter(
                qualifiedDiscriminatorParameter,
                TrackedChangeQueryPlanner.BuildQualifiedDiscriminator(descriptorResource)
            )
        );

        return (discriminatorParameter, qualifiedDiscriminatorParameter);
    }

    private static string BuildUnionMembership(
        SqlDialect dialect,
        DbTableName view,
        IReadOnlyList<string> arms
    ) =>
        $"EXISTS (SELECT 1 FROM ({string.Join(" UNION ", arms)}) {UnionAlias} "
        + $"WHERE {BuildViewMembership(dialect, view, $"{UnionAlias}.{Quote(dialect, _documentIdColumn)}")})";

    private static string BuildViewMembership(
        SqlDialect dialect,
        DbTableName view,
        string documentIdExpression
    ) =>
        $"{documentIdExpression} IN (SELECT {Quote(dialect, _documentIdColumn)} FROM {Quote(dialect, view)})";

    private static string BuildSubjectPredicate(
        SqlDialect dialect,
        string alias,
        ReadChangesAuthorizationSubject subject,
        string claimFilter
    )
    {
        string trackedColumn = $"{alias}.{Quote(dialect, subject.TrackedOldColumn)}";
        string hierarchyPredicate =
            $"{trackedColumn} IN (SELECT {Quote(dialect, subject.AuthViewSubjectColumn)} "
            + $"FROM {Quote(dialect, subject.AuthView)} WHERE {Quote(dialect, subject.AuthViewClaimColumn)} {claimFilter})";

        return subject.AuthView == AuthNames.EdOrgIdToEdOrgId
            ? $"({trackedColumn} {claimFilter} OR {hierarchyPredicate})"
            : hierarchyPredicate;
    }

    // Mirrors AuthorizationClaimEducationOrganizationIdSqlHelper.AppendClaimFilterSql so tracked-change
    // queries emit the exact claim-filter shapes the live single-record/page paths use.
    private static string BuildClaimFilterSql(
        SqlDialect dialect,
        AuthorizationClaimEducationOrganizationIdParameterization p
    ) =>
        p.Kind switch
        {
            AuthorizationClaimEducationOrganizationIdParameterizationKind.PgsqlArray =>
                $"= ANY(@{p.BaseParameterName})",
            AuthorizationClaimEducationOrganizationIdParameterizationKind.MssqlScalar =>
                p.ParameterNamesInOrder.Count == 0
                    ? "IN (SELECT 1 WHERE 1 = 0)" // no claims → match nothing
                    : "IN (" + string.Join(", ", p.ParameterNamesInOrder.Select(n => "@" + n)) + ")",
            AuthorizationClaimEducationOrganizationIdParameterizationKind.MssqlStructured => "IN (SELECT "
                + SqlIdentifierQuoter.QuoteIdentifier(
                    dialect,
                    AuthorizationClaimEducationOrganizationIdParameterizationFactory.MssqlStructuredParameterColumnName
                )
                + $" FROM @{p.BaseParameterName})",
            _ => throw new InvalidOperationException($"Unsupported claim parameterization kind '{p.Kind}'."),
        };

    // Reuses the live binding helpers so PG array, MSSQL scalars, and the MSSQL TVP (DataTable +
    // SqlDbType.Structured via the configurator callback) all bind identically to the single-record path.
    private static IEnumerable<RelationalParameter> BuildClaimParameters(
        AuthorizationClaimEducationOrganizationIdParameterization p,
        IRelationalParameterConfigurator parameterConfigurator
    )
    {
        IReadOnlyList<QuerySqlParameter> filterParameters =
            AuthorizationClaimEducationOrganizationIdSqlHelper.BuildFilterParametersInOrder(p);

        return p.Kind switch
        {
            // PgsqlArray and MssqlStructured each carry a single parameter bound to the whole id list.
            AuthorizationClaimEducationOrganizationIdParameterizationKind.PgsqlArray
            or AuthorizationClaimEducationOrganizationIdParameterizationKind.MssqlStructured =>
            [
                RelationshipAuthorizationCommandParameterBuilder.BuildParameter(
                    filterParameters[0],
                    p.ClaimEducationOrganizationIds,
                    parameterConfigurator
                ),
            ],
            // MssqlScalar binds one parameter per id, zipped positionally with the id list.
            AuthorizationClaimEducationOrganizationIdParameterizationKind.MssqlScalar => filterParameters
                .Select(
                    (querySqlParameter, index) =>
                        RelationshipAuthorizationCommandParameterBuilder.BuildParameter(
                            querySqlParameter,
                            p.ClaimEducationOrganizationIds[index],
                            parameterConfigurator
                        )
                )
                .ToArray(),
            _ => throw new InvalidOperationException($"Unsupported claim parameterization kind '{p.Kind}'."),
        };
    }

    private static string BuildNamespacePredicate(
        string alias,
        ReadChangesNamespaceCheckSpec check,
        NamespacePrefixParameterization p,
        SqlDialect dialect
    )
    {
        string column = $"{alias}.{Quote(dialect, check.TrackedOldNamespaceColumn)}";
        string likeChain = dialect switch
        {
            SqlDialect.Pgsql => $"{column} LIKE ANY(@{p.ParameterNamesInOrder[0]})",
            SqlDialect.Mssql => string.Join(
                " OR ",
                p.ParameterNamesInOrder.Select(n => $"{column} LIKE @{n} ESCAPE '\\'")
            ),
            _ => throw new InvalidOperationException($"Unsupported SQL dialect '{dialect}'."),
        };
        return $"({column} IS NOT NULL AND ({likeChain}))";
    }

    private static IEnumerable<RelationalParameter> BuildNamespaceParameters(
        NamespacePrefixParameterization p,
        SqlDialect dialect
    ) =>
        dialect switch
        {
            SqlDialect.Pgsql =>
            [
                new RelationalParameter("@" + p.ParameterNamesInOrder[0], p.LikePatternsInOrder.ToArray()),
            ],
            SqlDialect.Mssql => p.ParameterNamesInOrder.Select(
                (n, i) => new RelationalParameter("@" + n, p.LikePatternsInOrder[i])
            ),
            _ => throw new InvalidOperationException($"Unsupported SQL dialect '{dialect}'."),
        };

    private static string Quote(SqlDialect dialect, DbColumnName column) =>
        SqlIdentifierQuoter.QuoteIdentifier(dialect, column);

    private static string Quote(SqlDialect dialect, DbTableName table) =>
        SqlIdentifierQuoter.QuoteTableName(dialect, table);
}
