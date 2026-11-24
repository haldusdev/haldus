using System.Text.Json;

namespace Haldus;

public enum ChangeSetStatus
{
    /// <summary>Değişiklikler birikiyor; kayda henüz dokunulmadı.</summary>
    Open,

    /// <summary>Değişiklikler kayda uygulandı.</summary>
    Committed,

    /// <summary>Değişikliklerden vazgeçildi.</summary>
    Discarded,
}

/// <summary>
/// Bir kayda uygulanmak üzere biriktirilen değişiklikler (<c>.ChangeSets()</c>): verilmiş bir siparişin düzenlenmesi
/// gibi, adım adım hazırlanan, önizlenen ve sonunda tek seferde uygulanan bir düzenleme oturumu.
/// </summary>
/// <param name="Changes">Biriken değişiklikler: kaydın kısmi güncelleme (<c>PATCH</c>) gövdesiyle aynı biçimde.</param>
public sealed record ChangeSetInfo(
    Guid Id,
    ChangeSetStatus Status,
    JsonElement Changes,
    DateTimeOffset CreatedAt,
    string? CreatedBy,
    DateTimeOffset? ClosedAt,
    string? ClosedBy);
