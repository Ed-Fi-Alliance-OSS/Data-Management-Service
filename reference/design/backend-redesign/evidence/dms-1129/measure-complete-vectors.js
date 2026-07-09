#!/usr/bin/env node

// DMS-1129 complete-vector feasibility measurement.
//
// Usage from any directory:
//   node reference/design/backend-redesign/evidence/dms-1129/measure-complete-vectors.js
//   node reference/design/backend-redesign/evidence/dms-1129/measure-complete-vectors.js --write
//   node reference/design/backend-redesign/evidence/dms-1129/measure-complete-vectors.js --print

const fs = require("node:fs");
const path = require("node:path");

const evidenceDirectory = __dirname;
const repositoryRoot = path.resolve(evidenceDirectory, "../../../../..");
const summaryPath = path.join(evidenceDirectory, "complete-vector-feasibility-summary.json");

const fixtureConfigurations = [
  {
    name: "ds-5.2",
    label: "Data Standard 5.2",
    apiSchemaFiles: [
      "src/dms/backend/Fixtures/authoritative/ds-5.2/inputs/ds-5.2-api-schema-authoritative.json",
    ],
    manifest:
      "src/dms/backend/Fixtures/authoritative/ds-5.2/expected/relational-model.mssql.manifest.json",
    mssqlDdl: "src/dms/backend/Fixtures/authoritative/ds-5.2/expected/mssql.sql",
  },
  {
    name: "ds-5.2-tpdm",
    label: "Data Standard 5.2 with TPDM",
    apiSchemaFiles: [
      "src/dms/backend/Fixtures/authoritative/ds-5.2/inputs/ds-5.2-api-schema-authoritative.json",
      "src/dms/backend/Fixtures/authoritative/ds-5.2-tpdm/inputs/tpdm-api-schema-authoritative.json",
    ],
    manifest:
      "src/dms/backend/Fixtures/authoritative/ds-5.2-tpdm/expected/relational-model.mssql.manifest.json",
    mssqlDdl: "src/dms/backend/Fixtures/authoritative/ds-5.2-tpdm/expected/mssql.sql",
  },
];

function readRepositoryFile(relativePath) {
  return fs.readFileSync(path.join(repositoryRoot, relativePath), "utf8");
}

function qualifiedResourceKey(resource) {
  return `${resource.project_name}|${resource.resource_name}`;
}

function qualifiedTableKey(table) {
  return `${table.schema}|${table.name}`;
}

function clone(value) {
  return JSON.parse(JSON.stringify(value));
}

function compareText(left, right) {
  if (left < right) return -1;
  if (left > right) return 1;
  return 0;
}

function scalarPayloadBytes(type, dialect, pgsqlStringMode = "ascii") {
  switch (type.kind) {
    case "Boolean":
      return 1;
    case "Int16":
      return 2;
    case "Int32":
      return 4;
    case "Int64":
      return 8;
    case "Date":
      return dialect === "mssql" ? 3 : 4;
    case "Time":
      return dialect === "mssql" ? 5 : 8;
    case "DateTime":
      return 8;
    case "Guid":
      return 16;
    case "Decimal": {
      const precision = type.precision ?? 18;

      if (dialect === "mssql") {
        if (precision <= 9) return 5;
        if (precision <= 19) return 9;
        if (precision <= 28) return 13;
        return 17;
      }

      // PostgreSQL numeric is variable length. This is a deterministic payload estimate,
      // not a storage-engine limit calculation.
      return Math.ceil(precision / 4) * 2 + 8;
    }
    case "String": {
      if (type.max_length == null) {
        throw new Error("An unbounded string cannot participate in a measured propagation key.");
      }

      if (dialect === "mssql") {
        return type.max_length * 2;
      }

      return type.max_length * (pgsqlStringMode === "utf8-max" ? 4 : 1);
    }
    default:
      throw new Error(`Unsupported scalar kind '${type.kind}'.`);
  }
}

