# 微信读书自动签到

一个 C# 控制台应用程序，实现一键上传微信读书阅读时间。

## 功能特性

- 模拟自然阅读行为
- 随意指定阅读时长
- 一键签到，1s内完成阅读任务

## 前提条件

- 微信读书墨水屏版v1.9.3
- 一台root后的安卓设备（否则无法解析https请求）
- 使用抓包工具（如Fiddler、Charles、reqable）在账号登录时进行抓包，查看链接`https://i.weread.qq.com/login`的内容，获取到账号的 Vid、RefreshToken 和 DeviceId

## 配置文件格式

将上一步骤获取到的 Vid、RefreshToken 和 DeviceId 保存到 JSON 格式的配置文件`config.json`中：

```json
{
  "Vid": 123456789,
  "RefreshToken": "your_refresh_token_here",
  "DeviceId": "your_device_id_here"
}
```
## 运行

### 方案1：下载编译文件

您可以从本仓库的 [Releases](https://github.com/chieeh/WereadCheckin/releases) 页面下载已编译的应用程序。目前支持以下平台：

| 平台 | 文件名 |
|------|--------|
| Windows x64 | `wereread-checkin-win-x64.zip` |
| Linux x64 | `wereread-checkin-linux-x64.zip` |
| macOS x64 | `wereread-checkin-osx-x64.zip` |

下载后解压到任意目录，将配置文件 `config.json` 放在与可执行文件同一目录下，然后运行：

**Windows:**
```bash
app.exe checkin -i <bookId> -r <readTime> -s <speed>
```

**Linux/macOS:**
```bash
./app checkin -i <bookId> -r <readTime> -s <speed>
```

### 方案2：使用 .NET 运行源码

需先安装 .NET 10.0

首先clone本本仓库：

```bash
git clone https://github.com/chieeh/WereadCheckin.git
cd WereadCheckin
```

将配置文件 `config.json` 放在 `app.cs` 所在目录下，然后运行：

```bash
dotnet run app.cs -- checkin -i <bookId> -r <readTime> -s <speed>
```

### 方案3： GitHub Actions自动运行

如果在Github中运行，需要将`config.json`文件保存在远程目录（如S3等），并提供URL。启用[签到工作流](https://github.com/chieeh/WereadCheckin/blob/main/.github/workflows/checkin.yml)

在您的 GitHub 仓库中添加以下密钥：

| 密钥名称 | 描述 |
|-------------|-------------|
| `BOOK_ID` | 用于签到的微信读书书籍 ID |
| `CONFIG_PATH` | 包含账户凭据的配置文件路径或 URL |

工作流每天 8:00 UTC 自动运行，也可以手动触发。

工作流生成随机值以模拟自然阅读：
- **阅读时间**：5-10 分钟
- **阅读速度**：120-180 词/分钟

## 参数说明

| 参数 | 说明 | 必需 |
|------|------|------|
| `-i, --book-id` | 书籍 ID | 是 |
| `-r, --read-time` | 阅读时间（分钟） | 是 |
| `-s, --speed` | 阅读速度（词/分钟） | 是 |
| `-d, --delay` | 开始前延迟分钟数 | 否（默认：0） |
| `-c, --config-path` | 配置文件路径或 URL | 否（默认：config.json） |
| `-m, --mask` | 是否隐藏敏感信息 | 否 |

## 依赖项

- .NET 10.0
- ConsoleAppFramework
- Microsoft.Extensions.DependencyInjection
- Microsoft.Extensions.Http
