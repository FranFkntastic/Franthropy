using Dalamud.Game.Gui.Toast;


namespace Miosuke.Messages;

public static class Toast
{
    public static void Normal(string text, ToastPosition position = ToastPosition.Top, ToastSpeed speed = ToastSpeed.Fast)
    {
        Service.Toasts.ShowNormal(text, new ToastOptions
        {
            Position = position,
            Speed = speed,
        });
    }

    public static void Quest(string text, QuestToastPosition position = QuestToastPosition.Centre)
    {
        Service.Toasts.ShowQuest(text, new QuestToastOptions
        {
            Position = position,
            DisplayCheckmark = false,
            PlaySound = false,
        });
    }

    public static void Error(string text)
    {
        Service.Toasts.ShowError(text);
    }
}
