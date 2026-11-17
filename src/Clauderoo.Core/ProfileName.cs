using System.Text.RegularExpressions;

namespace Clauderoo.Core;

public static partial class ProfileName
{
    [GeneratedRegex(@"^[a-z0-9-]+\z")]
    private static partial Regex Valid();

    public static void Validate(string name)
    {
        // A leading - would be read as an option by the CLI (cr start -x).
        if (name is not null && name.StartsWith('-'))
            throw new ClauderooException("Names can't start with -.");
        if (name is null || !Valid().IsMatch(name))
            throw new ClauderooException(
                $"Invalid profile name '{name}'. Use lowercase letters, digits and '-' only.");
    }
}
