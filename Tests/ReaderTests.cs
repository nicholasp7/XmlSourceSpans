using System.Xml.Schema;

namespace XmlSourceSpans.Tests;

/// <summary>The reader driven by hand: constructed over a source, loaded by <see cref="XDocument.Load(XmlReader, LoadOptions)"/>
/// alone or under another reader, then annotated; whitespace kept or dropped by the reader; a file's URI carried by its
/// source; and the misuses <see cref="XSpanReader.Annotate"/> refuses.</summary>
public class ReaderTests
{
	// Spans as plain values, to compare across loads: each load's source is an object of its own.
	static (XRange Bytes, XRange Chars, int Line, int Column)[] Spans(XDocument doc)
		=> [.. doc.Descendants().Select(el => el.Span).Select(s => (s.Bytes, s.Chars, s.Line, s.Column))];

	[Theory]
	[MemberData(nameof(Fixtures.All), MemberType = typeof(Fixtures))]
	public void A_reader_driven_by_hand_loads_what_the_one_call_load_does(string xml)
	{
		using XSpanReader reader = new(XSource.FromText(xml), LoadOptions.PreserveWhitespace);

		XDocument doc = XDocument.Load(reader);

		reader.Annotate(doc);

		XDocument oneCall = XSpanReader.Parse(xml, LoadOptions.PreserveWhitespace);

		True(XNode.DeepEquals(oneCall, doc));
		Equal(Spans(oneCall), Spans(doc));
		Invariants.Check(doc, xml, Encoding.UTF8, bomLength: 0, reparse: false);
	}

	// A tree loaded from a reader keeps the whitespace the reader reports: keeping it is asked of the reader, and asking the
	// load comes too late.
	[Fact]
	public void Whitespace_is_kept_or_dropped_by_the_reader_not_the_load()
	{
		const string xml = "<r>\n  <x/>\n</r>";

		using XSpanReader drops = new(XSource.FromText(xml));
		using XSpanReader keeps = new(XSource.FromText(xml), LoadOptions.PreserveWhitespace);

		Single(XDocument.Load(drops, LoadOptions.PreserveWhitespace).Root.Nodes());
		Equal(3, XDocument.Load(keeps).Root.Nodes().Count());
	}

	// A reader stacked over this one reads through it, so every element still opens and closes under its eyes; this one
	// drops the comments on the way.
	[Fact]
	public void A_reader_wrapped_in_another_still_spans_every_element()
	{
		const string xml = "<r><!-- note --><x a='1'>one</x><y/></r>";

		XmlReaderSettings noComments = new() { IgnoreComments = true };

		using XSpanReader reader = new(XSource.FromText(xml));
		using XmlReader wrapper = XmlReader.Create(reader, noComments);

		XDocument doc = XDocument.Load(wrapper);

		reader.Annotate(doc);

		Empty(doc.DescendantNodes().OfType<XComment>());
		Equal(Spans(XSpanReader.Parse(xml)), Spans(doc));
		Equal("<x a='1'>one</x>", doc.Root.Element("x").OuterSource);
	}

	// A validating reader stacked over this one checks the document against a schema as it loads, and every element is still
	// spanned; a document the schema refuses fails the load.
	[Fact]
	public void A_validating_reader_stacked_over_this_one_validates_and_still_spans()
	{
		const string xsd = """
			<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">
				<xs:element name="r">
					<xs:complexType>
						<xs:sequence>
							<xs:element name="x" type="xs:string" maxOccurs="unbounded"/>
						</xs:sequence>
					</xs:complexType>
				</xs:element>
			</xs:schema>
			""";

		const string valid = "<r><x>a</x><x>b</x></r>";

		using XmlReader schemaReader = XmlReader.Create(new StringReader(xsd));

		XmlSchemaSet schemas = new();
		schemas.Add(targetNamespace: null, schemaReader);

		XmlReaderSettings validating = new() {
			ValidationType = ValidationType.Schema,
			Schemas = schemas,
		};

		using XSpanReader reader = new(XSource.FromText(valid));
		using XmlReader validator = XmlReader.Create(reader, validating);

		XDocument doc = XDocument.Load(validator);

		reader.Annotate(doc);

		Equal(Spans(XSpanReader.Parse(valid)), Spans(doc));

		using XSpanReader refusedReader = new(XSource.FromText("<r><y/></r>"));
		using XmlReader refusedValidator = XmlReader.Create(refusedReader, validating);

		Throws<XmlSchemaValidationException>(() => XDocument.Load(refusedValidator));
	}

