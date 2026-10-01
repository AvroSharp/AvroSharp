# Logical types

A logical type gives an Avro primitive type a meaning: an `int` that counts days is a `date`, and `bytes` that hold an unscaled integer are a `decimal`. This page writes a record of logical types with generated C# types, reads the same data in the generic model, and shows what happens to values that don't fit.

## A record of logical types

The schema, `Schemas/payment.avsc`, has a field for each logical type that the generator maps to a .NET type:

```json
{"name": "id", "type": {"type": "string", "logicalType": "uuid"}},
{"name": "booked_on", "type": {"type": "int", "logicalType": "date"}},
{"name": "cutoff", "type": {"type": "int", "logicalType": "time-millis"}},
{"name": "authorized_at", "type": {"type": "long", "logicalType": "timestamp-millis"}},
{"name": "captured_at", "type": {"type": "long", "logicalType": "timestamp-micros"}},
{"name": "terminal_time", "type": {"type": "long", "logicalType": "local-timestamp-millis"}},
{"name": "amount", "type": {"type": "bytes", "logicalType": "decimal", "precision": 9, "scale": 2}},
{"name": "exchange_rate", "type": {"type": "fixed", "name": "Rate", "size": 8, "logicalType": "decimal", "precision": 12, "scale": 6}}
```

The generated `Payment` class has a `Guid`, a `DateOnly`, a `TimeOnly`, two `DateTimeOffset`s, a `DateTime` for the local timestamp, and two `decimal`s, one on `bytes` and one on `fixed`. The serializer converts them to and from the underlying `string`, `int`, `long`, `bytes` and `fixed` values:

```csharp
var payment = new Payment
{
    Id = Guid.Parse("3f2b8c1e-9a4d-4e7b-8c61-2d5f0a9e7b13"),
    BookedOn = new DateOnly(2026, 9, 30),
    Cutoff = new TimeOnly(17, 30),
    AuthorizedAt = new DateTimeOffset(2026, 9, 30, 14, 5, 12, 345, TimeSpan.FromHours(2)),
    CapturedAt = new DateTimeOffset(2026, 9, 30, 12, 5, 12, 345, TimeSpan.Zero).AddTicks(6_780),
    TerminalTime = new DateTime(2026, 9, 30, 14, 5, 11, 900),
    Amount = 1_249.90m,
    ExchangeRate = 1.083415m,
};
byte[] bytes = payment.ToAvroBytes();
Payment copy = Payment.FromAvroBytes(bytes);
```

Every value comes back. A `timestamp-*` is a UTC instant, so `AuthorizedAt`, written with a +02:00 offset, reads back as the same instant with offset zero. A `local-timestamp-*` is a wall-clock time without a time zone, and reads back as a `DateTime` of unspecified kind:

```csharp
ok &= copy.Id == payment.Id && copy.BookedOn == payment.BookedOn && copy.Cutoff == payment.Cutoff;
ok &= copy.AuthorizedAt == payment.AuthorizedAt && copy.AuthorizedAt.Offset == TimeSpan.Zero;
ok &= copy.CapturedAt == payment.CapturedAt && copy.TerminalTime == payment.TerminalTime;
ok &= copy.Amount == payment.Amount && copy.ExchangeRate == payment.ExchangeRate;
```

