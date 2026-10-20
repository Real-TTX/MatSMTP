using MatSmtp.Core.Relay;

namespace MatSmtp.Tests.Core;

public sealed class DkimTests
{
    private sealed record Key(long Id, string Domain, bool Subdomains);

    [Theory]
    [InlineData("max@firma.de", "firma.de")]
    [InlineData("Max@Shop.Firma.DE", "shop.firma.de")]
    [InlineData("max@firma.de.", "firma.de")]
    [InlineData("kein-at", null)]
    [InlineData("", null)]
    [InlineData("a@b", null)]
    public void The_domain_of_an_address_is_the_part_after_the_at(string address, string? expected) => Assert.Equal(expected, Dkim.DomainOf(address));

    [Theory]
    [InlineData("matsmtp202610", true)]
    [InlineData("s1.mail", true)]
    [InlineData("-x", false)]
    [InlineData("a_b", false)]
    [InlineData("", false)]
    public void A_selector_is_a_dns_label(string selector, bool expected) => Assert.Equal(expected, Dkim.IsSelector(selector));

    [Fact]
    public void The_key_of_the_domain_wins_then_the_nearest_parent_meant_for_subdomains()
    {
        Key[] keys = [new(1, "firma.de", true), new(2, "shop.firma.de", false), new(3, "andere.de", false)];
        Key? Choose(string domain) => Dkim.Choose(keys, domain, k => k.Domain, k => k.Subdomains, k => k.Id);

        Assert.Equal(1, Choose("firma.de")?.Id);
        Assert.Equal(2, Choose("shop.firma.de")?.Id);
        Assert.Equal(1, Choose("news.firma.de")?.Id);
        Assert.Equal(1, Choose("a.b.firma.de")?.Id);
        Assert.Equal(3, Choose("andere.de")?.Id);
        Assert.Null(Choose("sub.andere.de"));
        Assert.Null(Choose("fremd.de"));
    }

    [Fact]
    public void Of_two_keys_for_a_domain_the_newer_signs()
    {
        Key[] keys = [new(4, "firma.de", false), new(9, "firma.de", false)];
        Assert.Equal(9, Dkim.Choose(keys, "firma.de", k => k.Domain, k => k.Subdomains, k => k.Id)?.Id);
    }

    [Fact]
    public void A_generated_key_can_be_imported_again_and_keeps_its_public_key()
    {
        var (pem, publicKey) = Dkim.Generate(1024);
        Assert.StartsWith("-----BEGIN RSA PRIVATE KEY-----", pem);

        var imported = Dkim.Import(pem);
        Assert.NotNull(imported);
        Assert.Equal(publicKey, imported.Value.PublicKey);
        Assert.Equal(1024, imported.Value.Bits);

        Assert.Null(Dkim.Import("-----BEGIN PUBLIC KEY-----\nAAAA\n-----END PUBLIC KEY-----"));
        Assert.Null(Dkim.Import("Unsinn"));
    }

    [Fact]
    public void The_record_is_compared_by_its_key_whatever_the_spacing()
    {
        const string key = "MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEAx";
        Assert.Equal("matsmtp._domainkey.firma.de", Dkim.RecordName("matsmtp", "firma.de"));
        Assert.Equal(DkimDnsState.Ok, Dkim.Compare([Dkim.RecordValue(key)], key));
        Assert.Equal(DkimDnsState.Ok, Dkim.Compare(["v=DKIM1;k=rsa; p=MIIBIjANBgkqhkiG9w0B AQEFAAOCAQ8AMIIBCgKCAQEAx"], key));
        Assert.Equal(DkimDnsState.Different, Dkim.Compare(["v=DKIM1; k=rsa; p=ANDERS"], key));
        Assert.Equal(DkimDnsState.Revoked, Dkim.Compare(["v=DKIM1; p="], key));
        Assert.Equal(DkimDnsState.Different, Dkim.Compare(["v=DKIM1; p=", "v=DKIM1; p=ANDERS"], key));
        Assert.Equal(DkimDnsState.Missing, Dkim.Compare(["v=spf1 -all"], key));
        Assert.Equal(DkimDnsState.Missing, Dkim.Compare([], key));
    }
}