function measureFixture(configuration) {
  const manifestText = readRepositoryFile(configuration.manifest);
  const manifest = JSON.parse(manifestText);
  const projectSchemas = configuration.apiSchemaFiles.map(
    apiSchemaFile => JSON.parse(readRepositoryFile(apiSchemaFile)).projectSchema,
  );

  const resourceDetailsByKey = new Map(
    manifest.resource_details.map(detail => [qualifiedResourceKey(detail.resource), detail]),
  );
  const projectSchemasByName = new Map(
    projectSchemas.map(projectSchema => [projectSchema.projectName, projectSchema]),
  );
  const targetTablesByResource = new Map();
  const tablesByKey = new Map();

  for (const detail of manifest.resource_details) {
    const rootTable = detail.tables.find(table => table.scope === "$");

    if (rootTable != null) {
      targetTablesByResource.set(qualifiedResourceKey(detail.resource), rootTable);
    }

    for (const table of detail.tables) {
      tablesByKey.set(qualifiedTableKey(table), table);
    }
  }

  for (const abstractIdentity of manifest.abstract_identity_tables) {
    targetTablesByResource.set(
      qualifiedResourceKey(abstractIdentity.resource),
      abstractIdentity.table,
    );
    tablesByKey.set(qualifiedTableKey(abstractIdentity.table), abstractIdentity.table);
  }

  const allReferenceSites = manifest.resource_details.flatMap(detail =>
    detail.document_reference_bindings.map(binding => ({ detail, binding })),
  );

  function tableColumn(table, columnName) {
    const result = table.columns.find(column => column.name === columnName);

    if (result == null) {
      throw new Error(`Column '${qualifiedTableKey(table)}.${columnName}' was not found.`);
    }

    return result;
  }

  function storageColumnName(table, columnName) {
    const column = tableColumn(table, columnName);
    return column.storage.kind === "UnifiedAlias"
      ? column.storage.canonical_column
      : columnName;
  }

  function referenceIsNullable(binding) {
    return tableColumn(tablesByKey.get(qualifiedTableKey(binding.table)), binding.fk_column)
      .is_nullable;
  }

  function propagationKeyConstraint(targetResourceKey) {
    const targetTable = targetTablesByResource.get(targetResourceKey);

    return targetTable?.constraints.find(
      constraint =>
        constraint.kind === "Unique" &&
        constraint.columns.at(-1) === "DocumentId",
    );
  }

  const anchorsByTarget = new Map();

  // Concrete target: each root-table identity-contributing document reference is one atomic
  // independently replaceable lineage. Its existing local DocumentId is the target anchor.
  for (const detail of manifest.resource_details) {
    const targetKey = qualifiedResourceKey(detail.resource);
    const rootTable = targetTablesByResource.get(targetKey);
    const anchors = detail.document_reference_bindings
      .filter(
        binding =>
          binding.is_identity_component &&
          qualifiedTableKey(binding.table) === qualifiedTableKey(rootTable),
      )
      .map(binding => ({
        targetResource: binding.target_resource,
        referenceObjectPath: binding.reference_object_path,
        targetColumn: binding.fk_column,
        identityBindings: binding.identity_bindings,
        targetStorageAlreadyExists: true,
      }));

    anchorsByTarget.set(targetKey, anchors);
  }

  // Abstract target: normalize the identity-contributing reference lineages shared by every
  // concrete member. The current authoritative model has the public abstract identity columns,
  // but these anchor columns are additions under the complete-vector proposal.
  for (const abstractIdentity of manifest.abstract_identity_tables) {
    const abstractKey = qualifiedResourceKey(abstractIdentity.resource);
    const abstractProject = projectSchemasByName.get(abstractIdentity.resource.project_name);
    const abstractResource = abstractProject?.abstractResources?.[
      abstractIdentity.resource.resource_name
    ];

    if (abstractResource == null) {
      throw new Error(`ApiSchema metadata for abstract resource '${abstractKey}' was not found.`);
    }

    const abstractIdentityPaths = new Set(abstractResource.identityJsonPaths);
    const members = [];

    for (const projectSchema of projectSchemas) {
      for (const resourceSchema of Object.values(projectSchema.resourceSchemas ?? {})) {
        if (
          resourceSchema.isSubclass === true &&
          resourceSchema.superclassProjectName === abstractIdentity.resource.project_name &&
          resourceSchema.superclassResourceName === abstractIdentity.resource.resource_name
        ) {
          const memberKey = `${projectSchema.projectName}|${resourceSchema.resourceName}`;
          const memberDetail = resourceDetailsByKey.get(memberKey);

          if (memberDetail == null) {
            throw new Error(`Relational model metadata for abstract member '${memberKey}' was not found.`);
          }

          members.push(memberDetail);
        }
      }
    }

    if (members.length === 0) {
      throw new Error(`Abstract resource '${abstractKey}' has no concrete members.`);
    }

    const normalizedBySignature = new Map();

    for (const [memberIndex, member] of members.entries()) {
      const memberRoot = targetTablesByResource.get(qualifiedResourceKey(member.resource));
      const memberLineages = member.document_reference_bindings.filter(
        binding =>
          binding.is_identity_component &&
          qualifiedTableKey(binding.table) === qualifiedTableKey(memberRoot) &&
          binding.identity_bindings.some(identity =>
            abstractIdentityPaths.has(identity.reference_json_path),
          ),
      );
      const signaturesInMember = new Set();

      for (const binding of memberLineages) {
        const normalizedBindings = binding.identity_bindings
          .filter(identity => abstractIdentityPaths.has(identity.reference_json_path))
          .map(identity => ({
            identityJsonPath: identity.identity_json_path,
            referenceJsonPath: identity.reference_json_path,
          }))
          .sort((left, right) =>
            compareText(left.referenceJsonPath, right.referenceJsonPath),
          );
        const signature = JSON.stringify({
          targetResource: binding.target_resource,
          identityBindings: normalizedBindings,
        });
        signaturesInMember.add(signature);

        if (memberIndex === 0) {
          const referenceBaseName = binding.reference_object_path
            .split(".")
            .at(-1)
            .replace(/Reference$/, "");
          normalizedBySignature.set(signature, {
            targetResource: binding.target_resource,
            referenceObjectPath: binding.reference_object_path,
            targetColumn: `${referenceBaseName[0].toUpperCase()}${referenceBaseName.slice(1)}_DocumentId`,
            identityBindings: binding.identity_bindings,
            targetStorageAlreadyExists: false,
          });
        }
      }

      if (memberIndex > 0) {
        for (const signature of normalizedBySignature.keys()) {
          if (!signaturesInMember.has(signature)) {
            throw new Error(
              `Abstract member '${qualifiedResourceKey(member.resource)}' cannot supply normalized lineage '${signature}'.`,
            );
          }
        }
      }
    }

    anchorsByTarget.set(
      abstractKey,
      [...normalizedBySignature.values()].sort((left, right) =>
        compareText(left.targetColumn, right.targetColumn),
      ),
    );
  }

  const incomingSitesByTarget = new Map();

  for (const site of allReferenceSites) {
    const targetKey = qualifiedResourceKey(site.binding.target_resource);
    const sites = incomingSitesByTarget.get(targetKey) ?? [];
    sites.push(site);
    incomingSitesByTarget.set(targetKey, sites);
  }

  const vectorMeasurements = [];

  for (const [targetKey, incomingSites] of incomingSitesByTarget) {
    const targetTable = targetTablesByResource.get(targetKey);
    const propagationKey = propagationKeyConstraint(targetKey);

    if (targetTable == null || propagationKey == null) {
      throw new Error(`Referenced target '${targetKey}' has no propagation key.`);
    }

    const anchors = anchorsByTarget.get(targetKey) ?? [];
    const vectorColumns = [
      ...propagationKey.columns.slice(0, -1),
      ...anchors.map(anchor => anchor.targetColumn),
      "DocumentId",
    ];

    // Abstract anchor columns are not in the current manifest, but they are always BIGINT.
    const columnType = columnName =>
      anchors.some(
        anchor =>
          !anchor.targetStorageAlreadyExists && anchor.targetColumn === columnName,
      )
        ? { kind: "Int64" }
        : tableColumn(targetTable, columnName).type;
    const bytes = (dialect, stringMode) =>
      vectorColumns.reduce(
        (sum, columnName) =>
          sum + scalarPayloadBytes(columnType(columnName), dialect, stringMode),
        0,
      );
    const baselineMssqlBytes = propagationKey.columns.reduce(
      (sum, columnName) =>
        sum + scalarPayloadBytes(tableColumn(targetTable, columnName).type, "mssql"),
      0,
    );

    vectorMeasurements.push({
      target: targetKey,
      incomingSiteCount: incomingSites.length,
      publicIdentityColumnCount: propagationKey.columns.length - 1,
      anchorCount: anchors.length,
      vectorColumnCount: vectorColumns.length,
      vectorColumns,
      baselineMssqlBytes,
      mssqlBytes: bytes("mssql"),
      pgsqlAsciiBytes: bytes("pgsql", "ascii"),
      pgsqlUtf8MaxBytes: bytes("pgsql", "utf8-max"),
    });
  }

  function reusableLocalAnchor(site, anchor) {
    const receiverTable = tablesByKey.get(qualifiedTableKey(site.binding.table));
    const targetPathToLocalStorage = new Map(
      site.binding.identity_bindings.map(identity => [
        identity.identity_json_path,
        storageColumnName(receiverTable, identity.column),
      ]),
    );
    const requiredStorageByAnchorIdentityPath = new Map();

    for (const identity of anchor.identityBindings) {
      const localStorage = targetPathToLocalStorage.get(identity.reference_json_path);

      if (localStorage == null) {
        return null;
      }

      requiredStorageByAnchorIdentityPath.set(identity.identity_json_path, localStorage);
    }

    const candidates = site.detail.document_reference_bindings.filter(
      candidate =>
        qualifiedTableKey(candidate.table) === qualifiedTableKey(site.binding.table) &&
        qualifiedResourceKey(candidate.target_resource) ===
          qualifiedResourceKey(anchor.targetResource),
    );

    for (const candidate of candidates) {
      const candidateStorageByTargetPath = new Map(
        candidate.identity_bindings.map(identity => [
          identity.identity_json_path,
          storageColumnName(receiverTable, identity.column),
        ]),
      );

      if (
        !referenceIsNullable(site.binding) &&
        !referenceIsNullable(candidate) &&
        [...requiredStorageByAnchorIdentityPath].every(
          ([identityPath, requiredStorage]) =>
            candidateStorageByTargetPath.get(identityPath) === requiredStorage,
        )
      ) {
        return candidate.fk_column;
      }
    }

    return null;
  }

  const receiverGrowthDedicated = new Map();
  const reuseSites = [];
  let dedicatedReceiverAnchorColumns = 0;
  let safelyReusableReceiverAnchors = 0;

  for (const site of allReferenceSites) {
    const targetKey = qualifiedResourceKey(site.binding.target_resource);
    const anchors = anchorsByTarget.get(targetKey) ?? [];
    const reuseColumns = anchors
      .map(anchor => reusableLocalAnchor(site, anchor))
      .filter(columnName => columnName != null);
    const receiverTableKey = qualifiedTableKey(site.binding.table);

    dedicatedReceiverAnchorColumns += anchors.length;
    safelyReusableReceiverAnchors += reuseColumns.length;
    receiverGrowthDedicated.set(
      receiverTableKey,
      (receiverGrowthDedicated.get(receiverTableKey) ?? 0) + anchors.length,
    );
    if (reuseColumns.length > 0) {
      reuseSites.push({
        owner: qualifiedResourceKey(site.detail.resource),
        reference_object_path: site.binding.reference_object_path,
        receiver_table: receiverTableKey,
        target: targetKey,
        reused_columns: reuseColumns.sort(compareText),
      });
    }
  }

  const targetAnchorColumnsAdded = [...anchorsByTarget.values()]
    .flat()
    .filter(anchor => !anchor.targetStorageAlreadyExists).length;
  const targetGrowthByTable = new Map();

  for (const [targetKey, anchors] of anchorsByTarget) {
    const addedColumns = anchors.filter(anchor => !anchor.targetStorageAlreadyExists).length;

    if (addedColumns > 0) {
      const targetTableKey = qualifiedTableKey(targetTablesByResource.get(targetKey));
      targetGrowthByTable.set(
        targetTableKey,
        (targetGrowthByTable.get(targetTableKey) ?? 0) + addedColumns,
      );
    }
  }

  const tableGrowth = [...tablesByKey]
    .map(([tableKey, table]) => {
      const addedColumns =
        (receiverGrowthDedicated.get(tableKey) ?? 0) +
        (targetGrowthByTable.get(tableKey) ?? 0);

      return {
        table: tableKey,
        baseline_column_count: table.columns.length,
        added_anchor_columns: addedColumns,
        added_fixed_bytes: addedColumns * 8,
        final_column_count: table.columns.length + addedColumns,
      };
    })
    .filter(table => table.added_anchor_columns > 0);
  const maxTableGrowth = tableGrowth.sort(
    (left, right) =>
      right.added_anchor_columns - left.added_anchor_columns ||
      compareText(left.table, right.table),
  )[0];
  const maxFinalTableColumnCount = Math.max(
    ...[...tablesByKey].map(
      ([tableKey, table]) =>
        table.columns.length +
        (receiverGrowthDedicated.get(tableKey) ?? 0) +
        (targetGrowthByTable.get(tableKey) ?? 0),
    ),
  );

  // Project only minimal generic table/column/constraint/index payload changes. Solver proofs,
  // stable hash protocols, and generalized deferred-reference plans are intentionally absent.
  const syntheticManifest = clone(manifest);
  const syntheticTablesByKey = new Map();

  for (const detail of syntheticManifest.resource_details) {
    for (const table of detail.tables) {
      syntheticTablesByKey.set(qualifiedTableKey(table), table);
    }
  }

  for (const abstractIdentity of syntheticManifest.abstract_identity_tables) {
    syntheticTablesByKey.set(
      qualifiedTableKey(abstractIdentity.table),
      abstractIdentity.table,
    );
  }

  const usedColumnNamesByTable = new Map(
    [...syntheticTablesByKey].map(([tableKey, table]) => [
      tableKey,
      new Set(table.columns.map(column => column.name)),
    ]),
  );
  for (const [targetKey, anchors] of anchorsByTarget) {
    if (anchors.length === 0) {
      continue;
    }

    const targetTable = syntheticTablesByKey.get(
      qualifiedTableKey(targetTablesByResource.get(targetKey)),
    );
    for (const anchor of anchors.filter(item => !item.targetStorageAlreadyExists)) {
      targetTable.columns.push({
        name: anchor.targetColumn,
        kind: "IdentityLineageAnchor",
        type: { kind: "Int64" },
        is_nullable: false,
        source_path: null,
        storage: { kind: "Stored" },
      });
      usedColumnNamesByTable.get(qualifiedTableKey(targetTable)).add(anchor.targetColumn);
    }
  }

  function allocateReceiverAnchorName(receiverTableKey, binding, anchor, ordinal) {
    const usedNames = usedColumnNamesByTable.get(receiverTableKey);
    const referencePrefix = binding.fk_column.replace(/_DocumentId$/, "");
    const lineagePrefix = anchor.targetColumn.replace(/_DocumentId$/, "");
    const baseName = `${referencePrefix}_${lineagePrefix}_AnchorDocumentId`;
    let candidate = baseName;
    let suffix = ordinal;

    while (usedNames.has(candidate)) {
      suffix += 1;
      candidate = `${baseName}_${suffix}`;
    }

    usedNames.add(candidate);
    return candidate;
  }

  for (const site of allReferenceSites) {
    const targetKey = qualifiedResourceKey(site.binding.target_resource);
    const anchors = anchorsByTarget.get(targetKey) ?? [];
    const receiverTableKey = qualifiedTableKey(site.binding.table);
    const receiverTable = syntheticTablesByKey.get(receiverTableKey);
    const localPublicStorageColumns = [];

    for (const identity of site.binding.identity_bindings) {
      const storageName = storageColumnName(
        tablesByKey.get(receiverTableKey),
        identity.column,
      );

      if (!localPublicStorageColumns.includes(storageName)) {
        localPublicStorageColumns.push(storageName);
      }
    }

    const localAnchorNames = anchors.map((anchor, ordinal) => {
      const name = allocateReceiverAnchorName(
        receiverTableKey,
        site.binding,
        anchor,
        ordinal,
      );
      receiverTable.columns.push({
        name,
        kind: "IdentityLineageAnchor",
        type: { kind: "Int64" },
        is_nullable: referenceIsNullable(site.binding),
        source_path: null,
        storage: { kind: "Stored" },
      });
      return name;
    });
    const localCompleteVector = [
      ...localPublicStorageColumns,
      ...localAnchorNames,
      site.binding.fk_column,
    ];
    const targetVector = vectorMeasurements.find(vector => vector.target === targetKey)
      .vectorColumns;
    const targetTableKey = qualifiedTableKey(targetTablesByResource.get(targetKey));
    const foreignKeys = receiverTable.constraints.filter(
      constraint =>
        constraint.kind === "ForeignKey" &&
        constraint.columns.includes(site.binding.fk_column) &&
        qualifiedTableKey(constraint.target_table) === targetTableKey,
    );

    if (foreignKeys.length !== 1) {
      throw new Error(
        `Expected one FK for '${receiverTableKey}:${site.binding.reference_object_path}', found ${foreignKeys.length}.`,
      );
    }

    foreignKeys[0].columns = localCompleteVector;
    foreignKeys[0].target_columns = targetVector;

    const allOrNone = receiverTable.constraints.find(
      constraint =>
        constraint.kind === "AllOrNoneNullability" &&
        constraint.fk_column === site.binding.fk_column,
    );

    if (allOrNone != null) {
      allOrNone.dependent_columns.push(...localAnchorNames);
    }

    const supportIndexes = syntheticManifest.indexes.filter(
      index =>
        qualifiedTableKey(index.table) === receiverTableKey &&
        index.kind === "ForeignKeySupport" &&
        index.key_columns.includes(site.binding.fk_column),
    );

    if (supportIndexes.length === 1) {
      supportIndexes[0].key_columns = localCompleteVector;
    }
  }

  for (const vector of vectorMeasurements.filter(item => item.anchorCount > 0)) {
    const targetTable = syntheticTablesByKey.get(
      qualifiedTableKey(targetTablesByResource.get(vector.target)),
    );
    const propagationKey = targetTable.constraints.find(
      constraint =>
        constraint.kind === "Unique" && constraint.columns.at(-1) === "DocumentId",
    );
    propagationKey.columns = vector.vectorColumns;
  }

  const syntheticManifestText = `${JSON.stringify(syntheticManifest, null, 2)}\n`;
  const anchorBearingVectors = vectorMeasurements.filter(vector => vector.anchorCount > 0);
  const widestByColumns = vectorMeasurements
    .slice()
    .sort(
      (left, right) =>
        right.vectorColumnCount - left.vectorColumnCount ||
        compareText(left.target, right.target),
    )[0];
  const widestByMssqlBytes = vectorMeasurements
    .slice()
    .sort(
      (left, right) =>
        right.mssqlBytes - left.mssqlBytes || compareText(left.target, right.target),
    )[0];
  const mssqlOver900 = vectorMeasurements
    .filter(vector => vector.mssqlBytes > 900)
    .sort((left, right) => compareText(left.target, right.target))
    .map(vector => ({
      target: vector.target,
      baseline_declared_bytes: vector.baselineMssqlBytes,
      complete_vector_declared_bytes: vector.mssqlBytes,
      vector_column_count: vector.vectorColumnCount,
    }));
  const newlyCrossesMssql900 = mssqlOver900.filter(
    vector => vector.baseline_declared_bytes <= 900,
  );
  const roughDdlDelta =
    (dedicatedReceiverAnchorColumns + targetAnchorColumnsAdded) * 280 +
    anchorBearingVectors.reduce(
      (sum, vector) => sum + vector.anchorCount * vector.incomingSiteCount * 40,
      0,
    );

  return {
    fixture: configuration.name,
    label: configuration.label,
    inputs: configuration.apiSchemaFiles,
    baseline: {
      concrete_resources: manifest.resource_details.length,
      abstract_identity_tables: manifest.abstract_identity_tables.length,
      physical_tables: tablesByKey.size,
      document_reference_sites: allReferenceSites.length,
      relational_manifest_bytes: Buffer.byteLength(manifestText),
      mssql_ddl_bytes: fs.statSync(path.join(repositoryRoot, configuration.mssqlDdl)).size,
    },
    complete_vectors: {
      referenced_targets: vectorMeasurements.length,
      anchor_bearing_targets: anchorBearingVectors.length,
      maximum_intrinsic_anchors: Math.max(
        ...vectorMeasurements.map(vector => vector.anchorCount),
      ),
      maximum_vector_columns: widestByColumns.vectorColumnCount,
      widest_by_columns: {
        target: widestByColumns.target,
        public_identity_columns: widestByColumns.publicIdentityColumnCount,
        anchors: widestByColumns.anchorCount,
        total_columns: widestByColumns.vectorColumnCount,
      },
      maximum_mssql_declared_bytes: widestByMssqlBytes.mssqlBytes,
      widest_by_mssql_declared_bytes: {
        target: widestByMssqlBytes.target,
        baseline_declared_bytes: widestByMssqlBytes.baselineMssqlBytes,
        anchors: widestByMssqlBytes.anchorCount,
        complete_vector_declared_bytes: widestByMssqlBytes.mssqlBytes,
        total_columns: widestByMssqlBytes.vectorColumnCount,
      },
      maximum_pgsql_ascii_payload_bytes: Math.max(
        ...vectorMeasurements.map(vector => vector.pgsqlAsciiBytes),
      ),
      maximum_pgsql_four_byte_utf8_payload_bytes: Math.max(
        ...vectorMeasurements.map(vector => vector.pgsqlUtf8MaxBytes),
      ),
    },
    anchor_storage: {
      target_anchor_columns_added: targetAnchorColumnsAdded,
      dedicated_receiver_anchor_columns_added: dedicatedReceiverAnchorColumns,
      total_anchor_columns_added_conservative:
        targetAnchorColumnsAdded + dedicatedReceiverAnchorColumns,
      obvious_safe_receiver_reuses: safelyReusableReceiverAnchors,
      total_anchor_columns_added_after_obvious_reuse:
        targetAnchorColumnsAdded +
        dedicatedReceiverAnchorColumns -
        safelyReusableReceiverAnchors,
      maximum_table_growth_conservative: maxTableGrowth,
      maximum_final_table_column_count_conservative: maxFinalTableColumnCount,
      reuse_sites: reuseSites.sort((left, right) =>
        compareText(
          `${left.owner}|${left.reference_object_path}`,
          `${right.owner}|${right.reference_object_path}`,
        ),
      ),
    },
    constraints: {
      full_composite_reference_fks: allReferenceSites.length,
      anchor_bearing_reference_fks: allReferenceSites.filter(
        site =>
          (anchorsByTarget.get(qualifiedResourceKey(site.binding.target_resource)) ?? [])
            .length > 0,
      ).length,
      propagation_unique_constraints: vectorMeasurements.length,
      widened_existing_propagation_unique_constraints: anchorBearingVectors.length,
      additional_propagation_unique_constraints_over_one_per_target: 0,
    },
    provider_limit_screen: {
      vector_column_limit_exceedances: vectorMeasurements
        .filter(vector => vector.vectorColumnCount > 32)
        .map(vector => vector.target),
      table_column_limit_exceedances: [...tablesByKey]
        .filter(
          ([tableKey, table]) =>
            table.columns.length +
              (receiverGrowthDedicated.get(tableKey) ?? 0) +
              (targetGrowthByTable.get(tableKey) ?? 0) >
            1024,
        )
        .map(([tableKey]) => tableKey),
      mssql_vectors_over_900_declared_bytes: mssqlOver900,
      mssql_vectors_newly_crossing_900_due_to_anchors: newlyCrossesMssql900,
      mssql_vectors_over_1700_declared_bytes: vectorMeasurements
        .filter(vector => vector.mssqlBytes > 1700)
        .map(vector => vector.target),
      pgsql_vectors_over_2704_ascii_payload_bytes: vectorMeasurements
        .filter(vector => vector.pgsqlAsciiBytes > 2704)
        .map(vector => vector.target),
      pgsql_vectors_over_2704_four_byte_utf8_payload_bytes: vectorMeasurements
        .filter(vector => vector.pgsqlUtf8MaxBytes > 2704)
        .map(vector => vector.target),
      full_inventory_fails_where_minimal_demand_would_fit: [],
    },
    artifact_projection: {
      relational_manifest_delta_bytes: Buffer.byteLength(syntheticManifestText) -
        Buffer.byteLength(manifestText),
      relational_manifest_delta_percent: Number(
        (
          ((Buffer.byteLength(syntheticManifestText) - Buffer.byteLength(manifestText)) /
            Buffer.byteLength(manifestText)) *
          100
        ).toFixed(2),
      ),
      mssql_ddl_delta_bytes_rough: roughDdlDelta,
      mssql_ddl_delta_percent_rough: Number(
        (
          (roughDdlDelta /
            fs.statSync(path.join(repositoryRoot, configuration.mssqlDdl)).size) *
          100
        ).toFixed(2),
      ),
      mapping_pack_delta: "unavailable-not-implemented",
    },
  };
}

