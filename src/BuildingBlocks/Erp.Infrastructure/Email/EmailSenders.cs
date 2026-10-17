using System.Collections.Concurrent;
using Erp.SharedKernel.Application;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace Erp.Infrastructure.Email;

public sealed class EmailOptions
{
    public const string Section = "Email";

    /// <summary>SMTP host. Empty = development mode: messages are logged and kept in memory, not sent.</summary>
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string FromAddress { get; set; } = "no-reply@example.qa";
    public string FromName { get; set; } = "Qatar SME ERP";
    public bool UseStartTls { get; set; } = true;
}

/// <summary>SMTP delivery (MailKit) — works with Microsoft 365, Google Workspace, Amazon SES, SendGrid SMTP, etc.</summary>
public sealed class SmtpEmailSender(EmailOptions options, ILogger<SmtpEmailSender> logger) : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        if (message.To.Count == 0) return;
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(options.FromName, options.FromAddress));
        foreach (var to in message.To.Distinct(StringComparer.OrdinalIgnoreCase)) mime.Bcc.Add(MailboxAddress.Parse(to));
        mime.Subject = message.Subject;
        mime.Body = new BodyBuilder { HtmlBody = message.HtmlBody, TextBody = message.TextBody }.ToMessageBody();

        using var client = new SmtpClient();
        await client.ConnectAsync(options.Host, options.Port, options.UseStartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.Auto, ct);
        if (!string.IsNullOrEmpty(options.Username)) await client.AuthenticateAsync(options.Username, options.Password, ct);
        await client.SendAsync(mime, ct);
        await client.DisconnectAsync(true, ct);
        logger.LogInformation("Email '{Subject}' sent to {Count} recipient(s)", message.Subject, message.To.Count);
    }
}

/// <summary>Development / test sender: logs and remembers the last messages instead of sending them.</summary>
public sealed class CapturingEmailSender(ILogger<CapturingEmailSender> logger) : IEmailSender
{
    private readonly ConcurrentQueue<EmailMessage> _sent = new();

    public IReadOnlyCollection<EmailMessage> Sent => _sent.ToArray();

    public Task SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        _sent.Enqueue(message);
        while (_sent.Count > 200 && _sent.TryDequeue(out _)) { }
        logger.LogInformation("[email not sent: no SMTP host configured] To {To}: {Subject}", string.Join(", ", message.To), message.Subject);
        return Task.CompletedTask;
    }
}
