using System.Xml.Linq;

namespace XmlSourceSpans;

/// <summary>An element's offsets in its source, and the source they slice, as properties: on the element its
/// <see cref="XSpan"/> and the source's own text for it; on the span its slices, each in its own unit; on the document,
/// the source itself.</summary>
public static class XSpanExtensions
{
	extension(XDocument document)
	{
		/// <summary>The source the document was loaded from — its text, and its bytes when loaded from bytes, both held by
		/// reference; null for a document <see cref="XSpanReader"/> did not load.</summary>
		public XSource Source => document.Annotation<XSource>();
	}

	extension(XElement element)
	{
		/// <summary>The element's <see cref="XSpan"/>; null for an element <see cref="XSpanReader"/> did not load — one built
		/// in code, or a copy, since <c>new XElement(other)</c> carries no annotations.</summary>
		public XSpan Span => element.Annotation<XSpan>();

		/// <summary>The element exactly as its source wrote it, tags and all; null for an element with no span. It reads
		/// the span's own source, so it holds wherever the element goes — removed, or moved into another document — and an
		/// edit made to the element after the load does not change it.</summary>
		public string OuterSource => element.Annotation<XSpan>() is XSpan span ? span.OuterChars.ToString() : null;

		/// <summary>The element's content exactly as its source wrote it — between its start tag and its end tag; "" for an
		/// empty element. Null as for <c>OuterSource</c>.</summary>
		public string InnerSource => element.Annotation<XSpan>() is XSpan span ? span.InnerChars.ToString() : null;
	}

	extension(XSpan span)
	{
		/// <summary>The element within its source's text, tags and all, without a copy.</summary>
		public ReadOnlySpan<char> OuterChars => span.Chars.Outer(span.Source.Text);

		/// <summary>The element's content within its source's text, without a copy.</summary>
		public ReadOnlySpan<char> InnerChars => span.Chars.Inner(span.Source.Text);

		/// <summary>The element within the bytes its source was loaded from, tags and all, without a copy; empty for a
		/// document parsed from a string, which has no bytes.</summary>
		public ReadOnlyMemory<byte> OuterBytes => span.Source.Bytes.IsEmpty ? default : span.Bytes.Outer(span.Source.Bytes);

		/// <summary>The element's content within the bytes its source was loaded from, without a copy; empty for a document
		/// parsed from a string.</summary>
		public ReadOnlyMemory<byte> InnerBytes => span.Source.Bytes.IsEmpty ? default : span.Bytes.Inner(span.Source.Bytes);
	}
}
