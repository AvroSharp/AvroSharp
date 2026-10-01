// Logical types: a generated record with dates, times, timestamps, decimals and a uuid, the same values in the generic
// model, and what happens to values that don't fit the logical type's precision.
using System;
using AvroSharp;
using AvroSharp.Generic;
using AvroSharp.IO;
using AvroSharp.Schemas;
using AvroSharp.Serialization;
using billing;

var ok = true;

// Generated properties have .NET types; the serializer converts them to the Avro primitive types.
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
Console.WriteLine($"Generated: {bytes.Length} bytes, {copy.Amount} booked on {copy.BookedOn:yyyy-MM-dd}, captured at {copy.CapturedAt:O}");

// Every value comes back; timestamps come back in UTC, which is the same instant.
ok &= copy.Id == payment.Id && copy.BookedOn == payment.BookedOn && copy.Cutoff == payment.Cutoff;
ok &= copy.AuthorizedAt == payment.AuthorizedAt && copy.AuthorizedAt.Offset == TimeSpan.Zero;
ok &= copy.CapturedAt == payment.CapturedAt && copy.TerminalTime == payment.TerminalTime;
ok &= copy.Amount == payment.Amount && copy.ExchangeRate == payment.ExchangeRate;

// The generic model holds the underlying values: a string, ints, longs, bytes and a fixed.
GenericRecord generic = GenericDatumReader.Create(Payment.Schema).Read(bytes).AsRecord();
string id = generic["id"].AsString();
int days = generic["booked_on"].AsInt32();
long authorizedMillis = generic["authorized_at"].AsInt64();
byte[] amountBytes = generic["amount"].AsBytes();
GenericFixed rate = generic["exchange_rate"].AsFixed();
Console.WriteLine($"Generic: id {id}, booked_on {days}, authorized_at {authorizedMillis}, amount {Convert.ToHexString(amountBytes)}");
ok &= string.Equals(id, "3f2b8c1e-9a4d-4e7b-8c61-2d5f0a9e7b13", StringComparison.Ordinal);
ok &= days == 20_726 && amountBytes.Length == 3;

// AvroLogicalValues converts them: a timestamp from its long, and a decimal from its unscaled bytes and the schema's scale.
DateTimeOffset authorizedAt = AvroLogicalValues.TimestampFromMilliseconds(authorizedMillis);
var scale = ((DecimalLogicalType)generic.Schema.GetField("amount").Schema.LogicalType!).Scale;
var amountReader = new AvroReader(amountBytes);
decimal amount = AvroLogicalValues.ReadDecimalFixed(ref amountReader, scale, amountBytes.Length);
var rateReader = new AvroReader(rate.Bytes.Span);
decimal exchangeRate = AvroLogicalValues.ReadDecimalFixed(ref rateReader, 6, rate.Schema.Size);
Console.WriteLine($"Converted: authorized at {authorizedAt:O}, amount {amount}, rate {exchangeRate}");
ok &= authorizedAt == payment.AuthorizedAt && amount == 1_249.90m && exchangeRate == 1.083415m;

// Going the other way, a generic record takes the converted value.
generic["authorized_at"] = AvroLogicalValues.MillisecondsFromTimestamp(new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero));
ok &= generic["authorized_at"].AsInt64() == 1_790_845_200_000L;

// A decimal must fit the schema: amount is decimal(9, 2), so at most 7 digits before the point and 2 after it.
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

// timestamp-millis drops the sub-millisecond ticks that timestamp-micros keeps.
var precise = new DateTimeOffset(2026, 9, 30, 12, 5, 12, 345, TimeSpan.Zero).AddTicks(6_789);
var rounded = Payment.FromAvroBytes(new Payment { AuthorizedAt = precise, CapturedAt = precise }.ToAvroBytes());
Console.WriteLine($"timestamp-millis: {rounded.AuthorizedAt:HH:mm:ss.fffffff}, timestamp-micros: {rounded.CapturedAt:HH:mm:ss.fffffff}");
ok &= rounded.AuthorizedAt == precise.AddTicks(-6_789) && rounded.CapturedAt == precise.AddTicks(-9);

Console.WriteLine(ok ? "OK" : "FAILED");
return ok ? 0 : 1;
