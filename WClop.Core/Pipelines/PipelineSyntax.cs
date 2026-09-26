using System.Globalization;
using System.Text;

namespace WClop.Core.Pipelines;

/// <summary>A value as written: <c>"text"</c>, <c>0.5</c>, <c>10MB</c>, <c>50%</c>, <c>webp</c>, <c>true</c>.</summary>
public sealed record PipelineValue(string Text, bool Quoted, int Position)
{
    public override string ToString() => Quoted ? $"\"{Text.Replace("\"", "\\\"")}\"" : Text;
}

/// <summary>One step: <c>name(arg: value, …)</c>. An argument without a name gets the step's default name.</summary>
public sealed record PipelineStep(string Name, IReadOnlyList<(string? Name, PipelineValue Value)> Args, int Position)
{
    public override string ToString() => Args.Count == 0
        ? Name
        : $"{Name}({string.Join(", ", Args.Select(a => a.Name is null ? a.Value.ToString() : $"{a.Name}: {a.Value}"))})";
}

public sealed class PipelineSyntaxException(string message, int position) : Exception(message)
{
    public int Position { get; } = position;
}

/// <summary>
/// Parses the pipeline language (project.md §19): steps joined by <c>-&gt;</c>, e.g.
/// <c>if(regex: "^screenshot") -&gt; downscale(longEdge: 1920) -&gt; convert(to: webp) -&gt; move(to: "~/Pictures/Web/")</c>.
/// Newlines are just whitespace and <c>#</c> starts a comment.
/// </summary>
public static class PipelineSyntax
{
    public static IReadOnlyList<PipelineStep> Parse(string text)
    {
        var tokens = Tokenise(text);
        var steps = new List<PipelineStep>();
        var i = 0;

        Token Peek() => tokens[i];
        Token Next() => tokens[i++];
        Token Expect(TokenKind kind, string what)
        {
            var token = Next();
            if (token.Kind != kind)
                throw new PipelineSyntaxException($"Expected {what} {Describe(token)}", token.Position);
            return token;
        }

        if (Peek().Kind == TokenKind.End)
            throw new PipelineSyntaxException("The pipeline is empty", 0);

        while (true)
        {
            var name = Expect(TokenKind.Word, "a step name");
            var args = new List<(string?, PipelineValue)>();
            if (Peek().Kind == TokenKind.Open)
            {
                Next();
                while (Peek().Kind != TokenKind.Close)
                {
                    string? argName = null;
                    if (Peek().Kind == TokenKind.Word && tokens[i + 1].Kind == TokenKind.Colon)
                    {
                        argName = Next().Text;
                        Next();
                    }

                    var value = Next();
                    if (value.Kind is not (TokenKind.Word or TokenKind.String))
                        throw new PipelineSyntaxException($"Expected a value {Describe(value)}", value.Position);
                    args.Add((argName, new PipelineValue(value.Text, value.Kind == TokenKind.String, value.Position)));

                    if (Peek().Kind == TokenKind.Comma)
                        Next();
                    else if (Peek().Kind != TokenKind.Close)
                        throw new PipelineSyntaxException($"Expected ',' or ')' {Describe(Peek())}", Peek().Position);
                }

                Next();
            }

            steps.Add(new PipelineStep(name.Text, args, name.Position));
            var separator = Next();
            if (separator.Kind == TokenKind.End)
                return steps;
            if (separator.Kind != TokenKind.Arrow)
                throw new PipelineSyntaxException($"Expected '->' between steps {Describe(separator)}", separator.Position);
        }
    }

    /// <summary>Writes steps back as text, one per line.</summary>
    public static string Format(IEnumerable<PipelineStep> steps) => string.Join("\n-> ", steps);

    private static string Describe(Token token) => token.Kind switch
    {
        TokenKind.End => "but the pipeline ended",
        TokenKind.String => $"but found \"{token.Text}\"",
        _ => $"but found '{token.Text}'",
    };

    private enum TokenKind
    {
        Word,
        String,
        Open,
        Close,
        Comma,
        Colon,
        Arrow,
        End,
    }

    private sealed record Token(TokenKind Kind, string Text, int Position);

    private static List<Token> Tokenise(string text)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
            }
            else if (c == '#')
            {
                while (i < text.Length && text[i] != '\n')
                    i++;
            }
            else if (c == '-' && i + 1 < text.Length && text[i + 1] == '>')
            {
                tokens.Add(new Token(TokenKind.Arrow, "->", i));
                i += 2;
            }
            else if (c == '→')
            {
                tokens.Add(new Token(TokenKind.Arrow, "->", i++));
            }
            else if (c is '(' or ')' or ',' or ':')
            {
                tokens.Add(new Token(c switch
                {
                    '(' => TokenKind.Open,
                    ')' => TokenKind.Close,
                    ',' => TokenKind.Comma,
                    _ => TokenKind.Colon,
                }, c.ToString(), i++));
            }
            else if (c is '"' or '\'')
            {
                var start = i++;
                var value = new StringBuilder();
                while (true)
                {
                    if (i >= text.Length)
                        throw new PipelineSyntaxException("A quoted value is never closed", start);
                    // \" is a quote inside the value, except where that quote plainly ends it: Windows folders end
                    // in a backslash ("C:\Out\"), so a quote followed by ) , -> # or the end of a line closes the value.
                    if (text[i] == '\\' && i + 1 < text.Length && text[i + 1] == c && !EndsValue(text, i + 2))
                    {
                        value.Append(c);
                        i += 2;
                    }
                    else if (text[i] == c)
                    {
                        i++;
                        break;
                    }
                    else
                    {
                        value.Append(text[i++]);
                    }
                }

                tokens.Add(new Token(TokenKind.String, value.ToString(), start));
            }
            else if (IsWordChar(c))
            {
                var start = i;
                while (i < text.Length && IsWordChar(text[i]) && !(text[i] == '-' && i + 1 < text.Length && text[i + 1] == '>'))
                    i++;
                tokens.Add(new Token(TokenKind.Word, text[start..i], start));
            }
            else
            {
                throw new PipelineSyntaxException($"Unexpected '{c}'", i);
            }
        }

        tokens.Add(new Token(TokenKind.End, "", text.Length));
        return tokens;
    }

    private static bool EndsValue(string text, int index)
    {
        while (index < text.Length && text[index] is ' ' or '\t')
            index++;
        return index >= text.Length || text[index] is ')' or ',' or '\r' or '\n' or '#'
               || text[index] == '-' && index + 1 < text.Length && text[index + 1] == '>';
    }

    // Unquoted values: names, numbers, sizes (10MB), percentages, ratios need quotes ("16:9").
    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '.' or '%' or '-' or '+';

    /// <summary>1-based line and column of a position, for error messages.</summary>
    public static (int Line, int Column) LineAndColumn(string text, int position)
    {
        position = Math.Clamp(position, 0, text.Length);
        var line = 1 + text[..position].Count(c => c == '\n');
        var column = position - (text.LastIndexOf('\n', Math.Max(0, position - 1)) + 1) + 1;
        return (line, column);
    }

    internal static string Invariant(double value) => value.ToString(CultureInfo.InvariantCulture);
}
