using System.Buffers.Text;

namespace CupriNet.Core;

/// <summary>Encodes and decodes an Intonation as a <c>cuprinet://intone/&lt;base64url&gt;</c> URL.</summary>
public static class IntonationUri
{
    /// <summary>The URL prefix for an Intonation link.</summary>
    public const string Prefix = "cuprinet://intone/";

    /// <summary>Renders an Intonation as its connection URL.</summary>
    public static string ToUri(Intonation intonation)
    {
        var document = IntonationCodec.Encode(intonation);
        return Prefix + Base64Url.EncodeToString(document);
    }

    /// <summary>
    /// Parses a connection URL back into an Intonation and the signed body bytes. Does NOT validate the
    /// signature, expiry, or network — use <see cref="IntonationValidator"/> for that.
    /// </summary>
    public static bool TryParse(string uri, out Intonation intonation, out byte[] signedBody)
    {
        intonation = null!;
        signedBody = [];

        if (string.IsNullOrEmpty(uri) || !uri.StartsWith(Prefix, StringComparison.Ordinal))
            return false;

        var encoded = uri.AsSpan(Prefix.Length);
        byte[] document;
        try
        {
            document = Base64Url.DecodeFromChars(encoded);
        }
        catch (FormatException)
        {
            return false;
        }

        try
        {
            (intonation, signedBody) = IntonationCodec.Decode(document);
            return true;
        }
        catch (Codex.CodexFormatException)
        {
            return false;
        }
    }
}
