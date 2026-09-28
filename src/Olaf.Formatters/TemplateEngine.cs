using System.Text;

namespace Olaf.Formatters;

/// <summary>
/// Issue #73: 1-based line number for template syntax errors.
/// </summary>
public sealed class TemplateSyntaxException : Exception
{
    public int Line { get; }

    public TemplateSyntaxException(int line, string message)
        : base(message)
    {
        Line = line;
    }
}

/// <summary>
/// Issue #73: minimal built-in mustache-ish renderer (~150 lines, zero deps).
/// Supports {{field}} (dotted paths), {{#each licenses}}…{{/each}},
/// {{#each groups}}…{{/each}} (nested {{#each items}}), {{#if field}}…{{/if}}
/// (no {{else}} v1). Unknown field/path renders empty (never throws, never
/// the null literal). No code execution, no includes/partials, no remote
/// fetch. Caps: input ≤ 256 KiB, expansion ≤ 1 MiB, loop iterations ≤ 10_000.
/// </summary>
public static class TemplateEngine
{
    public const int MaxTemplateBytes = 256 * 1024;
    public const int MaxOutputBytes = 1024 * 1024;
    public const int MaxIterations = 10_000;

    private abstract record Node;
    private sealed record TextNode(string Text) : Node;
    private sealed record VarNode(string Path, int Line) : Node;
    private sealed record EachNode(string Path, List<Node> Children, int Line) : Node;
    private sealed record IfNode(string Path, List<Node> Children, int Line) : Node;

    public static string Render(string template, TemplateModel model)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(model);
        if (Encoding.UTF8.GetByteCount(template) > MaxTemplateBytes)
        {
            throw new IOException($"Template exceeds {MaxTemplateBytes} bytes.");
        }

        var nodes = Parse(template);
        var sb = new StringBuilder();
        var iterations = 0;
        RenderNodes(nodes, [ToScope(model)], sb, ref iterations);
        var output = sb.ToString();
        if (Encoding.UTF8.GetByteCount(output) > MaxOutputBytes)
        {
            throw new IOException($"Template output exceeds {MaxOutputBytes} bytes.");
        }

