using System.Text;
using static ThreadsToxic.Web.Services.Export.PyUnicode;

namespace ThreadsToxic.Web.Services.Export;

/// <summary>
/// Bản port các hàm trong src/detector/text_normalizer.py của crawler mà file xuất dùng tới:
/// clean_ui_artifacts, clean_to_viet_eng, contains_foreign_script, is_vietnamese_content,
/// is_meta_ai_author, is_meta_ai_response. Kết quả phải giống hệt Python, nên các biểu thức
/// chính quy được viết lại bằng tay trên code point (Regex của .NET khác re của Python ở \w, \s,
/// \b, IGNORECASE và ký tự ngoài BMP).
/// </summary>
public static class VietText
{
    // Các từ khóa nút "Dịch" của Threads, đúng thứ tự trong mẫu (?:Translate|Xem bản dịch|Dịch)
    private static readonly string[] TranslateKeywords = ["Translate", "Xem bản dịch", "Dịch"];

    // FOREIGN_SCRIPTS_PATTERN: Hán, Nhật, Hàn, Thái, Ả Rập, Kirin, Devanagari
    private static readonly (int From, int To)[] ForeignScriptRanges =
    [
        (0x4E00, 0x9FFF), (0x3400, 0x4DBF), (0xF900, 0xFAFF), (0x3000, 0x303F), (0x3040, 0x309F),
        (0x30A0, 0x30FF), (0x31F0, 0x31FF), (0xFF65, 0xFF9F), (0xAC00, 0xD7AF), (0x1100, 0x11FF),
        (0x3130, 0x318F), (0x0E00, 0x0E7F), (0x0600, 0x06FF), (0x0400, 0x04FF), (0x0900, 0x097F),
    ];

    private static readonly HashSet<string> MarkerWords =
    [
        .. ("tôi bạn mình chúng ta không những được này đó nữa rất với một người đang thì sẽ đã tại như đâu " +
            "anh chị nhà đi ở xem quá lắm nhé nha ạ ủa trời ôi đúng vậy thế nói thật tốt giúp cảm ơn yêu thích " +
            "bình thường tiếng việt xin chào đẹp tuyệt vời ngon khỏe mạnh vui vẻ buồn gì nào cần biết rồi " +
            "nhưng mà còn bởi").Split(' '),
    ];

    private static readonly HashSet<string> AsciiCues =
    [
        "toi", "ban", "minh", "khong", "nhung", "duoc", "nay", "nguoi", "thoi",
        "vay", "nhe", "nha", "dep", "tuyet", "cam", "on", "yeu", "thich",
        "binh", "luan", "tieng", "viet", "xin", "chao", "qua", "rat", "troi",
        "dung", "dang", "mot", "lam", "vui", "buon", "anh", "chi",
    ];

    private static readonly HashSet<string> StrongAsciiCues =
    [
        "khong", "nhung", "duoc", "nguoi", "thoi", "vay", "nhe", "nha", "dep",
        "tuyet", "binh", "luan", "tieng", "viet", "chao", "troi", "dung", "dang",
    ];

    private static readonly HashSet<string> MetaAiIdentities = ["metaai", "metaaiofficial", "metaaiassistant", "metaaibot"];

    // VIETNAMESE_ACCENT_MAP
    private static readonly Dictionary<string, char> AccentMap = BuildAccentMap();

    public static string CleanUiArtifacts(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var cps = CodePoints(Strip(text));
        cps = RemoveTranslateCounters(cps);
        cps = RemoveTrailingTranslate(cps);
        cps = RemoveTrailingCounter(cps);
        return CollapseSpaces(FromCodePoints(cps));
    }

    public static string CleanToVietEng(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var cleaned = CodePoints(CleanUiArtifacts(text)).Where(cp => !IsForeignScript(cp));
        return CollapseSpaces(FromCodePoints(cleaned));
    }

    // Có chữ cái thuộc hệ chữ không phải Latin
    public static bool ContainsForeignScript(string? text) =>
        !string.IsNullOrEmpty(text) && CodePoints(text).Any(cp => IsLetter(cp) && !IsLatinLetter(cp));

    public static bool IsVietnameseContent(string? text)
    {
        if (string.IsNullOrEmpty(text) || Strip(text).Length == 0) return false;

        var raw = Strip(CleanUiArtifacts(text));
        if (raw.Length == 0 || ContainsForeignScript(raw)) return false;

        var folded = Casefold(raw);
        var words = Words(folded);
        if (words.Count == 0) return false;
        if (words.Any(MarkerWords.Contains)) return true;

        if (HasDistinctiveVietnameseMark(folded.Normalize(NormalizationForm.FormD))) return true;

        var cues = Words(Casefold(RemoveVietnameseAccents(raw))).Where(AsciiCues.Contains).ToHashSet();
        return cues.Overlaps(StrongAsciiCues) || cues.Count >= 2;
    }

