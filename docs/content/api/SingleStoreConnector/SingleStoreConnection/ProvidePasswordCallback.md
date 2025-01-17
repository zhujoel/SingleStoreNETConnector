# SingleStoreConnection.ProvidePasswordCallback property

Gets or sets the delegate used to generate a password for new database connections.

```csharp
public Func<SingleStoreProvidePasswordContext, string>? ProvidePasswordCallback { get; set; }
```

## Remarks

This delegate is executed when a new database connection is opened that requires a password. Due to connection pooling, this delegate is only executed when a new physical connection is established with a database server, not when a connection is retrieved from the pool.

The [`Password`](../SingleStoreConnectionStringBuilder/Password.md) option takes precedence over this delegate if it is specified.

Using this delegate can make more efficient use of connection pooling for servers that require frequently-changing passwords or authentication tokens. Changing the password in the connection string will create unique connection pools; this delegate allows a single connection pool to use multiple passwords.

## See Also

* class [SingleStoreProvidePasswordContext](../SingleStoreProvidePasswordContext.md)
* class [SingleStoreConnection](../SingleStoreConnection.md)
* namespace [SingleStoreConnector](../../SingleStoreConnector.md)

<!-- DO NOT EDIT: generated by xmldocmd for SingleStoreConnector.dll -->
