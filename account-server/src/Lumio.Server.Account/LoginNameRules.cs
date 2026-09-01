using System.Text.RegularExpressions;

namespace Lumio.Server.Account;

internal static partial class LoginNameRules
{
    public static bool IsValid(string loginName)
    {
        return loginName.Length >= AccountPort.LoginNameMinLength
            && loginName.Length <= AccountPort.LoginNameMaxLength
            && Grammar().IsMatch(loginName);
    }

    public static bool IsBotNamespace(string loginName) => BotNamespace().IsMatch(loginName);

    [GeneratedRegex(AccountPort.LoginNamePattern)]
    private static partial Regex Grammar();

    [GeneratedRegex(AccountPort.BotNamespacePattern)]
    private static partial Regex BotNamespace();
}