	// Not yet: the reader overrides no async member, so XmlReader's own ReadAsync throws. The README says so; this keeps it
	// true, and fails the day async reading lands.
	[Fact]
	public async Task Async_reading_is_not_supported_yet()
	{
		using XSpanReader reader = new(XSource.FromText("<r/>"));

		await ThrowsAsync<NotImplementedException>(() => XDocument.LoadAsync(reader, LoadOptions.None, TestContext.Current.CancellationToken));
	}

	[Fact]
	public void A_source_from_a_file_carries_the_files_URI_to_the_document()
	{
		string path = Path.Combine(Path.GetTempPath(), $"xmlsourcespans-{Guid.NewGuid():N}.xml");

		try {
			File.WriteAllText(path, "<r><x/></r>");

			XSource source = XSource.FromFile(path);

			using XSpanReader reader = new(source);

			XDocument doc = XDocument.Load(reader, LoadOptions.SetBaseUri);

			reader.Annotate(doc);

			Equal(new Uri(path).AbsoluteUri, source.BaseUri);
			Equal(source.BaseUri, doc.BaseUri);
			Same(source, doc.Source);
		}
		finally {
			File.Delete(path);
		}
	}

	[Fact]
	public void Only_a_source_from_a_file_has_a_base_URI()
	{
		Null(XSource.FromText("<r/>").BaseUri);
		Null(XSource.FromBytes(Encoding.UTF8.GetBytes("<r/>")).BaseUri);
	}

	[Fact]
	public void Annotate_refuses_a_reader_not_read_to_its_end()
	{
		using XSpanReader reader = new(XSource.FromText("<r><x/></r>"));

		reader.Read();

		Throws<InvalidOperationException>(() => reader.Annotate(new XDocument()));
	}

	[Fact]
	public void Annotate_refuses_a_second_document()
	{
		const string xml = "<r><x/></r>";

		using XSpanReader reader = new(XSource.FromText(xml));

		XDocument doc = XDocument.Load(reader);

		reader.Annotate(doc);

		Throws<InvalidOperationException>(() => reader.Annotate(XDocument.Parse(xml)));
	}

	[Fact]
	public void Annotate_refuses_a_document_with_spans_already()
	{
		const string xml = "<r><x/></r>";

		using XSpanReader reader = new(XSource.FromText(xml));

		_ = XDocument.Load(reader);

		Throws<InvalidOperationException>(() => reader.Annotate(XSpanReader.Parse(xml)));
	}

	// The counts are checked, a document that fails them is left as it was, and the reader can still annotate its own.
	[Fact]
	public void Annotate_refuses_a_document_of_another_shape_and_leaves_it_bare()
	{
		using XSpanReader reader = new(XSource.FromText("<r><x/></r>"));

		XDocument doc = XDocument.Load(reader);
		XDocument more = XDocument.Parse("<r><x/><y/></r>");
		XDocument fewer = XDocument.Parse("<r/>");

		Throws<InvalidOperationException>(() => reader.Annotate(more));
		Throws<InvalidOperationException>(() => reader.Annotate(fewer));

		All(more.Descendants().Concat(fewer.Descendants()), el => Null(el.Span));
		Null(more.Source);
		Null(fewer.Source);

		reader.Annotate(doc);

		NotNull(doc.Root.Span);
	}
}
