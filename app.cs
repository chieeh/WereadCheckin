#:package consoleappframework@*
#:package Microsoft.Extensions.DependencyInjection@*
#:package Microsoft.Extensions.Http@*

using System.Net.Http.Json;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using ConsoleAppFramework;
using Microsoft.Extensions.DependencyInjection;

var app = ConsoleApp
    .Create()
    .ConfigureServices(
        (_, services) =>
        {
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
            services.AddHttpClient(
                "api",
                client =>
                {
                    client.BaseAddress = new Uri("https://wereadtoolkit.zone.id/");
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
    public async Task<int> Checkin(
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
        Account? account = null;
        if (configPath.StartsWith("http"))
        {
            using var client = httpClientFactory.CreateClient();
            var rawContent = await client.GetStringAsync(configPath);
            account = JsonSerializer.Deserialize(
                rawContent,
                SourceGenerationContext.Default.Account
            );
        }
        else if (File.Exists(configPath))
        {
            account = JsonSerializer.Deserialize(
                File.ReadAllText(configPath),
                SourceGenerationContext.Default.Account
            );
        }
        if (account is null)
        {
            Utils.Log("Failed to get account");
            return 1;
        }
        Utils.SensitiveData.Add(account.RefreshToken);
        Utils.SensitiveData.Add(account.DeviceId);
        Utils.SensitiveData.Add(account.Vid.ToString());
        Utils.Log($"Account Vid: {account.Vid}");
        await Task.Delay(TimeSpan.FromMinutes(delay), stoppingToken);
        using var apiClient = httpClientFactory.CreateClient("api");
        _ = await apiClient.GetAsync("/");
        var signatureResult = await apiClient.GetFromJsonAsync<SignatureResponse>(
            $"/generation/signature?deviceId={account.DeviceId}",
            SourceGenerationContext.Default.SignatureResponse
        );
        if (!signatureResult.IsSuccessStatusCode || signatureResult.Value is null)
        {
            Utils.Log("Failed to get signature");
            return 1;
        }

        using var wereadClient = httpClientFactory.CreateClient("weread");
        var loginContent = new LoginRequest(
            deviceId: account.DeviceId,
            deviceName: Device.Name,
            inBackground: 0,
            kickType: 1,
            random: signatureResult.Value.Random,
            refCgi: "",
            refreshToken: account.RefreshToken,
            signature: signatureResult.Value.Signature,
            timestamp: signatureResult.Value.Timestamp,
            trackId: "",
            wxToken: 0
        );
        var loginResult = await wereadClient.PostAsJsonAsync<LoginRequest, LoginResponse>(
            "/login",
            loginContent,
            SourceGenerationContext.Default.LoginRequest,
            SourceGenerationContext.Default.LoginResponse
        );
        if (!loginResult.IsSuccessStatusCode || loginResult.Value?.accessToken is null)
        {
            Utils.Log("Failed to login");
            return 1;
        }
        var loginResponse = loginResult.Value;
        wereadClient.DefaultRequestHeaders.Add("accesstoken", loginResponse.accessToken);
        wereadClient.DefaultRequestHeaders.Add("vid", loginResponse.vid.ToString());
        var tokenResult = await wereadClient.GetFromJsonAsync<TokenResponse>(
            "/config?token=1",
            SourceGenerationContext.Default.TokenResponse
        );
        if (!tokenResult.IsSuccessStatusCode || tokenResult.Value?.token is null)
        {
            Utils.Log("Failed to get token");
            return 1;
        }
        string token = tokenResult.Value.token;
        Utils.SensitiveData.Add(token);
        Utils.Log($"Token: {token}");

        var chapterInfosResult = await wereadClient.PostAsJsonAsync<
            ChapterInfosRequest,
            ChapterInfosResponse
        >(
            "/book/chapterInfos",
            new ChapterInfosRequest(
                bookIds: new string[] { bookId.ToString() },
                synckeys: new int[] { 0 }
            ),
            SourceGenerationContext.Default.ChapterInfosRequest,
            SourceGenerationContext.Default.ChapterInfosResponse
        );
        if (
            !chapterInfosResult.IsSuccessStatusCode
            || chapterInfosResult.Value?.data?[0].updated is null
        )
        {
            Utils.Log("Failed to get chapter infos");
            return 1;
        }
        List<ChapterInfo> chapterInfos = chapterInfosResult.Value.data![0].updated!;

        var getBookProgressResult = await wereadClient.GetFromJsonAsync<GetBookProgressResponse>(
            $"/book/getProgress?bookId={bookId}",
            SourceGenerationContext.Default.GetBookProgressResponse
        );
        if (!getBookProgressResult.IsSuccessStatusCode || getBookProgressResult.Value?.book is null)
        {
            Utils.Log("Failed to get book progress");
            return 1;
        }
        var bookProgress = getBookProgressResult.Value.book;
        int readWord = readTime * speed;
        int chapterOffset = bookProgress.chapterOffset + readWord;
        int progress = bookProgress.progress;

        int chapterIdx = bookProgress.chapterIdx;
        if (chapterIdx == 0)
        {
            chapterIdx = chapterInfos.First().chapterIdx;
        }
        int chapterUid = bookProgress.chapterUid;
        for (int i = chapterIdx - 1; i < chapterInfos.Count; i = (i + 1) % chapterInfos.Count)
        {
            chapterIdx = chapterInfos[i].chapterIdx;
            chapterUid = chapterInfos[i].chapterUid;
            if (chapterOffset < chapterInfos[i].wordCount)
            {
                progress = chapterOffset * 100 / chapterInfos[i].wordCount;
                break;
            }
            chapterOffset -= chapterInfos[i].wordCount;
        }

        BookProgressInfo bookProgressInfo = new(
            appId: account.DeviceId,
            bookId: bookId.ToString(),
            bookVersion: bookProgress.bookVersion,
            chapterIdx: chapterIdx,
            chapterOffset: chapterOffset,
            chapterUid: chapterUid,
            progress: progress,
            readingTime: readTime * 60 + Random.Shared.Next(10, 50),
            resendReadingInfo: 0,
            summary: bookProgress.summary,
            synckey: bookProgress.synckey
        );
        signatureResult = await apiClient.GetFromJsonAsync<SignatureResponse>(
            $"/generation/signature?token={token}",
            SourceGenerationContext.Default.SignatureResponse
        );
        if (!signatureResult.IsSuccessStatusCode || signatureResult.Value is null)
        {
            Utils.Log("Failed to get signature");
            return 1;
        }
        var response = await wereadClient.PostAsJsonAsync<
            UploadBookProgressRequest,
            SimpleResponse
        >(
            "/book/batchUploadProgress",
            new UploadBookProgressRequest(
                books: [bookProgressInfo],
                random: signatureResult.Value.Random,
                signature: signatureResult.Value.Signature,
                timestamp: signatureResult.Value.Timestamp
            ),
            SourceGenerationContext.Default.UploadBookProgressRequest,
            SourceGenerationContext.Default.SimpleResponse
        );
        if (!response.IsSuccessStatusCode || response.Value?.succ != 1)
        {
            Utils.Log("Failed to update book progress");
            return 1;
        }
        Utils.Log("Book progress updated successfully");
        return 0;
    }
}

#region Models
[JsonSerializable(typeof(Account))]
[JsonSerializable(typeof(SimpleResponse))]
[JsonSerializable(typeof(SignatureResponse))]
[JsonSerializable(typeof(LoginResponse))]
[JsonSerializable(typeof(LoginRequest))]
[JsonSerializable(typeof(TokenResponse))]
[JsonSerializable(typeof(BookInfo))]
[JsonSerializable(typeof(ChapterInfo))]
[JsonSerializable(typeof(ChapterInfosData))]
[JsonSerializable(typeof(ChapterInfosResponse))]
[JsonSerializable(typeof(ChapterInfosRequest))]
[JsonSerializable(typeof(BookProgressInfoResponse))]
[JsonSerializable(typeof(GetBookProgressResponse))]
[JsonSerializable(typeof(BookProgressInfo))]
[JsonSerializable(typeof(UploadBookProgressRequest))]
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Default)]
internal partial class SourceGenerationContext : JsonSerializerContext { }

public record Account(int Vid, string RefreshToken, string DeviceId);

public record SimpleResponse(int succ);

public record SignatureResponse(string DeviceId, long Timestamp, int Random, string Signature);

public record LoginResponse(int vid, string accessToken, string? refreshToken);

public record LoginRequest(
    string deviceId,
    string deviceName,
    int inBackground,
    int kickType,
    int random,
    string refCgi,
    string refreshToken,
    string signature,
    long timestamp,
    string trackId,
    int wxToken
);

public record TokenResponse(string token, long timestamp);

public record BookInfo(string bookId, long version, string title, string author);

public record ChapterInfo(int chapterUid, int chapterIdx, string title, int wordCount);

public record ChapterInfosData(BookInfo book, List<ChapterInfo> updated);

internal record ChapterInfosResponse(List<ChapterInfosData> data);

public record ChapterInfosRequest(string[] bookIds, int[] synckeys);

public record BookProgressInfoResponse(
    string appId,
    int bookVersion,
    int chapterIdx,
    int chapterUid,
    int chapterOffset,
    string summary,
    int readingTime,
    int progress,
    int synckey
);

internal record GetBookProgressResponse(BookProgressInfoResponse book);

public record BookProgressInfo(
    string appId,
    string bookId,
    long bookVersion,
    int chapterIdx,
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
    public static async Task<HttpResponseMessageWrapper<TResponse>> GetFromJsonAsync<TResponse>(
        this HttpClient client,
        string? uri,
        JsonTypeInfo<TResponse> jsonTypeInfo
    )
    {
        var response = await client.GetAsync(uri);
        Utils.Log($"GET {response.RequestMessage?.RequestUri} {response.StatusCode}");
        TResponse? result = default;
        if (response.IsSuccessStatusCode)
        {
            result = await response.Content.ReadFromJsonAsync(jsonTypeInfo);
        }
        return new HttpResponseMessageWrapper<TResponse>(response, result);
    }

    public static async Task<HttpResponseMessageWrapper<TResponse>> PostAsJsonAsync<
        TRequest,
        TResponse
    >(
        this HttpClient client,
        string? uri,
        TRequest content,
        JsonTypeInfo<TRequest> requestTypeInfo,
        JsonTypeInfo<TResponse> responseTypeInfo
    )
    {
        using var stream = new MemoryStream();
        var writer = new Utf8JsonWriter(
            stream,
            new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }
        );
        await using (writer)
        {
            JsonSerializer.Serialize(writer, content, requestTypeInfo);
        }
        stream.Position = 0;
        var httpContent = new StreamContent(stream);
        httpContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
            "application/json"
        );
        var response = await client.PostAsync(uri, httpContent);
        Utils.Log($"POST {response.RequestMessage?.RequestUri} {response.StatusCode}");
        TResponse? result = default;
        if (response.IsSuccessStatusCode)
        {
            result = await response.Content.ReadFromJsonAsync(responseTypeInfo);
        }
        return new HttpResponseMessageWrapper<TResponse>(response, result);
    }
}

public class HttpResponseMessageWrapper<T> : HttpResponseMessage
{
    public T? Value { get; }

    public HttpResponseMessageWrapper(HttpResponseMessage response, T? value)
    {
        StatusCode = response.StatusCode;
        ReasonPhrase = response.ReasonPhrase;
        Version = response.Version;
        Content = response.Content;
        Value = value;
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
