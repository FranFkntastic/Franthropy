using Dalamud.Interface.ImGuiNotification;


namespace Miosuke.Messages;

public static class Notice
{
    public static void Success(string s) => Service.NotificationManager.AddNotification(new Notification()
    {
        Content = s,
        Title = Service.PluginInterface.InternalName,
        Type = NotificationType.Success,
    });

    public static void Info(string s) => Service.NotificationManager.AddNotification(new Notification()
    {
        Content = s,
        Title = Service.PluginInterface.InternalName,
        Type = NotificationType.Info,
    });

    public static void Error(string s) => Service.NotificationManager.AddNotification(new Notification()
    {
        Content = s,
        Title = Service.PluginInterface.InternalName,
        Type = NotificationType.Error,
    });

    public static void Warning(string s) => Service.NotificationManager.AddNotification(new Notification()
    {
        Content = s,
        Title = Service.PluginInterface.InternalName,
        Type = NotificationType.Warning,
    });

    public static void Plain(string s) => Service.NotificationManager.AddNotification(new Notification()
    {
        Content = s,
        Title = Service.PluginInterface.InternalName,
        Type = NotificationType.None,
    });
}
