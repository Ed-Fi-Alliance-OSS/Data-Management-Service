# DMS-916 Bootstrap - Responsibility Inventory

## 1. Purpose

This file is a supporting coverage map only. The authoritative phase sequence, ownership boundaries,
parameter ownership, selector rules, and wrapper contract live in
[`command-boundaries.md`](command-boundaries.md). If this file conflicts with `command-boundaries.md`,
`command-boundaries.md` wins.

## 2. Phase Contract Reference

| Phase command | Normative boundary | Primary story coverage |
|---|---|---|
| `prepare-dms-schema.ps1` | [`command-boundaries.md` Section 3.1](command-boundaries.md#31-prepare-dms-schemaps1--schema-selection-and-staging) | Story 00; Story 06 |
| `prepare-dms-claims.ps1` | [`command-boundaries.md` Section 3.2](command-boundaries.md#32-prepare-dms-claimsps1--claims-and-security-staging) | Story 00 |
| `start-local-dms.ps1` | [`command-boundaries.md` Section 3.3](command-boundaries.md#33-start-local-dmsps1--infrastructure-lifecycle) | Story 03 |
| `configure-local-dms-instance.ps1` | [`command-boundaries.md` Section 3.4](command-boundaries.md#34-configure-local-dms-instanceps1--instance-setup) | Story 03 |
| `provision-dms-schema.ps1` | [`command-boundaries.md` Section 3.5](command-boundaries.md#35-provision-dms-schemaps1--authoritative-schema-provisioning) | Story 01 |
| `load-dms-seed-data.ps1` | [`command-boundaries.md` Section 3.6](command-boundaries.md#36-load-dms-seed-dataps1--seed-delivery) | Story 02 |
| `bootstrap-local-dms.ps1` | [`command-boundaries.md` Section 3.7](command-boundaries.md#37-bootstrap-local-dmsps1--thin-convenience-wrapper-optional) | Story 03 |

## 3. Acceptance Coverage

| DMS-916 acceptance area | Story-specific acceptance criteria | Command-boundary owner |
|---|---|---|
| ApiSchema.json selection | [`../../epics/16-bootstrap/00-schema-and-security-selection.md`](../../epics/16-bootstrap/00-schema-and-security-selection.md), [`../../epics/16-bootstrap/06-package-backed-standard-schema-selection.md`](../../epics/16-bootstrap/06-package-backed-standard-schema-selection.md) | `prepare-dms-schema.ps1` |
| Security database configuration from ApiSchema.json | [`../../epics/16-bootstrap/00-schema-and-security-selection.md`](../../epics/16-bootstrap/00-schema-and-security-selection.md) | `prepare-dms-claims.ps1`, then `start-local-dms.ps1` for CMS startup readiness |
| Database schema provisioning | [`../../epics/16-bootstrap/01-schema-deployment-safety.md`](../../epics/16-bootstrap/01-schema-deployment-safety.md) | `provision-dms-schema.ps1` |
| API-based sample data loading | [`../../epics/16-bootstrap/02-api-seed-delivery.md`](../../epics/16-bootstrap/02-api-seed-delivery.md) | `load-dms-seed-data.ps1` |
| Extension selection and loading | [`../../epics/16-bootstrap/00-schema-and-security-selection.md`](../../epics/16-bootstrap/00-schema-and-security-selection.md), [`../../epics/16-bootstrap/02-api-seed-delivery.md`](../../epics/16-bootstrap/02-api-seed-delivery.md), [`../../epics/16-bootstrap/06-package-backed-standard-schema-selection.md`](../../epics/16-bootstrap/06-package-backed-standard-schema-selection.md) | `prepare-dms-schema.ps1`, `prepare-dms-claims.ps1`, `load-dms-seed-data.ps1` |
| Credential bootstrapping | [`../../epics/16-bootstrap/02-api-seed-delivery.md`](../../epics/16-bootstrap/02-api-seed-delivery.md), [`../../epics/16-bootstrap/03-entry-point-and-ide-workflow.md`](../../epics/16-bootstrap/03-entry-point-and-ide-workflow.md) | `start-local-dms.ps1`, `configure-local-dms-instance.ps1`, `load-dms-seed-data.ps1` |
| Single entry point and safe skip behavior | [`../../epics/16-bootstrap/03-entry-point-and-ide-workflow.md`](../../epics/16-bootstrap/03-entry-point-and-ide-workflow.md) | `bootstrap-local-dms.ps1` as optional wrapper over phase commands |
| IDE debugging workflow | [`../../epics/16-bootstrap/03-entry-point-and-ide-workflow.md`](../../epics/16-bootstrap/03-entry-point-and-ide-workflow.md), [`appsettings.Development.json.example`](appsettings.Development.json.example) | Existing phase commands plus IDE guidance; no extra control plane |
| Runtime ApiSchema content loading | [`../../epics/16-bootstrap/04-apischema-runtime-content-loading.md`](../../epics/16-bootstrap/04-apischema-runtime-content-loading.md) | Runtime content-loading story over the normalized filesystem workspace; bootstrap only stages the workspace |
| MetaEd asset-only ApiSchema packaging | [`../../epics/16-bootstrap/05-metaed-apischema-asset-packaging.md`](../../epics/16-bootstrap/05-metaed-apischema-asset-packaging.md) | Cross-repo package-production story; prerequisite for Story 06 but not for direct filesystem ApiSchema loading |
| Backend redesign awareness | [`bootstrap-design.md` Section 11](bootstrap-design.md#11-backend-redesign-impact-and-ddl-provisioning) | `provision-dms-schema.ps1` delegates to SchemaTools/runtime |
| ODS initdev audit | [`reference-initdev-workflow.md`](reference-initdev-workflow.md) | Informational only |
