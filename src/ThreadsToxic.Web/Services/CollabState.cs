namespace ThreadsToxic.Web.Services;

// Trạng thái cộng tác trong bộ nhớ: ai đang online, ai đang giữ bình luận nào (lease lock).
// Chỉ chạy trên một tiến trình; nếu cần nhiều máy chủ thì chuyển sang Redis.
public class CollabState
{
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(45);

    private readonly object _gate = new();
    private readonly Dictionary<string, string> _usersByConnection = new();
    private readonly Dictionary<string, Lease> _leasesByItem = new();

    private sealed record Lease(string ConnectionId, string UserName, DateTimeOffset ExpiresAt);

    public record Presence(string Name, string? FocusedItemId);

    public void SetUser(string connectionId, string name)
    {
        lock (_gate) _usersByConnection[connectionId] = name;
    }

    // Trả về (đã giữ được, tên người đang giữ)
    public (bool Granted, string? Holder) TryLock(string itemId, string connectionId, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!_usersByConnection.TryGetValue(connectionId, out var name))
                return (false, null);

            if (_leasesByItem.TryGetValue(itemId, out var current)
                && current.ExpiresAt > now
                && current.ConnectionId != connectionId)
            {
                return (false, current.UserName);
            }

            _leasesByItem[itemId] = new Lease(connectionId, name, now + LeaseDuration);
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
