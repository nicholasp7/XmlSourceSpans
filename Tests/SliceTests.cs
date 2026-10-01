using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace XmlSourceSpans.Tests;

/// <summary>Slices without copies: a span cuts its own source in each unit — characters from the text, bytes from the
/// bytes — and the slice is the source's own memory, not a copy of it; an <see cref="XRange"/> cuts any buffer of its
/// unit, a plain string included; the source itself, <see cref="XSource"/>, holds the very string or array it was
/// given.</summary>
public class SliceTests
{
	const string Xml = "<r><l n=\"1\">μῆνιν ἄειδε</l><l n=\"2\">θεὰ</l></r>";

	[Fact]
	public void A_span_of_the_text_is_the_texts_own_memory()
	{
		XDocument doc = XSpanReader.Parse(Xml);
		XElement line = doc.Root.Elements().Last();

		string text = doc.Source.Text;
		XRange chars = line.Span.Chars;

		ReadOnlySpan<char> outer = chars.Outer(text.AsSpan());
		ReadOnlySpan<char> inner = chars.Inner(text.AsSpan());

		Equal(line.OuterSource, outer.ToString());
		Equal("θεὰ", inner.ToString());
		True(Unsafe.AreSame(ref MemoryMarshal.GetReference(outer), ref Unsafe.AsRef(in text.AsSpan()[chars.Start])), "the span is the text's own characters");
	}

	[Fact]
	public void A_range_slices_a_plain_string_as_it_slices_a_span()
	{
		XDocument doc = XSpanReader.Parse(Xml);
		XRange chars = doc.Root.Elements().Last().Span.Chars;

		Equal(chars.Outer(Xml.AsSpan()).ToString(), chars.Outer(Xml).ToString());
		Equal("θεὰ", chars.Inner(Xml).ToString());
	}

	[Fact]
	public void A_memory_of_the_bytes_is_the_given_arrays_own()
	{
		byte[] bytes = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(Xml)];

		XDocument doc = XSpanReader.Load(bytes);
		XElement line = doc.Root.Elements().First();

		ReadOnlyMemory<byte> outer = line.Span.Bytes.Outer(doc.Source.Bytes);

		True(MemoryMarshal.TryGetArray(outer, out ArraySegment<byte> segment));
		Same(bytes, segment.Array);   // the very array given: held by reference, sliced in place
		Equal(line.Span.Bytes.Start, segment.Offset);
		Equal(line.OuterSource, Encoding.UTF8.GetString(outer.Span));
		Equal("μῆνιν ἄειδε", Encoding.UTF8.GetString(line.Span.Bytes.Inner(doc.Source.Bytes).Span));
	}

	[Fact]
	public void A_span_of_the_bytes_serves_a_hot_loop_the_same_way()
	{
		byte[] bytes = Encoding.UTF8.GetBytes(Xml);

		XDocument doc = XSpanReader.Load(bytes);

		foreach(XElement line in doc.Root.Elements()) {
			ReadOnlySpan<byte> outer = line.Span.Bytes.Outer(doc.Source.Bytes.Span);

			Equal(line.OuterSource, Encoding.UTF8.GetString(outer));
		}
	}

	// The span's own slices choose the buffer for each unit, so neither can be cut against the other's.
	[Fact]
	public void A_spans_own_slices_read_the_right_buffer_for_each_unit()
	{
		byte[] bytes = Encoding.UTF8.GetBytes(Xml);

		XSpan span = XSpanReader.Load(bytes).Root.Elements().First().Span;

		Equal("<l n=\"1\">μῆνιν ἄειδε</l>", span.OuterChars.ToString());
		Equal("μῆνιν ἄειδε", span.InnerChars.ToString());
		Equal("<l n=\"1\">μῆνιν ἄειδε</l>", Encoding.UTF8.GetString(span.OuterBytes.Span));
		Equal("μῆνιν ἄειδε", Encoding.UTF8.GetString(span.InnerBytes.Span));
	}

	[Fact]
	public void A_document_parsed_from_a_string_has_no_byte_slices()
	{
		XSpan span = XSpanReader.Parse(Xml).Root.Span;

		True(span.OuterBytes.IsEmpty);
		True(span.InnerBytes.IsEmpty);
		Equal(Xml, span.OuterChars.ToString());
	}

	[Fact]
	public void The_source_holds_the_string_it_was_parsed_from_and_no_bytes()
	{
		XSource source = XSpanReader.Parse(Xml).Source;

		Same(Xml, source.Text);
		True(source.Bytes.IsEmpty);
		Equal(0, source.BomLength);
		IsType<UTF8Encoding>(source.Encoding);
	}

	[Theory]
	[InlineData(false, 3)]
	[InlineData(true, 2)]
	public void The_source_names_the_encoding_and_mark_the_bytes_were_read_in(bool utf16, int bomLength)
	{
		byte[] bytes = utf16
			? [0xFF, 0xFE, .. Encoding.Unicode.GetBytes(Xml)]
			: [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(Xml)];

		XSource source = XSpanReader.Load(bytes).Source;

		Equal(bomLength, source.BomLength);
		Equal(Xml, source.Text);   // the text leaves the mark out; the bytes keep it
		Equal(bytes.Length, source.Bytes.Length);
		Equal(utf16 ? "utf-16" : "utf-8", source.Encoding.WebName);
	}

	[Fact]
	public void A_document_built_in_code_has_no_source()
		=> Null(new XDocument(new XElement("r")).Source);
}
