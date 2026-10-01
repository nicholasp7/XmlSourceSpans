namespace XmlSourceSpans;

/// <summary>
/// One element's offsets in the source it was loaded from: where the element starts and ends, and where its content
/// starts and ends, measured twice, in the source's <see cref="Bytes"/> and in its decoded <see cref="Chars"/>. It also
/// carries the line and column of the element's <c>&lt;</c>, and the <see cref="Source"/> the offsets count into. Read
/// it with <c>element.Span</c> on an element loaded by <see cref="XSpanReader"/>; read its slices with
/// <c>span.OuterChars</c> and <c>span.OuterBytes</c>, which pick the right buffer for each unit.
/// </summary>
/// <param name="Bytes">Byte offsets from the source's first byte; a byte-order mark counts. They index
/// <see cref="XSource.Bytes"/>.</param>
/// <param name="Chars">Character offsets (UTF-16 code units) into the decoded text. They index
/// <see cref="XSource.Text"/>.</param>
/// <param name="Line">The 1-based line of the element's <c>&lt;</c> in the source text; offsets a caller's reader settings
/// add to reported lines are not added here.</param>
/// <param name="Column">The 1-based column of the element's <c>&lt;</c>, in UTF-16 code units from the line's start.</param>
/// <param name="Source">The source the offsets count into, held by reference: the span reads true wherever the element
/// goes.</param>
public sealed record XSpan(XRange Bytes, XRange Chars, int Line, int Column, XSource Source);

/// <summary>
/// An element's four offsets in one unit, as two half-open ranges: the element is <c>[Start, End)</c>, from its
/// <c>&lt;</c> through the <c>&gt;</c> that closes it; its content is <c>[InnerStart, InnerEnd)</c>, between its start
/// tag and its end tag. An empty-element tag (<c>&lt;x/&gt;</c>) has empty content at its end,
/// <c>InnerStart == InnerEnd == End</c>; <c>&lt;x&gt;&lt;/x&gt;</c> has empty content between its two tags.
/// <para>A range does not know its unit: <see cref="XSpan.Bytes"/> slices the source's bytes and <see cref="XSpan.Chars"/>
/// its text, and a range sliced against the other buffer compiles and returns the wrong characters. The span's own
/// slices — <c>span.OuterChars</c>, <c>span.OuterBytes</c> and their inner forms — pick the buffer themselves.</para>
/// </summary>
public readonly record struct XRange(int Start, int InnerStart, int InnerEnd, int End)
{
	/// <summary>The element's length, tags included.</summary>
	public int Length => End - Start;

	/// <summary>The length of the element's content.</summary>
	public int InnerLength => InnerEnd - InnerStart;

	// Slices of a buffer in this range's own unit — the source's text for XSpan.Chars, its bytes for XSpan.Bytes — never
	// a copy: a span for a hot loop, a memory to keep, as string's own AsSpan and AsMemory.

	/// <summary>The element within <paramref name="source"/>, tags and all, without a copy.</summary>
	public ReadOnlySpan<T> Outer<T>(ReadOnlySpan<T> source) => source[Start..End];

	/// <summary>The element's content within <paramref name="source"/>, without a copy.</summary>
	public ReadOnlySpan<T> Inner<T>(ReadOnlySpan<T> source) => source[InnerStart..InnerEnd];

	/// <summary>The element within <paramref name="source"/>, tags and all, without a copy — to keep.</summary>
	public ReadOnlyMemory<T> Outer<T>(ReadOnlyMemory<T> source) => source[Start..End];

	/// <summary>The element's content within <paramref name="source"/>, without a copy — to keep.</summary>
	public ReadOnlyMemory<T> Inner<T>(ReadOnlyMemory<T> source) => source[InnerStart..InnerEnd];

	/// <summary>The element within the text <paramref name="source"/>, tags and all, without a copy.</summary>
	public ReadOnlySpan<char> Outer(string source) => source.AsSpan(Start, Length);

	/// <summary>The element's content within the text <paramref name="source"/>, without a copy.</summary>
	public ReadOnlySpan<char> Inner(string source) => source.AsSpan(InnerStart, InnerLength);
}
