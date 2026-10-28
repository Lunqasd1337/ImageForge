using ImageForge.Core.Registry.RegFile;

namespace ImageForge.Core.Tests.Registry;

public sealed class RegFileParserTests
{
    private const string V5 = "Windows Registry Editor Version 5.00";
    private static readonly RegFileParser Parser = new();

    private static RegFileParseResult Parse(params string[] lines) => Parser.Parse(string.Join("\r\n", lines) + "\r\n");

    private static RegFileParseResult Valid(params string[] lines)
    {
        var result = Parse(lines);
        result.Diagnostics.ShouldBeEmpty();
        return result;
    }

    private static RegFileDiagnostic Error(params string[] lines)
    {
        var result = Parse(lines);
        result.Operations.ShouldBeEmpty();
        return result.Diagnostics.ShouldHaveSingleItem();
    }

    private static string Hex(RegFileOperation op) => Convert.ToHexString(((SetRegValue)op).Data.Bytes);

    [Fact]
    public void Version5_header_is_accepted()
    {
        Valid(V5).Operations.ShouldBeEmpty();
    }

    [Fact]
    public void Regedit4_is_refused()
    {
        var error = Error("REGEDIT4", "[HKEY_LOCAL_MACHINE\\SOFTWARE\\A]", "\"a\"=\"b\"");

        error.Code.ShouldBe(RegFileCodes.FormatUnsupported);
        error.Line.ShouldBe(1);
    }

    [Fact]
    public void Missing_header_blocks_the_file()
    {
        Error("[HKEY_LOCAL_MACHINE\\SOFTWARE\\A]").Code.ShouldBe(RegFileCodes.HeaderMissing);
    }

    [Fact]
    public void Keys_values_and_deletions_keep_their_order_and_lines()
    {
        var ops = Valid(
            V5,
            "",
            "; comment",
            "[HKEY_LOCAL_MACHINE\\SOFTWARE\\A]",
            "\"Name\"=\"Text\"",
            "@=\"Default\"",
            "\"Gone\"=-",
            "@=-",
            "\"Name\"=\"Again\"",
            "[-HKEY_LOCAL_MACHINE\\SOFTWARE\\B]").Operations;

        ops.Select(o => (o.GetType().Name, o.Line, o.KeyPath)).ShouldBe(
        [
            ("CreateRegKey", 4, @"HKEY_LOCAL_MACHINE\SOFTWARE\A"),
            ("SetRegValue", 5, @"HKEY_LOCAL_MACHINE\SOFTWARE\A"),
            ("SetRegValue", 6, @"HKEY_LOCAL_MACHINE\SOFTWARE\A"),
            ("DeleteRegValue", 7, @"HKEY_LOCAL_MACHINE\SOFTWARE\A"),
            ("DeleteRegValue", 8, @"HKEY_LOCAL_MACHINE\SOFTWARE\A"),
            ("SetRegValue", 9, @"HKEY_LOCAL_MACHINE\SOFTWARE\A"),
            ("DeleteRegKey", 10, @"HKEY_LOCAL_MACHINE\SOFTWARE\B"),
        ]);
        ((SetRegValue)ops[1]).Name.ShouldBe("Name");
        ((SetRegValue)ops[2]).Name.ShouldBe(string.Empty);
        ((DeleteRegValue)ops[3]).Name.ShouldBe("Gone");
        ((DeleteRegValue)ops[4]).Name.ShouldBe(string.Empty);
    }

    [Fact]
    public void Strings_become_nul_terminated_utf16()
    {
        var op = (SetRegValue)Valid(V5, "[HKEY_LOCAL_MACHINE\\SOFTWARE\\A]", "\"N\"=\"Аб\"").Operations[1];

        op.Data.Type.ShouldBe(RegValueType.Sz);
        Convert.ToHexString(op.Data.Bytes).ShouldBe("100431040000");
    }

