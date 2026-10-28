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

    // null = dữ liệu chính; khác null = nạp từ hàng chờ lọc trùng, giá trị là lý do bị cách ly
    public string? Source { get; set; }

    // Các trường chỉ dùng khi xuất file (giữ nguyên NULL như bên crawler)
    public bool IsReply { get; set; }
    public string? ReviewStatus { get; set; }
    public string? ReviewStatusVi { get; set; }
    public string? UserReview { get; set; }
    public string? UserReviewVi { get; set; }
    public string? UserKeywords { get; set; }   // chuỗi JSON gốc
    public long? SourceRowid { get; set; }      // rowid bên SQLite: thứ tự cây hội thoại

    // Người xử lý gần nhất (duyệt, loại, khôi phục, hoàn tác)
    public string? ReviewedBy { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }

    // Luồng hội thoại (do trigger trong DB điền, xem db/migrations/007_thread_links.sql):
    // cha trực tiếp (null với F0) và bình luận gốc F0 của luồng
    public string? ThreadParentId { get; set; }
    public string? ThreadRootId { get; set; }

    // Người phụ trách (chia việc theo bài viết); null = chưa giao
    public string? AssignedTo { get; set; }
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
