using System.Text.RegularExpressions;

namespace MatSmtp.Tests.Web;

/// <summary>An icon name that is not in the sprite draws nothing (no error): every name the code uses must exist.</summary>
public sealed partial class IconTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MatSmtp.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("The solution folder was not found.");
    }

    [Fact]
    public void Every_icon_that_a_page_or_a_class_asks_for_is_in_the_sprite()
    {
        var web = Path.Combine(Root(), "src", "MatSmtp");
        var sprite = File.ReadAllText(Path.Combine(web, "wwwroot", "ui", "matui-icons.svg")) + File.ReadAllText(Path.Combine(web, "wwwroot", "ui", "ms-icons.svg"));
        var have = SymbolRegex().Matches(sprite).Select(m => m.Groups[1].Value).ToHashSet();
        var missing = new SortedSet<string>();

        var skip = new[] { $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", "Migrations" };
        var files = Directory.EnumerateFiles(web, "*.*", SearchOption.AllDirectories).Where(f => (f.EndsWith(".cs") || f.EndsWith(".cshtml")) && !skip.Any(f.Contains));
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            foreach (var pattern in new[] { IconCall(), IconAttribute(), EmptyIcon(), RowActionIcon() })
            {
                foreach (Match m in pattern.Matches(text))
                {
                    if (!have.Contains(m.Groups[1].Value))
                    {
                        missing.Add($"{m.Groups[1].Value} ({Path.GetFileName(file)})");
                    }
                }
            }
        }

        Assert.True(missing.Count == 0, "Icons that are not in the sprite: " + string.Join(", ", missing));
    }

    [Fact]
    public void The_sprite_has_no_icon_twice()
    {
        var web = Path.Combine(Root(), "src", "MatSmtp", "wwwroot", "ui");
        var ids = SymbolRegex().Matches(File.ReadAllText(Path.Combine(web, "matui-icons.svg")) + File.ReadAllText(Path.Combine(web, "ms-icons.svg"))).Select(m => m.Groups[1].Value).ToList();
        Assert.Empty(ids.GroupBy(i => i).Where(g => g.Count() > 1).Select(g => g.Key));
    }

    [GeneratedRegex("id=\"mat-i-([a-z0-9-]+)\"")]
    private static partial Regex SymbolRegex();

    [GeneratedRegex("Icon\\(\"([a-z0-9-]+)\"")]
    private static partial Regex IconCall();

    [GeneratedRegex("(?<![\\w-])icon=\"([a-z0-9-]+)\"")]
    private static partial Regex IconAttribute();

    [GeneratedRegex("EmptyIcon = \"([a-z0-9-]+)\"")]
    private static partial Regex EmptyIcon();

    [GeneratedRegex("RowAction\\(\"([a-z0-9-]+)\"")]
    private static partial Regex RowActionIcon();
}
