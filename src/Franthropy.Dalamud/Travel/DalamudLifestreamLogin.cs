using Dalamud.Plugin;
using ECommons;

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
    public const string ChangeCharacterChannel = "Lifestream.ChangeCharacter";

    private readonly Func<bool> canInitiateFromCharacterList;
    private readonly Func<string, string, bool> initiateFromCharacterList;
    private readonly Func<bool> canAutoLogin;
    private readonly Func<string, string, bool> connectAndLogin;
    private readonly Func<string, string, ErrorCode> changeCharacter;

    public DalamudLifestreamLogin(IDalamudPluginInterface pluginInterface)
    {
        ArgumentNullException.ThrowIfNull(pluginInterface);
        canInitiateFromCharacterList = () => pluginInterface.GetIpcSubscriber<bool>(CanInitiateFromCharacterListChannel).InvokeFunc();
        initiateFromCharacterList = (name, world) => pluginInterface.GetIpcSubscriber<string, string, bool>(InitiateFromCharacterListChannel).InvokeFunc(name, world);
        canAutoLogin = () => pluginInterface.GetIpcSubscriber<bool>(CanAutoLoginChannel).InvokeFunc();
        connectAndLogin = (name, world) => pluginInterface.GetIpcSubscriber<string, string, bool>(ConnectAndLoginChannel).InvokeFunc(name, world);
        changeCharacter = (name, world) => pluginInterface.GetIpcSubscriber<string, string, ErrorCode>(ChangeCharacterChannel).InvokeFunc(name, world);
    }

    internal DalamudLifestreamLogin(
        Func<bool> canInitiateFromCharacterList,
        Func<string, string, bool> initiateFromCharacterList,
        Func<bool> canAutoLogin,
        Func<string, string, bool> connectAndLogin,
        Func<string, string, ErrorCode> changeCharacter)
    {
        this.canInitiateFromCharacterList = canInitiateFromCharacterList;
        this.initiateFromCharacterList = initiateFromCharacterList;
        this.canAutoLogin = canAutoLogin;
        this.connectAndLogin = connectAndLogin;
        this.changeCharacter = changeCharacter;
    }

    public LifestreamLoginSubmissionResult TryBegin(LifestreamLoginRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            var canInitiateFromList = canInitiateFromCharacterList();
            if (canInitiateFromList)
            {
                var accepted = initiateFromCharacterList(request.CharacterName, request.HomeWorld);
                return accepted
                    ? new(true, "Submitted", "Lifestream accepted login from the rendered character-selection workflow.", "CharacterSelection")
                    : new(false, "Rejected", "Lifestream rejected login from the current character-selection state.", "CharacterSelection");
            }

            if (!canAutoLogin())
                return new(false, "NotReady", "Lifestream reports that neither the title screen nor character-selection workflow is ready for login.");

            var connected = connectAndLogin(request.CharacterName, request.HomeWorld);
            return connected
                ? new(true, "Submitted", "Lifestream accepted title-screen connection and login work.", "TitleScreen")
                : new(false, "Rejected", "Lifestream rejected title-screen connection and login work.", "TitleScreen");
        }
        catch (Exception ex)
        {
            return new(false, "IpcFailure", ex.Message);
        }
    }

    /// <summary>
    /// Requests Lifestream's complete logout-and-character-change workflow. The caller must own
    /// the target process and independently confirm the eventual logged-in identity.
    /// </summary>
    public LifestreamLoginSubmissionResult TryChangeCharacter(LifestreamLoginRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            return changeCharacter(request.CharacterName, request.HomeWorld) switch
            {
                ErrorCode.Success => new(true, "Submitted", "Lifestream accepted the logout and character-change workflow.", "CharacterSwitch"),
                ErrorCode.Plugin_is_busy => new(false, "NotReady", "Lifestream is already handling another operation.", "CharacterSwitch"),
                ErrorCode.Player_is_not_logged_in => new(false, "NotReady", "The logged-in character changed before Lifestream accepted the switch.", "CharacterSwitch"),
                ErrorCode.Invalid_world_specified => new(false, "Rejected", "Lifestream rejected the requested home world.", "CharacterSwitch"),
                var code => new(false, "Rejected", $"Lifestream rejected character switching with {code}.", "CharacterSwitch"),
            };
        }
        catch (Exception ex)
        {
            return new(false, "IpcFailure", ex.Message, "CharacterSwitch");
        }
    }
}
