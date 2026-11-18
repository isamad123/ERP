using Erp.Kernel.Application.Notifications;
using Erp.Kernel.Contracts.Jobs;
using Erp.Kernel.Infrastructure.Notifications.Email;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.Kernel.Infrastructure.Notifications;

/// <summary>Service registration of notifications (slice S1.11b1).</summary>
public static class NotificationRegistration
{
    /// <summary>
    /// The notification centre and administration of the API host. Modules register their <see cref="NotificationManifestSource"/>
    /// in both hosts; the catalogue is checked when the host starts.
    /// </summary>
    public static IServiceCollection AddKernelNotifications(this IServiceCollection services)
    {
        services.AddSingleton(sp => new NotificationCatalog(sp.GetServices<NotificationManifestSource>()));
        services.AddScoped<INotificationCentre, NotificationCentreService>();
        services.AddScoped<INotificationAdmin, NotificationAdminService>();
        return services;
    }

    /// <summary>The consumer of the worker host: turns outbox events into notification items (Build items 8 to 11).</summary>
    public static IServiceCollection AddKernelNotificationConsumer(this IServiceCollection services, MailOptions? mail = null)
    {
        services.AddKernelNotifications();
        services.AddSingleton(mail ?? MailOptions.None);
        services.AddScoped<IEventConsumer, NotificationConsumer>();

        // E-mail (slice S1.11b2): the SMTP adapter, the mail-server choice and the bounce mailbox reader; the sender and bounce jobs are
        // kernel job handlers (KernelJobs.AddKernelJobHandlers).
        services.AddSingleton<IEmailTransport, SmtpEmailTransport>();
        services.AddSingleton<MailServerResolver>();
        services.AddSingleton<IBounceMailboxReader, ImapBounceMailboxReader>();
        return services;
    }
}
