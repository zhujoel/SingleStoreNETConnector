# SingleStoreDecimal.Value property

Gets the value of this [`SingleStoreDecimal`](../SingleStoreDecimal.md) as a Decimal.

```csharp
public decimal Value { get; }
```

## Remarks

This method will throw an OverflowException if the value is too large to be represented.

## See Also

* struct [SingleStoreDecimal](../SingleStoreDecimal.md)
* namespace [SingleStoreConnector](../../SingleStoreConnector.md)

<!-- DO NOT EDIT: generated by xmldocmd for SingleStoreConnector.dll -->