    [Fact]
    public void Escapes_are_decoded_and_a_semicolon_inside_a_string_is_data()
    {
        var ops = Valid(V5, "[HKEY_LOCAL_MACHINE\\SOFTWARE\\A]", "\"a\\\\b \\\"q\\\"\"=\"C:\\\\x; not a comment\"  ; comment").Operations;

        var op = (SetRegValue)ops[1];
        op.Name.ShouldBe("a\\b \"q\"");
        op.Data.Describe().ShouldBe("REG_SZ: C:\\x; not a comment");
    }

    [Fact]
    public void Unknown_escape_is_an_error()
    {
        var error = Error(V5, "[HKEY_LOCAL_MACHINE\\SOFTWARE\\A]", "\"a\"=\"x\\n\"");
        error.Code.ShouldBe(RegFileCodes.ValueMalformed);
        error.Line.ShouldBe(3);
    }

    [Theory]
    [InlineData("dword:00000001", "01000000")]
    [InlineData("dword:FFFFFFFF", "FFFFFFFF")]
    [InlineData("dword:1a", "1A000000")]
    public void Dword_values_are_little_endian(string data, string hex)
    {
        var op = Valid(V5, "[HKEY_LOCAL_MACHINE\\SOFTWARE\\A]", $"\"d\"={data}").Operations[1];

        ((SetRegValue)op).Data.Type.ShouldBe(RegValueType.Dword);
        Hex(op).ShouldBe(hex);
    }

    [Theory]
    [InlineData("dword:")]
    [InlineData("dword:123456789")]
    [InlineData("dword:0000000g")]
    public void Bad_dword_is_an_error(string data)
    {
        Error(V5, "[HKEY_LOCAL_MACHINE\\SOFTWARE\\A]", $"\"d\"={data}").Code.ShouldBe(RegFileCodes.ValueMalformed);
    }

    [Theory]
    [InlineData("hex:", RegValueType.Binary, "")]
    [InlineData("hex:01,ab,FF", RegValueType.Binary, "01ABFF")]
    [InlineData("hex(0):01", RegValueType.None, "01")]
    [InlineData("hex(2):41,00,00,00", RegValueType.ExpandSz, "41000000")]
    [InlineData("hex(4):01,00,00,00", RegValueType.Dword, "01000000")]
    [InlineData("hex(5):00,00,00,01", RegValueType.DwordBigEndian, "00000001")]
    [InlineData("hex(6):41,00", RegValueType.Link, "4100")]
    [InlineData("hex(7):41,00,00,00,00,00", RegValueType.MultiSz, "410000000000")]
    [InlineData("hex(8):00", RegValueType.ResourceList, "00")]
    [InlineData("hex(9):00", RegValueType.FullResourceDescriptor, "00")]
    [InlineData("hex(a):00", RegValueType.ResourceRequirementsList, "00")]
    [InlineData("hex(b):01,00,00,00,00,00,00,00", RegValueType.Qword, "0100000000000000")]
    public void Hex_values_keep_their_type_and_bytes(string data, RegValueType type, string hex)
    {
        var op = Valid(V5, "[HKEY_LOCAL_MACHINE\\SOFTWARE\\A]", $"\"h\"={data}").Operations[1];

        ((SetRegValue)op).Data.Type.ShouldBe(type);
        Hex(op).ShouldBe(hex);
    }

    [Fact]
    public void Hex_continues_over_lines()
    {
        var ops = Valid(
            V5,
            "[HKEY_LOCAL_MACHINE\\SOFTWARE\\A]",
            "\"h\"=hex:01,02,\\",
            "  03,04,\\",
            "  05",
            "\"next\"=dword:00000002").Operations;

        Hex(ops[1]).ShouldBe("0102030405");
        ops[2].Line.ShouldBe(6);
    }

