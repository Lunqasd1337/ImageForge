using System.Text;
using ImageForge.Core.Registry.RegFile;

namespace ImageForge.Core.Tests.Registry;

public sealed class RegFileLoaderTests
{
    private const string V5 = "Windows Registry Editor Version 5.00\r\n\r\n[HKEY_LOCAL_MACHINE\\SOFTWARE\\Тест]\r\n\"Имя\"=\"Значение\"\r\n";
    private static readonly RegFileLoader Loader = new();

    [Fact]
    public void Utf16_with_bom_is_decoded()
    {
        var bytes = Concat([0xFF, 0xFE], Encoding.Unicode.GetBytes(V5));

        var result = Loader.Decode(bytes, RegFileEncoding.Auto, 1251);

        result.Text.ShouldBe(V5);
        result.Encoding.ShouldBe(RegFileEncodingKind.Utf16);
        result.Diagnostics.ShouldBeEmpty();
    }

    [Fact]
    public void Utf8_with_bom_is_decoded()
    {
        var bytes = Concat([0xEF, 0xBB, 0xBF], Encoding.UTF8.GetBytes(V5));

        var result = Loader.Decode(bytes, RegFileEncoding.Auto, 1251);

        result.Text.ShouldBe(V5);
        result.Encoding.ShouldBe(RegFileEncodingKind.Utf8);
    }

    [Fact]
    public void Ascii_without_bom_needs_no_code_page_choice()
    {
        var text = "REGEDIT4\r\n\r\n[HKEY_LOCAL_MACHINE\\SOFTWARE\\A]\r\n\"B\"=\"C\"\r\n";

        var result = Loader.Decode(Encoding.ASCII.GetBytes(text), RegFileEncoding.Auto, 1251);

        result.Text.ShouldBe(text);
        result.Encoding.ShouldBe(RegFileEncodingKind.Ansi);
        result.CodePage.ShouldBe(1251);
    }

    [Fact]
    public void Ansi_without_bom_uses_the_shown_code_page()
    {
        var text = "REGEDIT4\r\n\r\n[HKEY_LOCAL_MACHINE\\SOFTWARE\\Тест]\r\n\"Имя\"=\"Значение\"\r\n";
        var bytes = Cp(1251).GetBytes(text);

        var result = Loader.Decode(bytes, RegFileEncoding.Auto, 1251);

        result.Text.ShouldBe(text);
        result.Encoding.ShouldBe(RegFileEncodingKind.Ansi);
        result.CodePage.ShouldBe(1251);
        result.Diagnostics.ShouldBeEmpty();
    }

    [Fact]
    public void Non_ascii_text_that_is_also_valid_utf8_is_ambiguous_without_a_bom()
    {
        var bytes = Encoding.UTF8.GetBytes(V5);

        var result = Loader.Decode(bytes, RegFileEncoding.Auto, 1251);

        result.Text.ShouldBeNull();
        result.Diagnostics.Single().Code.ShouldBe(RegFileCodes.EncodingAmbiguous);
    }

    [Fact]
    public void An_explicit_choice_resolves_the_ambiguity()
    {
        var bytes = Encoding.UTF8.GetBytes(V5);

        Loader.Decode(bytes, RegFileEncoding.Utf8, 1251).Text.ShouldBe(V5);
    }

    [Fact]
    public void Zero_bytes_without_a_bom_are_ambiguous()
    {
        var bytes = Encoding.Unicode.GetBytes(V5);

        var result = Loader.Decode(bytes, RegFileEncoding.Auto, 1251);

        result.Text.ShouldBeNull();
        result.Diagnostics.Single().Code.ShouldBe(RegFileCodes.EncodingAmbiguous);
    }

    [Fact]
    public void Truncated_utf16_is_corrupt()
    {
        var bytes = Concat([0xFF, 0xFE], Encoding.Unicode.GetBytes(V5), [0x41]);

        var result = Loader.Decode(bytes, RegFileEncoding.Auto, 1251);

        result.Text.ShouldBeNull();
        result.Diagnostics.Single().Code.ShouldBe(RegFileCodes.EncodingInvalid);
    }

    [Fact]
    public void Unpaired_surrogate_in_utf16_is_corrupt()
    {
        var bytes = Concat([0xFF, 0xFE], Encoding.Unicode.GetBytes("REGEDIT4\r\n"), [0x00, 0xD8, 0x41, 0x00]);

        Loader.Decode(bytes, RegFileEncoding.Auto, 1251).Diagnostics.Single().Code.ShouldBe(RegFileCodes.EncodingInvalid);
    }

    [Fact]
    public void Bytes_invalid_in_the_code_page_are_corrupt()
    {
        // In Shift-JIS 0x81 is a lead byte; '"' cannot follow it.
        var bytes = Concat(Encoding.ASCII.GetBytes("REGEDIT4\r\n\"a\"=\""), [0x81], Encoding.ASCII.GetBytes("\"\r\n"));

        var result = Loader.Decode(bytes, RegFileEncoding.Ansi, 932);

        result.Text.ShouldBeNull();
        result.Diagnostics.Single().Code.ShouldBe(RegFileCodes.EncodingInvalid);
    }

    [Fact]
    public void Big_endian_utf16_is_not_supported()
    {
        var bytes = Concat([0xFE, 0xFF], Encoding.BigEndianUnicode.GetBytes(V5));

        Loader.Decode(bytes, RegFileEncoding.Auto, 1251).Diagnostics.Single().Code.ShouldBe(RegFileCodes.EncodingUnsupported);
    }

    [Fact]
    public void Unknown_code_page_is_reported()
    {
        Loader.Decode(Encoding.ASCII.GetBytes("REGEDIT4\r\n"), RegFileEncoding.Ansi, 99999).Diagnostics.Single().Code.ShouldBe(RegFileCodes.EncodingUnsupported);
    }

    internal static Encoding Cp(int codePage) => CodePagesEncodingProvider.Instance.GetEncoding(codePage)!;

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
}
