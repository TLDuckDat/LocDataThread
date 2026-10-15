namespace ThreadsToxic.Web.Data;

// Bài viết gốc, chỉ dùng khi xuất file (cột "Bài viết" và "Chủ đề bài viết")
public class Post
{
    public string Id { get; set; } = "";
    public string? Url { get; set; }
    public string? AuthorUsername { get; set; }
    public string? AuthorName { get; set; }
    public string? Content { get; set; }
    public string? Categories { get; set; }     // chuỗi JSON gốc
}
