using System.Text;

namespace EFCore.ComplexIndexes.Tests.TranslatorParity;

/// <summary>
/// Parses a PostgreSQL scalar expression the way PostgreSQL's grammar does and prints it back in one
/// spelling, so two renderings compare equal exactly when PostgreSQL builds the same tree from them.
/// </summary>
/// <remarks>
/// The parity sweep compares the package's SQL with EF Core's for the same lambda. Comparing text
/// fails on things PostgreSQL never sees: a table alias, redundant parentheses, <c>x::int</c> against
/// <c>CAST(x AS integer)</c>, <c>int4</c> against <c>integer</c>. Stripping parentheses instead, as the
/// earlier piecemeal comparisons did, also hides a real difference: <c>(a OR b) AND c</c> and
/// <c>a OR b AND c</c> have the same tokens. So both sides are parsed with PostgreSQL's precedence
/// (<c>OR</c> &lt; <c>AND</c> &lt; <c>NOT</c> &lt; <c>IS</c> &lt; comparison &lt; other operators such as
/// <c>||</c> and <c>-&gt;&gt;</c> &lt; <c>+ -</c> &lt; <c>* / %</c> &lt; unary minus &lt; <c>::</c>) and printed fully
/// parenthesized. <c>AND</c> and <c>OR</c> chains are flattened, as the planner flattens them;
/// every other operator keeps its associativity, because an expression index matches only the
/// identical tree, and <c>(a || b) || c</c> is not <c>a || (b || c)</c> to it.
/// <para>
/// Beyond the parse, the planner rewrites some expressions before it compares an index with a
/// query (<c>eval_const_expressions</c>). Those rewrites are <see cref="Rules"/>, applied only when
/// the waiver file switches them on with its reason, so each one is a stated decision rather than a
/// silent normalization.
/// </para>
/// </remarks>
internal static class PostgresCanonicalForm
{
    /// <summary>
    /// The planner's rewrites a waiver may switch on: <c>negator</c> (<c>negate_clause</c>: NOT over a
    /// comparison becomes its negator, NOT is pushed through AND/OR and cancels itself),
    /// <c>boolean-equality</c> (<c>simplify_boolean_equality</c>: <c>x = TRUE</c> is <c>x</c>,
    /// <c>x = FALSE</c> is <c>NOT x</c>), and <c>integer-length</c> (<c>length()</c> returns
    /// <c>integer</c>, so a cast of it to <c>integer</c> is no node at all).
    /// </summary>
    public static readonly IReadOnlySet<string> Rules = new HashSet<string> { "negator", "boolean-equality", "integer-length" };

    public static string Of(string sql, IReadOnlySet<string>? rules = null)
    {
        var parser = new Parser(Tokenize(sql));
        var node   = parser.ParseExpression();
        parser.ExpectEnd(sql);

        if (rules is not { Count: > 0 })
            return node.ToString();

        // To a fixed point: x <> TRUE becomes NOT x, which the negator rule may then take further.
        while (true)
        {
            var rewritten = Rewrite(node, rules);
            if (rewritten.ToString() == node.ToString())
                return rewritten.ToString();
            node = rewritten;
        }
    }

    private static readonly Dictionary<string, string> Negators = new()
    {
        ["="] = "<>", ["<>"] = "=", ["<"] = ">=", [">="] = "<", [">"] = "<=", ["<="] = ">",
    };

