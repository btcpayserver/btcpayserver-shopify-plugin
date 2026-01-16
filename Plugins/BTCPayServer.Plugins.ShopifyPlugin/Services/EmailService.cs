using System;
using System.Threading.Tasks;
using BTCPayServer.Logging;
using BTCPayServer.Plugins.Emails.Services;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace BTCPayServer.Plugins.ShopifyPlugin.Services;

public class EmailService
{

    private readonly EmailSenderFactory _emailSender;
    private readonly Logs _logs;
    public EmailService(EmailSenderFactory emailSender, Logs logs)
    {
        _logs = logs;
        _emailSender = emailSender;
    }


    public async Task SendRefundOrderEmail(string storeId, string recipientEmail, string recipientName, string shopifyOrderId, string claimUrl)
    {
        var settings = await (await _emailSender.GetEmailSender(storeId)).GetEmailSettings();
        if (!settings.IsComplete() || string.IsNullOrEmpty(recipientEmail))
            return;

        string refundEmailBody = $@"
Hello {recipientName},

A refund has been issued for your recent shopify order.

To claim your refund, please use the secure link below:
{claimUrl}

This link will guide you through completing your refund.

If you have any questions, please contact the merchant directly.

Thank you
";
        var client = await settings.CreateSmtpClient();
        try
        {
            var message = new MimeMessage();
            message.From.Add(MailboxAddress.Parse(settings.From));
            message.To.Add(new MailboxAddress("Customer", recipientEmail));
            message.Subject = $"Your refund for Shopify Order #{shopifyOrderId} is ready for claim";
            message.Body = new TextPart("plain") { Text = refundEmailBody };
            await client.SendAsync(message);
        }
        catch (Exception ex)
        {
            _logs.PayServer.LogError(ex, $"Error sending email to: {recipientEmail}");
        }
        finally
        {
            await client.DisconnectAsync(true);
        }
    }
}
