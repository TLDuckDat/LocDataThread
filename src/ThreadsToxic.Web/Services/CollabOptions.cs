namespace ThreadsToxic.Web.Services;

// Cấu hình mục "Collab" trong appsettings.json
public class CollabOptions
{
    // Thời hạn một lease khóa bình luận. Frontend gia hạn mỗi 20 giây nên không nên đặt dưới ~30.
    public int LeaseSeconds { get; set; } = 45;

    // Chu kỳ quét để mở khóa các lease đã hết hạn.
    public int SweepSeconds { get; set; } = 5;

    public TimeSpan Lease => TimeSpan.FromSeconds(Math.Max(1, LeaseSeconds));
    public TimeSpan Sweep => TimeSpan.FromSeconds(Math.Max(1, SweepSeconds));
}