const summary = {
  schema_version: 1,
  generated_by:
    "reference/design/backend-redesign/evidence/dms-1129/measure-complete-vectors.js",
  assumptions: {
    intrinsic_lineage:
      "One root identity-contributing document reference is one independently replaceable lineage. Abstract lineages are normalized across every concrete member. Descriptor references are excluded.",
    propagation_vector:
      "Target public RefKey storage columns, every intrinsic lineage DocumentId anchor, then target DocumentId.",
    incoming_site_storage:
      "The conservative result allocates one BIGINT per incoming site and lineage. A sensitivity result reuses a required same-table direct-reference DocumentId only when every correlated canonical public column is identical.",
    mssql_strings: "nvarchar(n), two bytes per declared character.",
    pgsql_strings:
      "Both ASCII and four-byte UTF-8 payload bounds are reported; B-tree tuple overhead and compression are not modeled.",
    artifact_projection:
      "Adds only generic table columns and expanded FK, propagation-key, all-or-none, and FK-index arrays. It adds no proof, hash, solver, or generalized deferred-resolution protocol.",
  },
  provider_limits_used_for_screening: {
    mssql_columns_per_foreign_key: 32,
    mssql_documented_bytes_per_foreign_key_screen: 900,
    mssql_bytes_per_nonclustered_unique_key: 1700,
    mssql_columns_per_table: 1024,
    pgsql_columns_per_index_default_build: 32,
    pgsql_btree_payload_screen_bytes_default_8k_page: 2704,
  },
  implementation_status: {
    mapping_pack:
      "MappingPackPayload is an empty placeholder and MappingSet.FromPayload throws NotSupportedException; no .mpack size can be measured.",
  },
  fixtures: fixtureConfigurations.map(measureFixture),
};

const summaryText = `${JSON.stringify(summary, null, 2)}\n`;
const mode = process.argv[2] ?? "--check";

if (mode === "--write") {
  fs.writeFileSync(summaryPath, summaryText, "utf8");
  process.stdout.write(`Wrote ${path.relative(repositoryRoot, summaryPath)}\n`);
} else if (mode === "--print") {
  process.stdout.write(summaryText);
} else if (mode === "--check") {
  const existing = fs.readFileSync(summaryPath, "utf8");

  if (existing !== summaryText) {
    process.stderr.write(
      `Summary is stale. Run: node ${path.relative(repositoryRoot, __filename)} --write\n`,
    );
    process.exitCode = 1;
  } else {
    process.stdout.write(
      `Verified ${path.relative(repositoryRoot, summaryPath)}\n`,
    );
  }
} else {
  throw new Error(`Unknown option '${mode}'. Use --check, --write, or --print.`);
}
