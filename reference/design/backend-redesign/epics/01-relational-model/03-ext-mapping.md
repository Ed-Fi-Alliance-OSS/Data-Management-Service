# Story: Model `_ext` Extension Tables

## Description

Derive extension table models for Ed-Fi-style extensions (`_ext`) as defined in `reference/design/backend-redesign/extensions.md`:

- Detect `_ext.{project}` subtrees at any depth during schema traversal.
- Resolve `_ext` project keys to configured projects (by `ProjectEndpointName`, fallback to `ProjectName`).
- Create extension tables in the extension project schema:
  - `{Resource}Extension` for resource-level extension fields (1:1 by `DocumentId`),
  - scope-aligned extension tables for `_ext` inside collections/common types,
  - extension child tables for arrays under `_ext` using parent+ordinal keys.
- Apply the same reference/descriptor binding rules inside extensions.

## Acceptance Criteria

- Extension project schemas are created deterministically from resolved `ProjectEndpointName`.
- Extension table keys align exactly to the base scope keys they extend (including ordinals).
- Extension table naming follows the patterns described in `reference/design/backend-redesign/extensions.md`.
- Unknown `_ext` project keys fail fast at model compilation time.
- A small fixture with `_ext` at root and inside a collection produces the expected extension table inventory.

## Tasks

1. Implement `_ext` site detection as part of schema traversal (no hard-coded paths beyond `_ext`).
2. Implement `_ext` project key resolution and validation against the effective schema projects list.
3. Implement extension table derivation rules (root extension, scope extension, extension arrays).
4. Integrate reference/descriptor binding into extension table derivation.
5. Add unit tests for:
   1. root `_ext` + collection `_ext`,
   2. multiple extension projects,
   3. unknown `_ext` key failure.

