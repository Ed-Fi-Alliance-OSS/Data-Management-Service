Semantic Identity Validation blocker

Attempted task: `Semantic Identity Validation` from `tasks.json` for story `reference/design/backend-redesign/epics/01-relational-model/11-stable-collection-row-identity.md`.

Problem:
- Adding the fail-fast validation pass immediately exposes supported positive fixtures whose persisted multi-item scopes do not have applicable `arrayUniquenessConstraints`, so they cannot compile non-empty semantic identity metadata.
- Repo instructions say authoritative input test files are production-level goldens and must not be modified. That blocks completing the validation task without violating the instructions.

Concrete failures seen with the validation pass enabled:
- Authoritative derived relational-model tests fail on resource `Ed-Fi:Assessment`, scope `$.programs[*]`, table `edfi.AssessmentProgram`.
- Authoritative relational-model manifest tests fail on the same missing semantic identity for `Ed-Fi:Assessment`.
- Existing positive fixture tests also fail for missing semantic identity on scopes like `$.addresses[*]` for `Ed-Fi:Person` / `edfi.PersonSite` and `Ed-Fi:Student` / `edfi.PersonAddress`, which indicates the cleanup surface is broader than a single negative-fixture addition.

Why blocked:
- The intended task requires fail-fast validation for supported persisted multi-item scopes.
- Making that change green requires updating supported fixture inputs so those scopes declare semantic identity metadata.
- The authoritative input fixtures needed for that are not allowed to be edited under the current instructions.

Recommended next step:
- Decide whether authoritative input fixtures may be updated for this story. If yes, this task can proceed together with the broader fixture-cleanup task. If no, semantic-identity fail-fast validation must remain deferred.
