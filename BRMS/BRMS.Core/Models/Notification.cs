namespace BRMS.Core.Models;

/// <summary>
/// Represents a notification to be sent.
/// </summary>
public class Notification
{
    /// <summary>
    /// Gets or sets the notification identifier.
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Gets or sets the notification title.
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the notification message.
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the notification type.
    /// </summary>
    public NotificationType Type { get; set; } = NotificationType.Information;

    /// <summary>
    /// Gets or sets the notification priority.
    /// </summary>
    public NotificationPriority Priority { get; set; } = NotificationPriority.Normal;

    /// <summary>
    /// Gets or sets the recipient of the notification.
    /// </summary>
    public string Recipient { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the sender of the notification.
    /// </summary>
    public string Sender { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the notification channel.
    /// </summary>
    public NotificationChannel Channel { get; set; } = NotificationChannel.Email;

    /// <summary>
    /// Gets or sets the timestamp when the notification was created.
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Gets or sets the timestamp when the notification should be sent.
    /// </summary>
    public DateTime? ScheduledAt { get; set; }

    /// <summary>
    /// Gets or sets additional metadata for the notification.
    /// </summary>
    public Dictionary<string, object> Metadata { get; set; } = [];

    /// <summary>
    /// Gets or sets a value indicating whether the notification has been sent.
    /// </summary>
    public bool IsSent { get; set; }

    /// <summary>
    /// Gets or sets the timestamp when the notification was sent.
    /// </summary>
    public DateTime? SentAt { get; set; }

    /// <summary>
    /// Gets or sets any error message if the notification failed to send.
    /// </summary>
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Enumeration of notification types.
/// </summary>
public enum NotificationType
{
    /// <summary>
    /// Information notification.
    /// </summary>
    Information,

    /// <summary>
    /// Warning notification.
    /// </summary>
    Warning,

    /// <summary>
    /// Error notification.
    /// </summary>
    Error,

    /// <summary>
    /// Success notification.
    /// </summary>
    Success,

    /// <summary>
    /// Alert notification.
    /// </summary>
    Alert
}

/// <summary>
/// Enumeration of notification priorities.
/// </summary>
public enum NotificationPriority
{
    /// <summary>
    /// Low priority notification.
    /// </summary>
    Low,

    /// <summary>
    /// Normal priority notification.
    /// </summary>
    Normal,

    /// <summary>
    /// High priority notification.
    /// </summary>
    High,

    /// <summary>
    /// Critical priority notification.
    /// </summary>
    Critical
}

/// <summary>
/// Enumeration of notification channels.
/// </summary>
public enum NotificationChannel
{
    /// <summary>
    /// Email notification channel.
    /// </summary>
    Email,

    /// <summary>
    /// SMS notification channel.
    /// </summary>
    SMS,

    /// <summary>
    /// Push notification channel.
    /// </summary>
    Push,

    /// <summary>
    /// In-app notification channel.
    /// </summary>
    InApp,

    /// <summary>
    /// Webhook notification channel.
    /// </summary>
    Webhook,

    /// <summary>
    /// Slack notification channel.
    /// </summary>
    Slack,

    /// <summary>
    /// Teams notification channel.
    /// </summary>
    Teams
}
