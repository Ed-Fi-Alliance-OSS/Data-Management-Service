# Reference validation

Please refer to [Reference
Validation](../reference/design/backend-redesign/design-docs/transactions-and-concurrency.md#reference-validation)
for more details.

## Remove reference validation

If a user wants to remove referential integrity checking to facilitate faster
loading of test data, the following database script can be used to remove the
foreign key constraints on the `References` table.

``` sql
ALTER TABLE Reference  DROP CONSTRAINT FK_Reference_ReferencedAlias
```

> [!NOTE]
> The other foreign key constraints on the tables do not affect the
> referential integrity checking, so there is no need to remove them for this
> purpose.