    private static Node Rewrite(Node node, IReadOnlySet<string> rules)
    {
        node = node switch
        {
            Binary binary   => binary with { Left = Rewrite(binary.Left, rules), Right = Rewrite(binary.Right, rules) },
            Logical logical => Logical.Of(logical.Operator, logical.Operands.Select(o => Rewrite(o, rules))),
            Prefix prefix   => prefix with { Operand = Rewrite(prefix.Operand, rules) },
            Is test         => test with { Operand = Rewrite(test.Operand, rules) },
            Call call       => call with { Arguments = call.Arguments.Select(a => Rewrite(a, rules)).ToList() },
            Cast cast       => cast with { Operand = Rewrite(cast.Operand, rules) },
            Case @case      => new Case(@case.Branches.Select(b => (Rewrite(b.When, rules), Rewrite(b.Then, rules))).ToList(),
                                        @case.Else is null ? null : Rewrite(@case.Else, rules)),
            _               => node,
        };

        if (rules.Contains("negator") && node is Prefix { Operator: "NOT", Operand: var negated })
        {
            switch (negated)
            {
                case Binary binary when Negators.TryGetValue(binary.Operator, out var negator):
                    return binary with { Operator = negator };
                case Prefix { Operator: "NOT", Operand: var inner }:
                    return inner;
                case Is { Test: "NULL" or "NOT NULL" } test:
                    return test with { Test = test.Test == "NULL" ? "NOT NULL" : "NULL" };
                case Logical logical:
                    return Logical.Of(logical.Operator == "AND" ? "OR" : "AND", logical.Operands.Select(o => (Node)new Prefix("NOT", o)));
            }
        }

        if (rules.Contains("boolean-equality") && node is Binary { Operator: "=" or "<>" } equality)
        {
            var (operand, constant) = equality.Right is Leaf { Text: "TRUE" or "FALSE" } right ? (equality.Left, right.Text)
                                    : equality.Left is Leaf { Text: "TRUE" or "FALSE" } left   ? (equality.Right, left.Text)
                                                                                                : (null, null);
            if (operand is not null)
                return (constant == "TRUE") == (equality.Operator == "=") ? operand : new Prefix("NOT", operand);
        }

        if (rules.Contains("integer-length") && node is Cast { Type: "integer", Operand: Call { Name: "length" } length })
            return length;

        return node;
    }

    // ── Tokens ──

    private enum TokenKind { Word, QuotedIdentifier, String, Number, Symbol, End }

    private sealed record Token(TokenKind Kind, string Text)
    {
        public bool IsWord(string word) => Kind == TokenKind.Word && string.Equals(Text, word, StringComparison.OrdinalIgnoreCase);

        public bool IsSymbol(string symbol) => Kind == TokenKind.Symbol && Text == symbol;
    }

    private static readonly string[] Symbols =
        ["->>", "#>>", "::", "->", "#>", "||", "<>", "!=", "<=", ">=", "@>", "<@", "=", "<", ">", "+", "-", "*", "/", "%", "^",
         "(", ")", ",", "[", "]", "."];

