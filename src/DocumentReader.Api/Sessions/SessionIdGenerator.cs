using System.Buffers.Text;
using System.Security.Cryptography;

namespace DocumentReader.Api.Sessions;

/// <summary>
/// Creates and checks session IDs: 32 cryptographically random bytes encoded as
/// unpadded base64url, which is always 43 characters.
/// </summary>
public static class SessionIdGenerator
{
    private const int RandomByteCount = 32;
    public const int SessionIdLength = 43;

    public static string Create() =>
        Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(RandomByteCount));

    /// <summary>Checks the format only; it does not tell whether the session exists.</summary>
    public static bool IsWellFormed(string? value)
    {
        if (value is null || value.Length != SessionIdLength)
        {
            return false;
        }

        foreach (var character in value)
        {
            var isBase64UrlCharacter = char.IsAsciiLetterOrDigit(character) || character is '-' or '_';
            if (!isBase64UrlCharacter)
            {
                return false;
            }
        }

        return true;
    }
}
