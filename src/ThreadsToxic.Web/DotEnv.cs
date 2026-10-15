using Microsoft.Extensions.Configuration.Memory;

namespace ThreadsToxic.Web;

/// <summary>
/// Nạp file .env ở gốc repo (thư mục có ThreadsToxic.slnx) vào configuration với độ ưu tiên
/// thấp nhất: appsettings và biến môi trường đều đè được. "__" trong tên khóa là ":",
/// giống quy ước biến môi trường, nên ConnectionStrings__Default thành ConnectionStrings:Default.
/// </summary>
internal static class DotEnv
{
    public static void AddTo(ConfigurationManager configuration, string startDirectory)
    {
        var path = Find(startDirectory);
        if (path is null) return;

        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var eq = line.IndexOf('=');
            if (eq < 1) continue;

            var value = line[(eq + 1)..].Trim();
            if (value.Length >= 2 && (value[0] == '"' && value[^1] == '"' || value[0] == '\'' && value[^1] == '\''))
                value = value[1..^1];
            values[line[..eq].Trim().Replace("__", ":")] = value;
        }

        configuration.Sources.Insert(0, new MemoryConfigurationSource { InitialData = values });
    }

    private static string? Find(string startDirectory)
    {
        for (var dir = new DirectoryInfo(startDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ThreadsToxic.slnx")))
            {
                var path = Path.Combine(dir.FullName, ".env");
                return File.Exists(path) ? path : null;
            }
        }

        return null;
    }
}