        return output;
    }

    private static Dictionary<string, object?> ToScope(TemplateModel model)
    {
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["total"] = model.Total,
            ["resolved"] = model.Resolved,
            ["unknown"] = model.Unknown,
            ["generatedAt"] = model.GeneratedAt,
            ["toolVersion"] = model.ToolVersion,
            ["licenses"] = model.Licenses.Select(ToScope).ToList<object?>(),
            ["groups"] = model.Groups.Select(ToScope).ToList<object?>(),
        };
    }

    private static Dictionary<string, object?> ToScope(TemplateLicenseEntry l)
    {
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["name"] = l.Name,
            ["version"] = l.Version,
            ["ecosystem"] = l.Ecosystem,
            ["direct"] = l.Direct,
            ["spdx"] = l.Spdx,
            ["status"] = l.Status,
            ["reason"] = l.Reason,
            ["sourceUrl"] = l.SourceUrl,
            ["purl"] = l.Purl,
            ["supplier"] = l.Supplier,
            ["downloadUrl"] = l.DownloadUrl,
            ["hashes"] = l.Hashes,
            ["copyright"] = l.Copyright,
        };
    }

    private static Dictionary<string, object?> ToScope(TemplateGroup g)
    {
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["spdx"] = g.Spdx,
            ["count"] = g.Count,
            ["items"] = g.Items.Select(ToScope).ToList<object?>(),
        };
    }

    private static void RenderNodes(
        List<Node> nodes,
        List<Dictionary<string, object?>> stack,
        StringBuilder sb,
        ref int iterations)
    {
        foreach (var node in nodes)
        {
            switch (node)
            {
                case TextNode t:
                    sb.Append(t.Text);
                    ThrowIfOutputTooLarge(sb);
                    break;
                case VarNode v:
                    sb.Append(FormatValue(Resolve(v.Path, stack)));
                    ThrowIfOutputTooLarge(sb);
                    break;
                case IfNode n:
                    if (IsTruthy(Resolve(n.Path, stack)))
                    {
                        RenderNodes(n.Children, stack, sb, ref iterations);
                    }

                    break;
                case EachNode n:
                    var items = Resolve(n.Path, stack) as List<object?> ?? [];
                    foreach (var item in items)
                    {
                        iterations++;
                        if (iterations > MaxIterations)
                        {
                            throw new IOException($"Template exceeds {MaxIterations} loop iterations.");
                        }

                        stack.Add(item as Dictionary<string, object?> ?? []);
                        RenderNodes(n.Children, stack, sb, ref iterations);
                        stack.RemoveAt(stack.Count - 1);
                    }

                    break;
            }
        }
    }

    private static void ThrowIfOutputTooLarge(StringBuilder sb)
    {
        if (sb.Length > MaxOutputBytes)
        {
            throw new IOException($"Template output exceeds {MaxOutputBytes} bytes.");
        }
    }

    private static object? Resolve(string path, List<Dictionary<string, object?>> stack)
    {
        var segments = path.Split('.');
        for (var i = stack.Count - 1; i >= 0; i--)
        {
            if (!stack[i].TryGetValue(segments[0], out var current))
            {
                continue;
            }

            for (var s = 1; s < segments.Length; s++)
            {
                if (current is Dictionary<string, object?> dict
                    && dict.TryGetValue(segments[s], out var next))
                {
                    current = next;
                }
                else
                {
                    return null;
                }
            }

            return current;
        }

        return null;
    }

    private static bool IsTruthy(object? value)
    {
        return value switch
        {
            null => false,
            bool b => b,
            string s => s.Length > 0,
            int n => n != 0,
            List<object?> list => list.Count > 0,
            Dictionary<string, object?> => true,
            _ => true,
        };
    }

    private static string FormatValue(object? value)
    {
        return value switch
        {
            null => string.Empty,
            bool b => b ? "true" : "false",
            int n => n.ToString(System.Globalization.CultureInfo.InvariantCulture),
            string s => s,
            List<object?> => string.Empty,
            Dictionary<string, object?> => string.Empty,
            _ => string.Empty,
        };
    }

    private static int LineAt(string template, int index)
    {
        var line = 1;
        for (var i = 0; i < index && i < template.Length; i++)
        {
            if (template[i] == '\n')
            {
                line++;
            }
        }

        return line;
    }

    private static List<Node> Parse(string template)
    {
        var root = new List<Node>();
        var stack = new Stack<(string Kind, string Path, List<Node> Children, int Line)>();
        var current = root;
        var pos = 0;

        while (pos < template.Length)
        {
            var open = template.IndexOf("{{", pos, StringComparison.Ordinal);
            if (open < 0)
            {
                current.Add(new TextNode(template[pos..]));
                break;
            }

            if (open > pos)
            {
                current.Add(new TextNode(template[pos..open]));
            }

            var close = template.IndexOf("}}", open + 2, StringComparison.Ordinal);
            if (close < 0)
            {
                throw new TemplateSyntaxException(LineAt(template, open), "Unclosed tag.");
            }

            var tag = template[(open + 2)..close].Trim();
            var line = LineAt(template, open);
            if (tag.StartsWith("#each ", StringComparison.Ordinal))
            {
                var path = tag["#each ".Length..].Trim();
                if (path.Length == 0)
                {
                    throw new TemplateSyntaxException(line, "Empty {{#each}} path.");
                }

                stack.Push(("each", path, [], line));
                current = stack.Peek().Children;
            }
            else if (tag.StartsWith("#if ", StringComparison.Ordinal))
            {
                var path = tag["#if ".Length..].Trim();
                if (path.Length == 0)
                {
                    throw new TemplateSyntaxException(line, "Empty {{#if}} path.");
                }

                stack.Push(("if", path, [], line));
                current = stack.Peek().Children;
            }
            else if (tag.Equals("/each", StringComparison.Ordinal))
            {
                if (stack.Count == 0 || !stack.Peek().Kind.Equals("each", StringComparison.Ordinal))
                {
                    throw new TemplateSyntaxException(line, "Mismatched {{/each}}.");
                }

                var block = stack.Pop();
                Node node = new EachNode(block.Path, block.Children, block.Line);
                PeekCurrent(stack, root).Add(node);
                current = PeekCurrent(stack, root);
            }
            else if (tag.Equals("/if", StringComparison.Ordinal))
            {
                if (stack.Count == 0 || !stack.Peek().Kind.Equals("if", StringComparison.Ordinal))
                {
                    throw new TemplateSyntaxException(line, "Mismatched {{/if}}.");
                }

                var block = stack.Pop();
                Node node = new IfNode(block.Path, block.Children, block.Line);
                PeekCurrent(stack, root).Add(node);
                current = PeekCurrent(stack, root);
            }
            else if (tag.Length == 0)
            {
                throw new TemplateSyntaxException(line, "Empty tag.");
            }
            else if (tag.StartsWith("#", StringComparison.Ordinal) || tag.StartsWith("/", StringComparison.Ordinal))
            {
                throw new TemplateSyntaxException(line, $"Unknown block tag '{{{{{tag}}}}}'.");
            }
            else
            {
                current.Add(new VarNode(tag, line));
            }

            pos = close + 2;
        }

        if (stack.Count > 0)
        {
            var block = stack.Peek();
            throw new TemplateSyntaxException(block.Line, $"Unclosed '{{{{#{block.Kind}}}}}' block.");
        }

        return root;
    }

    private static List<Node> PeekCurrent(
        Stack<(string Kind, string Path, List<Node> Children, int Line)> stack,
        List<Node> root)
    {
        return stack.Count > 0 ? stack.Peek().Children : root;
    }
}
