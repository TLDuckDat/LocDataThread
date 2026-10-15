namespace ThreadsToxic.Web.Data;

// Kho phụ: bản ghi từ threads_review.db, chưa vào "Đang lọc" (xem db/migrations/004_review_queue.sql)
public class ReviewQueueItem
{
    public string Id { get; set; } = "";
    public string RecordType { get; set; } = "comment";     // comment | post
    public string Reason { get; set; } = "";
    public string RecordId { get; set; } = "";
    public string PostId { get; set; } = "";
    public string PostUrl { get; set; } = "";
    public string? CommentUrl { get; set; }
    public string? AuthorUsername { get; set; }
    public string? AuthorName { get; set; }
    public string Content { get; set; } = "";
    public string? PostedAt { get; set; }
    public int Likes { get; set; }
    public int ReplyLevel { get; set; }
    public bool IsReply { get; set; }
    public string? ParentCommentId { get; set; }
    public string F0 { get; set; } = "";
    public string F1 { get; set; } = "";
    public string F2 { get; set; } = "";
    public string F3 { get; set; } = "";
    public float ToxicScore { get; set; }
    public string? Severity { get; set; }
    public string? ReviewStatus { get; set; }
    public string? ReviewStatusVi { get; set; }
    public CommentAnalysis AnalysisData { get; set; } = new();
    public string? Categories { get; set; }                 // post: chuỗi JSON
    public int Occurrences { get; set; }
    public string? FirstSeen { get; set; }
    public string? LastSeen { get; set; }
    public bool ExistsInMain { get; set; }
    public string Decision { get; set; } = "pending";       // pending | promoted | rejected
    public string? DecidedBy { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public DateTimeOffset? SyncedAt { get; set; }
}
