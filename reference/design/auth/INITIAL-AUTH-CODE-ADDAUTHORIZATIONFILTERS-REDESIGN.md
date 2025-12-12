• AddAuthorizationFilters is currently wired to the old JSONB/trigger design. It needs to change to:

  - Stop touching dms.Document’s JSON columns and auth arrays.
  - Express authorization purely as an EXISTS against DocumentSubject + SubjectEdOrg (for relationships) and QueryFields (for namespace).
  - Use the AuthorizationStrategyEvaluator[] the same way you already do for writes, but translate them into SQL predicates over the new tables.

  Below is how I’d reshape it, step by step.

  ———

  1) What the current AddAuthorizationFilters does (for context)

  Today, AddAuthorizationFilters:

  - Looks at queryRequest.AuthorizationSecurableInfo (Namespace, EducationOrganization, StudentUniqueId, ContactUniqueId, StaffUniqueId).
  - For each securable key, it pulls filters from AuthorizationStrategyEvaluators and calls helper builders:
      - BuildNamespaceFilter → SecurityElements->'Namespace'->>0 LIKE ...
      - BuildEducationOrganizationFilter → SecurityElements->'EducationOrganization'->0->>'Id' = ANY(SELECT jsonb_array_elements_text(hierarchy) FROM dms.educationorganizationhierarchytermslookup ...)
      - BuildStudentFilter → studentschoolauthorizationedorgids ?| ARRAY[...]
      - BuildContactFilter → contactstudentschoolauthorizationedorgids ?| ARRAY[...]
      - BuildStaffFilter → staffeducationorganizationauthorizationedorgids ?| ARRAY[...]
  - These all push conditions onto andConditions and add Npgsql parameters, and the final query is FROM dms.Document WHERE ....

  We’re going to replace all of this.

  ———

  2) New high-level behavior

  In the new model, AddAuthorizationFilters (or a renamed equivalent like AddAuthorizationPredicatesForQuery) should:

  1. Derive the caller’s authorized EdOrg IDs from the AuthorizationStrategyEvaluators (relationship strategies).
  2. Derive any namespace constraints from AuthorizationStrategyEvaluators (namespace strategy).
  3. Add a single EXISTS predicate to the WHERE clause that:
      - Starts from DocumentIndex row (alias di).
      - Joins to dms.DocumentSubject by doc key.
      - Joins to dms.SubjectEdOrg by (SubjectType, SubjectKey).
      - Requires SubjectEdOrg.EducationOrganizationId to be in the caller’s EdOrg set.
      - Optionally restricts SubjectType and Pathway based on securables/strategies.
  4. Keep namespace filtering in the QueryFields @> ... JSON (or as a separate predicate), not in this EXISTS.

  So you end up with something like:

```sql
  AND EXISTS (
      SELECT 1
      FROM dms.DocumentSubject s
      JOIN dms.SubjectEdOrg se
        ON se.SubjectType = s.SubjectType
       AND se.SubjectKey  = s.SubjectKey
      WHERE s.ProjectName          = di.ProjectName
        AND s.ResourceName         = di.ResourceName
        AND s.DocumentPartitionKey = di.DocumentPartitionKey
        AND s.DocumentId           = di.DocumentId
        -- Optional SubjectType/Pathway filters
        AND se.EducationOrganizationId = ANY($N::bigint[])
  )
```

  ———

  3) Deriving authorized_edorg_ids in C#

  Inside SqlAction, we can compute the set of EdOrg IDs allowed by relationship-based strategies using the existing AuthorizationStrategyEvaluator[]:

