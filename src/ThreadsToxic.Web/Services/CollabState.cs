using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace ThreadsToxic.Web.Services;

// Trạng thái cộng tác trong bộ nhớ: ai đang online, ai đang giữ bình luận nào (lease lock).
// Chỉ chạy trên một tiến trình; nếu cần nhiều máy chủ thì chuyển sang Redis.
public class CollabState(IOptions<CollabOptions> options)
{
    private readonly TimeSpan _leaseDuration = options.Value.Lease;

    private readonly object _gate = new();
    private readonly Dictionary<string, AppUser> _usersByConnection = new();
    private readonly Dictionary<string, Lease> _leasesByItem = new();
    private readonly Dictionary<string, string> _connectionByToken = new();
    private readonly Dictionary<string, string> _tokenByConnection = new();

    private sealed record Lease(string ConnectionId, string UserName, DateTimeOffset ExpiresAt);

    public record Presence(string Name, string? FocusedItemId);

    // Danh tính do server gán cho một kết nối đã Join (tên và vai trò lấy từ mã truy cập)
    public record Session(string ConnectionId, string UserName, string Role)
    {
        public bool IsAdmin => Role == Roles.Admin;
    }

    public void SetUser(string connectionId, AppUser user)
    {
        lock (_gate) _usersByConnection[connectionId] = user;
    }

    // Cấp token phiên cho kết nối. Token cũ của cùng kết nối (nếu có) mất hiệu lực.
    // Token là bí mật: không ghi log, không đưa vào URL.
    public string IssueToken(string connectionId)
    {
        lock (_gate)
        {
            if (_tokenByConnection.TryGetValue(connectionId, out var previous))
                _connectionByToken.Remove(previous);

            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            _tokenByConnection[connectionId] = token;
            _connectionByToken[token] = connectionId;
            return token;
        }
    }

    // null nếu token không tồn tại, đã bị thay, hoặc kết nối đã đóng
    public Session? ResolveSession(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;

        lock (_gate)
        {
            if (_connectionByToken.TryGetValue(token, out var connectionId)
                && _usersByConnection.TryGetValue(connectionId, out var user))
            {
                return new Session(connectionId, user.Name, user.Role);
            }
            return null;
        }
    }

    // Trả về (đã giữ được, tên người đang giữ)
    public (bool Granted, string? Holder) TryLock(string itemId, string connectionId, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!_usersByConnection.TryGetValue(connectionId, out var user))
                return (false, null);
            var name = user.Name;

            if (_leasesByItem.TryGetValue(itemId, out var current)
                && current.ExpiresAt > now
                && current.ConnectionId != connectionId)
            {
                return (false, current.UserName);
            }

            _leasesByItem[itemId] = new Lease(connectionId, name, now + _leaseDuration);
            return (true, name);
        }
    }

    public bool Unlock(string itemId, string connectionId)
    {
        lock (_gate)
        {
            if (_leasesByItem.TryGetValue(itemId, out var lease) && lease.ConnectionId == connectionId)
            {
                _leasesByItem.Remove(itemId);
                return true;
            }
            return false;
        }
    }

    // Trả về các bình luận thực sự đang bị khóa và vừa được giải phóng
    public List<string> ReleaseItems(IEnumerable<string> itemIds)
    {
        lock (_gate)
        {
            var released = new List<string>();
            foreach (var id in itemIds)
            {
                if (_leasesByItem.Remove(id)) released.Add(id);
            }
            return released;
        }
    }

    // Gỡ kết nối (đóng tab/mất mạng): trả về các bình luận đang được giải phóng
    public List<string> RemoveConnection(string connectionId)
    {
        lock (_gate)
        {
            _usersByConnection.Remove(connectionId);
            if (_tokenByConnection.Remove(connectionId, out var token))
                _connectionByToken.Remove(token);
            var released = _leasesByItem
                .Where(kv => kv.Value.ConnectionId == connectionId)
                .Select(kv => kv.Key)
                .ToList();
            foreach (var id in released) _leasesByItem.Remove(id);
            return released;
        }
    }

    public List<string> ExpireLeases(DateTimeOffset now)
    {
        lock (_gate)
        {
            var expired = _leasesByItem
                .Where(kv => kv.Value.ExpiresAt <= now)
                .Select(kv => kv.Key)
                .ToList();
            foreach (var id in expired) _leasesByItem.Remove(id);
            return expired;
        }
    }

    public List<(string ItemId, string Holder)> GetLeases(DateTimeOffset now)
    {
        lock (_gate)
        {
            return _leasesByItem
                .Where(kv => kv.Value.ExpiresAt > now)
                .Select(kv => (kv.Key, kv.Value.UserName))
                .ToList();
        }
    }

    public List<Presence> GetPresence()
    {
        lock (_gate)
        {
            return _usersByConnection.Values
                .Select(u => u.Name)
                .Distinct()
                .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)
                .Select(name =>
                {
                    var focused = _leasesByItem
                        .FirstOrDefault(kv => kv.Value.UserName == name).Key;
                    return new Presence(name, focused);
                })
                .ToList();
        }
    }
}
