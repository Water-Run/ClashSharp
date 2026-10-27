using System;

namespace ClashSharp.ViewModel;

/// <summary>Recognizes known public site names locally, without performing network discovery.</summary>
internal static class ConnectionTestSiteCatalog
{
    private static readonly (string Name, string[] Hosts)[] Sites =
    [
        ("Microsoft", ["microsoft.com", "microsoftonline.com", "live.com", "office.com", "outlook.com", "msn.com"]),
        ("Bing", ["bing.com", "bing.net"]),
        ("Apple", ["apple.com", "icloud.com"]),
        ("Cloudflare", ["cloudflare.com", "cloudflare-dns.com", "one.one.one.one", "1.1.1.1"]),
        ("Google", ["google.co.uk", "google.co.jp", "google.de", "google.fr", "google.ca", "google.com.hk", "google.com.tw", "google.com.sg", "google.com.au", "google.co.kr", "gstatic.com", "googleapis.com"]),
        ("Gmail", ["gmail.com"]),
        ("Facebook", ["facebook.com", "fb.com", "fbcdn.net"]),
        ("Instagram", ["instagram.com", "cdninstagram.com"]),
        ("WhatsApp", ["whatsapp.com", "whatsapp.net"]),
        ("Threads", ["threads.net", "threads.com"]),
        ("X", ["x.com", "twitter.com", "t.co", "twimg.com"]),
        ("Telegram", ["telegram.org", "t.me"]),
        ("Discord", ["discord.com", "discord.gg", "discordapp.com"]),
        ("Reddit", ["reddit.com", "redd.it", "redditstatic.com"]),
        ("Wikipedia", ["wikipedia.org", "wikimedia.org"]),
        ("Stack Overflow", ["stackoverflow.com", "stackexchange.com"]),
        ("GitLab", ["gitlab.com"]),
        ("GitHub", ["githubusercontent.com", "githubassets.com", "github.io"]),
        ("Bitbucket", ["bitbucket.org"]),
        ("npm", ["npmjs.com", "npmjs.org"]),
        ("PyPI", ["pypi.org", "python.org", "pythonhosted.org"]),
        ("Docker", ["docker.com", "docker.io"]),
        ("NuGet", ["nuget.org"]),
        ("Debian", ["debian.org"]),
        ("Ubuntu", ["ubuntu.com", "canonical.com"]),
        ("Arch Linux", ["archlinux.org"]),
        ("Mozilla", ["mozilla.org", "mozilla.com", "firefox.com"]),
        ("Netflix", ["netflix.com", "nflxvideo.net"]),
        ("Spotify", ["spotify.com", "scdn.co"]),
        ("Twitch", ["twitch.tv", "ttvnw.net"]),
        ("Disney+", ["disneyplus.com"]),
        ("Amazon", ["amazon.com", "amazon.co.jp", "amazon.co.uk", "amazon.de", "amazonaws.com", "primevideo.com"]),
        ("Steam", ["steampowered.com", "steamcommunity.com", "steamstatic.com"]),
        ("Epic Games", ["epicgames.com"]),
        ("GOG", ["gog.com"]),
        ("Xbox", ["xbox.com", "xboxlive.com"]),
        ("PlayStation", ["playstation.com", "playstation.net"]),
        ("Nintendo", ["nintendo.com", "nintendo.co.jp"]),
        ("Adobe", ["adobe.com", "adobelogin.com"]),
        ("Zoom", ["zoom.us", "zoom.com"]),
        ("Notion", ["notion.so", "notion.site"]),
        ("Dropbox", ["dropbox.com", "dropboxusercontent.com"]),
        ("OneDrive", ["onedrive.com", "1drv.ms"]),
        ("LinkedIn", ["linkedin.com", "licdn.com"]),
        ("Pinterest", ["pinterest.com", "pinimg.com"]),
        ("Yahoo", ["yahoo.com", "yahoo.co.jp"]),
        ("DuckDuckGo", ["duckduckgo.com"]),
        ("Hugging Face", ["huggingface.co"]),
        ("Anthropic", ["anthropic.com", "claude.ai"]),
        ("Perplexity", ["perplexity.ai"]),
        ("DeepSeek", ["deepseek.com"]),
        ("阿里云", ["aliyun.com", "alibabacloud.com", "aliyuncs.com"]),
        ("腾讯", ["tencent.com", "qq.com", "qcloud.com", "myqcloud.com"]),
        ("微信", ["weixin.qq.com", "wechat.com", "weixin.com"]),
        ("淘宝", ["taobao.com", "tmall.com", "tb.cn"]),
        ("支付宝", ["alipay.com", "alipayobjects.com"]),
        ("京东", ["jd.com", "jd.hk", "360buyimg.com"]),
        ("微博", ["weibo.com", "weibo.cn"]),
        ("小红书", ["xiaohongshu.com", "xhslink.com"]),
        ("抖音", ["douyin.com", "iesdouyin.com"]),
        ("TikTok", ["tiktok.com", "tiktokcdn.com"]),
        ("网易", ["163.com", "126.com", "netease.com"]),
        ("豆瓣", ["douban.com", "doubanio.com"]),
        ("优酷", ["youku.com"]),
        ("爱奇艺", ["iqiyi.com"]),
        ("搜狐", ["sohu.com"]),
    ];

    /// <summary>Returns the most specific known DNS suffix, never a substring match.</summary>
    internal static string? Find(string host)
    {
        string normalized = host.TrimEnd('.');
        string? result = null;
        int specificity = -1;
        foreach ((string name, string[] domains) in Sites)
        {
            foreach (string domain in domains)
            {
                if (domain.Length > specificity && (normalized.Equals(domain, StringComparison.OrdinalIgnoreCase)
                    || normalized.EndsWith('.' + domain, StringComparison.OrdinalIgnoreCase)))
                {
                    result = name;
                    specificity = domain.Length;
                }
            }
        }
        return result;
    }
}
