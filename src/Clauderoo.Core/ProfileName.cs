using System.Text.RegularExpressions;

namespace Clauderoo.Core;

public static partial class ProfileName
{
    [GeneratedRegex("^[a-z0-9-]+$")]
    private static partial Regex Valid();

    public static void Validate(string name)
    {
        if (!Valid().IsMatch(name))
            throw new ClauderooException(
                $"Invalid profile name '{name}'. Use lowercase letters, digits and '-' only.");
    }
}
