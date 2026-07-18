using Dalamud.Plugin;

namespace Franthropy.Dalamud.Travel;

public sealed record LifestreamLoginRequest(string CharacterName, string HomeWorld)
{
    public static bool TryCreate(string? characterName, string? homeWorld, out LifestreamLoginRequest? request, out string error)
    {
        var normalizedName = characterName?.Trim() ?? string.Empty;
        var normalizedWorld = homeWorld?.Trim() ?? string.Empty;
        if (normalizedName.Length is < 3 or > 64 || normalizedName.Any(char.IsControl))
        {
            request = null;
            error = "A rendered character name between 3 and 64 characters is required.";
            return false;
        }
        if (normalizedWorld.Length is < 3 or > 32 || normalizedWorld.Any(char.IsControl))
        {
            request = null;
            error = "A rendered home-world name between 3 and 32 characters is required.";
            return false;
        }

        request = new(normalizedName, normalizedWorld);
        error = string.Empty;
        return true;
    }
}

public sealed record LifestreamLoginSubmissionResult(
    bool Success,
    string Code,
    string Message,
    string? SubmissionMode = null);

/// <summary>
/// Submits title-screen or already-open character-selection login work through Lifestream IPC.
/// Lifestream owns the low-level lobby mechanics; callers retain character allowlisting and must
/// prove the rendered selection and eventual logged-in identity independently.
/// </summary>
public sealed class DalamudLifestreamLogin
{
    public const string CanAutoLoginChannel = "Lifestream.CanAutoLogin";
    public const string CanInitiateFromCharacterListChannel = "Lifestream.CanInitiateTravelFromCharaSelectList";
    public const string ConnectAndLoginChannel = "Lifestream.ConnectAndLogin";
    public const string InitiateFromCharacterListChannel = "Lifestream.InitiateLoginFromCharaSelectScreen";

    private readonly IDalamudPluginInterface pluginInterface;

    public DalamudLifestreamLogin(IDalamudPluginInterface pluginInterface) =>
        this.pluginInterface = pluginInterface ?? throw new ArgumentNullException(nameof(pluginInterface));

    public LifestreamLoginSubmissionResult TryBegin(LifestreamLoginRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            var canInitiateFromList = pluginInterface
                .GetIpcSubscriber<bool>(CanInitiateFromCharacterListChannel)
                .InvokeFunc();
            if (canInitiateFromList)
            {
                var accepted = pluginInterface
                    .GetIpcSubscriber<string, string, bool>(InitiateFromCharacterListChannel)
                    .InvokeFunc(request.CharacterName, request.HomeWorld);
                return accepted
                    ? new(true, "Submitted", "Lifestream accepted login from the rendered character-selection workflow.", "CharacterSelection")
                    : new(false, "Rejected", "Lifestream rejected login from the current character-selection state.", "CharacterSelection");
            }

            var canAutoLogin = pluginInterface.GetIpcSubscriber<bool>(CanAutoLoginChannel).InvokeFunc();
            if (!canAutoLogin)
                return new(false, "NotReady", "Lifestream reports that neither the title screen nor character-selection workflow is ready for login.");

            var connected = pluginInterface
                .GetIpcSubscriber<string, string, bool>(ConnectAndLoginChannel)
                .InvokeFunc(request.CharacterName, request.HomeWorld);
            return connected
                ? new(true, "Submitted", "Lifestream accepted title-screen connection and login work.", "TitleScreen")
                : new(false, "Rejected", "Lifestream rejected title-screen connection and login work.", "TitleScreen");
        }
        catch (Exception ex)
        {
            return new(false, "IpcFailure", ex.Message);
        }
    }
}
