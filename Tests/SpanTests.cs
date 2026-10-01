namespace XmlSourceSpans.Tests;

/// <summary>Where an element's span falls, pinned on small documents by hand-counted offsets — the outer range, the
/// inner range, bytes against characters, lines and columns — and every invariant over every fixture.</summary>
public class SpanTests
{
	static int At(string xml, string part) => xml.IndexOf(part, StringComparison.Ordinal);

	[Fact]
	public void An_element_spans_its_lt_through_its_last_gt_and_its_inside_lies_between_its_tags()
	{
		const string xml = """<r><a n="1">text</a></r>""";

		XSpan a = XSpanReader.Parse(xml).Root.Element("a").Span;

		Equal(new XRange(Start: 3, InnerStart: 12, InnerEnd: 16, End: 20), a.Chars);
		Equal(a.Chars, a.Bytes);   // ASCII: a byte a character
		Equal(1, a.Line);
		Equal(4, a.Column);
	}

	[Fact]
	public void An_empty_element_tag_has_its_inside_at_its_end_and_a_pair_between_its_tags()
	{
		const string xml = "<r><x/><y /><z></z></r>";

		XElement r = XSpanReader.Parse(xml).Root;

		Equal(new XRange(Start: 3, InnerStart: 7, InnerEnd: 7, End: 7), r.Element("x").Span.Chars);
		Equal(new XRange(Start: 7, InnerStart: 12, InnerEnd: 12, End: 12), r.Element("y").Span.Chars);
		Equal(new XRange(Start: 12, InnerStart: 15, InnerEnd: 15, End: 19), r.Element("z").Span.Chars);   // written open and closed: an end tag of its own
	}

	[Fact]
	public void Nested_elements_of_one_name_close_in_their_own_order()
	{
		const string xml = "<a><a><a/></a></a>";

		List<XRange> spans = [.. XSpanReader.Parse(xml).Descendants().Select(e => e.Span.Chars)];

		Equal(new XRange(Start: 0, InnerStart: 3, InnerEnd: 14, End: 18), spans[0]);
		Equal(new XRange(Start: 3, InnerStart: 6, InnerEnd: 10, End: 14), spans[1]);
		Equal(new XRange(Start: 6, InnerStart: 10, InnerEnd: 10, End: 10), spans[2]);
	}

	[Fact]
	public void Multibyte_text_counts_bytes_in_the_bytes_and_characters_in_the_characters()
	{
		const string xml = "<r><l>μῆνιν</l><l>b</l></r>";

		XSpan second = XSpanReader.Parse(xml).Root.Elements().Last().Span;

		Equal(At(xml, "<l>b"), second.Chars.Start);
		Equal(Encoding.UTF8.GetByteCount(xml[..second.Chars.Start]), second.Bytes.Start);
		Equal(6, second.Bytes.Start - second.Chars.Start);   // μῆνιν is five characters in eleven bytes
	}

	[Fact]
	public void A_character_beyond_the_basic_plane_is_two_columns_and_four_bytes()
	{
		Equal(2, "𒀭".Length);
		Equal(4, Encoding.UTF8.GetByteCount("𒀭"));

		const string xml = "<r><s>𒀭</s><s>b</s></r>";

		XSpan second = XSpanReader.Parse(xml).Root.Elements().Last().Span;

		Equal(At(xml, "<s>b"), second.Chars.Start);
		Equal(second.Chars.Start + 2, second.Bytes.Start);
		Equal(second.Chars.Start + 1, second.Column);
	}

	[Theory]
	[InlineData("\n")]
	[InlineData("\r\n")]
	[InlineData("\r")]
	public void Every_XML_line_end_starts_a_line(string lineEnd)
	{
		string xml = $"<r>{lineEnd}  <x>a{lineEnd}b</x>{lineEnd}  <y/>{lineEnd}</r>";

		XElement r = XSpanReader.Parse(xml).Root;
		XSpan x = r.Element("x").Span;
		XSpan y = r.Element("y").Span;

		Equal((2, 3), (x.Line, x.Column));
		Equal((4, 3), (y.Line, y.Column));
		Equal(At(xml, "<y/>"), y.Chars.Start);
		Equal($"<x>a{lineEnd}b</x>", r.Element("x").OuterSource);
	}

	[Fact]
	public void A_tab_is_one_column()
	{
		XSpan x = XSpanReader.Parse("<r>\n\t\t<x/></r>").Root.Element("x").Span;

		Equal((2, 3), (x.Line, x.Column));
	}

	[Fact]
	public void Markup_inside_CDATA_a_comment_or_an_instruction_is_no_element()
	{
		const string xml = "<r><![CDATA[</r>]]><!-- </r> --><?pi </r> ?><x/></r>";

		XElement r = XSpanReader.Parse(xml).Root;

		Equal(xml.Length, r.Span.Chars.End);
		Equal(At(xml, "<x/>"), r.Element("x").Span.Chars.Start);
	}

	[Fact]
	public void A_gt_inside_a_quoted_attribute_does_not_close_the_tag()
	{
		XElement x = XSpanReader.Parse("""<r><x a="1 > 0" b='>'>in</x></r>""").Root.Element("x");

		Equal("in", x.InnerSource);
	}

