using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace Haldus.Sample.Shop.Features.Auth;

/// <summary>
/// Hesap e-postalarını (doğrulama ve parola sıfırlama kodları) gönderir. Uygulama kendi posta servisini bu arayüzü
/// uygulayarak bağlar; örnek mağaza SMTP ile gönderir (<c>Shop:Smtp</c>), ayar yoksa göndermez.
/// </summary>
public interface IAccountMailer
{
    Task SendCodeAsync(string email, AccountCodePurpose purpose, string code, CancellationToken cancellationToken);
}

/// <summary>SMTP ayarları (<c>Shop:Smtp</c>). Parola commit'lenmez; user-secrets veya ortam değişkeniyle verilir.</summary>
public sealed class SmtpOptions
{
    public string? Host { get; set; }

    public int Port { get; set; } = 587;

    public bool EnableSsl { get; set; } = true;

    public string? UserName { get; set; }

    public string? Password { get; set; }

    public string From { get; set; } = "no-reply@example.com";
}

/// <summary>Kodu SMTP ile gönderir. Metinler isteğin dilindedir (<c>Resources/ShopMessages</c>).</summary>
public sealed class SmtpAccountMailer(IOptions<SmtpOptions> options, IStringLocalizer<ShopMessages> messages) : IAccountMailer
{
    public async Task SendCodeAsync(string email, AccountCodePurpose purpose, string code, CancellationToken cancellationToken)
    {
        var smtp = options.Value;
        var (subject, body) = purpose == AccountCodePurpose.PasswordReset
            ? ("PasswordResetSubject", "PasswordResetBody")
            : ("EmailVerificationSubject", "EmailVerificationBody");

        using var message = new MailMessage(smtp.From, email, messages[subject], messages[body, code]);
        using var client = new SmtpClient(smtp.Host, smtp.Port) { EnableSsl = smtp.EnableSsl };

        if (!string.IsNullOrEmpty(smtp.UserName))
        {
            client.Credentials = new NetworkCredential(smtp.UserName, smtp.Password);
        }

        await client.SendMailAsync(message, cancellationToken);
    }
}

/// <summary>
/// Posta ayarı olmayan kurulumun göndericisi: e-posta gönderilmez. Yalnızca geliştirme ortamında, örnek arayüzler
/// posta sunucusu kurmadan denenebilsin diye kod günlüğe yazılır; başka ortamlarda kod hiçbir yere yazılmaz.
/// </summary>
public sealed partial class LogAccountMailer(ILogger<LogAccountMailer> logger, IHostEnvironment environment) : IAccountMailer
{
    public Task SendCodeAsync(string email, AccountCodePurpose purpose, string code, CancellationToken cancellationToken)
    {
        if (environment.IsDevelopment())
        {
            LogCode(logger, purpose, email, code);
        }
        else
        {
            LogNotConfigured(logger, purpose);
        }

        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Mail is not configured (Shop:Smtp). {Purpose} code for {Email}: {Code}")]
    private static partial void LogCode(ILogger logger, AccountCodePurpose purpose, string email, string code);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Mail is not configured (Shop:Smtp); the {Purpose} message was not sent.")]
    private static partial void LogNotConfigured(ILogger logger, AccountCodePurpose purpose);
}
