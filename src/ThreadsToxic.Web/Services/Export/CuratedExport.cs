using System.Text.Json;
using static ThreadsToxic.Web.Services.Export.PyUnicode;

namespace ThreadsToxic.Web.Services.Export;

// Bộ lọc của trang xuất, đúng các lựa chọn bên crawler
public record CuratedExportOptions(
    string Status = "all",          // all | bad | ambiguous | clean
    string CommentType = "all",     // all | root | reply
    int MinLength = 2,
    int MaxLength = 300,
    bool Deduplicate = true);

// Số liệu phụ của một lần dựng bảng (để trang xuất báo cho người dùng)
public sealed class CuratedExportStats
{
    // Dòng bị bỏ vì chuỗi ngữ cảnh đứt: một ô F cấp trên rỗng sau khi làm sạch (vd ngữ cảnh không phải tiếng Việt)
    public int BrokenChainDropped { get; set; }
}

// Một bình luận kèm bài viết gốc (LEFT JOIN: bài viết có thể không tồn tại)
public record CuratedSource(
    string CommentId,
    long? SourceRowid,
    string PostId,
    string CommentContent,
    string? CommentAuthorUsername,
    string? CommentAuthorName,
    string F0,
    string F1,
    string F2,
    string F3,
    int ReplyLevel,
    bool IsReply,
    float ToxicScore,
    string? SeverityVi,
    string? ReviewStatusVi,
    string? UserReview,
    string? UserReviewVi,
    string? UserKeywords,
    List<string> CommentCategories,
    string? PostContent,
    string? PostAuthorUsername,
    string? PostAuthorName,
    string? PostUrl,
    string? PostCategories);

/// <summary>
/// Bản port db_manager.get_curated_export_df của crawler: "BẢNG 4 CỘT CHUẨN ĐỂ TỰ ĐÁNH GIÁ".
/// Đầu vào phải đã lọc theo trạng thái/loại bình luận và sắp theo scraped_at giảm dần (như câu SQL bên crawler).
///
/// Giá trị NULL: pandas 3 đọc NULL trong một cột có giá trị khác thành NaN (truthy, str() ra "nan"), còn cột
/// toàn NULL thành None. Crawler hiển thị theo đúng quy tắc đó (vd bình luận không có bài viết có chủ đề "nan"),
/// nên ở đây cũng phân biệt hai trường hợp bằng <see cref="NullStyle"/>.
/// </summary>
public static class CuratedExport
{
    public const string ColId = "Mã ID";
    public const string ColPost = "Bài viết";
    public const string ColContent = "Nội dung";
    public const string ColScore = "Điểm đánh giá";
    public const string ColTopic = "Chủ đề bài viết";
    public const string ColUserReview = "Tự đánh giá của bạn (Xấu luôn / Chưa rõ / Trong sạch)";
    public const string ColUserKeywords = "Từ lóng mới bổ sung (nếu có)";

    public static readonly IReadOnlyList<string> Columns =
        [ColId, ColPost, "f0", "f1", "f2", "f3", ColContent, ColScore, ColTopic, ColUserReview, ColUserKeywords];

    public static readonly IReadOnlyList<string> Statuses = ["all", "bad", "ambiguous", "clean"];
    public static readonly IReadOnlyList<string> CommentTypes = ["all", "root", "reply"];

    // Cách một cột hiểu giá trị NULL sau khi pandas đọc
    private readonly record struct NullStyle(bool IsNaN)
    {
        // bool(value)
        public bool Truthy(string? value) => value is null ? IsNaN : value.Length > 0;

        // str(value or fallback)
        public string Or(string? value, string fallback) =>
            value is null ? (IsNaN ? "nan" : fallback) : value.Length > 0 ? value : fallback;

        // str(value): f-string của None là "None"
        public string Str(string? value) => value ?? (IsNaN ? "nan" : "None");
    }

    private sealed record NullStyles(
        NullStyle SeverityVi, NullStyle ReviewStatusVi, NullStyle UserReviewVi, NullStyle UserKeywords,
        NullStyle PostContent, NullStyle PostUrl, NullStyle PostCategories);