```c#
  private static long[] GetAuthorizedEdOrgIds(IQueryRequest queryRequest)
  {
      // Relationship-based strategies produce EducationOrganization filters
      var edOrgIds = queryRequest.AuthorizationStrategyEvaluators
          .SelectMany(e => e.Filters)
          .OfType<AuthorizationFilter.EducationOrganization>()
          .Select(f => long.Parse(f.Value))
          .Distinct()
          .ToArray();

      return edOrgIds;
  }
```

  - If edOrgIds is empty but a relationship strategy is present, this is the same error condition you already enforce in AuthorizationFiltersProviderBase.GetRelationshipFilters (it throws AuthorizationException).

  Namespace filters (AuthorizationFilter.Namespace) are used only to build the QueryFields JSON for DocumentIndex, not in AddAuthorizationFilters.

  ———

  4) Subject type / pathway selection

  We also need to know which subjects/pathways to consider for queries:

  - From ResourceInfo.AuthorizationSecurableInfo (passed into QueryRequest), we know which securable dimensions apply to this resource:
      - e.g., StudentUniqueId, ContactUniqueId, StaffUniqueId, EducationOrganization.
  - From AuthorizationStrategyEvaluators, we know which strategies are active, e.g.:
      - RelationshipsWithStudentsOnly
      - RelationshipsWithStudentsOnlyThroughResponsibility
      - RelationshipsWithEdOrgsAndPeople
      - RelationshipsWithEdOrgsOnly
      - NamespaceBased, NoFurtherAuthorizationRequired (non-relationship)

  You can map this to subject/pathway conditions for the EXISTS:

```C#
  private record SubjectPathwayFilter(SubjectType SubjectType, Pathway[] Pathways);

  private static SubjectPathwayFilter[] GetSubjectPathwayFilters(
      ResourceInfo resourceInfo,
      AuthorizationStrategyEvaluator[] evaluators)
  {
      var securables = resourceInfo.AuthorizationSecurableInfo.Select(x => x.SecurableKey).ToHashSet();

      var subjectFilters = new List<SubjectPathwayFilter>();

      bool hasStudentStrategy = evaluators.Any(e =>
          e.AuthorizationStrategyName is AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly
                                             or AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnlyThroughResponsibility
                                             or AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsAndPeople);

      if (hasStudentStrategy && securables.Contains(SecurityElementNameConstants.StudentUniqueId))
      {
          var pathways = new List<Pathway>();

          if (evaluators.Any(e => e.AuthorizationStrategyName ==
                                  AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly))
          {
              pathways.Add(Pathway.StudentSchool);
          }

          if (evaluators.Any(e => e.AuthorizationStrategyName ==
                                  AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnlyThroughResponsibility))
          {
              pathways.Add(Pathway.StudentResponsibility);
          }

          // RelationshipsWithEdOrgsAndPeople may also include students, depending on your design
          if (evaluators.Any(e => e.AuthorizationStrategyName ==
                                  AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsAndPeople))
          {
              pathways.AddRange(new[] { Pathway.StudentSchool, Pathway.StudentResponsibility });
          }

          subjectFilters.Add(new SubjectPathwayFilter(SubjectType.Student, pathways.Distinct().ToArray()));
      }

      bool hasStaffStrategy = evaluators.Any(e =>
          e.AuthorizationStrategyName == AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsAndPeople);

      if (hasStaffStrategy && securables.Contains(SecurityElementNameConstants.StaffUniqueId))
      {
          subjectFilters.Add(new SubjectPathwayFilter(
              SubjectType.Staff,
              new[] { Pathway.StaffEdOrg }));
      }

      bool hasContactStrategy = evaluators.Any(e =>
          e.AuthorizationStrategyName == AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsAndPeople);

      if (hasContactStrategy && securables.Contains(SecurityElementNameConstants.ContactUniqueId))
      {
          subjectFilters.Add(new SubjectPathwayFilter(
              SubjectType.Contact,
              new[] { Pathway.ContactStudentSchool }));
      }

      bool hasEdOrgOnlyStrategy = evaluators.Any(e =>
          e.AuthorizationStrategyName == AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly);

      if (hasEdOrgOnlyStrategy && securables.Contains(SecurityElementNameConstants.EducationOrganization))
      {
          subjectFilters.Add(new SubjectPathwayFilter(
              SubjectType.EducationOrganization,
              new[] { Pathway.EducationOrganizationDirect }));
      }

      return subjectFilters.ToArray();
  }
```

  Notes:

  - SubjectType and Pathway are your backend enums that correspond to the codes stored in SubjectEdOrg.SubjectType and SubjectEdOrg.Pathway.
  - This mapping is where you encode “which strategies use which subject/pathway pairs”.

  For an initial implementation, you can keep this mapping simple and treat relationship strategies as “OR” across their pathways (as above).

  ———

  5) New AddAuthorizationFilters implementation (for queries)

  Assuming we’re now building queries against dms.DocumentIndex with alias di:

```C#
  private void AddAuthorizationFiltersForQuery(
      IQueryRequest queryRequest,
      List<string> andConditions,
      List<NpgsqlParameter> parameters)
  {
      // 1. Determine if any relationship-based strategies are active.
      bool hasRelationshipStrategy = queryRequest.AuthorizationStrategyEvaluators.Any(e =>
          e.AuthorizationStrategyName is AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly
                                           or AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnlyThroughResponsibility
                                           or AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly
                                           or AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsAndPeople);

      if (!hasRelationshipStrategy)
      {
          // No relationship-based auth: e.g., Namespace-only or NoFurtherAuthorizationRequired.
          // Nothing to add here; namespace is handled in QueryFields filtering.
          return;
      }

      // 2. Compute caller's authorized EdOrgIds.
      long[] authorizedEdOrgIds = GetAuthorizedEdOrgIds(queryRequest);
      if (authorizedEdOrgIds.Length == 0)
      {
          // Strategies require EdOrg claims but token has none.
          throw new AuthorizationException(
              "The API client has been given permissions on a resource that uses a relationships-based authorization strategy but the client doesn't have any education organizations assigned.");
      }

      // Add the EdOrgIds array as a single parameter.
      var edOrgIdsParam = new NpgsqlParameter
      {
          Value = authorizedEdOrgIds
      };
      parameters.Add(edOrgIdsParam);
      int edOrgParamIndex = parameters.Count; // 1-based index in SQL ($edOrgParamIndex)

      // 3. Determine which subject types and pathways to check.
      var subjectFilters = GetSubjectPathwayFilters(
          queryRequest.ResourceInfo,
          queryRequest.AuthorizationSecurableInfo,
          queryRequest.AuthorizationStrategyEvaluators);

      if (subjectFilters.Length == 0)
      {
          // Fallback: if there is a relationship strategy but no securables mapped, deny by default.
          throw new AuthorizationException(
              "No securable subject types are configured for this resource's authorization strategies.");
      }

      // 4. Build EXISTS clause.
      // We'll always require that there exists at least one (subject, pathway, EdOrg) matching the caller's EdOrg set.
      // More complex AND/OR compositions can be added later if needed.
      var subjectTypeConditions = new List<string>();

      foreach (var sf in subjectFilters)
      {
          // SubjectType condition: s.SubjectType = @subjectTypeX
          var subjectTypeParam = new NpgsqlParameter { Value = (int)sf.SubjectType };
          parameters.Add(subjectTypeParam);
          int subjectTypeIndex = parameters.Count;

          string typeCond = $"s.SubjectType = ${subjectTypeIndex}";

          if (sf.Pathways.Any())
          {
              // Pathway IN (...)
              var pathwayValues = sf.Pathways.Select(p => (int)p).ToArray();
              var pathwayParam = new NpgsqlParameter { Value = pathwayValues };
              parameters.Add(pathwayParam);
              int pathwayIndex = parameters.Count;

              typeCond += $" AND se.Pathway = ANY(${pathwayIndex})";
          }

          subjectTypeConditions.Add($"({typeCond})");
      }

      string subjectTypeWhere = string.Join(" OR ", subjectTypeConditions);

      string existsSql = $@"
          EXISTS (
              SELECT 1
              FROM dms.DocumentSubject s
              JOIN dms.SubjectEdOrg se
                ON se.SubjectType = s.SubjectType
               AND se.SubjectKey  = s.SubjectKey
              WHERE s.ProjectName          = di.ProjectName
                AND s.ResourceName         = di.ResourceName
                AND s.DocumentPartitionKey = di.DocumentPartitionKey
                AND s.DocumentId           = di.DocumentId
                AND ({subjectTypeWhere})
                AND se.EducationOrganizationId = ANY(${edOrgParamIndex})
          )";

      andConditions.Add(existsSql);
  }
```

  Key points:

  - We only add this EXISTS when a relationships-based strategy is active.
  - We aggregate EdOrg IDs once and use ANY($N) inside the EXISTS.
  - We generate OR’d subject-type/pathway branches inside the EXISTS, so a document is visible if it is reachable through any applicable subject/pathway for which the caller has a matching EdOrg membership.
  - Namespace filters are not handled here—they stay in the QueryFields @> $json filter.

  ———

  6) Integrating into the new DocumentIndex-based query

  With the new query layout (DocumentIndex + EXISTS), GetAllDocumentsByResourceNameAsync would look conceptually like:

```C#
  public async Task<JsonArray> GetAllDocumentsByResourceNameAsync(
      string resourceName,
      IQueryRequest queryRequest,
      NpgsqlConnection connection,
      NpgsqlTransaction transaction,
      TraceId traceId,
      CancellationToken cancellationToken)
  {
      var andConditions = new List<string>
      {
          "di.ProjectName = $1",
          "di.ResourceName = $2"
      };
      var parameters = new List<NpgsqlParameter>
      {
          new() { Value = queryRequest.ResourceInfo.ProjectName.Value },
          new() { Value = resourceName }
      };

      // Build QueryFields JSON filter and add as AND condition:
      AddQueryFieldsFilter(queryRequest.QueryElements, andConditions, parameters);

      // Add authorization EXISTS over DocumentSubject + SubjectEdOrg:
      AddAuthorizationFiltersForQuery(queryRequest, andConditions, parameters);

      string where = string.Join(" AND ", andConditions);

      // OFFSET / LIMIT params:
      parameters.Add(new NpgsqlParameter { Value = queryRequest.PaginationParameters.Offset ?? 0 });
      int offsetIndex = parameters.Count;
      parameters.Add(new NpgsqlParameter
      {
          Value = queryRequest.PaginationParameters.Limit
                  ?? queryRequest.PaginationParameters.MaximumPageSize
      });
      int limitIndex = parameters.Count;

      string sql = $@"
          WITH page AS (
              SELECT di.DocumentPartitionKey, di.DocumentId, di.ProjectName, di.ResourceName, di.CreatedAt
              FROM dms.DocumentIndex di
              WHERE {where}
              ORDER BY di.CreatedAt
              OFFSET ${offsetIndex}
              LIMIT  ${limitIndex}
          )
          SELECT d.EdfiDoc
          FROM page p
          JOIN dms.Document d
            ON d.DocumentPartitionKey = p.DocumentPartitionKey
           AND d.Id                   = p.DocumentId
          ORDER BY p.CreatedAt;
      ";

      await using var command = new NpgsqlCommand(sql, connection, transaction);
      command.Parameters.AddRange(parameters.ToArray());

      // Execute and read docs as today...
  }
```

  And GetTotalDocumentsForResourceName uses the same where (including EXISTS) against DocumentIndex without the CTE/offset/limit.

  ———

  7) Summary

  The redesigned AddAuthorizationFilters (for queries):

  - Inputs:
      - AuthorizationStrategyEvaluators (from claimset + strategy providers).
      - AuthorizationSecurableInfo / ResourceInfo (what subjects the resource is securable on).
  - Outputs:
      - A single EXISTS( ... ) predicate against DocumentSubject + SubjectEdOrg driven by:
          - Caller’s EdOrgIds (AuthorizationFilter.EducationOrganization).
          - Subject types & pathways implied by strategies and securables.
  - Drops all use of:
      - JSONB arrays on dms.Document (*AuthorizationEdOrgIds).
      - EducationOrganizationHierarchyTermsLookup and SecurityElements JSON‑field auth predicates.

  This keeps the strategy/evaluator architecture intact but redirects the actual filtering to the new relational auth tables and the DocumentIndex query plan.
