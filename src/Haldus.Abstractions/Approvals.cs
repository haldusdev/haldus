using System.ComponentModel.DataAnnotations;

namespace Haldus;

/// <summary>Onay bekleyen bir kaydın (<c>.Approvable()</c>) onay durumu.</summary>
public enum ApprovalStatus
{
    /// <summary>Karar bekleniyor; çok düzeyli onayda henüz bütün düzeyler onaylamadı.</summary>
    Pending,

    Approved,

    Rejected,
}

/// <summary>
/// İkinci bir kişinin onayına bağlı kayıt (<c>.Approvable()</c>). Alanları Haldus üretir ve yönetir: istekle
/// yazılamazlar, yalnızca onay uçları ve kaydın içeriğini değiştiren yazmalar tarafından değişirler.
/// </summary>
public interface IApprovable
{
    ApprovalStatus ApprovalStatus { get; set; }

    /// <summary>Şimdiye kadar onaylayan düzey sayısı. İçerik değişince sıfırlanır.</summary>
    int ApprovalLevel { get; set; }

    /// <summary>İçeriği son yazan kullanıcı; kendi yazdığını onaylayamaz.</summary>
    string? RequestedBy { get; set; }

    /// <summary>İçeriğin son yazıldığı an. Karar, kararı verenin gördüğü içeriğe bağlansın diye her yazmada ilerler.</summary>
    DateTimeOffset RequestedAt { get; set; }

    /// <summary>Son kararı veren kullanıcı.</summary>
    string? DecidedBy { get; set; }

    DateTimeOffset? DecidedAt { get; set; }

    /// <summary>Son kararla birlikte yazılan not.</summary>
    string? DecisionNote { get; set; }
}

/// <summary>Onay ve ret uçlarının isteğe bağlı gövdesi.</summary>
public sealed record ApprovalDecisionRequest
{
    /// <summary>Kararın gerekçesi; karar kaydında saklanır.</summary>
    [MaxLength(1000)]
    public string? Note { get; init; }
}

/// <summary>Bir kayıt için verilmiş tek bir karar.</summary>
/// <param name="Level">Kararın verildiği düzey (1'den başlar).</param>
/// <param name="Approved">Onay mı, ret mi.</param>
public sealed record ApprovalDecision(int Level, bool Approved, string UserId, string? Note, DateTimeOffset DecidedAt);
