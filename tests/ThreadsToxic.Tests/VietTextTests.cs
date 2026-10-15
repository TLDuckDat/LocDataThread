using ThreadsToxic.Web.Services.Export;

namespace ThreadsToxic.Tests;

/// <summary>
/// Kết quả mong đợi lấy từ chính text_normalizer.py của crawler (Python 3.14), để bản port cho ra đúng như vậy.
/// </summary>
public class VietTextTests
{
    [Theory]
    [InlineData("Bạn ơi đẹp quá Translate 1 / 2", "Bạn ơi đẹp quá", true)]
    [InlineData("  Xem bản dịch 2/3 hay quá đi  ", "hay quá đi", true)]
    [InlineData("Dịch", "", false)]
    [InlineData("Hay lắm Dịch", "Hay lắm", true)]
    [InlineData("XEM BẢN DỊCH 1 / 2", "", false)]
    [InlineData("Tranſlate 1 / 2 ok", "ok", false)]                  // re.IGNORECASE: 's' khớp cả 'ſ'
    [InlineData("abcTranslate 1 / 2", "abcTranslate", false)]        // \b: không xóa giữa từ
    [InlineData("giá 1 / 2", "giá", false)]
    [InlineData("1 / 2 / 3", "1 /", false)]
    [InlineData("a12 / 3", "a12 / 3", false)]
    [InlineData("khong biet noi gi", "khong biet noi gi", true)]
    [InlineData("ok nha", "ok nha", true)]
    [InlineData("hello world", "hello world", false)]
    [InlineData("😂😂😂", "😂😂😂", false)]
    [InlineData("đi đâu đó", "đi đâu đó", true)]
    [InlineData("ª là gì vậy", "ª là gì vậy", false)]                // ª không mang tên LATIN: bị coi là ngoại ngữ
    [InlineData("𝐊𝐡ô𝐧𝐠 biết", "𝐊𝐡ô𝐧𝐠 biết", false)]
    [InlineData("Ơ kìa", "Ơ kìa", true)]
    [InlineData("thật sự\tkhông\n\nbiết", "thật sự không biết", true)]
    [InlineData("Translate 𝟏 / 𝟐 đẹp quá", "đẹp quá", true)]          // \d của Python gồm chữ số ngoài BMP
    [InlineData("mình  Translate\u00a01 / 2", "mình", true)]
    [InlineData("İ love Việt Nam", "İ love Việt Nam", true)]
    [InlineData("cảm ơn ạ", "cảm ơn ạ", true)]
    [InlineData("ngươi", "ngươi", true)]
    [InlineData("x\u001ckhông\u001fbiết", "x không biết", true)]     // \x1c-\x1f là khoảng trắng với Python
    public void Matches_crawler_cleaning_and_vietnamese_detection(string text, string cleaned, bool isVietnamese)
    {
        Assert.Equal(cleaned, VietText.CleanUiArtifacts(text));
        Assert.Equal(isVietnamese, VietText.IsVietnameseContent(text));
    }

    [Fact]
    public void CleanToVietEng_strips_foreign_scripts()
    {
        Assert.Equal("Xin chào", VietText.CleanToVietEng("Xin chào 你好"));
        Assert.False(VietText.IsVietnameseContent("Xin chào 你好"));
    }

    [Theory]
    [InlineData("Meta AI: xin chào", true)]
    [InlineData("@meta_ai ... trả lời", true)]
    [InlineData("meta ai", true)]
    [InlineData("Hỏi Meta AI đi", false)]
    [InlineData("  METAİ – bạn", false)]
    [InlineData("", false)]
    public void Detects_meta_ai_responses(string text, bool expected) =>
        Assert.Equal(expected, VietText.IsMetaAiResponse(text));

    [Theory]
    [InlineData("meta.ai", "", true)]
    [InlineData("", "Meta AI", true)]
    [InlineData("metaai_official", "", true)]
    [InlineData("META AI Bot", "", true)]
    [InlineData("metaaİ", "", true)]                                   // casefold: İ -> i + dấu chấm
    [InlineData("tuan", "Tuấn", false)]
    [InlineData(null, null, false)]
    public void Detects_meta_ai_authors(string? username, string? name, bool expected) =>
        Assert.Equal(expected, VietText.IsMetaAiAuthor(username, name));

    [Theory]
    [InlineData(0.345, "0.34")]     // 0.345 thực chất nhỏ hơn 0.345 trong nhị phân
    [InlineData(2.675, "2.67")]
    [InlineData(0.125, "0.12")]     // đúng nửa: làm tròn về số chẵn
    [InlineData(0.375, "0.38")]
    [InlineData(1.0, "1.0")]
    [InlineData(0.0, "0.0")]
    [InlineData(0.7, "0.7")]
    public void Round_and_repr_match_python(double value, string expected) =>
        Assert.Equal(expected, PyUnicode.Repr(PyUnicode.Round(value, 2)));

    [Fact]
    public void Float_scores_from_postgres_round_like_the_original_double()
    {
        // toxic_score lưu dạng real (float4); bên crawler là double. Điểm 2 chữ số thập phân vẫn ra cùng chuỗi.
        foreach (var score in new[] { 0.35f, 0.39f, 0.42f, 0.45f, 0.73f, 0.77f, 0.84f, 0.98f })
            Assert.Equal(score.ToString(System.Globalization.CultureInfo.InvariantCulture), PyUnicode.Repr(PyUnicode.Round(score, 2)));
    }

    [Theory]
    [InlineData(1e-05, "1e-05")]
    [InlineData(0.0001, "0.0001")]
    [InlineData(1e16, "1e+16")]
    [InlineData(1e15, "1000000000000000.0")]
    [InlineData(123.5, "123.5")]
    public void Repr_uses_python_float_format(double value, string expected) =>
        Assert.Equal(expected, PyUnicode.Repr(value));

    [Fact]
    public void Length_counts_code_points_and_collapse_uses_python_whitespace()
    {
        Assert.Equal(3, PyUnicode.Length("😂😂😂"));
        Assert.Equal("a b c", PyUnicode.CollapseSpaces("\u3000a\u001cb\u2028 c\u00a0"));
        Assert.Equal("i\u0307", PyUnicode.Lower("İ"));
        Assert.Equal("ss", PyUnicode.Casefold("ß"));
    }
}
