#:package consoleappframework@*
#:package Microsoft.Extensions.DependencyInjection@*
#:package Microsoft.Extensions.Http@*

#:property PublishAot=false

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ConsoleAppFramework;
using Microsoft.Extensions.DependencyInjection;

var app = ConsoleApp
    .Create()
    .ConfigureServices(
        (_, services) =>
        {
            services.AddHttpClient(
                "api",
                client =>
                {
                    client.BaseAddress = new Uri("https://wereadtoolkit.zone.id/");
                }
            );
            services.AddHttpClient(
                "weread",
                client =>
                {
                    client.BaseAddress = new Uri("https://i.weread.qq.com/");
                    client.DefaultRequestHeaders.Add("User-Agent", Device.UserAgent);
                    client.DefaultRequestHeaders.Add("baseapi", Device.BaseApi);
                    client.DefaultRequestHeaders.Add("appver", Device.Appver);
                    client.DefaultRequestHeaders.Add("osver", Device.OsVer);
                    client.DefaultRequestHeaders.Add("channelid", Device.ChannelId);
                    client.DefaultRequestHeaders.Add("basever", Device.Appver);
                }
            );
        }
    );
app.Add<Commands>();
app.Run(args);

public class Commands
{
    /// <summary>
    /// 签到
    /// </summary>
    /// <param name="httpClientFactory">HTTP client factory</param>
    /// <param name="bookId">-i,Book ID</param>
    /// <param name="readTime">-r,Read time in minutes</param>
    /// <param name="speed">-s,Read speed in words per minute</param>
    /// <param name="stoppingToken">Cancellation token</param>
    /// <param name="delay">-d,Delay minutes before starting</param>
    /// <param name="configPath">-c, config file path or URL</param>
    /// <param name="mask">-m,Mask sensitive information in logs</param>
    /// <returns></returns>
    public async Task Checkin(
        [FromServices] IHttpClientFactory httpClientFactory,
        int bookId,
        int readTime,
        int speed,
        CancellationToken stoppingToken,
        int delay = 0,
        string configPath = "config.json",
        bool mask = false
    )
    {
        Utils.Mask = mask;
        Account? account;
        if (configPath.StartsWith("http"))
        {
            var result = await httpClientFactory.CreateClient().GetFromAsync<Account>(configPath);
            if (result?.StatusCode != HttpStatusCode.OK)
            {
                Utils.Log("Failed to get account");
                return;
            }
            account = result.Result;
        }
        else if (File.Exists(configPath))
        {
            account = JsonSerializer.Deserialize<Account>(File.ReadAllText(configPath));
        }
        else
        {
            Utils.Log("Failed to get account");
            return;
        }
        if (account is null)
        {
            Utils.Log("Failed to get account");
            return;
        }
        Utils.SensitiveData.Add(account.RefreshToken);
        Utils.SensitiveData.Add(account.DeviceId);
        Utils.SensitiveData.Add(account.Vid.ToString());
        Utils.Log($"Account: {account.Vid}");
        await Task.Delay(TimeSpan.FromMinutes(delay), stoppingToken);
        using var apiClient = httpClientFactory.CreateClient("api");
        var signatureResult = await apiClient.GetFromAsync<SignatureResponse>(
            $"/generation/signature?deviceId={account.DeviceId}"
        );
        if (signatureResult is null || signatureResult.Result is null)
        {
            Utils.Log("Failed to get signature");
            return;
        }

        using var wereadClient = httpClientFactory.CreateClient("weread");
        var loginResult = await wereadClient.PostFromAsync<LoginResponse>(
            "/login",
            new
            {
                deviceId = account.DeviceId,
                deviceName = Device.Name,
                inBackground = 0,
                kickType = 1,
                random = signatureResult.Result.Random,
                refCgi = "",
                refreshToken = account.RefreshToken,
                signature = signatureResult.Result.Signature,
                timestamp = signatureResult.Result.Timestamp,
                trackId = "",
                wxToken = 0,
            }
        );
        if (loginResult.StatusCode != HttpStatusCode.OK || loginResult.Result is null)
        {
            Utils.Log("Failed to login");
            return;
        }
        wereadClient.DefaultRequestHeaders.Add("accesstoken", loginResult.Result.accessToken);
        wereadClient.DefaultRequestHeaders.Add("vid", loginResult.Result.vid.ToString());
        var tokenResult = await wereadClient.GetFromAsync<TokenResponse>("/config?token=1");
        if (tokenResult is null || tokenResult.Result is null)
        {
            Utils.Log("Failed to get token");
            return;
        }
        string token = tokenResult.Result.token;
        Utils.SensitiveData.Add(token);
        Utils.Log($"Token: {token}");

        var chapterInfosResult = await wereadClient.PostFromAsync<ChapterInfosResponse>(
            "/book/chapterInfos",
            new { bookIds = new string[] { bookId.ToString() }, synckeys = new int[] { 0 } }
        );
        if (chapterInfosResult is null || chapterInfosResult.Result?.data?[0].updated is null)
        {
            Utils.Log("Failed to get chapter infos");
            return;
        }
        List<ChapterInfo> chapterInfos = chapterInfosResult.Result.data![0].updated!;

        var getBookProgressResult = await wereadClient.GetFromAsync<GetBookProgressResponse>(
            $"/book/getProgress?bookId={bookId}"
        );
        if (getBookProgressResult?.Result?.book is null)
        {
            Utils.Log("Failed to get book progress");
            return;
        }
        var bookProgress = getBookProgressResult.Result.book;
        int readWord = readTime * speed;
        int chapterOffset = bookProgress.chapterOffset + readWord;

        int charpterIdx = bookProgress.chapterIdx;
        if (charpterIdx == 0)
        {
            charpterIdx = chapterInfos.First().chapterIdx;
        }
        int chapterUid = bookProgress.charpterUid;
        for (int i = charpterIdx - 1; i < chapterInfos.Count; i = (i + 1) % chapterInfos.Count)
        {
            charpterIdx = chapterInfos[i].chapterIdx;
            chapterUid = chapterInfos[i].chapterUid;
            if (chapterOffset < chapterInfos[i].wordCount)
            {
                break;
            }
            chapterOffset -= chapterInfos[i].wordCount;
        }

        BookProgressInfo bookProgressInfo = new(
            appId: account.DeviceId,
            bookId: bookId.ToString(),
            bookVersion: bookProgress.bookVersion,
            charpterIdx: charpterIdx,
            chapterOffset: chapterOffset,
            chapterUid: chapterUid,
            progress: chapterOffset / chapterInfos[charpterIdx].wordCount,
            readingTime: readTime * 60 + Random.Shared.Next(10, 50),
            resendReadingInfo: 0,
            summary: bookProgress.summary,
            synckey: bookProgress.synckey
        );
        signatureResult = await apiClient.GetFromAsync<SignatureResponse>(
            $"/generation/signature?token={token}"
        );
        if (signatureResult is null || signatureResult.Result is null)
        {
            Utils.Log("Failed to get signature");
            return;
        }
        var signature = signatureResult.Result;
        var response = await wereadClient.PostFromAsync<SimpleResponse>(
            "/book/batchUploadProgress",
            new UploadBookProgressRequest(
                books: [bookProgressInfo],
                signatureResult.Result.Random,
                signatureResult.Result.Signature,
                signatureResult.Result.Timestamp
            )
        );
        if (response?.Result is null || response.Result.succ != 1)
        {
            Utils.Log("Failed to update book progress");
            return;
        }
        Utils.Log("Book progress updated successfully");
    }
}