    public static bool IsMetaAiAuthor(string? username, string? authorName) =>
        MetaAiIdentities.Contains(Identity(username)) || MetaAiIdentities.Contains(Identity(authorName));

    // ^\s*@?meta[\s._-]*ai(?:\s*(?::|：|[-–—|]|\.\.\.|$))  (re.IGNORECASE)
    public static bool IsMetaAiResponse(string? text)
    {
        // Thoát sớm: ký tự đầu tiên sau khoảng trắng phải là '@' hoặc 'm' (tránh đổi cả bài viết dài sang code point)
        var first = (text ?? "").EnumerateRunes().FirstOrDefault(r => !IsSpace(r.Value)).Value;
        if (first != '@' && !MatchesIgnoreCase(first, 'm')) return false;

        var cps = CodePoints(text!);
        var i = SkipSpaces(cps, 0);
        if (i < cps.Length && cps[i] == '@') i++;
        if (!MatchLiteral(cps, i, "meta", out i)) return false;
        while (i < cps.Length && (IsSpace(cps[i]) || cps[i] is '.' or '_' or '-')) i++;
        if (!MatchLiteral(cps, i, "ai", out i)) return false;
        i = SkipSpaces(cps, i);
        if (i == cps.Length) return true;
        if (cps[i] is ':' or '：' or '-' or '–' or '—' or '|') return true;
        return i + 2 < cps.Length && cps[i] == '.' && cps[i + 1] == '.' && cps[i + 2] == '.';
    }

    // re.sub(r"[^a-z0-9]", "", str(value or "").casefold())
    private static string Identity(string? value)
    {
        var sb = new StringBuilder();
        foreach (var ch in Casefold(value ?? ""))
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9')
                sb.Append(ch);
        return sb.ToString();
    }

