using GxbrielZapret.Models;
using GxbrielZapret.Services.Strategies;
using Xunit;

namespace GxbrielZapret.Tests;

public sealed class BlockcheckPlanBuilderTests
{
    [Fact]
    public void DifferentServiceRecipesBecomeOneScopedProcess()
    {
        const string report = "* SUMMARY\ncurl_test_https_tls12 ipv4 discord.com : winws2 --wf-l3=ipv4 --wf-tcp-out=443 --payload=tls_client_hello --lua-desync=multisplit:pos=1\nhttps_tls13 ipv4 www.youtube.com : winws2.exe --wf-tcp-out=443 --payload=tls_client_hello --lua-desync=multidisorder:pos=midsld";
        var plan = Assert.Single(BlockcheckPlanBuilder.Build(report, AutoScope.Both));
        Assert.Equal(1, plan.Args.Count(a => a == "--wf-tcp-out=443"));
        Assert.Equal(1, plan.Args.Count(a => a == "--new"));
        Assert.Contains("{HOSTLIST:discord}", plan.Args);
        Assert.Contains("{HOSTLIST:youtube}", plan.Args);
        Assert.True(plan.Args.IndexOf("--lua-desync=multisplit:pos=1") < plan.Args.IndexOf("--new"));
        Assert.True(plan.Args.IndexOf("--lua-desync=multidisorder:pos=midsld") > plan.Args.IndexOf("--new"));
        Assert.Equal("UNTESTED", plan.Status);
        Assert.Null(plan.FilePath);
    }

    [Fact]
    public void CommonAndPerServicePlansAreDeduplicatedAndBounded()
    {
        var lines = Enumerable.Range(1, 30).Select(i => $"https_tls12 ipv4 discord.com : winws2 --lua-desync=multisplit:pos={i}")
            .Concat(Enumerable.Range(1, 30).Select(i => $"https_tls13 ipv4 www.youtube.com : winws2 --lua-desync=multidisorder:pos={i}"));
        string report = "* SUMMARY\n" + string.Join('\n', lines) + "\n* COMMON\nhttps_tls12 ipv4 : winws2 --lua-desync=fake\nhttps_tls13 ipv4 : winws2 --lua-desync=fake";
        var plans = BlockcheckPlanBuilder.Build(report, AutoScope.Both);
        Assert.Equal(BlockcheckPlanBuilder.MaxPlans, plans.Count);
        Assert.Equal(plans.Count, plans.Select(p => p.Fingerprint).Distinct().Count());
    }

    [Theory]
    [InlineData("winws2 --lua-desync=fake\n!!!!! AVAILABLE !!!!!")]
    [InlineData("* SUMMARY\nhttps_tls12 ipv4 example.org : winws2 --lua-desync=fake")]
    [InlineData("* SUMMARY\nhttps_tls12 ipv4 www.youtube.com : winws2 not working")]
    [InlineData("* COMMON\nhttps_tls12 ipv4 : winws2 --lua-desync=fake --blob=data:@/tmp/test.bin")]
    [InlineData("* COMMON\nhttps_tls12 ipv4 : winws2 --lua-desync=fake ; exit")]
    [InlineData("* COMMON\nhttps_tls12 ipv4 : winws2 --lua-desync='unterminated")]
    [InlineData("* COMMON\nhttps_tls12 ipv4 : winws2 --lua-desync=$(exit)")]
    [InlineData("* OTHER\nhttps_tls12 ipv4 : winws2 --lua-desync=fake")]
    public void UnsupportedTraceOrUnsafeCommandsNeverProducePlans(string report) =>
        Assert.Empty(BlockcheckPlanBuilder.Build(report, AutoScope.YouTube));

    [Fact]
    public void PreservesLuaStringLiteralsAndPayloadOrder()
    {
        const string report = "* COMMON\nhttps_tls12 ipv4 : winws2 --wf-tcp-out=443 --lua-init=fake_default_tls=tls_mod(fake_default_tls,'rnd') --lua-desync=wssize:wsize=1:scale=6 --payload=tls_client_hello --lua-desync=multidisorder:pos=1";
        var plan = Assert.Single(BlockcheckPlanBuilder.Build(report, AutoScope.YouTube));
        Assert.Contains("--lua-init=fake_default_tls=tls_mod(fake_default_tls,'rnd')", plan.Args);
        Assert.True(plan.Args.IndexOf("--lua-desync=wssize:wsize=1:scale=6") < plan.Args.IndexOf("--payload=tls_client_hello"));
        Assert.DoesNotContain("{HOSTLIST:discord}", plan.Args);
    }

    [Fact]
    public void ConflictingGlobalLuaInitializersAreNotSilentlyCombined()
    {
        const string report = "* SUMMARY\nhttps_tls12 ipv4 discord.com : winws2 --lua-init=fake_default_tls=first --lua-desync=fake\nhttps_tls13 ipv4 www.youtube.com : winws2 --lua-init=fake_default_tls=second --lua-desync=fake";
        Assert.Empty(BlockcheckPlanBuilder.Build(report, AutoScope.Both));
    }

    [Fact]
    public void DirectServiceDoesNotRequireInventedBypassRecipe()
    {
        string report = "* SUMMARY\n" + string.Join('\n', AutoScope.Discord.GoalHosts().Select(h => $"https_tls12 ipv4 {h} : working without bypass")) +
            "\nhttps_tls13 ipv4 www.youtube.com : winws2 --lua-desync=multisplit:pos=1";
        var plan = Assert.Single(BlockcheckPlanBuilder.Build(report, AutoScope.Both));
        Assert.Contains("{HOSTLIST:discord}", plan.Args);
        Assert.Equal(1, plan.Args.Count(a => a.StartsWith("--lua-desync=")));
        Assert.True(plan.Args.IndexOf("--lua-desync=multisplit:pos=1") > plan.Args.IndexOf("--new"));
    }

    [Theory]
    [InlineData(AutoScope.YouTube, 1)]
    [InlineData(AutoScope.Discord, 1)]
    [InlineData(AutoScope.Both, 2)]
    public void QuicFallbackIsExplicitAndServiceScoped(AutoScope scope, int expectedRules)
    {
        const string report = "* COMMON\nhttps_tls12 ipv4 : winws2 --lua-desync=multisplit:pos=1";
        var regular = Assert.Single(BlockcheckPlanBuilder.Build(report, scope));
        Assert.DoesNotContain("--wf-udp-out=443", regular.Args);
        var quicOff = Assert.Single(BlockcheckPlanBuilder.Build(report, scope, disableQuic: true));
        Assert.Contains("--wf-udp-out=443", quicOff.Args);
        Assert.Equal(expectedRules, quicOff.Args.Count(a => a == "--lua-desync=drop"));
        Assert.Equal(expectedRules * 2, quicOff.Args.Count(a => a.StartsWith("{HOSTLIST:")));
    }
}
