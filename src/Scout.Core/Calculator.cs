using System.Globalization;
using System.Text.RegularExpressions;

namespace Scout.Core;

/// <summary>Bounded arithmetic parser. No script evaluation or external execution.</summary>
public static class Calculator
{
    public static string? Evaluate(string text)
    {
        if (text.Length > 256) return null;
        var value = text.Trim().ToLowerInvariant();
        var explicitMath = value.StartsWith('=') || value.StartsWith("calc ", StringComparison.Ordinal) || value.StartsWith("calculate ", StringComparison.Ordinal);
        value = Regex.Replace(value, @"^(?:=\s*|calc(?:ulate)?\s+)", "");
        value = Regex.Replace(value, @"\bpercent\b", "%");
        value = Regex.Replace(value, @"\bof\b", "*");
        value = Regex.Replace(value, @"\bx\b", "*").Replace('×', '*').Replace('÷', '/').Replace('−', '-').Replace("π", "pi");
        if (!explicitMath && !value.Any(c => "+-*/^%()".Contains(c)) && !value.StartsWith("sqrt", StringComparison.Ordinal)) return null;
        try
        {
            var parser = new Parser(value);
            var result = parser.Expression(); parser.Skip();
            if (!parser.End || !double.IsFinite(result)) return null;
            if (Math.Abs(result) < .000000000001) result = 0;
            return result.ToString(Math.Abs(result) is >= 1e15 or > 0 and < 1e-6 ? "G10" : "0.######", CultureInfo.InvariantCulture);
        }
        catch (ArithmeticException) { return null; }
        catch (FormatException) { return null; }
    }
    private sealed class Parser(string text)
    {
        private int pos, depth;
        public bool End => pos == text.Length;
        public void Skip() { while (pos < text.Length && char.IsWhiteSpace(text[pos])) pos++; }
        private bool Eat(char c) { Skip(); if (pos < text.Length && text[pos] == c) { pos++; return true; } return false; }
        public double Expression()
        {
            if (++depth > 32) throw new FormatException();
            var n = Product();
            while (true) { if (Eat('+')) n += Product(); else if (Eat('-')) n -= Product(); else break; }
            depth--; return n;
        }
        private double Product()
        {
            var n = Unary();
            while (true)
            {
                if (Eat('*')) n *= Unary();
                else if (Eat('/')) { var denominator = Unary(); if (denominator == 0) throw new ArithmeticException(); n /= denominator; }
                else { Skip(); if (pos < text.Length && (text[pos] == '(' || char.IsLetter(text[pos]))) n *= Unary(); else break; }
            }
            return n;
        }
        private double Unary()
        {
            if (++depth > 32) throw new FormatException();
            var n = Eat('+') ? Unary() : Eat('-') ? -Unary() : Power(); depth--; return n;
        }
        private double Power() { var n = Atom(); while (Eat('%')) n /= 100; if (Eat('^')) n = Math.Pow(n, Unary()); return n; }
        private double Atom()
        {
            Skip();
            if (Eat('(')) { var n = Expression(); if (!Eat(')')) throw new FormatException(); return n; }
            if (text.AsSpan(pos).StartsWith("sqrt", StringComparison.Ordinal)) { pos += 4; if (!Eat('(')) throw new FormatException(); var n = Expression(); if (!Eat(')') || n < 0) throw new FormatException(); return Math.Sqrt(n); }
            if (text.AsSpan(pos).StartsWith("pi", StringComparison.Ordinal)) { pos += 2; return Math.PI; }
            var start = pos;
            while (pos < text.Length && (char.IsAsciiDigit(text[pos]) || text[pos] == '.')) pos++;
            if (pos < text.Length && text[pos] == 'e') { pos++; if (pos < text.Length && text[pos] is '+' or '-') pos++; while (pos < text.Length && char.IsAsciiDigit(text[pos])) pos++; }
            if (start == pos || !double.TryParse(text.AsSpan(start, pos - start), NumberStyles.Float, CultureInfo.InvariantCulture, out var result)) throw new FormatException();
            return result;
        }
    }
}