    /// <param name="limited">true khi đầu vào bị cắt bởi LIMIT (xem trước): khi đó không khử trùng thì giữ nguyên thứ tự.</param>
    public static List<string[]> Build(IReadOnlyList<CuratedSource> sources, CuratedExportOptions options, bool limited = false,
        CuratedExportStats? stats = null)
    {
        if (sources.Count == 0) return [];

        static NullStyle StyleOf(IReadOnlyList<CuratedSource> rows, Func<CuratedSource, string?> field) =>
            new(rows.Any(r => field(r) is not null));

        var nulls = new NullStyles(
            StyleOf(sources, r => r.SeverityVi),
            StyleOf(sources, r => r.ReviewStatusVi),
            StyleOf(sources, r => r.UserReviewVi),
            StyleOf(sources, r => r.UserKeywords),
            StyleOf(sources, r => r.PostContent),
            StyleOf(sources, r => r.PostUrl),
            StyleOf(sources, r => r.PostCategories));

        // Chỉ giữ tiếng Việt, loại Meta AI
        IEnumerable<CuratedSource> rows = sources.Where(r =>
            VietText.IsVietnameseContent(r.CommentContent)
            && !IsMetaAi(r));

        // Độ dài sau khi gộp khoảng trắng, tính theo code point
        rows = rows.Where(r =>
        {
            var length = Length(CollapseSpaces(r.CommentContent));
            return length >= options.MinLength && length <= options.MaxLength;
        });

        // Chuỗi ngữ cảnh phải liền mạch (khác crawler, có chủ đích): bản trả lời cấp L cần f0..f(L-1) còn nội dung
        // sau khi làm sạch. Nếu không, file sẽ có dòng f3 có mà f1 trống, mất logic hội thoại.
        var cache = new TextCache();
        var filtered = rows.ToList();
        var list = filtered.Where(r => HasIntactContext(r, cache)).ToList();
        if (stats is not null) stats.BrokenChainDropped = filtered.Count - list.Count;

        if (options.Deduplicate)
        {
            // Ưu tiên bản đã được người dùng đánh giá, rồi điểm cao hơn; OrderBy của LINQ ổn định như sort của pandas
            list = list
                .OrderBy(r => r.UserReview, DescendingNullsLast)
                .ThenByDescending(r => (double)r.ToxicScore)
                .DistinctBy(r => Lower(CollapseSpaces(r.CommentContent)))
                .ToList();
            list = SortByConversation(list);
        }
        else if (!limited)
        {
            list = SortByConversation(list);
        }

        return list.Select(r => FormatRow(r, nulls, cache)).ToList();
    }

    // Chọn cột như exporter: bỏ tên cột lạ; không còn cột nào thì giữ tất cả
    public static IReadOnlyList<string> ResolveColumns(IEnumerable<string>? requested)
    {
        var valid = (requested ?? []).Where(Columns.Contains).ToList();
        return valid.Count > 0 ? valid : Columns;
    }

    public static List<string[]> Project(List<string[]> rows, IReadOnlyList<string> columns)
    {
        var indexes = columns.Select(c => Columns.ToList().IndexOf(c)).ToArray();
        return rows.Select(row => indexes.Select(i => row[i]).ToArray()).ToList();
    }

    private static bool IsMetaAi(CuratedSource r) =>
        VietText.IsMetaAiAuthor(r.CommentAuthorUsername, r.CommentAuthorName)
        || VietText.IsMetaAiAuthor(r.PostAuthorUsername, r.PostAuthorName)
        || VietText.IsMetaAiResponse(r.CommentContent)
        || VietText.IsMetaAiResponse(r.F0) || VietText.IsMetaAiResponse(r.F1)
        || VietText.IsMetaAiResponse(r.F2) || VietText.IsMetaAiResponse(r.F3)
        || VietText.IsMetaAiResponse(r.PostContent);

    // sort_values(["post_id", "comment_rowid"]): rowid thiếu (bình luận nạp từ hàng chờ) xếp cuối
    private static List<CuratedSource> SortByConversation(List<CuratedSource> rows) =>
        rows.OrderBy(r => r.PostId, Comparer<string>.Create(Compare))
            .ThenBy(r => r.SourceRowid ?? long.MaxValue)
            .ToList();

    private static readonly Comparer<string?> DescendingNullsLast = Comparer<string?>.Create((a, b) =>
        a is null ? (b is null ? 0 : 1) : b is null ? -1 : Compare(b, a));

    private static string[] FormatRow(CuratedSource r, NullStyles nulls, TextCache cache) =>
    [
        r.CommentId,
        CollapseSpaces(FormatPostContent(r, nulls, cache)),
        ResolveF0(r, cache),
        ResolveLevel(r, 1, r.F1, cache),
        ResolveLevel(r, 2, r.F2, cache),
        ResolveLevel(r, 3, r.F3, cache),
        CollapseSpaces(VietText.CleanToVietEng(r.CommentContent)),
        CollapseSpaces(FormatScore(r, nulls)),
        CollapseSpaces(FormatCategories(r, nulls)),
        CollapseSpaces(r.UserReviewVi ?? ""),
        CollapseSpaces(FormatUserKeywords(r, nulls)),
    ];

    // Bài viết và chuỗi F0-F3 lặp lại ở mọi bình luận cùng nhánh: chỉ làm sạch mỗi chuỗi một lần
    private sealed class TextCache
    {
        private readonly Dictionary<string, string> _fields = new();
        private readonly Dictionary<string, string> _posts = new();

        // clean_field: chỉ giữ nội dung tiếng Việt đã làm sạch
        public string CleanField(string? value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            if (_fields.TryGetValue(value, out var cached)) return cached;
            var text = Strip(value);
            var cleaned = VietText.IsVietnameseContent(text) ? CollapseSpaces(VietText.CleanToVietEng(text)) : "";
            _fields[value] = cleaned;
            return cleaned;
        }

        public string PostContent(string text)
        {
            if (_posts.TryGetValue(text, out var cached)) return cached;
            var cleaned = VietText.IsVietnameseContent(text) ? VietText.CleanToVietEng(text) : "";
            _posts[text] = cleaned;
            return cleaned;
        }
    }

