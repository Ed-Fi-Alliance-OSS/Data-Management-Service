# Identity wire contract review records

When the identity contract version is incremented, the gate compares the last published baseline with the served document, and the published package with the packed one: its provider surface, its XML documentation and its declared dependencies.
Each difference that is not a hard failure needs a review record entry in `<published>-to-<current>.json` in this directory.
The published version compared against is the highest one on the feed that is not greater than the contract version, so an increment on a servicing branch is compared with the last release of its own line rather than with a newer line's release.

```json
{
  "from": "1.0.0",
  "to": "1.1.0",
  "reviewed": [
    { "pointer": "/paths/~1identities/post/summary", "change": "changed", "reason": "Who reviewed what, and why it is compatible." }
  ]
}
```

The pointer names the difference:

- a JSON pointer into the served document, such as `/paths/~1identities/post/summary`;
- `SURFACE:<line>` for a provider surface line;
- `XMLDOC:<member id>` for one member's XML documentation, such as `XMLDOC:T:EdFi.DataManagementService.Identity.IIdentityService`;
- `DEPENDENCY:<target framework> | <package id>` for one declared dependency.

The change is `added`, `removed` or `changed`.
An entry must match a difference exactly by pointer and change.
An entry that matches no difference is stale and fails the gate.
The gate's failure message lists each difference as its change and pointer, so an entry can be copied from it.

Hard-fail categories cannot be waived, and an entry naming one does not help.
In the served document, they are changes to a request body, request parameter or request media type of an existing operation, changes to a response schema, status code, media type, header or problem type of an existing operation, changes to security, and removed paths or operations.
A `$ref` that is not a local pointer into `#/components/<group>/<name>` fails too, whether or not it changed, because the gate cannot classify what it points at.
In the provider surface, they are removed or changed lines, any line the gate cannot classify, and, on a type that was already published, new interface members without a default implementation, new abstract members, new required members and a private protected abstract member that closes the type to outside derivers.

An interface member with a default implementation is inherited by every existing provider, so it needs an entry like any other addition.
The members of a type that was not published before need entries, not a new contract, because no existing provider implements, derives from or constructs that type.
A change to the XML documentation or the dependencies needs an entry, because the contract's rules live in that documentation and a dependency changes what every implementer inherits.
Example validation is never an approval path.
