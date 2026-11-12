# Resolved metadata presentation fixtures

`input.json` is a typed shared-resolver response shape with intentionally unordered
catalog, claim, authorization and action collections. It includes empty sets,
empty leaf permissions, empty strategy lists, repeated strategy names, opposite
strategy sequences, duplicate identical authorizations and reused IDs across sets.

`expected.json` is the reviewed literal CLI output (the file's trailing newline is
not part of serialization). Sets and claims use ordinal name order. Actions use
ordinal name order; permissions compare sorted action names and positional strategy
names lexicographically, shorter prefixes first. IDs start at 1 across the sorted
catalog and permission collections; identical permission sequences share an ID
within a set. Strategy arrays remain unchanged. Every reference is rewritten.

The duplicate and opposite-sequence records exercise the serializer's own identity
rules independently of shared resolver deduplication. The serializer cannot recover
strategy order already discarded by that resolver. Tests also call the real resolver
and verify presentation does not mutate its catalog, actions or allocated IDs.
