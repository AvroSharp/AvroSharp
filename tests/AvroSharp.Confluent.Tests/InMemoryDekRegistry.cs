using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Confluent.SchemaRegistry;
using Confluent.SchemaRegistry.Encryption;

namespace AvroSharp.Confluent.Tests;

/// <summary>
/// The key registry that Confluent's encryption rules use (KEKs and DEKs), in memory, as in Confluent's own tests.
/// </summary>
internal sealed class InMemoryDekRegistry : IDekRegistryClient
{
    private readonly Dictionary<string, RegisteredKek> _keks = new(StringComparer.Ordinal);
    private readonly List<RegisteredDek> _deks = [];

    public int MaxCachedKeys => 1000;

    public Task<RegisteredKek> CreateKekAsync(Kek kek)
    {
        lock (_keks)
        {
            if (!_keks.TryGetValue(kek.Name, out var registered))
            {
                registered = new RegisteredKek
                {
                    Name = kek.Name,
                    KmsType = kek.KmsType,
                    KmsKeyId = kek.KmsKeyId,
                    KmsProps = kek.KmsProps,
                    Doc = kek.Doc,
                    Shared = kek.Shared,
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                };
                _keks[kek.Name] = registered;
            }

            return Task.FromResult(registered);
        }
    }

    public Task<RegisteredKek> GetKekAsync(string name, bool ignoreDeletedKeks)
    {
        lock (_keks)
        {
            return Task.FromResult(_keks.TryGetValue(name, out var kek) ? kek : null!);
        }
    }

    public Task<List<string>> GetKeksAsync(bool ignoreDeletedKeks)
    {
        lock (_keks)
        {
            return Task.FromResult(_keks.Keys.ToList());
        }
    }

    public Task<RegisteredKek> UpdateKekAsync(string name, UpdateKek kek) => throw new NotSupportedException();

    public Task<RegisteredDek> CreateDekAsync(string kekName, Dek dek)
    {
        lock (_deks)
        {
            var version = dek.Version ?? 1;
            var registered = Find(kekName, dek.Subject, version, dek.Algorithm);
            if (registered is null)
            {
                registered = new RegisteredDek
                {
                    KekName = kekName,
                    Subject = dek.Subject,
                    Version = version,
                    Algorithm = dek.Algorithm,
                    EncryptedKeyMaterial = dek.EncryptedKeyMaterial,
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                };
                _deks.Add(registered);
            }

            return Task.FromResult(registered);
        }
    }

    public Task<RegisteredDek> GetDekAsync(string kekName, string subject, DekFormat? algorithm, bool ignoreDeletedDeks) =>
        GetDekVersionAsync(kekName, subject, 1, algorithm, ignoreDeletedDeks);

    public Task<RegisteredDek> GetDekVersionAsync(string kekName, string subject, int version, DekFormat? algorithm, bool ignoreDeletedDeks)
    {
        lock (_deks)
        {
            if (version == -1)
            {
                version = _deks.Where(d => Matches(d, kekName, subject, algorithm)).Select(d => d.Version).DefaultIfEmpty(1).Max() ?? 1;
            }

            return Find(kekName, subject, version, algorithm) is { } dek
                ? Task.FromResult(dek)
                : throw new SchemaRegistryException($"No DEK for '{subject}'.", HttpStatusCode.NotFound, 40470);
        }
    }

    public Task<RegisteredDek> GetDekLatestVersionAsync(string kekName, string subject, DekFormat? algorithm, bool ignoreDeletedDeks) =>
        GetDekVersionAsync(kekName, subject, -1, algorithm, ignoreDeletedDeks);

    public Task<List<string>> GetDeksAsync(string kekName, bool ignoreDeletedDeks)
    {
        lock (_deks)
        {
            return Task.FromResult(_deks.Where(d => string.Equals(d.KekName, kekName, StringComparison.Ordinal)).Select(d => d.Subject).Distinct(StringComparer.Ordinal).ToList());
        }
    }

    public Task<List<int>> GetDekVersionsAsync(string kekName, string subject, DekFormat? algorithm, bool ignoreDeletedDeks)
    {
        lock (_deks)
        {
            return Task.FromResult(_deks.Where(d => Matches(d, kekName, subject, algorithm)).Select(d => d.Version ?? 1).ToList());
        }
    }

    public void Dispose()
    {
    }

    private static bool Matches(RegisteredDek dek, string kekName, string subject, DekFormat? algorithm) =>
        string.Equals(dek.KekName, kekName, StringComparison.Ordinal)
        && string.Equals(dek.Subject, subject, StringComparison.Ordinal)
        && (algorithm is null || dek.Algorithm == algorithm);

    private RegisteredDek? Find(string kekName, string subject, int version, DekFormat? algorithm) =>
        _deks.Find(d => Matches(d, kekName, subject, algorithm) && d.Version == version);
}
