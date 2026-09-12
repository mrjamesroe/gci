using Gci.Core.Services;

namespace Gci.Core.Tests;

public class NtfyTopicTests
{
    [Fact]
    public void New_topics_are_long_random_and_easy_to_type()
    {
        var a = NtfyTopic.NewTopicUrl();
        var b = NtfyTopic.NewTopicUrl();

        Assert.NotEqual(a, b);
        Assert.Matches(@"^https://ntfy\.sh/gci-[a-km-np-z2-9]{18}$", a);
    }

    [Fact]
    public void Topic_name_and_android_subscribe_link()
    {
        const string url = "https://ntfy.sh/gci-syb6vnzsm6w3j4bu6w";
        Assert.Equal("gci-syb6vnzsm6w3j4bu6w", NtfyTopic.TopicName(url));
        Assert.Equal("ntfy://ntfy.sh/gci-syb6vnzsm6w3j4bu6w?display=GCI", NtfyTopic.SubscribeLink(url));
        Assert.True(NtfyTopic.IsPublicServer(url));
    }

    [Fact]
    public void Self_hosted_servers_keep_host_port_and_scheme()
    {
        Assert.Equal("ntfy://home.lan:8080/alerts?display=GCI&secure=false", NtfyTopic.SubscribeLink("http://home.lan:8080/alerts"));
        Assert.False(NtfyTopic.IsPublicServer("https://ntfy.example.com/alerts"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://ntfy.sh/")]
    [InlineData("not a url")]
    public void Invalid_urls_have_no_topic(string? url)
    {
        Assert.Null(NtfyTopic.TopicName(url));
        Assert.Null(NtfyTopic.SubscribeLink(url));
    }
}
