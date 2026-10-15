using System.Globalization;
using System.Numerics;
using System.Text;

namespace ThreadsToxic.Web.Services.Export;

/// <summary>
/// Hành vi chuỗi của Python mà file xuất của crawler phụ thuộc vào: str.split()/strip(), len(),
/// lower()/casefold(), các lớp ký tự \s \d \w của re, round() và repr() của float.
/// Category Unicode lấy từ .NET (khớp với Python khi cùng phiên bản Unicode); phần .NET không có
/// nằm trong PyUnicode.g.cs do tools/gen_py_unicode.py sinh ra.
/// </summary>
internal static partial class PyUnicode
{
    private static readonly Dictionary<int, string> LowerMap = ParseMapping(LowerTable);
    private static readonly Dictionary<int, string> CasefoldMap = ParseMapping(CasefoldTable);
    private static readonly Dictionary<int, HashSet<int>> IgnoreCaseMap = ParseIgnoreCase(IgnoreCaseTable);

    // Như EnumerateRunes: surrogate lẻ thành U+FFFD
    public static int[] CodePoints(string text)
    {
        var result = new int[text.Length];
        var count = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                result[count++] = char.ConvertToUtf32(c, text[++i]);
            else
                result[count++] = char.IsSurrogate(c) ? 0xFFFD : c;
        }
        return count == result.Length ? result : result[..count];
    }

    public static string FromCodePoints(IEnumerable<int> codePoints)
    {
        var sb = new StringBuilder();
        foreach (var cp in codePoints)
            Append(sb, cp);
        return sb.ToString();
    }

    // Ghi một code point mà không tạo chuỗi tạm
    public static void Append(StringBuilder sb, int cp)
    {
        if (cp < 0x10000)
        {
            sb.Append((char)cp);
            return;
        }
        Span<char> pair = stackalloc char[2];
        new Rune(cp).EncodeToUtf16(pair);
        sb.Append(pair);
    }

    // str.isspace() / \s
    public static bool IsSpace(int cp) => cp is (>= 0x09 and <= 0x0D) or (>= 0x1C and <= 0x20) or 0x85 or 0xA0
        or 0x1680 or (>= 0x2000 and <= 0x200A) or 0x2028 or 0x2029 or 0x202F or 0x205F or 0x3000;

    private static UnicodeCategory Category(int cp) => Rune.GetUnicodeCategory(new Rune(cp));

    // str.isalnum(): chữ cái hoặc chữ số (category L*, N*)
    public static bool IsAlnum(int cp) => Category(cp) is
        UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
        or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter
        or UnicodeCategory.DecimalDigitNumber or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber;

    // \w của re
    public static bool IsWord(int cp) => cp == '_' || IsAlnum(cp);

    // \d của re (category Nd)
    public static bool IsDecimal(int cp) => Category(cp) == UnicodeCategory.DecimalDigitNumber;

    public static bool IsLetter(int cp) => Category(cp) is
        UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
        or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter;

    public static bool IsNonSpacingMark(int cp) => Category(cp) == UnicodeCategory.NonSpacingMark;

    // unicodedata.name(c).startswith("LATIN ")
    public static bool IsLatinLetter(int cp)
    {
        for (var i = 0; i < LatinLetterRanges.Length; i += 2)
        {
            if (cp < LatinLetterRanges[i]) return false;
            if (cp <= LatinLetterRanges[i + 1]) return true;
        }
        return false;
    }

    // Ký tự cp khớp với chữ cái của mẫu khi dùng re.IGNORECASE
    public static bool MatchesIgnoreCase(int cp, char patternLetter) =>
        IgnoreCaseMap.TryGetValue(patternLetter, out var set) ? set.Contains(cp) : cp == patternLetter;

    // str.lower(). Không xử lý ngữ cảnh Σ cuối từ: nội dung có chữ Hy Lạp đã bị bộ lọc tiếng Việt loại trước.
    public static string Lower(string text) => MapEach(text, LowerMap);

    // str.casefold()
    public static string Casefold(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            if (CasefoldMap.TryGetValue(rune.Value, out var folded))
                sb.Append(folded);
            else if (LowerMap.TryGetValue(rune.Value, out var lowered))
                sb.Append(lowered);
            else
                Append(sb, rune.Value);
        }
        return sb.ToString();
    }

    private static string MapEach(string text, Dictionary<int, string> map)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            if (map.TryGetValue(rune.Value, out var mapped))
                sb.Append(mapped);
            else
                Append(sb, rune.Value);
        }
        return sb.ToString();
    }

    // len(): số code point, không phải số UTF-16 char (emoji tính là 1)
    public static int Length(string text)
    {
        var count = 0;
        foreach (var _ in text.EnumerateRunes())
            count++;
        return count;
    }

    // " ".join(text.split())
    // Mọi khoảng trắng của Python đều nằm trong BMP nên duyệt theo char là đủ.
    public static string CollapseSpaces(string text)
    {
        if (IsCollapsed(text)) return text;
        var sb = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var c in text)
        {
            if (IsSpace(c))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }
            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    // Đã gộp sẵn: không có khoảng trắng đầu/cuối, chỉ có từng dấu cách đơn
    private static bool IsCollapsed(string text)
    {
        var previousSpace = true;
        foreach (var c in text)
        {
            var space = IsSpace(c);
            if (space && (previousSpace || c != ' ')) return false;
            previousSpace = space;
        }
        return !previousSpace;
    }

    // str.strip()
    public static string Strip(string text)
    {
        var cps = CodePoints(text);
        int start = 0, end = cps.Length;
        while (start < end && IsSpace(cps[start])) start++;
        while (end > start && IsSpace(cps[end - 1])) end--;
        return start == 0 && end == cps.Length ? text : FromCodePoints(cps[start..end]);
    }

    // So sánh chuỗi theo code point như Python (khác string.CompareOrdinal ở ký tự ngoài BMP)
    public static int Compare(string a, string b)
    {
        var ea = a.EnumerateRunes().GetEnumerator();
        var eb = b.EnumerateRunes().GetEnumerator();
        while (true)
        {
            var hasA = ea.MoveNext();
            var hasB = eb.MoveNext();
            if (!hasA || !hasB) return hasA == hasB ? 0 : hasA ? 1 : -1;
            var diff = ea.Current.Value - eb.Current.Value;
            if (diff != 0) return diff;
        }
    }

    // round(x, ndigits) của Python: làm tròn nửa-chẵn trên giá trị nhị phân chính xác
    public static double Round(double x, int ndigits)
    {
        if (!double.IsFinite(x) || x == 0) return x;
        var bits = BitConverter.DoubleToInt64Bits(x);
        var exponent = (int)((bits >> 52) & 0x7FF);
        var mantissa = bits & 0xFFFFFFFFFFFFFL;
        if (exponent == 0) exponent = 1; else mantissa |= 1L << 52;
        exponent -= 1075;   // |x| = mantissa * 2^exponent

        var scale = BigInteger.Pow(10, ndigits);
        BigInteger numerator = mantissa * scale, denominator = BigInteger.One;
        if (exponent > 0) numerator <<= exponent; else denominator <<= -exponent;
        var quotient = BigInteger.DivRem(numerator, denominator, out var remainder);
        var cmp = (remainder * 2).CompareTo(denominator);
        if (cmp > 0 || (cmp == 0 && !quotient.IsEven)) quotient += 1;

        var result = (double)quotient / (double)scale;
        return x < 0 ? -result : result;
    }

    // repr(float) của Python: số chữ số ngắn nhất, luôn có phần thập phân (1.0), dạng mũ ngoài [1e-4, 1e16)
    public static string Repr(double x)
    {
        if (double.IsNaN(x)) return "nan";
        if (double.IsInfinity(x)) return x > 0 ? "inf" : "-inf";
        if (x == 0) return double.IsNegative(x) ? "-0.0" : "0.0";

        var sign = x < 0 ? "-" : "";
        var (digits, exp10) = ShortestDigits(Math.Abs(x));

        if (exp10 < -4 || exp10 >= 16)
        {
            var mantissa = digits.Length > 1 ? digits[0] + "." + digits[1..] : digits;
            return $"{sign}{mantissa}e{(exp10 < 0 ? "-" : "+")}{Math.Abs(exp10):00}";
        }
        if (exp10 < 0)
            return sign + "0." + new string('0', -exp10 - 1) + digits;

        var intLength = exp10 + 1;
        var intPart = digits.Length > intLength ? digits[..intLength] : digits.PadRight(intLength, '0');
        var fracPart = digits.Length > intLength ? digits[intLength..] : "0";
        return sign + intPart + "." + fracPart;
    }

    // Chữ số có nghĩa ngắn nhất (định dạng "R" của .NET) và số mũ thập phân của chữ số đầu tiên
    private static (string Digits, int Exp10) ShortestDigits(double positive)
    {
        var text = positive.ToString("R", CultureInfo.InvariantCulture);
        var ePos = text.IndexOfAny(['E', 'e']);
        var mantissa = ePos >= 0 ? text[..ePos] : text;
        var exponent = ePos >= 0 ? int.Parse(text[(ePos + 1)..], CultureInfo.InvariantCulture) : 0;
        var dot = mantissa.IndexOf('.');
        var intDigits = dot >= 0 ? mantissa[..dot] : mantissa;
        var allDigits = intDigits + (dot >= 0 ? mantissa[(dot + 1)..] : "");
        var significant = allDigits.TrimStart('0');
        var leadingZeros = allDigits.Length - significant.Length;
        return (significant.TrimEnd('0'), intDigits.Length - 1 - leadingZeros + exponent);
    }

    private static Dictionary<int, string> ParseMapping(string table)
    {
        var map = new Dictionary<int, string>();
        foreach (var entry in table.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var (cp, targets) = ParseEntry(entry);
            map[cp] = FromCodePoints(targets);
        }
        return map;
    }

    private static Dictionary<int, HashSet<int>> ParseIgnoreCase(string table)
    {
        var map = new Dictionary<int, HashSet<int>>();
        foreach (var entry in table.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var (cp, targets) = ParseEntry(entry);
            map[cp] = targets.ToHashSet();
        }
        return map;
    }

    private static (int CodePoint, int[] Targets) ParseEntry(string entry)
    {
        var eq = entry.IndexOf('=');
        var cp = int.Parse(entry[..eq], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var targets = entry[(eq + 1)..]
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(h => int.Parse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture))
            .ToArray();
        return (cp, targets);
    }
}