	[Fact]
	public void Whitespace_inside_tags_belongs_to_the_tags()
	{
		XElement x = XSpanReader.Parse("<r><x\n  a = '1'\n>in</x  \n></r>").Root.Element("x");

		Equal("in", x.InnerSource);
		Equal("<x\n  a = '1'\n>in</x  \n>", x.OuterSource);
	}

	[Fact]
	public void A_prefixed_element_spans_from_its_prefix()
	{
		XElement x = XSpanReader.Parse("""<r xmlns:p="urn:p"><p:x><p:y/></p:x></r>""").Root.Elements().Single();

		Equal("<p:x><p:y/></p:x>", x.OuterSource);
	}

	[Fact]
	public void An_entity_expanded_from_the_DTD_leaves_every_position_in_the_source()
	{
		const string xml = "<!DOCTYPE r [<!ENTITY e \"hello there\">]><r>&e;<x/></r>";

		XElement r = XSpanReader.Parse(xml).Root;

		Equal("hello there", r.Nodes().OfType<XText>().First().Value);
		Equal(At(xml, "<x/>"), r.Element("x").Span.Chars.Start);
		Equal("<r>&e;<x/></r>", r.OuterSource);
	}

	// An element an entity's replacement text brings in stands nowhere in the document's own text: refused, not spanned
	// somewhere misleading.
	[Fact]
	public void Markup_an_entity_brings_in_is_refused()
	{
		const string xml = "<!DOCTYPE r [<!ENTITY e \"<b>bold</b>\">]><r>&e;</r>";

		NotSupportedException ex = Throws<NotSupportedException>(() => XSpanReader.Parse(xml));

		Contains("entity", ex.Message);
	}

	// Entity markup spelled with a character reference sits in the DTD, behind the reading, without a '<' of its own.
	[Fact]
	public void Markup_an_entity_spells_with_a_character_reference_is_refused_as_entity_markup()
	{
		const string xml = "<!DOCTYPE r [<!ENTITY e \"&#60;b>bold&#60;/b>\">]><r>&e;</r>";

		NotSupportedException ex = Throws<NotSupportedException>(() => XSpanReader.Parse(xml));

		Contains("entity", ex.Message);
	}

	// An external entity's elements report their lines in the entity's own file. Here its <b/> stands on line 4, where
	// the document holds a comment: without the refusal, <b/> would take the comment's span.
	[Fact]
	public void An_element_an_external_entity_brings_in_is_refused()
	{
		string entity = Path.Combine(Path.GetTempPath(), $"xmlsourcespans-{Guid.NewGuid():N}.xml");

		try {
			File.WriteAllText(entity, "\n\n\n<b/>");

			string xml = $"<!DOCTYPE r [<!ENTITY ext SYSTEM \"{new Uri(entity).AbsoluteUri}\">]>\n<r>\n&ext;\n<!-- c -->\n</r>";
			XmlReaderSettings resolving = new() { DtdProcessing = DtdProcessing.Parse, XmlResolver = new XmlUrlResolver() };

			NotSupportedException ex = Throws<NotSupportedException>(() => XSpanReader.Parse(xml, settings: resolving));

			Contains("<b>", ex.Message);
		}
		finally {
			File.Delete(entity);
		}
	}

	// The caller's settings may add to the lines and columns the reader reports; the spans are of the text all the same.
	[Fact]
	public void The_callers_line_offsets_leave_the_spans_unmoved()
	{
		const string xml = "<r>\n  <x a='1'/><y/>\n</r>";

		XmlReaderSettings offset = new() { DtdProcessing = DtdProcessing.Parse, LineNumberOffset = 2, LinePositionOffset = 3 };

		List<XSpan> expected = [.. XSpanReader.Parse(xml).Descendants().Select(e => e.Span)];
		List<XSpan> actual = [.. XSpanReader.Parse(xml, settings: offset).Descendants().Select(e => e.Span)];

		Equal(expected.Select(s => (s.Chars, s.Bytes, s.Line, s.Column)), actual.Select(s => (s.Chars, s.Bytes, s.Line, s.Column)));
	}

	[Theory]
	[MemberData(nameof(Fixtures.PlainOnly), MemberType = typeof(Fixtures))]
	public void Every_plain_fixture_keeps_every_invariant(string xml)
		=> Invariants.Check(XSpanReader.Parse(xml, LoadOptions.PreserveWhitespace), xml, Encoding.UTF8, bomLength: 0, reparse: true);

	// Whitespace dropped from the tree moves no span: the same checks, the document loaded without it.
	[Theory]
	[MemberData(nameof(Fixtures.PlainOnly), MemberType = typeof(Fixtures))]
	public void Every_plain_fixture_keeps_every_invariant_with_whitespace_dropped(string xml)
		=> Invariants.Check(XSpanReader.Parse(xml, LoadOptions.None), xml, Encoding.UTF8, bomLength: 0, reparse: true, reparseAs: LoadOptions.None);

	[Fact]
	public void The_contextual_fixtures_keep_every_invariant_but_standing_alone()
	{
		foreach(string xml in Fixtures.Contextual)
			Invariants.Check(XSpanReader.Parse(xml, LoadOptions.PreserveWhitespace), xml, Encoding.UTF8, bomLength: 0, reparse: false);
	}
}
