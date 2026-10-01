namespace XmlSourceSpans.Tests;

/// <summary>Bytes in: UTF-8 or UTF-16 told apart by the byte-order mark (none means UTF-8, and UTF-16 needs one), the
/// mark counted in every byte offset and in none of the character offsets, the bytes decoded strictly, a failing byte
/// named exactly, and a declaration refused — before anything is decoded — when it names an encoding the bytes are not
/// read in. Text in: a leading U+FEFF is counted, since the caller's text holds it, and a declaration is not consulted.
/// A file: its own URI the document's base URI.</summary>
public class EncodingTests
{
	const string Xml = "<r><a>μῆνιν</a><b/></r>";

	static byte[] With(byte[] mark, byte[] body) => [.. mark, .. body];

	[Fact]
	public void UTF8_bytes_without_a_mark_count_from_the_first_byte()
	{
		XDocument doc = XSpanReader.Load(Encoding.UTF8.GetBytes(Xml));

		Invariants.Check(doc, Xml, Encoding.UTF8, bomLength: 0, reparse: true);
	}

	[Fact]
	public void A_UTF8_mark_counts_in_the_bytes_and_not_in_the_characters()
	{
		XDocument doc = XSpanReader.Load(With([0xEF, 0xBB, 0xBF], Encoding.UTF8.GetBytes(Xml)));

		XSpan b = doc.Root.Element("b").Span;

		Equal(Xml.IndexOf("<b/>", StringComparison.Ordinal), b.Chars.Start);
		Equal(3 + Encoding.UTF8.GetByteCount(Xml[..b.Chars.Start]), b.Bytes.Start);

		Invariants.Check(doc, Xml, Encoding.UTF8, bomLength: 3, reparse: true);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void UTF16_counts_two_bytes_a_unit_after_its_mark(bool bigEndian)
	{
		UnicodeEncoding utf16 = new(bigEndian, byteOrderMark: false);
		byte[] mark = bigEndian ? [0xFE, 0xFF] : [0xFF, 0xFE];

		XDocument doc = XSpanReader.Load(With(mark, utf16.GetBytes(Xml)));

		XSpan b = doc.Root.Element("b").Span;

		Equal(2 + (2 * b.Chars.Start), b.Bytes.Start);

		Invariants.Check(doc, Xml, utf16, bomLength: 2, reparse: true);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void UTF16_without_a_mark_refuses_saying_so(bool bigEndian)
	{
		UnicodeEncoding utf16 = new(bigEndian, byteOrderMark: false);

		InvalidDataException ex = Throws<InvalidDataException>(() => XSpanReader.Load(utf16.GetBytes(Xml)));

		Contains("without a byte-order mark", ex.Message);
	}

	[Fact]
	public void An_invalid_UTF8_byte_refuses_naming_it()
	{
		byte[] bytes = [.. Encoding.UTF8.GetBytes("<r>"), 0xFF, .. Encoding.UTF8.GetBytes("</r>")];

		InvalidDataException ex = Throws<InvalidDataException>(() => XSpanReader.Load(bytes));

		Contains("not UTF-8", ex.Message);
		Contains("at byte 3", ex.Message);
	}

	[Fact]
	public void An_invalid_byte_after_a_mark_is_named_counting_the_mark()
	{
		byte[] bytes = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("<r>"), 0xFF, .. Encoding.UTF8.GetBytes("</r>")];

		InvalidDataException ex = Throws<InvalidDataException>(() => XSpanReader.Load(bytes));

		Contains("at byte 6", ex.Message);
	}

	// "<r>" is six bytes after the two of the mark; the lone high surrogate's own two bytes start at byte 8.
	[Fact]
	public void A_lone_high_surrogate_in_UTF16_is_named_at_its_own_byte()
	{
		byte[] bytes = [0xFF, 0xFE, (byte)'<', 0, (byte)'r', 0, (byte)'>', 0, 0x00, 0xD8, (byte)'x', 0, (byte)'<', 0, (byte)'/', 0, (byte)'r', 0, (byte)'>', 0];

		InvalidDataException ex = Throws<InvalidDataException>(() => XSpanReader.Load(bytes));

		Contains("not UTF-16", ex.Message);
		Contains("at byte 8", ex.Message);
	}

	[Fact]
	public void A_UTF32_mark_refuses()
		=> Throws<InvalidDataException>(() => XSpanReader.Load(With([0xFF, 0xFE, 0, 0], Encoding.UTF32.GetBytes("<r/>"))));

	[Fact]
	public void A_second_mark_refuses()
	{
		byte[] bytes = [0xEF, 0xBB, 0xBF, 0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("<?xml version=\"1.0\" encoding=\"latin1\"?><r/>")];

		InvalidDataException ex = Throws<InvalidDataException>(() => XSpanReader.Load(bytes));

		Contains("second byte-order mark", ex.Message);
	}

	[Theory]
	[InlineData("iso-8859-1")]
	[InlineData("windows-1252")]
	[InlineData("utf-16")]
	public void A_declaration_naming_an_encoding_the_bytes_are_not_read_in_refuses(string declared)
	{
		byte[] bytes = Encoding.UTF8.GetBytes($"<?xml version=\"1.0\" encoding=\"{declared}\"?><r/>");

		InvalidDataException ex = Throws<InvalidDataException>(() => XSpanReader.Load(bytes));

		Contains(declared, ex.Message);
	}

	// The declaration is read before the bytes are decoded, so an honestly declared Latin-1 file hears about its
	// declaration, not about its first byte that is not UTF-8.
	[Fact]
	public void A_declared_Latin1_file_hears_about_its_declaration_not_its_bytes()
	{
		byte[] bytes = Encoding.Latin1.GetBytes("<?xml version=\"1.0\" encoding=\"iso-8859-1\"?><r>é</r>");

		InvalidDataException ex = Throws<InvalidDataException>(() => XSpanReader.Load(bytes));

		Contains("iso-8859-1", ex.Message);
		DoesNotContain("invalid byte sequence", ex.Message);
	}

	[Theory]
	[InlineData("utf-8")]
	[InlineData("UTF-8")]
	[InlineData("us-ascii")]
	public void A_declaration_naming_UTF8_or_its_ASCII_subset_loads(string declared)
	{
		byte[] bytes = Encoding.UTF8.GetBytes($"<?xml version=\"1.0\" encoding='{declared}'?><r/>");

		NotNull(XSpanReader.Load(bytes).Root.Span);
	}

	[Fact]
	public void An_ASCII_declaration_over_a_byte_above_0x7F_refuses_naming_it()
	{
		const string xml = "<?xml version=\"1.0\" encoding=\"us-ascii\"?><r>μ</r>";
		byte[] bytes = Encoding.UTF8.GetBytes(xml);

		InvalidDataException ex = Throws<InvalidDataException>(() => XSpanReader.Load(bytes));

		Contains($"at byte {xml.IndexOf('μ', StringComparison.Ordinal)}", ex.Message);
	}

	[Fact]
	public void A_UTF16_declaration_under_a_UTF16_mark_loads()
	{
		byte[] bytes = With([0xFF, 0xFE], Encoding.Unicode.GetBytes("<?xml version=\"1.0\" encoding=\"UTF-16\"?><r/>"));

		NotNull(XSpanReader.Load(bytes).Root.Span);
	}

	[Theory]
	[InlineData("utf-16le", false, true)]
	[InlineData("utf-16be", false, false)]
	[InlineData("utf-16be", true, true)]
	[InlineData("utf-16le", true, false)]
	public void A_UTF16_declaration_naming_a_byte_order_must_be_the_marks(string declared, bool bigEndian, bool loads)
	{
		UnicodeEncoding utf16 = new(bigEndian, byteOrderMark: false);
		byte[] mark = bigEndian ? [0xFE, 0xFF] : [0xFF, 0xFE];
		byte[] bytes = With(mark, utf16.GetBytes($"<?xml version=\"1.0\" encoding=\"{declared}\"?><r/>"));

		if(loads)
			NotNull(XSpanReader.Load(bytes).Root.Span);
		else
			Contains(declared, Throws<InvalidDataException>(() => XSpanReader.Load(bytes)).Message);
	}

	[Fact]
	public void An_instruction_whose_name_starts_with_xml_is_no_declaration()
	{
		byte[] bytes = Encoding.UTF8.GetBytes("<?xml-stylesheet href=\"s.css\" encoding=\"latin1\"?><r/>");

		NotNull(XSpanReader.Load(bytes).Root.Span);
	}

	[Fact]
	public void Text_opening_with_a_kept_mark_counts_it()
	{
		string text = (char)0xFEFF + "<r><a/></r>";

		XElement a = XSpanReader.Parse(text).Root.Element("a");
		XSpan span = a.Span;

		Equal(4, span.Chars.Start);
		Equal(3 + 3, span.Bytes.Start);   // the mark is three bytes in UTF-8
		Equal(5, span.Column);
		Equal("<a/>", a.OuterSource);
	}

	// Text is already decoded: its declaration names nothing to check, and its byte offsets are its UTF-8.
	[Fact]
	public void Parse_does_not_consult_a_declarations_encoding()
	{
		const string xml = "<?xml version=\"1.0\" encoding=\"iso-8859-1\"?><r>μ</r>";

		XSpan r = XSpanReader.Parse(xml).Root.Span;

		Equal(Encoding.UTF8.GetByteCount(xml), r.Bytes.End);
	}

	[Fact]
	public void A_file_loads_as_its_bytes()
	{
		string path = Path.Combine(Path.GetTempPath(), $"xmlsourcespans-{Guid.NewGuid():N}.xml");

		try {
			File.WriteAllBytes(path, With([0xEF, 0xBB, 0xBF], Encoding.UTF8.GetBytes(Xml)));

			XSpan b = XSpanReader.LoadFile(path).Root.Element("b").Span;

			Equal(3 + Encoding.UTF8.GetByteCount(Xml[..b.Chars.Start]), b.Bytes.Start);
		}
		finally {
			File.Delete(path);
		}
	}

	[Fact]
	public void A_file_is_its_documents_base_URI()
	{
		string path = Path.Combine(Path.GetTempPath(), $"xmlsourcespans-{Guid.NewGuid():N}.xml");

		try {
			File.WriteAllBytes(path, Encoding.UTF8.GetBytes(Xml));

			XDocument doc = XSpanReader.LoadFile(path, LoadOptions.SetBaseUri);

			Equal(new Uri(path).AbsoluteUri, doc.BaseUri);
			Equal(XDocument.Load(path, LoadOptions.SetBaseUri).BaseUri, doc.BaseUri);
		}
		finally {
			File.Delete(path);
		}
	}
}