#region Models
public class ClientResult<T>
{
    public HttpStatusCode StatusCode { get; set; }
    public HttpResponseHeaders? Headers { get; set; }
    public T? Result { get; set; }
}

public record Account(int Vid, string RefreshToken, string DeviceId);

public record SimpleResponse(int succ);

public record SignatureResponse(string DeviceId, long Timestamp, int Random, string Signature);

public record LoginResponse(int vid, string accessToken, string? refreshToken);

public record TokenResponse(string token, long timestamp);

public record BookInfo(string bookId, long version, string title, string author);

public record ChapterInfo(int chapterUid, int chapterIdx, string title, int wordCount);

public record ChapterInfosData(BookInfo book, List<ChapterInfo> updated);

internal record ChapterInfosResponse(List<ChapterInfosData> data);

public record BookProgressInfoResponse(
    string appId,
    int bookVersion,
    int chapterIdx,
    int charpterUid,
    int chapterOffset,
    string summary,
    int readingTime,
    int synckey
);

internal record GetBookProgressResponse(BookProgressInfoResponse book);

public record BookProgressInfo(
    string appId,
    string bookId,
    long bookVersion,
    int charpterIdx,
    int chapterOffset,
    int chapterUid,
    int readingTime,
    int progress = 1,
    int resendReadingInfo = 1,
    string? summary = null,
    long synckey = 0
);

public record UploadBookProgressRequest(
    List<BookProgressInfo> books,
    int random,
    string signature,
    long timestamp
);

#endregion
public static class HttpClientExtensions
{
    public static async Task<ClientResult<T>> GetFromAsync<T>(this HttpClient client, string? uri)
    {
        var response = await client.GetAsync(uri);
        Utils.Log($"GET {response.RequestMessage?.RequestUri} {response.StatusCode}");
        var result = new ClientResult<T>()
        {
            StatusCode = response.StatusCode,
            Headers = response.Headers,
        };
        if (response.IsSuccessStatusCode)
        {
            result.Result = await response.Content.ReadFromJsonAsync<T>();
        }

        return result;
    }

    public static async Task<ClientResult<T>> PostFromAsync<T>(
        this HttpClient client,
        string? uri,
        object? content
    )
    {
        var response = await client.PostAsJsonAsync(uri, content);
        Utils.Log($"POST {response.RequestMessage?.RequestUri} {response.StatusCode}");
        var result = new ClientResult<T>()
        {
            StatusCode = response.StatusCode,
            Headers = response.Headers,
        };

        if (response.IsSuccessStatusCode)
        {
            result.Result = await response.Content.ReadFromJsonAsync<T>();
        }

        return result;
    }
}

public static class Utils
{
    public static bool Mask { get; set; } = false;
    public static List<string> SensitiveData { get; set; } = new();

    public static void Log(string message)
    {
        if (SensitiveData.Count == 0 || !Mask)
        {
            Console.WriteLine(message);
            return;
        }

        foreach (var data in SensitiveData)
        {
            message = message.Replace(data, "**********");
        }
        Console.WriteLine(message);
    }
}

public static class Device
{
    public const string Name = "微信阅读器(第二代)";

    public const string UserAgent =
        "WeRead/1.9.3 WRBrand/null wr_eink Dalvik/2.1.0 (Linux; U; Android 14)";

    public const string BaseApi = "34";

    public const string Appver = "1.9.3.10244349";

    public const string OsVer = "14";

    public const string ChannelId = "990";
}