    // re.findall(r"[^\W_]+", text)
    private static List<string> Words(string text)
    {
        var words = new List<string>();
        var current = new StringBuilder();
        var index = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (IsAlnum(rune.Value))
            {
                current.Append(text, index, rune.Utf16SequenceLength);
            }
            else if (current.Length > 0)
            {
                words.Add(current.ToString());
                current.Clear();
            }
            index += rune.Utf16SequenceLength;
        }
        if (current.Length > 0) words.Add(current.ToString());
        return words;
    }

    // _VIETNAMESE_DISTINCTIVE_NFD_PATTERN: đ | [ou]+móc (ơ ư) | a+trăng+thanh | [aeo]+mũ+thanh
    private static bool HasDistinctiveVietnameseMark(string decomposed)
    {
        static bool IsTone(char c) => c is '\u0300' or '\u0301' or '\u0303' or '\u0309' or '\u0323';
        for (var i = 0; i < decomposed.Length; i++)
        {
            var c = decomposed[i];
            if (c == '\u0111') return true;
            if (i + 1 >= decomposed.Length) continue;
            var next = decomposed[i + 1];
            if (c is 'o' or 'u' && next == '\u031B') return true;
            if (i + 2 >= decomposed.Length) continue;
            if (c == 'a' && next == '\u0306' && IsTone(decomposed[i + 2])) return true;
            if (c is 'a' or 'e' or 'o' && next == '\u0302' && IsTone(decomposed[i + 2])) return true;
        }
        return false;
    }

    // remove_vietnamese_accents: NFD, bỏ dấu (Mn), đổi chữ có trong VIETNAMESE_ACCENT_MAP
    private static string RemoveVietnameseAccents(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var rune in text.Normalize(NormalizationForm.FormD).EnumerateRunes())
        {
            if (IsNonSpacingMark(rune.Value)) continue;
            // Chữ ASCII viết thường vẫn là ASCII, không có trong bảng: bỏ qua bước tra cho nhanh
            if (rune.Value >= 0x80 && AccentMap.TryGetValue(Lower(rune.ToString()), out var plain))
                sb.Append(plain);
            else
                Append(sb, rune.Value);
        }
        return sb.ToString();
    }

    private static bool IsForeignScript(int cp)
    {
        foreach (var (from, to) in ForeignScriptRanges)
            if (cp >= from && cp <= to) return true;
        return false;
    }

    private static int SkipSpaces(int[] cps, int i)
    {
        while (i < cps.Length && IsSpace(cps[i])) i++;
        return i;
    }

    // \b đứng ngay trước một ký tự \w: ký tự liền trước phải không phải \w
    private static bool WordStartsAt(int[] cps, int i) => i == 0 || !IsWord(cps[i - 1]);

    // So khớp chuỗi literal theo re.IGNORECASE (khoảng trắng trong mẫu là ký tự ' ' đúng nghĩa)
    private static bool MatchLiteral(int[] cps, int start, string literal, out int end)
    {
        end = start;
        foreach (var ch in literal)
        {
            if (end >= cps.Length) return false;
            var ok = ch == ' ' ? cps[end] == ' ' : MatchesIgnoreCase(cps[end], ch);
            if (!ok) return false;
            end++;
        }
        return true;
    }

    // \s*\b(?:Translate|Xem bản dịch|Dịch) : trả về vị trí sau từ khóa, -1 nếu không khớp
    private static int MatchTranslateKeyword(int[] cps, int start)
    {
        var j = SkipSpaces(cps, start);
        // Theo bảng IGNORECASE, chữ đầu của ba từ khóa chỉ khớp T/t, X/x, D/d: loại nhanh các vị trí khác
        if (j >= cps.Length || cps[j] is not ('T' or 't' or 'X' or 'x' or 'D' or 'd') || !WordStartsAt(cps, j)) return -1;
        foreach (var keyword in TranslateKeywords)
            if (MatchLiteral(cps, j, keyword, out var end))
                return end;
        return -1;
    }

    // \d+\s*/\s*\d+ : trả về vị trí sau chữ số cuối, -1 nếu không khớp
    private static int MatchCounter(int[] cps, int start)
    {
        var i = start;
        while (i < cps.Length && IsDecimal(cps[i])) i++;
        if (i == start) return -1;
        i = SkipSpaces(cps, i);
        if (i >= cps.Length || cps[i] != '/') return -1;
        i = SkipSpaces(cps, i + 1);
        var digitsStart = i;
        while (i < cps.Length && IsDecimal(cps[i])) i++;
        return i == digitsStart ? -1 : i;
    }

    // re.sub(r'\s*\b(?:Translate|Xem bản dịch|Dịch)\s+\d+\s*/\s*\d+\b', '', s, flags=re.IGNORECASE)
    private static int[] RemoveTranslateCounters(int[] cps)
    {
        if (Array.IndexOf(cps, '/') < 0) return cps;
        var result = new List<int>(cps.Length);
        int copied = 0, pos = 0;
        while (pos <= cps.Length)
        {
            var end = MatchTranslateCounterAt(cps, pos);
            if (end < 0)
            {
                pos++;
                continue;
            }
            result.AddRange(cps[copied..pos]);
            copied = pos = end;
        }
        result.AddRange(cps[copied..]);
        return result.ToArray();
    }

    private static int MatchTranslateCounterAt(int[] cps, int start)
    {
        var afterKeyword = MatchTranslateKeyword(cps, start);
        if (afterKeyword < 0) return -1;
        var digits = SkipSpaces(cps, afterKeyword);
        if (digits == afterKeyword) return -1;                       // \s+
        var end = MatchCounter(cps, digits);
        if (end < 0) return -1;
        return end == cps.Length || !IsWord(cps[end]) ? end : -1;    // \b sau chữ số cuối
    }

    // re.sub(r'\s*\b(?:Translate|Xem bản dịch|Dịch)\s*$', '', s, flags=re.IGNORECASE)
    // Từ khóa phải kết thúc ngay trước khoảng trắng cuối chuỗi, nên mỗi từ khóa chỉ có một vị trí để thử;
    // regex khớp từ đầu dãy khoảng trắng đứng trước từ khóa, và lấy vị trí sớm nhất (khớp trái nhất).
    private static int[] RemoveTrailingTranslate(int[] cps)
    {
        var end = cps.Length;
        while (end > 0 && IsSpace(cps[end - 1])) end--;
        var best = -1;
        foreach (var keyword in TranslateKeywords)
        {
            var j = end - keyword.Length;      // từ khóa toàn ký tự BMP: số char bằng số code point
            if (j < 0 || !MatchLiteral(cps, j, keyword, out _) || !WordStartsAt(cps, j)) continue;
            var start = j;
            while (start > 0 && IsSpace(cps[start - 1])) start--;
            if (best < 0 || start < best) best = start;
        }
        return best < 0 ? cps : cps[..best];
    }

    // re.sub(r'\s*\b\d+\s*/\s*\d+\s*$', '', s)
    private static int[] RemoveTrailingCounter(int[] cps)
    {
        if (Array.IndexOf(cps, '/') < 0) return cps;
        for (var start = 0; start <= cps.Length; start++)
        {
            var j = SkipSpaces(cps, start);
            if (j >= cps.Length || !IsDecimal(cps[j]) || !WordStartsAt(cps, j)) continue;
            var end = MatchCounter(cps, j);
            if (end >= 0 && SkipSpaces(cps, end) == cps.Length)
                return cps[..start];
        }
        return cps;
    }

    private static Dictionary<string, char> BuildAccentMap()
    {
        var groups = new (char Plain, string Accented)[]
        {
            ('a', "àáảãạăằắẳẵặâầấẩẫậ"), ('d', "đ"), ('e', "èéẻẽẹêềếểễệ"), ('i', "ìíỉĩị"),
            ('o', "òóỏõọôồốổỗộơờớởỡợ"), ('u', "ùúủũụưừứửữự"), ('y', "ỳýỷỹỵ"),
        };
        var map = new Dictionary<string, char>();
        foreach (var (plain, accented) in groups)
            foreach (var ch in accented)
                map[ch.ToString()] = plain;
        return map;
    }
}
