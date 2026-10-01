namespace XmlSourceSpans.Tests;

/// <summary>An element read back as its source wrote it — never as the serializer would write it — wherever the element
/// goes once loaded; and where there is no source to read: a copy, an element built in code.</summary>
public class SourceTests
{
	[Theory]
	[InlineData("<x/>")]
	[InlineData("<x />")]
	[InlineData("<x></x>")]
	[InlineData("<x a='1'/>")]
	[InlineData("<x a=\"1 > 0\"/>")]
	[InlineData("<x a=\"it's\" b='say \"hi\"'/>")]
	[InlineData("<x\n\ta=\"1\"\n/>")]
	[InlineData("<x>&amp;&#945;&#x1202D;</x>")]
	[InlineData("<x><![CDATA[</x> <x>]]></x>")]
	[InlineData("<x><!-- </x> --></x>")]
	[InlineData("<x><?pi </x> ?></x>")]
	[InlineData("<x><x><x/></x></x>")]
	[InlineData("<x>a</x   >")]
	public void An_element_reads_back_exactly_as_written(string element)
	{
		XElement x = XSpanReader.Parse($"<r>{element}</r>").Root.Elements().Single();

		Equal(element, x.OuterSource);
	}

	[Theory]
	[InlineData("<x a=\"1\">in <b/> side</x>", "in <b/> side")]
	[InlineData("<x/>", "")]
	[InlineData("<x></x>", "")]
	[InlineData("<x>\n  <y/>\n</x>", "\n  <y/>\n")]
	public void An_elements_inside_reads_back_exactly_as_written(string element, string inside)
	{
		XElement x = XSpanReader.Parse($"<r>{element}</r>", LoadOptions.PreserveWhitespace).Root.Elements().Single();

		Equal(inside, x.InnerSource);
	}

	[Fact]
	public void The_source_is_not_what_the_serializer_writes()
	{
		XElement x = XSpanReader.Parse("<r><x a='1'><y/></x></r>").Root.Element("x");

		Equal("<x a='1'><y/></x>", x.OuterSource);
		Equal("<x a=\"1\"><y /></x>", x.ToString(SaveOptions.DisableFormatting));
	}

	[Fact]
	public void A_copy_has_no_span_and_no_source()
	{
		XElement x = XSpanReader.Parse("<r><x/></r>").Root.Element("x");

		XElement copy = new(x);

		Null(copy.Span);
		Null(copy.OuterSource);
	}

	[Fact]
	public void An_element_built_in_code_has_no_span_and_no_source()
	{
		XElement built = new("x");

		Null(built.Span);
		Null(built.OuterSource);
		Null(built.InnerSource);
	}

	// The span keeps its own source, so the element's source travels with it.
	[Fact]
	public void An_element_that_has_left_its_document_still_reads_as_its_source_wrote_it()
	{
		XElement x = XSpanReader.Parse("<r><x/></r>").Root.Element("x");

		x.Remove();

		NotNull(x.Span);
		Equal("<x/>", x.OuterSource);
	}

	[Theory]
	[InlineData("<s>longer, other text<q>abc</q></s>")]
	[InlineData("<s/>")]
	public void An_element_moved_into_another_loaded_document_reads_its_own_source(string other)
	{
		XElement x = XSpanReader.Parse("<r><x>one</x></r>").Root.Element("x");
		XDocument target = XSpanReader.Parse(other);

		x.Remove();
		target.Root.Add(x);

		Equal("<x>one</x>", x.OuterSource);
		Equal("one", x.InnerSource);
	}

	[Fact]
	public void An_edit_after_the_load_leaves_the_source_as_it_was()
	{
		XElement x = XSpanReader.Parse("<r><x>one</x></r>").Root.Element("x");

		x.Value = "edited";

		Equal("<x>one</x>", x.OuterSource);
	}

	[Fact]
	public void The_root_reads_back_whole()
	{
		const string xml = "<r a='1'>\n  <x/>\n</r>";

		Equal(xml, XSpanReader.Parse(xml).Root.OuterSource);
	}
}
