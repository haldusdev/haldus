using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;

namespace Haldus;

/// <summary>Boşluksuz numaralamanın kapsam başına sayacı. Tablosu numaralı bir kaynak varsa modele eklenir.</summary>
public sealed class HaldusCounter
{
    /// <summary>Sayacın kapsamı: kaynak, varsa kiracı ve desendeki tarih parçaları.</summary>
    public string Scope { get; set; } = "";

    public long Value { get; set; }
}

/// <summary>
/// <c>.Numbered(...)</c> deseni: sabit metin ve <c>{seq}</c>, <c>{seq:6}</c>, <c>{yyyy}</c>, <c>{yy}</c>,
/// <c>{MM}</c>, <c>{dd}</c> parçaları. Desendeki tarih parçaları sayacın ne zaman sıfırlanacağını da
/// belirler: <c>{yyyy}</c> varsa sayaç her yıl, <c>{MM}</c> de varsa her ay yeniden başlar.
/// </summary>
public sealed class NumberFormat
{
    private readonly (string? Token, string Text, int Width)[] _parts;

    /// <param name="counter">Sayacın adı; genellikle entity'nin adıdır.</param>
    /// <param name="pattern">Numara deseni.</param>
    /// <param name="gapless">
    /// Numara, kayıtla aynı transaction'da bir sayaç satırından mı alınır. <c>false</c> ise
    /// <paramref name="sequence"/> adlı veritabanı sequence'i kullanılır.
    /// </param>
    /// <param name="perTenant">Sayaç kiracı başına ayrı mı tutulur.</param>
    /// <param name="sequence">Boşluksuz olmayan modda kullanılan sequence'in adı.</param>
    public NumberFormat(string counter, string pattern, bool gapless, bool perTenant, string? sequence = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(counter);
        ArgumentNullException.ThrowIfNull(pattern);

        Counter = counter;
        Pattern = pattern;
        Gapless = gapless;
        PerTenant = perTenant;
        Sequence = sequence;
        _parts = Parse(pattern);
    }

    public string Counter { get; }

    public string Pattern { get; }

    public bool Gapless { get; }

    public bool PerTenant { get; }

    public string? Sequence { get; }

    public string Render(long value, DateTimeOffset now)
    {
        var builder = new StringBuilder();

        foreach (var (token, text, width) in _parts)
        {
            builder.Append(token switch
            {
                null => text,
                "seq" => value.ToString(CultureInfo.InvariantCulture).PadLeft(width, '0'),
                _ => now.ToString(token, CultureInfo.InvariantCulture),
            });
        }

        return builder.ToString();
    }

    /// <summary>Sayacın kapsamı. Desendeki tarih parçaları kapsama girer; böylece dönem değişince sayaç yeniden başlar.</summary>
    public string Scope(Guid? tenantId, DateTimeOffset now)
    {
        var builder = new StringBuilder(Counter);

        if (PerTenant)
        {
            builder.Append('|').Append(tenantId?.ToString("N") ?? "-");
        }

        foreach (var (token, _, _) in _parts)
        {
            if (token is not (null or "seq"))
            {
                builder.Append('|').Append(now.ToString(token, CultureInfo.InvariantCulture));
            }
        }

        return builder.ToString();
    }

    private static (string? Token, string Text, int Width)[] Parse(string pattern)
    {
        var parts = new List<(string? Token, string Text, int Width)>();
        var index = 0;

        while (index < pattern.Length)
        {
            var open = pattern.IndexOf('{', index);
            if (open < 0)
            {
                parts.Add((null, pattern[index..], 0));
                break;
            }

            var close = pattern.IndexOf('}', open);
            if (close < 0)
            {
                throw new FormatException($"The number pattern '{pattern}' has an unclosed '{{'.");
            }

            if (open > index)
            {
                parts.Add((null, pattern[index..open], 0));
            }

            var token = pattern[(open + 1)..close];
            if (token == "seq")
            {
                parts.Add(("seq", string.Empty, 1));
            }
            else if (token.StartsWith("seq:", StringComparison.Ordinal)
                && int.TryParse(token.AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out var width)
                && width is > 0 and <= 18)
            {
                parts.Add(("seq", string.Empty, width));
            }
            else if (token is "yyyy" or "yy" or "MM" or "dd")
            {
                parts.Add((token, string.Empty, 0));
            }
            else
            {
                throw new FormatException($"The number pattern '{pattern}' has an unknown part '{{{token}}}'.");
            }

            index = close + 1;
        }

        if (parts.Count(part => part.Token == "seq") != 1)
        {
            throw new FormatException($"The number pattern '{pattern}' must contain {{seq}} exactly once.");
        }

        return [.. parts];
    }
}

/// <summary>
/// Ayrılmış bir numara. Boşluksuz modda numara açık bir transaction içinde ayrılır: kayıt kaydedildikten
/// sonra <see cref="CommitAsync"/> çağrılmazsa transaction geri alınır ve numara da geri verilir.
/// </summary>
public sealed class NumberAllocation : IAsyncDisposable
{
    private readonly IDbContextTransaction? _transaction;

