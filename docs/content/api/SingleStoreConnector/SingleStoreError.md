# SingleStoreError class

[`SingleStoreError`](./SingleStoreError.md) represents an error or warning that occurred during the execution of a SQL statement.

```csharp
public sealed class SingleStoreError
```

## Public Members

| name | description |
| --- | --- |
| [ErrorCode](SingleStoreError/ErrorCode.md) { get; } | The [`SingleStoreErrorCode`](./SingleStoreErrorCode.md) for the error or warning. |
| [Level](SingleStoreError/Level.md) { get; } | The error level. This comes from the SingleStore Server. Possible values include `Note`, `Warning`, and `Error`. |
| [Message](SingleStoreError/Message.md) { get; } | A human-readable description of the error or warning. |

## See Also

* namespace [SingleStoreConnector](../SingleStoreConnector.md)

<!-- DO NOT EDIT: generated by xmldocmd for SingleStoreConnector.dll -->