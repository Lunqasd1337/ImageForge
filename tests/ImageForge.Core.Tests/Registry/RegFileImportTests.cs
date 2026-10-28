using System.Text;
using ImageForge.Core.Changes;
using ImageForge.Core.Registry.RegFile;

namespace ImageForge.Core.Tests.Registry;

public sealed class RegFileImportTests
{
    private static byte[] Utf16(string text) => [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(text)];

    [Fact]
    public void A_valid_file_becomes_changes_and_names_its_encoding()
    {
        var input = new RegFileInput("a.reg", Utf16("Windows Registry Editor Version 5.00\r\n\r\n[HKEY_LOCAL_MACHINE\\SOFTWARE\\X]\r\n\"a\"=dword:00000001\r\n"), RegFileEncoding.Auto, 1251);

        var result = RegFileImport.Read(input);

        result.Diagnostics.ShouldBeEmpty();
        result.Encoding.ShouldBe(RegFileEncodingKind.Utf16);
        result.Changes.ShouldNotBeNull().ShouldBe(
        [
            new CreateRegistryKey(@"HKEY_LOCAL_MACHINE\SOFTWARE\X"),
            new SetRegistryValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\X", "a", new RegistryValue(RegistryValueType.Dword, [1, 0, 0, 0])),
        ]);
    }

    [Fact]
    public void Decoding_problems_block_the_file()
    {
        var input = new RegFileInput("a.reg", Encoding.UTF8.GetBytes("Windows Registry Editor Version 5.00\r\n; Привет\r\n"), RegFileEncoding.Auto, 1251);

        var result = RegFileImport.Read(input);

        result.Changes.ShouldBeNull();
        result.Diagnostics.Single().Code.ShouldBe(RegFileCodes.EncodingAmbiguous);
    }

    [Fact]
    public void Syntax_problems_block_the_file_with_line_numbers()
    {
        var input = new RegFileInput("a.reg", Utf16("Windows Registry Editor Version 5.00\r\n\r\n[HKEY_LOCAL_MACHINE\\SOFTWARE\\X]\r\n\"a\"=dword:zz\r\n"), RegFileEncoding.Auto, 1251);

        var result = RegFileImport.Read(input);

        result.Changes.ShouldBeNull();
        result.Diagnostics.Single().Line.ShouldBe(4);
    }

    [Fact]
    public void A_file_with_no_changes_is_refused()
    {
        var result = RegFileImport.Read(new RegFileInput("a.reg", Utf16("Windows Registry Editor Version 5.00\r\n\r\n; только комментарий\r\n"), RegFileEncoding.Auto, 1251));

        result.Changes.ShouldBeNull();
        result.Diagnostics.Single().Code.ShouldBe(RegFileCodes.Empty);
    }

    [Fact]
    public void A_file_above_the_limit_is_refused_before_decoding()
    {
        var result = RegFileImport.Read(new RegFileInput("a.reg", new byte[RegFileImport.MaxBytes + 1], RegFileEncoding.Auto, 1251));

        result.Changes.ShouldBeNull();
        result.Diagnostics.Single().Code.ShouldBe(RegFileCodes.TooLarge);
    }

    [Fact]
    public void Every_code_has_a_russian_description()
    {
        var codes = typeof(RegFileCodes).GetFields().Select(f => (string)f.GetValue(null)!).ToList();

        codes.ShouldNotBeEmpty();
        foreach (var code in codes)
        {
            var text = RegFileDiagnosticText.Describe(new RegFileDiagnostic(code, 3, "details"));
            text.ShouldContain("строка 3");
            text.ShouldNotContain(code);
            text.Any(c => c is >= 'а' and <= 'я').ShouldBeTrue(code);
        }
    }

    [Fact]
    public void A_whole_file_problem_has_no_line()
    {
        RegFileDiagnosticText.Describe(new RegFileDiagnostic(RegFileCodes.TooLarge, 0, "x")).ShouldNotContain("строка");
    }
}
