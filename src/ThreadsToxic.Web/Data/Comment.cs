using System.Text.Json.Serialization;

namespace ThreadsToxic.Web.Data;

public class Comment
{
    public string Id { get; set; } = "";
    public string PostId { get; set; } = "";
    public string PostUrl { get; set; } = "";
    public string? CommentUrl { get; set; }
    public string? AuthorUsername { get; set; }
    public string? AuthorName { get; set; }
    public string Content { get; set; } = "";
    public string? PostedAt { get; set; }
    public int Likes { get; set; }
    public int ReplyLevel { get; set; }
    public string? ParentCommentId { get; set; }

    // Chuỗi ngữ cảnh F0 (bài gốc của nhánh) -> F3
    public string F0 { get; set; } = "";
    public string F1 { get; set; } = "";
    public string F2 { get; set; } = "";
    public string F3 { get; set; } = "";

    public float ToxicScore { get; set; }
    public string? Severity { get; set; }
    public CommentAnalysis AnalysisData { get; set; } = new();

    // false = đang nằm trong danh sách lọc; true = đã loại (vẫn còn trong DB, khôi phục được)
    public bool IsExcluded { get; set; }
    public string? ExcludedBy { get; set; }
    public DateTimeOffset? ExcludedAt { get; set; }

    public string? ScrapedAt { get; set; }
}

// Cột JSONB: dữ liệu phi quan hệ từ bộ phát hiện tự động
public class CommentAnalysis
{
    [JsonPropertyName("matched_words")]
    public List<string> MatchedWords { get; set; } = new();

    [JsonPropertyName("matched_emojis")]
    public List<string> MatchedEmojis { get; set; } = new();

    [JsonPropertyName("categories")]
    public List<string> Categories { get; set; } = new();

    [JsonPropertyName("has_emoji_slang")]
    public bool HasEmojiSlang { get; set; }

    [JsonPropertyName("is_auto_toxic")]
    public bool IsAutoToxic { get; set; }
}