    [Fact]
    public void Continuation_at_end_of_file_is_an_error()
    {
        Error(V5, "[HKEY_LOCAL_MACHINE\\SOFTWARE\\A]", "\"h\"=hex:01,\\").Code.ShouldBe(RegFileCodes.ValueMalformed);
    }

    [Theory]
    [InlineData("hex:1g")]
    [InlineData("hex:01,,02")]
    [InlineData("hex:123")]
    [InlineData("hex(2:00")]
    public void Bad_hex_is_an_error(string data)
    {
        Error(V5, "[HKEY_LOCAL_MACHINE\\SOFTWARE\\A]", $"\"h\"={data}").Code.ShouldBe(RegFileCodes.ValueMalformed);
    }

    [Theory]
    [InlineData("hex(c):00")]
    [InlineData("hex(100):00")]
    public void Types_outside_0_to_b_are_blocked(string data)
    {
        Error(V5, "[HKEY_LOCAL_MACHINE\\SOFTWARE\\A]", $"\"h\"={data}").Code.ShouldBe(RegFileCodes.ValueTypeUnsupported);
    }

    [Fact]
    public void Version5_string_hex_is_kept_as_utf16_bytes()
    {
        var ops = Valid(V5, "[HKEY_LOCAL_MACHINE\\SOFTWARE\\A]", "\"e\"=hex(2):25,00,41,00,25,00,00,00").Operations;

        Hex(ops[1]).ShouldBe("2500410025000000");
        ((SetRegValue)ops[1]).Data.Describe().ShouldBe("REG_EXPAND_SZ: %A%");
    }

    [Fact]
    public void Value_before_any_key_is_an_error()
    {
        var error = Error(V5, "\"a\"=\"b\"");
        error.Code.ShouldBe(RegFileCodes.ValueOutsideKey);
        error.Line.ShouldBe(2);
    }

    [Fact]
    public void Value_after_a_deleted_key_is_an_error()
    {
        Error(V5, "[-HKEY_LOCAL_MACHINE\\SOFTWARE\\A]", "\"a\"=\"b\"").Code.ShouldBe(RegFileCodes.ValueOutsideKey);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[-]")]
    [InlineData("[HKEY_LOCAL_MACHINE\\SOFTWARE\\A")]
    [InlineData("[HKEY_LOCAL_MACHINE\\SOFTWARE\\A] extra")]
    public void Malformed_key_is_an_error(string line)
    {
        Error(V5, line).Code.ShouldBe(RegFileCodes.KeyMalformed);
    }

    [Theory]
    [InlineData("\"a\"")]
    [InlineData("\"a\"=")]
    [InlineData("\"a\"=text")]
    [InlineData("\"a=\"b\"")]
    [InlineData("\"a\"=\"b\" trailing")]
    [InlineData("\"a\"=- trailing")]
    public void Malformed_value_is_an_error(string line)
    {
        Error(V5, "[HKEY_LOCAL_MACHINE\\SOFTWARE\\A]", line).Code.ShouldBe(RegFileCodes.ValueMalformed);
    }

    [Fact]
    public void Unrecognised_line_is_an_error()
    {
        Error(V5, "[HKEY_LOCAL_MACHINE\\SOFTWARE\\A]", "garbage").Code.ShouldBe(RegFileCodes.LineUnrecognized);
    }

    [Fact]
    public void One_error_blocks_the_whole_file_and_all_errors_are_reported()
    {
        var result = Parse(V5, "[HKEY_LOCAL_MACHINE\\SOFTWARE\\A]", "\"ok\"=\"1\"", "bad", "\"d\"=dword:zz");

        result.Operations.ShouldBeEmpty();
        result.Diagnostics.Select(d => d.Line).ShouldBe([4, 5]);
    }

    [Fact]
    public void Lf_only_line_endings_work()
    {
        Parser.Parse(V5 + "\n[HKEY_LOCAL_MACHINE\\SOFTWARE\\A]\n\"a\"=\"b\"\n").Operations.Count.ShouldBe(2);
    }
}
