namespace Meridian.Persistence.Tests;

public class PasswordHasherTests
{
    private static readonly PasswordHasher Fast = new(memoryKib: 1024, passes: 1);

    /// <summary>
    /// "correct horse battery staple", salt bytes 0..15, 1 MiB, 2 passes, one lane. The same 32 bytes
    /// come out of libsodium, Konscious (managed C#) and argon2-cffi (the RFC 9106 reference C code),
    /// so this pins Argon2id itself, not just this build's output.
    /// </summary>
    private const string Golden = "$argon2id$v=19$m=1024,t=2,p=1$AAECAwQFBgcICQoLDA0ODw$WHgvyW8Gptf87EcoCZ9qeI7pigT0zY+gonr224t6Rrg";

    [Fact]
    public void Argon2id_matches_the_reference_implementation()
    {
        var sameCost = new PasswordHasher(memoryKib: 1024, passes: 2);
        Assert.Equal(PasswordCheck.Valid, sameCost.Verify("correct horse battery staple", Golden));
        Assert.Equal(PasswordCheck.Invalid, sameCost.Verify("correct horse battery stapler", Golden));
    }

    [Fact]
    public void The_default_cost_is_64_MiB_and_3_passes()
    {
        // Tuning: changing the default must change this test (and is worth a sentence in the commit).
        Assert.Equal((65_536, 3), (PasswordHasher.DefaultMemoryKib, PasswordHasher.DefaultPasses));
        Assert.StartsWith("$argon2id$v=19$m=65536,t=3,p=1$", new PasswordHasher().Hash("pw123456"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_hash_verifies_its_password_and_nothing_else()
    {
        var hash = Fast.Hash("correct horse");
        Assert.StartsWith("$argon2id$v=19$m=1024,t=1,p=1$", hash, StringComparison.Ordinal);
        Assert.Equal(PasswordCheck.Valid, Fast.Verify("correct horse", hash));
        Assert.Equal(PasswordCheck.Invalid, Fast.Verify("correct horsf", hash));
        Assert.Equal(PasswordCheck.Invalid, Fast.Verify("", hash));
        Assert.Equal(PasswordCheck.Invalid, Fast.Verify(null, hash));
    }

    [Fact]
    public void The_same_password_gets_a_different_salt_each_time()
    {
        Assert.NotEqual(Fast.Hash("pw123456"), Fast.Hash("pw123456"));
    }

    [Fact]
    public void A_weaker_argon2_hash_still_verifies_but_asks_to_be_replaced()
    {
        var lessMemory = new PasswordHasher(memoryKib: 512, passes: 1).Hash("pw123456");
        var fewerPasses = new PasswordHasher(memoryKib: 1024, passes: 1).Hash("pw123456");
        var stronger = new PasswordHasher(memoryKib: 2048, passes: 2).Hash("pw123456");
        var current = new PasswordHasher(memoryKib: 1024, passes: 2);
        Assert.Equal(PasswordCheck.ValidNeedsRehash, current.Verify("pw123456", lessMemory));
        Assert.Equal(PasswordCheck.ValidNeedsRehash, current.Verify("pw123456", fewerPasses));
        Assert.Equal(PasswordCheck.Valid, current.Verify("pw123456", stronger)); // never downgrade
    }

    [Fact]
    public void Pbkdf2_hashes_from_before_argon2_verify_and_ask_to_be_replaced()
    {
        // Built here the way the PBKDF2 hasher wrote them, so the old format is pinned independently.
        var salt = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
        var hash = System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2("pw123456"u8, salt, 1_000, System.Security.Cryptography.HashAlgorithmName.SHA256, 32);
        var stored = $"$pbkdf2-sha256$i=1000${Convert.ToBase64String(salt).TrimEnd('=')}${Convert.ToBase64String(hash).TrimEnd('=')}";
        Assert.Equal(PasswordCheck.ValidNeedsRehash, Fast.Verify("pw123456", stored));
        Assert.Equal(PasswordCheck.Invalid, Fast.Verify("pw123457", stored));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("plaintext")]
    [InlineData("$argon2i$v=19$m=1024,t=1,p=1$AAECAwQFBgcICQoLDA0ODw$WHgvyW8Gptf87EcoCZ9qeI7pigT0zY+gonr224t6Rrg")]    // not argon2id
    [InlineData("$argon2id$v=16$m=1024,t=1,p=1$AAECAwQFBgcICQoLDA0ODw$WHgvyW8Gptf87EcoCZ9qeI7pigT0zY+gonr224t6Rrg")]   // old version
    [InlineData("$argon2id$v=19$m=1024,t=1,p=4$AAECAwQFBgcICQoLDA0ODw$WHgvyW8Gptf87EcoCZ9qeI7pigT0zY+gonr224t6Rrg")]   // lanes libsodium lacks
    [InlineData("$argon2id$v=19$m=4194304,t=1,p=1$AAECAwQFBgcICQoLDA0ODw$WHgvyW8Gptf87EcoCZ9qeI7pigT0zY+gonr224t6Rrg")] // 4 GiB: refused, not run
    [InlineData("$argon2id$v=19$m=1024,t=99,p=1$AAECAwQFBgcICQoLDA0ODw$WHgvyW8Gptf87EcoCZ9qeI7pigT0zY+gonr224t6Rrg")]
    [InlineData("$argon2id$v=19$m=1024,t=0,p=1$AAECAwQFBgcICQoLDA0ODw$WHgvyW8Gptf87EcoCZ9qeI7pigT0zY+gonr224t6Rrg")]
    [InlineData("$argon2id$v=19$m=+1024,t=1,p=1$AAECAwQFBgcICQoLDA0ODw$WHgvyW8Gptf87EcoCZ9qeI7pigT0zY+gonr224t6Rrg")]
    [InlineData("$argon2id$v=19$t=1,m=1024,p=1$AAECAwQFBgcICQoLDA0ODw$WHgvyW8Gptf87EcoCZ9qeI7pigT0zY+gonr224t6Rrg")]   // order is fixed
    [InlineData("$argon2id$v=19$m=1024,t=1,p=1$c2FsdA$WHgvyW8Gptf87EcoCZ9qeI7pigT0zY+gonr224t6Rrg")]                   // 4-byte salt
    [InlineData("$argon2id$v=19$m=1024,t=1,p=1$AAECAwQFBgcICQoLDA0ODw$WHgvyW8Gptf87EcoCZ9qeI7pigT0zY+gonr224t6Rrg$x")]
    [InlineData("$pbkdf2-sha256$i=0$c2FsdHNhbHRzYWx0c2FsdA$aGFzaGhhc2hoYXNoaGFzaA")]
    [InlineData("$pbkdf2-sha256$i=99999999$c2FsdHNhbHRzYWx0c2FsdA$aGFzaGhhc2hoYXNoaGFzaA")] // would burn minutes
    [InlineData("$pbkdf2-sha256$i=1000$c2FsdHNhbHRzYWx0c2FsdA==$aGFzaGhhc2hoYXNoaGFzaA")]  // padded
    [InlineData("$pbkdf2-sha256$i=1000$!!!$aGFzaGhhc2hoYXNoaGFzaA")]
    [InlineData("$pbkdf2-sha256$i=+1000$c2FsdHNhbHRzYWx0c2FsdA$aGFzaGhhc2hoYXNoaGFzaA")]
    [InlineData("$pbkdf2-sha256$i=1000$c2FsdHNhbHRzYWx0c2FsdA$aGFzaGhhc2hoYXNoaGFzaA$extra")]
    public void Malformed_or_unknown_hashes_never_verify(string? stored)
    {
        Assert.Equal(PasswordCheck.Invalid, Fast.Verify("pw123456", stored));
    }

    [Fact]
    public void Costs_outside_the_bounds_are_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PasswordHasher(memoryKib: PasswordHasher.MinMemoryKib - 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PasswordHasher(memoryKib: PasswordHasher.MaxMemoryKib + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PasswordHasher(passes: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PasswordHasher(passes: PasswordHasher.MaxPasses + 1));
    }

    [Fact]
    public void Overlong_passwords_are_refused_before_any_hashing()
    {
        var huge = new string('x', PasswordHasher.MaxPasswordBytes + 1);
        Assert.False(PasswordHasher.IsAcceptableLength(huge));
        Assert.True(PasswordHasher.IsAcceptableLength(new string('é', PasswordHasher.MaxPasswordBytes / 2)));
        Assert.Throws<ArgumentException>(() => Fast.Hash(huge));
        Assert.Equal(PasswordCheck.Invalid, Fast.Verify(huge, Fast.Hash("pw123456")));
    }

    [Fact]
    public void The_decoy_check_does_not_throw()
    {
        Fast.VerifyNothing("anything");
        Fast.VerifyNothing(null);
    }
}

public class NameRulesTests
{
    [Theory]
    [InlineData("Rook", true)]
    [InlineData("r00k", true)]
    [InlineData("abc", true)]
    [InlineData("ab", false)]
    [InlineData("1abc", false)]
    [InlineData("Rook\n", false)]   // $ would accept this; \z does not
    [InlineData("Ro ok", false)]
    [InlineData("Röök", false)]
    [InlineData("abcdefghijklmnopqrstuvwx", true)]
    [InlineData("abcdefghijklmnopqrstuvwxy", false)]
    [InlineData(null, false)]
    public void Names(string? name, bool valid)
    {
        Assert.Equal(valid, NameRules.IsValidUsername(name));
        Assert.Equal(valid, NameRules.IsValidCharacterName(name));
    }
}
