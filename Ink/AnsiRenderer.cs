using System.Text;

namespace DungeonsOfSkaraBrae.Ink;

public static class AnsiRenderer
{
    public const string Reset = "\x1b[0m";

    public static string Line(string text, IList<string>? tags)
    {
        var prefix = new StringBuilder();
        if (tags is not null)
        {
            foreach (var t in tags) prefix.Append(TagToAnsi(t));
        }
        return prefix.Length == 0 ? text : prefix + text + Reset;
    }

    public static string Choice(int index, string text)
        => $"\x1b[1;93m\x1b]8;;ink://choice/{index}\x1b\\{text}\x1b]8;;\x1b\\{Reset}";

    public static string Warning(string text)
        => $"\x1b[2;31m{text}{Reset}";

    public static string Notice(string text)
        => $"\x1b[2;36m{text}{Reset}";

    private static string TagToAnsi(string tag)
    {
        var t = tag.Trim().ToLowerInvariant();
        switch (t)
        {
            case "bold":  return "\x1b[1m";
            case "dim":   return "\x1b[2m";
            case "clear": return Reset;
        }
        if (t.StartsWith("color:"))
        {
            return t[6..] switch
            {
                "red"     => "\x1b[31m",
                "green"   => "\x1b[32m",
                "yellow"  => "\x1b[33m",
                "blue"    => "\x1b[34m",
                "magenta" => "\x1b[35m",
                "cyan"    => "\x1b[36m",
                "white"   => "\x1b[37m",
                "gray"    => "\x1b[90m",
                _ => "",
            };
        }
        return "";
    }
}