    internal NumberAllocation(string number, IDbContextTransaction? transaction)
    {
        Number = number;
        _transaction = transaction;
    }

    public string Number { get; }

    public Task CommitAsync(CancellationToken cancellationToken) =>
        _transaction is null ? Task.CompletedTask : _transaction.CommitAsync(cancellationToken);

    public ValueTask DisposeAsync() => _transaction is null ? ValueTask.CompletedTask : _transaction.DisposeAsync();
}

/// <summary><c>.Numbered(...)</c> trait'inin numara kaynağı.</summary>
public static class HaldusNumbering
{
    /// <summary>Sayaç tablosunu modele ekler; üretilen kod, boşluksuz numaralı bir kaynak varsa çağırır.</summary>
    public static void Configure(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<HaldusCounter>(entity =>
        {
            entity.ToTable("HaldusCounters");
            entity.HasKey(counter => counter.Scope);
            entity.Property(counter => counter.Scope).HasMaxLength(200);
        });
    }

    /// <summary>
    /// Sıradaki numarayı ayırır. Boşluksuz modda kapsam ve sayaç satırı transaction bitene kadar kilitli kalır; aynı
    /// kapsamdaki eşzamanlı yazmalar geliş sırasıyla ilerler ve geri alınan işlem numarasını da geri verir. Zaten açık
    /// bir transaction varsa numara onun içinde ayrılır ve onunla birlikte sonuçlanır.
    /// </summary>
    public static async Task<NumberAllocation> AllocateAsync(DbContext context, NumberFormat format, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(format);

        var now = context.GetService<TimeProvider>().GetUtcNow();

        if (!format.Gapless)
        {
            var next = await ScalarAsync(context, SequenceSql(context, format), scope: null, cancellationToken);
            return new NumberAllocation(format.Render(next, now), transaction: null);
        }

        var tenantId = format.PerTenant ? HaldusScope.TenantId(context) : null;
        var transaction = context.Database.CurrentTransaction is null
            ? await context.Database.BeginTransactionAsync(cancellationToken)
            : null;

        try
        {
            var scope = format.Scope(tenantId, now);

            // Sayacı satır kilidi korur, ama PostgreSQL o kilidi bekleyenleri sırayla uyandırmaz: yük altında bazı
            // istekler defalarca öne geçilir (ölçümde 32 istemcide p99 1,5 sn idi). Önce kapsamın danışma kilidi
            // alınır; bekleyenler geliş sırasıyla ilerler ve satır kilidinde artık bekleyen olmaz.
            await HaldusOrdering.LockAsync(context, "Haldus.Counter", cancellationToken, scope);

            var value = await ScalarAsync(context, CounterSql(context), scope, cancellationToken);
            return new NumberAllocation(format.Render(value, now), transaction);
        }
        catch
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }

            throw;
        }
    }

    private static string CounterSql(DbContext context)
    {
        var entityType = context.Model.FindEntityType(typeof(HaldusCounter))
            ?? throw new InvalidOperationException(
                "The Haldus counter table is not part of this DbContext's model. Make sure the context is configured with UseHaldus().");

        var helper = context.GetService<ISqlGenerationHelper>();
        var tableName = entityType.GetTableName()!;
        var schema = entityType.GetSchema();
        var storeObject = StoreObjectIdentifier.Table(tableName, schema);
        var table = helper.DelimitIdentifier(tableName, schema);
        var scope = helper.DelimitIdentifier(entityType.FindProperty(nameof(HaldusCounter.Scope))!.GetColumnName(storeObject)!);
        var value = helper.DelimitIdentifier(entityType.FindProperty(nameof(HaldusCounter.Value))!.GetColumnName(storeObject)!);

        // Tek ifade hem ilk numarayı açar hem sonrakileri artırır; satır kilidi transaction sonuna kadar kalır.
        return $"INSERT INTO {table} ({scope}, {value}) VALUES (@scope, 1) "
            + $"ON CONFLICT ({scope}) DO UPDATE SET {value} = {table}.{value} + 1 RETURNING {value}";
    }

    private static string SequenceSql(DbContext context, NumberFormat format)
    {
        var sequence = (format.Sequence is null ? null : context.Model.FindSequence(format.Sequence))
            ?? throw new InvalidOperationException(
                $"The sequence '{format.Sequence}' is not part of this DbContext's model. Make sure the context is configured with UseHaldus().");

        var name = context.GetService<ISqlGenerationHelper>().DelimitIdentifier(sequence.Name, sequence.Schema);
        return $"SELECT nextval('{name.Replace("'", "''", StringComparison.Ordinal)}')";
    }

    private static async Task<long> ScalarAsync(DbContext context, string sql, string? scope, CancellationToken cancellationToken)
    {
        await context.Database.OpenConnectionAsync(cancellationToken);

        try
        {
            await using var command = context.Database.GetDbConnection().CreateCommand();
            command.CommandText = sql;
            command.Transaction = context.Database.CurrentTransaction?.GetDbTransaction();

            if (scope is not null)
            {
                var parameter = command.CreateParameter();
                parameter.ParameterName = "scope";
                parameter.Value = scope;
                command.Parameters.Add(parameter);
            }

            return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }
    }
}
