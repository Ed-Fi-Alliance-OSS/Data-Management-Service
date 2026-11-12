# Resolve command expectations

`inheritance-expected.json` retains the reviewed T06 permissions from the linked
AuthorizationMetadata/inheritance-expected.json. Only T11 presentation changes:
catalog Alpha, Empty, Zoo; ordinal actions; Alpha authorization 1, Zoo Zebra 2,
Zoo Alpha/Beta 3. Strategy sequences and empty lists are unchanged. Tests compare
exact UTF-8 output (excluding this fixture's final newline), and independently
compare all permissions with the original T06 expectation using ordered strategies.
No production resolver or serializer generated this expectation.

Converted-document checks use the existing unmodified ODS export: AssessmentRead
inherits NamespaceBased for assessment and RelationshipsWithEdOrgsAndPeople for
student. Both merged standards add Offline with exactly Read/NoFurtherAuthorizationRequired
on the student URI. Repeated command calls must produce identical bytes.

Empty catalog entries remain in command output. The complete-document schema
requires nonempty actions on claim-set associations, so empty leaf action lists
cannot originate in a valid command input. The existing T06 resolver and T11
serializer fixtures cover preservation of that CMS response shape; resolve retains
the schema gate and rejects such input without publishing output.
