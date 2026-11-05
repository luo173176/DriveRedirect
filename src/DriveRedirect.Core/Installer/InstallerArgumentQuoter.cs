using System.Text;

namespace DriveRedirect.Core.Installer;

public static class InstallerArgumentQuoter
{
    public static string Quote(string argument)
    {
        if (argument.Length == 0)
            return "\"\"";
        if (!argument.Any(ch => char.IsWhiteSpace(ch) || ch == '"'))
            return argument;

        var sb = new StringBuilder();
        sb.Append('"');
        var backslashes = 0;
        foreach (var ch in argument)
        {
            if (ch == '\\')
            {
                backslashes++;
                continue;
            }
            if (ch == '"')
            {
                sb.Append('\\', backslashes * 2 + 1);
                sb.Append('"');
                backslashes = 0;
            }
            else
            {
                sb.Append('\\', backslashes);
                sb.Append(ch);
                backslashes = 0;
            }
        }
        sb.Append('\\', backslashes * 2);
        sb.Append('"');
        return sb.ToString();
    }

    public static string Join(IEnumerable<string> arguments)
        => string.Join(' ', arguments.Select(Quote));
}