    private static List<Token> Tokenize(string sql)
    {
        var tokens = new List<Token>();
        var i      = 0;

        while (i < sql.Length)
        {
            var ch = sql[i];

            if (char.IsWhiteSpace(ch))
            {
                i++;
                continue;
            }

            if ((ch is 'E' or 'e') && i + 1 < sql.Length && sql[i + 1] == '\'')
            {
                i++;
                tokens.Add(new Token(TokenKind.String, ReadQuoted(sql, ref i, '\'', escapes: true)));
                continue;
            }

            if (ch == '\'')
            {
                tokens.Add(new Token(TokenKind.String, ReadQuoted(sql, ref i, '\'', escapes: false)));
                continue;
            }

            if (ch == '"')
            {
                tokens.Add(new Token(TokenKind.QuotedIdentifier, ReadQuoted(sql, ref i, '"', escapes: false)));
                continue;
            }

            if (char.IsDigit(ch))
            {
                var start = i;
                while (i < sql.Length && (char.IsDigit(sql[i]) || sql[i] == '.' && i + 1 < sql.Length && char.IsDigit(sql[i + 1])))
                    i++;
                if (i < sql.Length && sql[i] is 'e' or 'E' && i + 1 < sql.Length && (char.IsDigit(sql[i + 1]) || sql[i + 1] is '+' or '-'))
                {
                    i += 2;
                    while (i < sql.Length && char.IsDigit(sql[i]))
                        i++;
                }

                tokens.Add(new Token(TokenKind.Number, sql[start..i]));
                continue;
            }

            if (char.IsLetter(ch) || ch is '_' or '@')
            {
                var start = i;
                i++;
                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] == '_'))
                    i++;
                tokens.Add(new Token(TokenKind.Word, sql[start..i]));
                continue;
            }

            var symbol = Symbols.FirstOrDefault(s => string.CompareOrdinal(sql, i, s, 0, s.Length) == 0)
                      ?? throw new FormatException($"Unexpected '{ch}' at {i} in: {sql}");
            tokens.Add(new Token(TokenKind.Symbol, symbol));
            i += symbol.Length;
        }

        tokens.Add(new Token(TokenKind.End, ""));
        return tokens;
    }

    // Returns the value, not the spelling: 'a''b' and E'a\'b' are the same constant to PostgreSQL.
    private static string ReadQuoted(string sql, ref int i, char quote, bool escapes)
    {
        var value = new StringBuilder();
        i++;

        while (true)
        {
            if (i >= sql.Length)
                throw new FormatException($"Unterminated {quote} in: {sql}");

            var ch = sql[i];

            if (escapes && ch == '\\' && i + 1 < sql.Length)
            {
                value.Append(sql[i + 1] switch { 't' => '\t', 'n' => '\n', 'r' => '\r', 'b' => '\b', 'f' => '\f', var other => other });
                i += 2;
                continue;
            }

            if (ch == quote)
            {
                if (i + 1 < sql.Length && sql[i + 1] == quote)
                {
                    value.Append(quote);
                    i += 2;
                    continue;
                }

                i++;
                return value.ToString();
            }

            value.Append(ch);
            i++;
        }
    }

    // ── Tree ──

    private abstract record Node;

    private sealed record Leaf(string Text) : Node
    {
        public override string ToString() => Text;
    }

    private sealed record Binary(string Operator, Node Left, Node Right) : Node
    {
        public override string ToString() => $"({Left} {Operator} {Right})";
    }

    private sealed record Logical(string Operator, IReadOnlyList<Node> Operands) : Node
    {
        // The planner flattens AND/OR chains whatever their parentheses, so the canonical form does too.
        public static Node Of(string op, IEnumerable<Node> operands)
        {
            var flat = operands.SelectMany(o => o is Logical l && l.Operator == op ? l.Operands : [o]).ToList();
            return flat.Count == 1 ? flat[0] : new Logical(op, flat);
        }

        public override string ToString() => "(" + string.Join($" {Operator} ", Operands) + ")";
    }

    private sealed record Prefix(string Operator, Node Operand) : Node
    {
        public override string ToString() => $"{Operator}({Operand})";
    }

    private sealed record Is(Node Operand, string Test) : Node
    {
        public override string ToString() => $"({Operand} IS {Test})";
    }

    private sealed record Call(string Name, IReadOnlyList<Node> Arguments) : Node
    {
        public override string ToString() => $"{Name}({string.Join(", ", Arguments)})";
    }

    private sealed record Cast(Node Operand, string Type) : Node
    {
        public override string ToString() => $"CAST({Operand} AS {Type})";
    }

    private sealed record Case(IReadOnlyList<(Node When, Node Then)> Branches, Node? Else) : Node
    {
        public override string ToString()
            => "CASE" + string.Concat(Branches.Select(b => $" WHEN {b.When} THEN {b.Then}")) + (Else is null ? "" : $" ELSE {Else}") + " END";
    }

    // ── Grammar, by PostgreSQL's operator precedence ──

    private sealed class Parser(List<Token> tokens)
    {
        private int _position;

        private Token Current => tokens[_position];

        private Token Next() => tokens[_position++];

        private void Expect(Func<Token, bool> match, string what)
        {
            if (!match(Current))
                throw new FormatException($"Expected {what} at '{Current.Text}'.");
            _position++;
        }

        public void ExpectEnd(string sql)
        {
            if (Current.Kind != TokenKind.End)
                throw new FormatException($"Unexpected '{Current.Text}' after a complete expression in: {sql}");
        }

        public Node ParseExpression() => ParseOr();

        private Node ParseOr() => ParseChain("OR", ParseAnd);

        private Node ParseAnd() => ParseChain("AND", ParseNot);

        private Node ParseChain(string keyword, Func<Node> operand)
        {
            var operands = new List<Node> { operand() };
            while (Current.IsWord(keyword))
            {
                _position++;
                operands.Add(operand());
            }

            return Logical.Of(keyword, operands);
        }

        private Node ParseNot()
        {
            if (!Current.IsWord("NOT"))
                return ParseIs();

            _position++;
            return new Prefix("NOT", ParseNot());
        }

        private Node ParseIs()
        {
            var operand = ParseComparison();

            while (Current.IsWord("IS"))
            {
                _position++;
                var not = Current.IsWord("NOT");
                if (not)
                    _position++;

                var test = Next();
                operand = new Is(operand, (not ? "NOT " : "") + test.Text.ToUpperInvariant());
            }

            return operand;
        }

        private Node ParseComparison()
        {
            var left = ParseOther();

            if (Current.Kind == TokenKind.Symbol && Current.Text is "=" or "<>" or "!=" or "<" or ">" or "<=" or ">=")
            {
                var op = Next().Text;
                return new Binary(op == "!=" ? "<>" : op, left, ParseOther());
            }

            if (Current.IsWord("LIKE") || Current.IsWord("ILIKE"))
            {
                var op = Next().Text.ToUpperInvariant();
                return new Binary(op, left, ParseOther());
            }

            return left;
        }

        // Every operator PostgreSQL has no dedicated precedence for: ||, ->>, #>>, ->, #>, @>, <@.
        private Node ParseOther()
        {
            var left = ParseAdditive();
            while (Current.Kind == TokenKind.Symbol && Current.Text is "||" or "->>" or "#>>" or "->" or "#>" or "@>" or "<@")
            {
                var op = Next().Text;
                left = new Binary(op, left, ParseAdditive());
            }

            return left;
        }

        private Node ParseAdditive()
        {
            var left = ParseMultiplicative();
            while (Current.IsSymbol("+") || Current.IsSymbol("-"))
            {
                var op = Next().Text;
                left = new Binary(op, left, ParseMultiplicative());
            }

            return left;
        }

        private Node ParseMultiplicative()
        {
            var left = ParseUnary();
            while (Current.IsSymbol("*") || Current.IsSymbol("/") || Current.IsSymbol("%"))
            {
                var op = Next().Text;
                left = new Binary(op, left, ParseUnary());
            }

            return left;
        }

        private Node ParseUnary()
        {
            if (!Current.IsSymbol("-"))
                return ParsePostfix();

            _position++;
            var operand = ParseUnary();

            // PostgreSQL folds a minus in front of a numeric constant into the constant.
            return operand is Leaf { Text: var number } && number.Length > 0 && char.IsDigit(number[0])
                       ? new Leaf("-" + number)
                       : new Prefix("-", operand);
        }

        private Node ParsePostfix()
        {
            var node = ParsePrimary();

            while (true)
            {
                if (Current.IsSymbol("::"))
                {
                    _position++;
                    node = TypedLiteral(node, ParseType());
                }
                else if (Current.IsSymbol("["))
                {
                    _position++;
                    var index = ParseExpression();
                    Expect(t => t.IsSymbol("]"), "]");
                    node = new Binary("[]", node, index);
                }
                else
                    return node;
            }
        }

        private Node ParsePrimary()
        {
            var token = Next();

            switch (token.Kind)
            {
                case TokenKind.Number:
                    return new Leaf(token.Text);

                case TokenKind.String:
                    return new Leaf("'" + token.Text.Replace("'", "''") + "'");

                case TokenKind.QuotedIdentifier:
                    return new Leaf("\"" + token.Text + "\"");

                case TokenKind.Symbol when token.Text == "(":
                {
                    var inner = ParseExpression();
                    Expect(t => t.IsSymbol(")"), ")");
                    return inner;
                }

                case TokenKind.Word:
                    return ParseWord(token);
            }

            throw new FormatException($"Unexpected '{token.Text}'.");
        }

        private Node ParseWord(Token word)
        {
            // A table alias: p."Name" is the column "Name" to an expression over one table.
            if (Current.IsSymbol(".") && tokens[_position + 1].Kind == TokenKind.QuotedIdentifier)
            {
                _position++;
                return ParsePrimary();
            }

            var upper = word.Text.ToUpperInvariant();

            switch (upper)
            {
                case "TRUE" or "FALSE" or "NULL":
                    return new Leaf(upper);

                case "CAST":
                {
                    Expect(t => t.IsSymbol("("), "(");
                    var operand = ParseExpression();
                    Expect(t => t.IsWord("AS"), "AS");
                    var type = ParseType();
                    Expect(t => t.IsSymbol(")"), ")");
                    return TypedLiteral(operand, type);
                }

                case "ARRAY":
                {
                    Expect(t => t.IsSymbol("["), "[");
                    var elements = ParseList("]");
                    return new Call("ARRAY", elements);
                }

                case "CASE":
                {
                    var branches = new List<(Node, Node)>();
                    Node? otherwise = null;
                    while (Current.IsWord("WHEN"))
                    {
                        _position++;
                        var when = ParseExpression();
                        Expect(t => t.IsWord("THEN"), "THEN");
                        branches.Add((when, ParseExpression()));
                    }

                    if (Current.IsWord("ELSE"))
                    {
                        _position++;
                        otherwise = ParseExpression();
                    }

                    Expect(t => t.IsWord("END"), "END");
                    return new Case(branches, otherwise);
                }
            }

            if (Current.IsSymbol("("))
            {
                _position++;
                return new Call(word.Text.ToLowerInvariant(), ParseList(")"));
            }

            return new Leaf(word.Text);
        }

        private List<Node> ParseList(string close)
        {
            var items = new List<Node>();
            if (Current.IsSymbol(close))
            {
                _position++;
                return items;
            }

            while (true)
            {
                items.Add(ParseExpression());
                if (Current.IsSymbol(close))
                {
                    _position++;
                    return items;
                }

                Expect(t => t.IsSymbol(","), ", or " + close);
            }
        }

        // 'A'::text is the text constant 'A' to the parser, the same Const an untyped 'A' becomes
        // where text is expected; any other cast stays a node.
        private static Node TypedLiteral(Node operand, string type)
            => type == "text" && operand is Leaf { Text: ['\'', ..] } ? operand : new Cast(operand, type);

        private string ParseType()
        {
            var words = new List<string>();
            while (Current.Kind == TokenKind.Word && !Current.IsWord("AS"))
                words.Add(Next().Text.ToLowerInvariant());

            var type = TypeAliases.GetValueOrDefault(string.Join(" ", words), string.Join(" ", words));

            if (Current.IsSymbol("("))
            {
                _position++;
                type += "(" + string.Join(",", ParseList(")")) + ")";
            }

            while (Current.IsSymbol("["))
            {
                _position++;
                Expect(t => t.IsSymbol("]"), "]");
                type += "[]";
            }

            return type;
        }
    }

    // The names PostgreSQL's parser maps onto one type before it builds the tree.
    private static readonly Dictionary<string, string> TypeAliases = new()
    {
        ["int"]                         = "integer",
        ["int4"]                        = "integer",
        ["int8"]                        = "bigint",
        ["int2"]                        = "smallint",
        ["float8"]                      = "double precision",
        ["float4"]                      = "real",
        ["bool"]                        = "boolean",
        ["varchar"]                     = "character varying",
        ["timestamp"]                   = "timestamp without time zone",
        ["timestamptz"]                 = "timestamp with time zone",
        ["decimal"]                     = "numeric",
    };
}
