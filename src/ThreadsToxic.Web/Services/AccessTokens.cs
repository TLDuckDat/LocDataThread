using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace ThreadsToxic.Web.Services;

// Một tài khoản trong mục "Auth:Users". Token gốc không nằm trong cấu hình, chỉ có SHA-256 (hex) của nó.
// Tạo bằng: python tools/new_token.py --index <n> --name <tên> [--role admin]
public class AuthUser
{
    public string Name { get; set; } = "";
    public string Role { get; set; } = Roles.Member;
    public string TokenSha256 { get; set; } = "";
}

public class AuthOptions
{
    public List<AuthUser> Users { get; set; } = new();
}

public static class Roles
{
    public const string Admin = "admin";
    public const string Member = "member";
}

public record AppUser(string Name, string Role)
{
    public bool IsAdmin => Role == Roles.Admin;
}

/// <summary>
/// Mã truy cập vĩnh viễn: mỗi người một mã, chỉ đổi khi admin thay hash trong cấu hình.
/// Mã là bí mật: không ghi log, không đưa vào URL; server chỉ so sánh SHA-256.
/// </summary>
public class AccessTokens
{
    public const string Header = "X-Access-Token";

    private readonly Dictionary<string, AppUser> _byHash;

    public AccessTokens(IOptions<AuthOptions> options)
    {
        var users = options.Value.Users;
        if (users.Count == 0)
            throw new InvalidOperationException(
                "Chưa cấu hình tài khoản (Auth:Users). Tạo bằng `python tools/new_token.py` rồi đặt biến môi trường " +
                "Auth__Users__0__Name / Auth__Users__0__Role / Auth__Users__0__TokenSha256.");

        _byHash = new Dictionary<string, AppUser>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        foreach (var user in users)
        {
            var name = (user.Name ?? "").Trim();
            var hash = (user.TokenSha256 ?? "").Trim().ToLowerInvariant();
            var role = (user.Role ?? Roles.Member).Trim().ToLowerInvariant();

            if (name.Length is 0 or > 40)
                throw new InvalidOperationException($"Auth:Users: tên '{name}' phải từ 1 đến 40 ký tự.");
            if (!names.Add(name))
                throw new InvalidOperationException($"Auth:Users: tên '{name}' bị trùng.");
            if (role is not (Roles.Admin or Roles.Member))
                throw new InvalidOperationException($"Auth:Users: vai trò của '{name}' phải là admin hoặc member.");
            if (hash.Length != 64 || !hash.All(Uri.IsHexDigit))
                throw new InvalidOperationException($"Auth:Users: TokenSha256 của '{name}' phải là 64 ký tự hex.");
            if (!_byHash.TryAdd(hash, new AppUser(name, role)))
                throw new InvalidOperationException($"Auth:Users: mã của '{name}' trùng với người khác.");
        }
    }

    public IReadOnlyCollection<AppUser> Users => _byHash.Values;

    public AppUser? Resolve(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        return _byHash.GetValueOrDefault(Hash(token.Trim()));
    }

    public static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
}
