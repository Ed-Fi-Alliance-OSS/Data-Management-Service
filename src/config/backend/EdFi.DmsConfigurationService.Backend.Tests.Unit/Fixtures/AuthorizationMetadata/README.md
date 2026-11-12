# Authorization metadata characterization

The original literal expectations were authored independently and checked against the CMS
response factory before extracting the shared resolver. Inputs contain the catalog
and hierarchy separately; expected files contain the endpoint's JSON array, including
CMS ordering and authorization IDs. CLI tests can link these files as content.

- `inheritance`: ancestor and child grants, nearest per-action defaults, a nearer
  empty strategy list shadowing an ancestor default, child overrides, multiple
  strategies in deliberately unsorted order, and defaults that do not grant actions.
  The ancestor Read override loses to Zebra's own overrides and to the nearer empty
  Read grants for Beta/Alpha, which select the nearest defaults instead.
  Depth-first traversal encounters Zebra before Beta/Alpha; returned claims are
  sorted, but IDs follow traversal. Equivalent Beta/Alpha permissions reuse the
  first encountered action/strategy sequences. Catalog order is Zoo, Empty, Alpha;
  Empty has no grants, and Alpha's IDs continue from Zoo's. This input is a complete,
  schema-valid document suitable for later offline resolution/verification tests.
- `collisions` (T07): six leaves in each of two claim sets; the first two reorder
  equivalent Read/Create permissions and reuse the first strategy sequence (Z, A).
  Other leaves differ by strategy, action case, strategy case, or strategy multiplicity.
  Alpha allocates IDs 1–5; Beta allocates 6–10. Literal expected permissions run
  through both CMS/shared entry points and a constant-hash dictionary comparer.
  Separate model tests use derived Action records with constant hashes to exercise
  structural equality independently of dictionary lookup.
- `filtered-alpha` / `filtered-empty`: literal outputs for case-insensitive catalog
  filtering, preserving catalog spelling and restarting IDs within each response.
- `empty-actions`: direct and inherited associations without grants still include
  leaves and share an empty authorization. Omitted actions use model defaults;
  hierarchy associations absent from the catalog never create output claim sets.
- `empty-hierarchy` / `empty-catalog`: resolver boundary behavior with empty inputs.

The empty-input/action fixtures characterize the resolver's existing contract, not
complete-document validation: their empty lists or undeclared association may be
rejected by schema/integrity validation before offline resolution. Do not weaken
validation to accept them. These fixtures do not impose CLI canonical ordering (T11).
