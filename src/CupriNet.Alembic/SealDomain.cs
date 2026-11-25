using System.Text;

namespace CupriNet.Alembic;

/// <summary>
/// Domain separation for signed documents. One Seal signs many kinds of thing — a link, an overlay record, a
/// channel advert, a LAN announcement, the ownership documents — and nothing in the signature said which, so they
/// were kept apart only by their layouts happening to differ. Each now signs <c>label ‖ 0x00 ‖ body</c>, the label
/// naming the document and its version (<c>cuprinet/decree/v2</c>), so a signature made for one kind of document
/// can never be offered as another, whatever any future layout looks like.
/// </summary>
public static class SealDomain
{
    /// <summary>The bytes to sign or verify for <paramref name="body"/> under <paramref name="label"/>.</summary>
    public static byte[] Input(string label, ReadOnlySpan<byte> body)
    {
        ArgumentException.ThrowIfNullOrEmpty(label);
        var prefix = Encoding.UTF8.GetBytes(label);
        var input = new byte[prefix.Length + 1 + body.Length];
        prefix.CopyTo(input, 0);
        input[prefix.Length] = 0; // ends the label, so no label is ever a prefix of another label plus its body
        body.CopyTo(input.AsSpan(prefix.Length + 1));
        return input;
    }

    /// <summary>The label for a versioned document: <c>cuprinet/&lt;document&gt;/v&lt;version&gt;</c>.</summary>
    public static string Label(string document, int version) => $"cuprinet/{document}/v{version}";
}
