using Microsoft.AspNetCore.Http.HttpResults;

namespace ThreadsToxic.Web.Services;

public static class AuthFilters
{
    public const string AdminOnlyMessage = "Chỉ admin được làm thao tác này.";

    // Mọi API (kể cả chỉ đọc) cần người dùng đã xác thực: token phiên của kết nối hub (X-Session-Token)
    // hoặc mã truy cập (X-Access-Token, cho trang không mở kết nối hub như trang xuất file).
    // API ghi còn tự kiểm tra token phiên, nên mã truy cập một mình không ghi được.
    public static RouteGroupBuilder RequireUser(this RouteGroupBuilder group) =>
        group.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            var state = http.RequestServices.GetRequiredService<CollabState>();
            var tokens = http.RequestServices.GetRequiredService<AccessTokens>();
            var authenticated =
                state.ResolveSession(http.Request.Headers["X-Session-Token"]) is not null
                || tokens.Resolve(http.Request.Headers[AccessTokens.Header]) is not null;
            return authenticated ? await next(context) : Results.Unauthorized();
        });

    // Người đang gọi API: theo token phiên (trang có kết nối hub) hoặc mã truy cập
    public static AppUser? CurrentUser(HttpContext http)
    {
        var session = http.RequestServices.GetRequiredService<CollabState>()
            .ResolveSession(http.Request.Headers["X-Session-Token"]);
        if (session is not null) return new AppUser(session.UserName, session.Role);
        return http.RequestServices.GetRequiredService<AccessTokens>().Resolve(http.Request.Headers[AccessTokens.Header]);
    }

    public static JsonHttpResult<string> AdminOnly() => TypedResults.Json(AdminOnlyMessage, statusCode: 403);
}
