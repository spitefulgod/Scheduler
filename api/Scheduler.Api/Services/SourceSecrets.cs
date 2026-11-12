using System.Security.Cryptography;
using System.Text;

namespace Scheduler.Api.Services
{
    // Encrypts connection passwords before they go in the database, so the database alone doesn't give them away
    // the key is the base64 of 32 random bytes in Sources:Key, each server has its own and it's never in the database
    public interface ISourceSecrets
    {
        string Encrypt(string value);
        string Decrypt(string secret);
    }

    public class SourceSecrets : ISourceSecrets
    {
        // AES-GCM, kept as v1:<base64 of nonce, tag then the encrypted text> so a different way can be added later
        private const string Prefix = "v1:";
        private const int NonceSize = 12;
        private const int TagSize = 16;
        private readonly byte[]? _key;

        public SourceSecrets(IConfiguration configuration)
        {
            _key = GetKey(configuration["Sources:Key"]);
        }
        private byte[] Key => _key ?? throw new Exception($"The API's source key isn't set up, Sources:Key needs to be at least {MinimumLength} characters, e.g. the base64 or hex of 32 random bytes");

        // AES-256 takes a 32 byte key, the base64 or hex of exactly 32 bytes is used as it is
        // anything else long enough, e.g. 64 bytes or a long passphrase, is hashed down to 32 bytes with SHA-256
        // keys that were already set up keep working, as they were the base64 of 32 bytes
        public const int MinimumLength = 32;
        public static byte[]? GetKey(string? key)
        {
            key = key?.Trim() ?? "";
            if (key.Length < MinimumLength || key.StartsWith("<"))
                return null;
            if (key.Length == 64 && key.All(Uri.IsHexDigit))
                return Convert.FromHexString(key);
            try
            {
                byte[] bytes = Convert.FromBase64String(key);
                if (bytes.Length == 32)
                    return bytes;
            }
            catch (FormatException)
            {
            }
            return SHA256.HashData(Encoding.UTF8.GetBytes(key));
        }

        public string Encrypt(string value)
        {
            byte[] plain = Encoding.UTF8.GetBytes(value);
            byte[] result = new byte[NonceSize + TagSize + plain.Length];
            Span<byte> nonce = result.AsSpan(0, NonceSize);
            RandomNumberGenerator.Fill(nonce);
            using (AesGcm aes = new AesGcm(Key, TagSize))
                aes.Encrypt(nonce, plain, result.AsSpan(NonceSize + TagSize), result.AsSpan(NonceSize, TagSize));
            return Prefix + Convert.ToBase64String(result);
        }
        public string Decrypt(string secret)
        {
            if (!secret.StartsWith(Prefix))
                throw new Exception("The connection's password can't be read, set it again");
            byte[] data = Convert.FromBase64String(secret.Substring(Prefix.Length));
            if (data.Length < NonceSize + TagSize)
                throw new Exception("The connection's password can't be read, set it again");
            byte[] plain = new byte[data.Length - NonceSize - TagSize];
            try
            {
                using (AesGcm aes = new AesGcm(Key, TagSize))
                    aes.Decrypt(data.AsSpan(0, NonceSize), data.AsSpan(NonceSize + TagSize), data.AsSpan(NonceSize, TagSize), plain);
            }
            catch (CryptographicException)
            {
                // most likely a different key from the one it was saved with
                throw new Exception("The connection's password can't be read with this server's key, set it again");
            }
            return Encoding.UTF8.GetString(plain);
        }
    }
}
