# 微信读书自动签到

一个 C# 控制台应用程序，通过 GitHub Actions 集成实现微信读书自动签到功能。

## 功能特性

- 自动每日签到，使用随机阅读参数
- 使用 GitHub Secrets 安全管理凭据
- 支持手动工作流触发
- 模拟自然阅读行为

## 前提条件

- 微信读书墨水屏版v1.9.3
- 一台root后的安卓设备（否则无法解析https请求）
- 使用抓包工具（如Fiddler、Charles、reqable）在账号登录时进行抓包，获取到账号的 Vid、RefreshToken 和 DeviceId

## 配置文件格式

将上一步骤获取到的 Vid、RefreshToken 和 DeviceId 保存到 JSON 格式的配置文件中：

```json
{
  "Vid": 123456789,
  "RefreshToken": "your_refresh_token_here",
  "DeviceId": "your_device_id_here"
}
```

## 本地运行

如果在本地运行，只需将文件保存为 `config.json` 放在 `app.cs` 所在目录下，然后运行应用程序（需先安装 .NET 10.0）：

```bash
dotnet run app.cs -- checkin -i <bookId> -r <readTime> -s <speed> -c <configPath>
```

参数：
- `-i, --book-id`: 书籍 ID（必需）
- `-r, --read-time`: 阅读时间，单位分钟（必需）
- `-s, --speed`: 阅读速度，单位词/分钟（必需）
- `-d, --delay`: 开始前延迟分钟数（默认：0）
- `-c, --config-path`: 配置文件路径或 URL（默认：config.json）
- `-m, --mask`: 是否隐藏敏感信息



## GitHub Actions 工作流

如果在Github中运行，需要将文件保存在远程目录（如S3等），并提供URL。

### 1. 仓库密钥

在您的 GitHub 仓库中添加以下密钥：

| 密钥名称 | 描述 |
|-------------|-------------|
| `BOOK_ID` | 用于签到的微信读书书籍 ID |
| `CONFIG_PATH` | 包含账户凭据的配置文件路径或 URL |

工作流每天 8:00 UTC 自动运行，也可以手动触发。

### 2. 随机参数

工作流生成随机值以模拟自然阅读：
- **阅读时间**：30-60 分钟
- **阅读速度**：120-180 词/分钟

## 3. 安全性

- 所有敏感数据存储在 GitHub Secrets 中
- 工作流中没有硬编码凭据
- 支持从本地文件或远程 URL 加载配置

## 依赖项

- .NET 10.0
- ConsoleAppFramework
- Microsoft.Extensions.DependencyInjection
- Microsoft.Extensions.Http
