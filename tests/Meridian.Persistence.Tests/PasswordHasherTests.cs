namespace Meridian.Persistence.Tests;

public class PasswordHasherTests
{
    private static readonly PasswordHasher Fast = new(iterations: 1_000);

    [Fact]
    public void A_hash_verifies_its_password_and_nothing_else()
    {
        var hash = Fast.Hash("correct horse");
        Assert.StartsWith("$pbkdf2-sha256$i=1000$", hash, StringComparison.Ordinal);
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
    public void A_weaker_hash_still_verifies_but_asks_to_be_replaced()
    {
        var old = new PasswordHasher(iterations: 500).Hash("pw123456");
        Assert.Equal(PasswordCheck.ValidNeedsRehash, Fast.Verify("pw123456", old));
        Assert.Equal(PasswordCheck.Valid, new PasswordHasher(iterations: 500).Verify("pw123456", old));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("plaintext")]
    [InlineData("$argon2id$v=19$m=65536,t=3,p=1$c2FsdHNhbHQ$aGFzaGhhc2g")]        // unknown to this build
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
