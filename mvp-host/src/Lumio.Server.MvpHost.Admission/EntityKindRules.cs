using System.Text.RegularExpressions;

namespace Lumio.Server.MvpHost.Admission;

internal static partial class EntityKindRules
{
    public static bool IsBotNamespace(string loginName) => BotNamespace().IsMatch(loginName);

    public static BoundEntityKind Classify(string loginName, bool botToolContext)
    {
        return IsBotNamespace(loginName) && botToolContext
            ? BoundEntityKind.Bot
            : BoundEntityKind.Player;
    }

    [GeneratedRegex("^Bot[0-9]+$")]
    private static partial Regex BotNamespace();
}
