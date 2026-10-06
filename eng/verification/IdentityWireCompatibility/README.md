# Identity wire contract review records

When the identity contract version is incremented, the gate compares the last published baseline with the served document and the published provider surface with the packed one.
Each difference that is not a hard failure needs a review record entry in `<published>-to-<current>.json` in this directory.

```json
{
  "from": "1.0.0",
  "to": "1.1.0",
  "reviewed": [
    { "pointer": "/paths/~1identities/post/summary", "change": "changed", "reason": "Who reviewed what, and why it is compatible." }
  ]
}
```

The pointer is a JSON pointer into the document, or `SURFACE:<line>` for a provider surface line.
The change is `added`, `removed` or `changed`.
An entry must match a difference exactly by pointer and change.
An entry that matches no difference is stale and fails the gate.

Hard-fail categories cannot be waived, and an entry naming one does not help.
They are changes to a request body, request parameter or request media type of an existing operation, changes to a response schema, status code, media type, header or problem type of an existing operation, changes to security, removed paths or operations, and provider surface removals, new interface members and new required members.
Example validation is never an approval path.
