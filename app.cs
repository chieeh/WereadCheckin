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
    private const string _accountFile = "account.json";
    private readonly IHttpClientFactory _httpClientFactory;
    private Account? _account;

    public Commands(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
        if (!File.Exists(_accountFile))
        {
            throw new FileNotFoundException($"{_accountFile} not found");
        }
        _account = JsonSerializer.Deserialize(
            File.ReadAllText(_accountFile),
            SourceGenerationContext.Default.Account
        );
        if (_account?.RefreshToken is null)
        {
            throw new InvalidOperationException("RefreshToken is null");
        }
        Utils.SensitiveData.Add(_account.RefreshToken);
        Utils.SensitiveData.Add(_account.DeviceId);
        Utils.SensitiveData.Add(_account.Vid.ToString());
    }

    /// <summary>
    /// 刷新账户凭证
    /// </summary>
    /// <param name="mask">-m,Mask sensitive information in logs</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns></returns>
    public async Task<int> Refresh(bool mask = false, CancellationToken cancellationToken = default)
    {
        Utils.Mask = mask;
        using var apiClient = _httpClientFactory.CreateClient("api");
        _ = await apiClient.GetAsync("/", cancellationToken);
        var signatureResult = await apiClient.GetFromJsonAsync(
            $"/generation/signature?deviceId={_account!.DeviceId}",
            SourceGenerationContext.Default.SignatureResponse,
            cancellationToken
        );
        if (!signatureResult.IsSuccessStatusCode || signatureResult.Value is null)
        {
            Utils.Log("Failed to get signature");
            return 1;
        }
        var wereadClient = _httpClientFactory.CreateClient("weread");
        var loginContent = new LoginRequest(
            deviceId: _account!.DeviceId,
            deviceName: Device.Name,
            random: signatureResult.Value.Random,
            refreshToken: _account.RefreshToken,
            signature: signatureResult.Value.Signature,
            timestamp: signatureResult.Value.Timestamp
        );
        var loginResult = await wereadClient.PostAsJsonAsync(
            "/login",
            loginContent,
            SourceGenerationContext.Default.LoginRequest,
            SourceGenerationContext.Default.LoginResponse,
            cancellationToken
        );
        if (!loginResult.IsSuccessStatusCode || loginResult.Value?.accessToken is null)
        {
            Utils.Log("Failed to login");
            return 1;
        }
        _account = _account with { AccessToken = loginResult.Value.accessToken };

        await File.WriteAllTextAsync(
            _accountFile,
            JsonSerializer.Serialize(_account, SourceGenerationContext.Default.Account),
            cancellationToken
        );
        Utils.SensitiveData.Add(_account.AccessToken);
        Utils.Log($"Account Vid: {_account.Vid}, AccessToken: {_account.AccessToken}");
        return 0;
    }

    /// <summary>
    /// 签到
    /// </summary>
    /// <param name="bookId">-i,Book ID</param>
    /// <param name="readTime">-r,Read time in minutes</param>
    /// <param name="speed">-s,Read speed in words per minute</param>
    /// <param name="mask">-m,Mask sensitive information in logs</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns></returns>
    public async Task<int> Checkin(
        int bookId,
        int readTime,
        int speed,
        bool mask = false,
        CancellationToken cancellationToken = default
    )
    {
        Utils.Mask = mask;
        var wereadClient = CreateWereadClient();
        if (wereadClient is null)
        {
            Utils.Log("Failed to create weread client");
            return 1;
        }
        var tokenResult = await wereadClient.GetFromJsonAsync(
            "/config?token=1",
            SourceGenerationContext.Default.TokenResponse,
            cancellationToken
        );
        if (!tokenResult.IsSuccessStatusCode || tokenResult.Value?.token is null)
        {
            Utils.Log("Failed to get token");
            return 1;
        }
        string token = tokenResult.Value.token;
        Utils.SensitiveData.Add(token);
        Utils.Log($"Token: {token}");

        var chapterInfosResult = await wereadClient.PostAsJsonAsync(
            "/book/chapterInfos",
            new ChapterInfosRequest(bookIds: [bookId.ToString()], synckeys: [0]),
            SourceGenerationContext.Default.ChapterInfosRequest,
            SourceGenerationContext.Default.ChapterInfosResponse,
            cancellationToken
        );
        if (
            !chapterInfosResult.IsSuccessStatusCode
            || chapterInfosResult.Value?.data[0].updated is null
        )
        {
            Utils.Log("Failed to get chapter infos");
            return 1;
        }
        List<ChapterInfo> chapterInfos = chapterInfosResult.Value.data[0].updated;

        var getBookProgressResult = await wereadClient.GetFromJsonAsync(
            $"/book/getProgress?bookId={bookId}",
            SourceGenerationContext.Default.GetBookProgressResponse,
            cancellationToken
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
            appId: _account!.DeviceId,
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
        using var apiClient = _httpClientFactory.CreateClient("api");
        var signatureResult = await apiClient.GetFromJsonAsync(
            $"/generation/signature?token={token}",
            SourceGenerationContext.Default.SignatureResponse,
            cancellationToken
        );
        if (!signatureResult.IsSuccessStatusCode || signatureResult.Value is null)
        {
            Utils.Log("Failed to get signature");
            return 1;
        }
        var response = await wereadClient.PostAsJsonAsync(
            "/book/batchUploadProgress",
            new UploadBookProgressRequest(
                books: [bookProgressInfo],
                random: signatureResult.Value.Random,
                signature: signatureResult.Value.Signature,
                timestamp: signatureResult.Value.Timestamp
            ),
            SourceGenerationContext.Default.UploadBookProgressRequest,
            SourceGenerationContext.Default.SimpleResponse,
            cancellationToken
        );
        if (!response.IsSuccessStatusCode || response.Value?.succ != 1)
        {
            Utils.Log("Failed to update book progress");
            return 1;
        }
        Utils.Log("Book progress updated successfully");
        return 0;
    }

    /// <summary>
    /// 领取每周奖励
    /// </summary>
    /// <param name="gainType">-t, 1 for 无限卡, 2 for 书币</param>
    /// <param name="configPath">-c, config file path or URL</param>
    /// <param name="mask">-m,Mask sensitive information in logs</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns></returns>
    public async Task<int> Gain(
        int gainType = 1,
        string configPath = "config.json",
        bool mask = false,
        CancellationToken cancellationToken = default
    )
    {
        Utils.Mask = mask;
        var wereadClient = CreateWereadClient();
        if (wereadClient is null)
        {
            Utils.Log("Failed to create weread client");
            return 1;
        }

        var exchangeResult = await wereadClient.PostAsJsonAsync(
            "/weekly/exchange",
            new WeeklyExchangeRequest(0, 0, 0, 1),
            SourceGenerationContext.Default.WeeklyExchangeRequest,
            SourceGenerationContext.Default.WeeklyExchangeResponse,
            cancellationToken
        );
        if (!exchangeResult.IsSuccessStatusCode || exchangeResult.Value is null)
        {
            Utils.Log("Failed to get exchange detail");
            return 1;
        }
        List<ExchangeAward> awards =
        [
            .. exchangeResult.Value.readtimeAwards,
            .. exchangeResult.Value.readdayAwards,
            .. exchangeResult.Value.readgoalAwards,
        ];
        foreach (var award in awards)
        {
            if (award.awardStatus != 1)
            {
                continue;
            }
            await wereadClient.PostAsJsonAsync(
                "/weekly/exchange",
                new WeeklyExchangeRequest(award.awardLevelId, gainType, 1, 1),
                SourceGenerationContext.Default.WeeklyExchangeRequest,
                SourceGenerationContext.Default.WeeklyExchangeResponse,
                cancellationToken
            );
            Utils.Log($"Gain {award.awardLevelId} {gainType}");
        }
        Utils.Log("Gain completed");
        return 0;
    }

    private HttpClient? CreateWereadClient()
    {
        if (_account?.AccessToken is null)
        {
            Utils.Log("AccessToken is null");
            return null;
        }
        Utils.SensitiveData.Add(_account.AccessToken);
        var wereadClient = _httpClientFactory.CreateClient("weread");
        wereadClient.DefaultRequestHeaders.Add("accesstoken", _account.AccessToken);
        wereadClient.DefaultRequestHeaders.Add("vid", _account.Vid.ToString());
        return wereadClient;
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
[JsonSerializable(typeof(WeeklyExchangeRequest))]
[JsonSerializable(typeof(WeeklyExchangeResponse))]
[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Default,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
)]
internal partial class SourceGenerationContext : JsonSerializerContext { }

public record Account(int Vid, string RefreshToken, string DeviceId, string? AccessToken);

public record SimpleResponse(int succ);

public record SignatureResponse(string DeviceId, long Timestamp, int Random, string Signature);

public record LoginResponse(int vid, string accessToken, string? refreshToken);

public record LoginRequest(
    string deviceId,
    string deviceName,
    int random,
    string refreshToken,
    string signature,
    long timestamp,
    int inBackground = 0,
    int kickType = 1,
    string refCgi = "",
    string trackId = "",
    int wxToken = 0
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

public record WeeklyExchangeRequest(
    int awardLevelId,
    int awardChoiceType,
    int isExchangeAward,
    int? isVisitReadGoal = null,
    int unread = 1,
    string pf = "wechat_wx-2001-android-100-weread"
);

public record ExchangeAward(int awardLevelId, int awardStatus);

public record WeeklyExchangeResponse(
    List<ExchangeAward> readtimeAwards,
    List<ExchangeAward> readdayAwards,
    List<ExchangeAward> readgoalAwards
);

#endregion

public static class HttpClientExtensions
{
    public static async Task<HttpResponseMessageWrapper<TResponse>> GetFromJsonAsync<TResponse>(
        this HttpClient client,
        string? uri,
        JsonTypeInfo<TResponse> jsonTypeInfo,
        CancellationToken cancellationToken = default
    )
    {
        var response = await client.GetAsync(uri, cancellationToken);
        Utils.Log($"GET {response.RequestMessage?.RequestUri} {response.StatusCode}");
        TResponse? result = default;
        if (response.IsSuccessStatusCode)
        {
            result = await response.Content.ReadFromJsonAsync(
                jsonTypeInfo,
                cancellationToken: cancellationToken
            );
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
        JsonTypeInfo<TResponse> responseTypeInfo,
        CancellationToken cancellationToken = default
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
        var response = await client.PostAsync(uri, httpContent, cancellationToken);
        Utils.Log($"POST {response.RequestMessage?.RequestUri} {response.StatusCode}");
        TResponse? result = default;
        if (response.IsSuccessStatusCode)
        {
            result = await response.Content.ReadFromJsonAsync(
                responseTypeInfo,
                cancellationToken: cancellationToken
            );
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