`time-micros` and `local-timestamp-micros` map the same way. `duration`, `timestamp-nanos`, `big-decimal` and decimals with a precision above 28 keep their underlying type; [the type mapping](../code-generation.md#type-mapping) lists them all.

## The same values in the generic model

The generic model holds the underlying values, not .NET conversions of them. [`GenericDatumReader`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Generic.GenericDatumReader.html) reads the uuid as a string, the date as an `int` of days since 1970-01-01, the timestamps as `long`s, the `bytes` decimal as a `byte[]` and the `fixed` decimal as a [`GenericFixed`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Generic.GenericFixed.html):

```csharp
GenericRecord generic = GenericDatumReader.Create(Payment.Schema).Read(bytes).AsRecord();
string id = generic["id"].AsString();
int days = generic["booked_on"].AsInt32();
long authorizedMillis = generic["authorized_at"].AsInt64();
byte[] amountBytes = generic["amount"].AsBytes();
GenericFixed rate = generic["exchange_rate"].AsFixed();
```

[`AvroLogicalValues`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Serialization.AvroLogicalValues.html) has the conversions the generated code uses. [`TimestampFromMilliseconds`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Serialization.AvroLogicalValues.TimestampFromMilliseconds.html) turns the `long` into a `DateTimeOffset`. A decimal is a two's-complement big-endian unscaled integer, and [`ReadDecimalFixed`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Serialization.AvroLogicalValues.ReadDecimalFixed.html) reads one from an [`AvroReader`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.IO.AvroReader.html), given the scale from the schema's [`DecimalLogicalType`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Schemas.DecimalLogicalType.html). For the `bytes` decimal, the size is the array's length:

```csharp
DateTimeOffset authorizedAt = AvroLogicalValues.TimestampFromMilliseconds(authorizedMillis);
var scale = ((DecimalLogicalType)generic.Schema.GetField("amount").Schema.LogicalType!).Scale;
var amountReader = new AvroReader(amountBytes);
decimal amount = AvroLogicalValues.ReadDecimalFixed(ref amountReader, scale, amountBytes.Length);
var rateReader = new AvroReader(rate.Bytes.Span);
decimal exchangeRate = AvroLogicalValues.ReadDecimalFixed(ref rateReader, 6, rate.Schema.Size);
```

The conversions also go the other way, for values put into a [`GenericRecord`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Generic.GenericRecord.html). [`MillisecondsFromTimestamp`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Serialization.AvroLogicalValues.MillisecondsFromTimestamp.html) gives the `long` for a timestamp:

```csharp
generic["authorized_at"] = AvroLogicalValues.MillisecondsFromTimestamp(new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero));
```

## Precision and range

Decimals are written exactly or not at all. `amount` is `decimal(9, 2)`: at most 9 digits, 2 of them after the point. A value with more digits, or with more fractional digits, raises an [`AvroException`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.AvroException.html) instead of being rounded:

```csharp
foreach (var tooBig in new[] { 12_345_678.90m, 19.999m })
{
    try
    {
        _ = new Payment { Amount = tooBig }.ToAvroBytes();
        ok = false;
    }
    catch (AvroException ex)
    {
        Console.WriteLine($"Rejected {tooBig}: {ex.Message}");
    }
}
```

The messages are "The decimal 12345678.90 has more than 9 digits." and "The decimal 19.999 has more than 2 fractional digits."

Times and timestamps are truncated to their logical type's unit, as the Java implementation does. A `DateTimeOffset` has 100-nanosecond ticks; `timestamp-micros` keeps microseconds, and `timestamp-millis` keeps only milliseconds:

```csharp
var precise = new DateTimeOffset(2026, 9, 30, 12, 5, 12, 345, TimeSpan.Zero).AddTicks(6_789);
var rounded = Payment.FromAvroBytes(new Payment { AuthorizedAt = precise, CapturedAt = precise }.ToAvroBytes());
Console.WriteLine($"timestamp-millis: {rounded.AuthorizedAt:HH:mm:ss.fffffff}, timestamp-micros: {rounded.CapturedAt:HH:mm:ss.fffffff}");
ok &= rounded.AuthorizedAt == precise.AddTicks(-6_789) && rounded.CapturedAt == precise.AddTicks(-9);
```

It prints `timestamp-millis: 12:05:12.3450000, timestamp-micros: 12:05:12.3456780`.

## Keeping the underlying types

To generate the underlying types (`int`, `long`, `string`, `byte[]`) instead of `DateOnly`, `Guid`, `decimal` and the others, set `<AvroSharpLogicalTypes>raw</AvroSharpLogicalTypes>`; see [the MSBuild properties](../code-generation.md#msbuild-properties). For one field, such as a timestamp that holds `Long.MaxValue` as a sentinel, which `DateTimeOffset` can't hold, add `"avrosharp.logicalType": "raw"` to its schema.

The whole program is the [LogicalTypes sample](https://github.com/AvroSharp/AvroSharp/tree/main/samples/LogicalTypes).

## Next

- [Getting started](index.md), [container files](containers.md), [schema evolution](schema-evolution.md) and [JSON](json.md).
- [Code generation](../code-generation.md): every MSBuild property and the full type mapping.
