using System.Text;
using ImageForge.Core.Changes;
using ImageForge.Core.Registry.RegFile;

namespace ImageForge.Core.Tests.Registry;

/// <summary>
/// regtest-v5.reg is the file `reg export` wrote in S2-REG-PARSE (Win11 26H2, 30.10.2026); every value matched the live
/// registry byte for byte there, so these bytes are what Windows itself means by the file.
/// </summary>
public sealed class RegFileFixtureTests
{
    private const string Key = @"HKEY_CURRENT_USER\Software\ImageForgeRegFileTest";

    private static byte[] Sz(string text) => Encoding.Unicode.GetBytes(text + "\0");

    [Fact]
    public void A_regedit_export_becomes_the_values_windows_stored()
    {
        var content = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Registry", "Fixtures", "regtest-v5.reg"));

        var result = RegFileImport.Read(new RegFileInput("regtest-v5.reg", content, RegFileEncoding.Auto, 1251));

        result.Diagnostics.ShouldBeEmpty();
        result.Encoding.ShouldBe(RegFileEncodingKind.Utf16);
        result.Changes.ShouldNotBeNull().ShouldBe(
        [
            new CreateRegistryKey(Key),
            new SetRegistryValue(Key, "Строка", new RegistryValue(RegistryValueType.Sz, Sz(@"Привет; это не комментарий \ а обратная черта"))),
            new SetRegistryValue(Key, string.Empty, new RegistryValue(RegistryValueType.Sz, Sz("по умолчанию"))),
            new SetRegistryValue(Key, "Путь", new RegistryValue(RegistryValueType.ExpandSz, Sz(@"%SystemRoot%\Тест"))),
            new SetRegistryValue(Key, "Список", new RegistryValue(RegistryValueType.MultiSz, Encoding.Unicode.GetBytes("один\0два\0три\0\0"))),
            new SetRegistryValue(Key, "Число", new RegistryValue(RegistryValueType.Dword, [0x78, 0x56, 0x34, 0x12])),
            new SetRegistryValue(Key, "Большое", new RegistryValue(RegistryValueType.Qword, [0x88, 0x77, 0x66, 0x55, 0x44, 0x33, 0x22, 0x11])),
            new SetRegistryValue(Key, "Двоичное", new RegistryValue(RegistryValueType.Binary, [0x00, 0xFF, 0x10, 0xAB])),
            new SetRegistryValue(Key, "Пустое", new RegistryValue(RegistryValueType.None, [0, 0])),
            new CreateRegistryKey(Key + @"\Вложенный ключ"),
            new SetRegistryValue(Key + @"\Вложенный ключ", "Длинное", new RegistryValue(RegistryValueType.Binary,
                [.. Enumerable.Repeat<byte[]>([0x01, 0x23, 0x45, 0x67, 0x89, 0xAB, 0xCD, 0xEF], 16).SelectMany(b => b)])),
        ]);
    }
}
