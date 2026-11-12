using Microsoft.Extensions.Configuration;
using Scheduler.Api.Services;
using System.Security.Cryptography;
using Xunit;

namespace Scheduler.Api.Tests
{
    public class SourceSecretsTests
    {
        private static SourceSecrets Create(string? key)
        {
            return new SourceSecrets(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Sources:Key"] = key }).Build());
        }

        [Fact]
        public void Keys_OfMostShapesWork()
        {
            byte[] bytes = RandomNumberGenerator.GetBytes(32);
            Assert.Equal(bytes, SourceSecrets.GetKey(Convert.ToBase64String(bytes)));
            Assert.Equal(bytes, SourceSecrets.GetKey(Convert.ToHexString(bytes)));
            // 64 bytes, or a long passphrase, are hashed down to the 32 AES needs
            Assert.Equal(32, SourceSecrets.GetKey(Convert.ToBase64String(RandomNumberGenerator.GetBytes(64)))!.Length);
            Assert.Equal(32, SourceSecrets.GetKey("a long passphrase that is plenty long enough")!.Length);
            Assert.Null(SourceSecrets.GetKey("too short"));
            Assert.Null(SourceSecrets.GetKey("<base64 of 32 random bytes, each server needs its own and keeps it>"));
        }

        [Fact]
        public void Passwords_OnlyComeBackWithTheSameKey()
        {
            string key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
            string secret = Create(key).Encrypt("p@ss word!");

            Assert.StartsWith("v1:", secret);
            Assert.Equal("p@ss word!", Create(key).Decrypt(secret));
            Assert.Throws<Exception>(() => Create(Convert.ToBase64String(RandomNumberGenerator.GetBytes(64))).Decrypt(secret));
            Assert.Throws<Exception>(() => Create(null).Encrypt("x"));
        }
    }
}