    private static bool HasIntactContext(CuratedSource r, TextCache cache)
    {
        var level = Math.Min(3, r.ReplyLevel != 0 ? r.ReplyLevel : r.IsReply ? 1 : 0);
        string[] context = [r.F0, r.F1, r.F2];
        for (var k = 0; k < level; k++)
        {
            if (cache.CleanField(context[k]).Length == 0) return false;
        }
        return true;
    }

    // F0 không bao giờ để trống: lấy nội dung bình luận nếu thiếu
    private static string ResolveF0(CuratedSource r, TextCache cache)
    {
        var f0 = cache.CleanField(r.F0);
        return f0.Length > 0 ? f0 : cache.CleanField(r.CommentContent);
    }

    private static string ResolveLevel(CuratedSource r, int level, string value, TextCache cache)
    {
        var replyLevel = r.ReplyLevel != 0 ? r.ReplyLevel : r.IsReply ? 1 : 0;
        var cleaned = cache.CleanField(value);
        if (replyLevel == level && cleaned.Length == 0) return cache.CleanField(r.CommentContent);
        return replyLevel >= level ? cleaned : "";
    }

    private static string FormatPostContent(CuratedSource r, NullStyles nulls, TextCache cache)
    {
        var postText = Strip(nulls.PostContent.Or(r.PostContent, ""));
        if (postText.Length > 0)
            return cache.PostContent(postText);
        return nulls.PostUrl.Or(r.PostUrl, "");
    }

    private static string FormatScore(CuratedSource r, NullStyles nulls)
    {
        var statusVi = nulls.ReviewStatusVi.Truthy(r.ReviewStatusVi)
            ? nulls.ReviewStatusVi.Str(r.ReviewStatusVi)
            : nulls.SeverityVi.Str(r.SeverityVi);
        var score = Repr(Round(r.ToxicScore, 2));
        var userReview = Strip(nulls.UserReviewVi.Or(r.UserReviewVi, ""));
        return userReview.Length > 0
            ? $"{score} ({statusVi}) [Bạn đánh giá: {userReview}]"
            : $"{score} ({statusVi})";
    }

    private static string FormatCategories(CuratedSource r, NullStyles nulls)
    {
        var categories = new List<PyValue>();
        if (nulls.PostCategories.Truthy(r.PostCategories))
            AppendCategories(categories, r.PostCategories);
        if (categories.Count == 0)
            categories.AddRange(r.CommentCategories.Select(PyValue.FromString));

        var unique = categories
            .Where(c => c.Truthy)
            .Select(c => Strip(c.Text))
            .Distinct()
            .ToList();
        return unique.Count > 0 ? string.Join(", ", unique) : "Thảo luận chung";
    }

    // json.loads(raw): danh sách thì nối thêm từng phần tử, còn lại (kể cả NaN, JSON lỗi) thêm nguyên chuỗi
    private static void AppendCategories(List<PyValue> categories, string? raw)
    {
        if (raw is null)
        {
            categories.Add(PyValue.FromString("nan"));
            return;
        }
        var parsed = TryParseJson(raw);
        if (parsed is { ValueKind: JsonValueKind.Array } array)
            categories.AddRange(array.EnumerateArray().Select(PyValue.FromJson));
        else
            categories.Add(PyValue.FromString(raw));
    }

    private static string FormatUserKeywords(CuratedSource r, NullStyles nulls)
    {
        if (!nulls.UserKeywords.Truthy(r.UserKeywords)) return "";
        if (r.UserKeywords is not { } raw) return "nan";
        var parsed = TryParseJson(raw);
        if (parsed is null) return raw;
        if (parsed.Value.ValueKind != JsonValueKind.Array) return "";
        // ", ".join(...) lỗi khi có phần tử không phải chuỗi: crawler trả về nguyên chuỗi gốc
        var items = parsed.Value.EnumerateArray().ToList();
        return items.All(i => i.ValueKind == JsonValueKind.String)
            ? string.Join(", ", items.Select(i => i.GetString()))
            : raw;
    }

    private static JsonElement? TryParseJson(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Giá trị Python của một phần tử JSON: str() và tính truthy
    private readonly record struct PyValue(string Text, bool Truthy)
    {
        public static PyValue FromString(string text) => new(text, text.Length > 0);

        public static PyValue FromJson(JsonElement e) => e.ValueKind switch
        {
            JsonValueKind.String => FromString(e.GetString() ?? ""),
            JsonValueKind.True => new("True", true),
            JsonValueKind.False => new("False", false),
            JsonValueKind.Null => new("None", false),
            JsonValueKind.Number when e.TryGetInt64(out var n) && !e.GetRawText().Contains('.')
                && !e.GetRawText().Contains('e', StringComparison.OrdinalIgnoreCase) => new(n.ToString(), n != 0),
            JsonValueKind.Number => new(Repr(e.GetDouble()), e.GetDouble() != 0),
            // Danh sách/đối tượng lồng nhau: crawler không sinh ra; dùng nguyên văn JSON
            _ => new(e.GetRawText(), e.GetRawText().Length > 2),
        };
    }
}
