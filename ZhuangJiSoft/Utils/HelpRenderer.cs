using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace DeskApp.Utils;

/// <summary>
/// 轻量 Markdown → FlowDocument 渲染器，仅覆盖使用文档中用到的语法，
/// 不引入第三方依赖：
///   # 一级标题 / ## 小节 / &gt; 提示块 / 数字. 有序列表 / - 无序列表 /
///   **加粗** 与 `行内代码`。
/// </summary>
public static class HelpRenderer
{
    private static readonly Regex InlineRegex = new(@"(\*\*.+?\*\*|`[^`]+`)", RegexOptions.Compiled);
    private static readonly Regex OrderedRegex = new(@"^\s*\d+\.\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex BulletRegex = new(@"^\s*[-*]\s+(.*)$", RegexOptions.Compiled);

    private static readonly Brush TextBrush = new SolidColorBrush(Color.FromRgb(0x1F, 0x1F, 0x1F));
    private static readonly Brush TitleBrush = new SolidColorBrush(Color.FromRgb(0x0F, 0x6C, 0xBD));
    private static readonly Brush MutedBrush = new SolidColorBrush(Color.FromRgb(0x5A, 0x64, 0x72));
    private static readonly Brush TipBg = new SolidColorBrush(Color.FromRgb(0xEF, 0xF6, 0xFD));
    private static readonly Brush TipBorder = new SolidColorBrush(Color.FromRgb(0x0F, 0x6C, 0xBD));
    private static readonly Brush CodeBg = new SolidColorBrush(Color.FromRgb(0xF2, 0xF4, 0xF7));

    public static FlowDocument Render(string? markdown)
    {
        var doc = new FlowDocument
        {
            FontFamily = new FontFamily("Microsoft YaHei"),
            FontSize = 13,
            TextAlignment = TextAlignment.Left,
            PagePadding = new Thickness(4, 6, 4, 16),
            LineHeight = 1.55
        };
        doc.Blocks.Clear();

        if (string.IsNullOrWhiteSpace(markdown))
        {
            doc.Blocks.Add(MakeParagraph("该模块暂无使用说明。", MutedBrush, 13, false));
            return doc;
        }

        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var i = 0;
        while (i < lines.Length)
        {
            var line = lines[i];
            var trimmed = line.Trim();

            if (trimmed.Length == 0)
            {
                i++;
                continue;
            }

            if (trimmed.StartsWith("# "))
            {
                var h = MakeParagraph(trimmed.Substring(2).Trim(), TitleBrush, 20, true);
                h.Margin = new Thickness(0, 0, 0, 10);
                doc.Blocks.Add(h);
                i++;
                continue;
            }

            if (trimmed.StartsWith("## "))
            {
                var h = MakeParagraph(trimmed.Substring(3).Trim(), TextBrush, 15, true);
                h.Margin = new Thickness(0, 14, 0, 6);
                doc.Blocks.Add(h);
                i++;
                continue;
            }

            if (trimmed.StartsWith("> "))
            {
                var tip = MakeParagraph(trimmed.Substring(2).Trim(), MutedBrush, 12, false);
                tip.Background = TipBg;
                tip.BorderBrush = TipBorder;
                tip.BorderThickness = new Thickness(3, 0, 0, 0);
                tip.Padding = new Thickness(10, 7, 8, 7);
                tip.Margin = new Thickness(0, 10, 0, 4);
                doc.Blocks.Add(tip);
                i++;
                continue;
            }

            if (OrderedRegex.IsMatch(line) || BulletRegex.IsMatch(line))
            {
                var ordered = OrderedRegex.IsMatch(line);
                var list = new List
                {
                    MarkerStyle = ordered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                    Margin = new Thickness(12, 2, 0, 8),
                    Padding = new Thickness(8, 0, 0, 0)
                };

                while (i < lines.Length)
                {
                    var l = lines[i];
                    var mOrdered = OrderedRegex.Match(l);
                    var mBullet = BulletRegex.Match(l);
                    var isOrdered = mOrdered.Success;
                    var matched = isOrdered ? mOrdered : mBullet;
                    if (!matched.Success || isOrdered != ordered)
                    {
                        break;
                    }

                    var item = new ListItem();
                    var p = new Paragraph { Margin = new Thickness(0, 0, 0, 3), LineHeight = 1.45 };
                    AddInline(p, matched.Groups[1].Value);
                    item.Blocks.Add(p);
                    list.ListItems.Add(item);
                    i++;
                }
                doc.Blocks.Add(list);
                continue;
            }

            var para = MakeParagraph(string.Empty, TextBrush, 13, false);
            para.Margin = new Thickness(0, 0, 0, 6);
            AddInline(para, trimmed);
            doc.Blocks.Add(para);
            i++;
        }

        return doc;
    }

    private static Paragraph MakeParagraph(string text, Brush brush, double size, bool bold)
    {
        var p = new Paragraph { Foreground = brush, FontSize = size };
        if (bold)
        {
            p.FontWeight = FontWeights.SemiBold;
        }
        if (text.Length > 0)
        {
            p.Inlines.Add(new Run(text));
        }
        return p;
    }

    private static void AddInline(Paragraph p, string text)
    {
        var pos = 0;
        foreach (Match part in InlineRegex.Matches(text))
        {
            if (part.Index > pos)
            {
                p.Inlines.Add(new Run(text.Substring(pos, part.Index - pos)));
            }

            var token = part.Value;
            if (token.StartsWith("**") && token.EndsWith("**"))
            {
                p.Inlines.Add(new Run(token.Substring(2, token.Length - 4))
                {
                    FontWeight = FontWeights.SemiBold
                });
            }
            else if (token.StartsWith("`") && token.EndsWith("`"))
            {
                p.Inlines.Add(new Run(token.Substring(1, token.Length - 2))
                {
                    FontFamily = new FontFamily("Consolas"),
                    Background = CodeBg,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B))
                });
            }
            else
            {
                p.Inlines.Add(new Run(token));
            }

            pos = part.Index + part.Length;
        }

        if (pos < text.Length)
        {
            p.Inlines.Add(new Run(text.Substring(pos)));
        }
    }
}
